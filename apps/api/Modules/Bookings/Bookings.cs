using Hotel.Api.Data;
using Hotel.Api.Messaging;
using Hotel.Api.Modules.Rooms;
using Hotel.Api.Modules.Vouchers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hotel.Api.Modules.Bookings;

public enum BookingStatus { Requested, Held, Confirmed, Cancelled, Rejected }

public class Booking
{
    public Guid Id { get; set; }
    public Guid RoomTypeId { get; set; }
    public string RoomTypeCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public DateOnly CheckIn { get; set; }
    public int Nights { get; set; }
    public int Rooms { get; set; }
    public long TotalCents { get; set; }
    public BookingStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? VoucherNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RoomsReleasedAt { get; set; }
}

public class BookingOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the cancellation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public class DomainException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record CreateBookingRequest(string? RoomTypeCode, string? Reference, DateOnly? CheckIn, long Nights, long Rooms);

public record BookingDto(
    Guid Id, string RoomTypeCode, string Reference, DateOnly CheckIn, DateOnly CheckOut, int Nights, int Rooms,
    long TotalCents, string Status, string? Reason, string? VoucherNumber, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? RoomsReleasedAt)
{
    public static BookingDto From(Booking b) => new(
        b.Id, b.RoomTypeCode, b.Reference, b.CheckIn, b.CheckIn.AddDays(b.Nights), b.Nights, b.Rooms,
        b.TotalCents, b.Status.ToString(), b.Reason, b.VoucherNumber, b.CreatedAt, b.UpdatedAt, b.RoomsReleasedAt);
}

/// <summary>HTTP side of the saga: writes the booking and its first outbox row in one local transaction.</summary>
public class BookingService(HotelDbContext db)
{
    public async Task<Booking> CreateAsync(CreateBookingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RoomTypeCode))
            throw new DomainException(400, "Room type code is required.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var error = BookingRules.Validate(request.Reference, request.CheckIn, request.Nights, request.Rooms, today);
        if (error is not null)
            throw new DomainException(400, error);

        var roomType = await db.RoomTypes.AsNoTracking().FirstOrDefaultAsync(r => r.Code == request.RoomTypeCode.Trim(), ct)
            ?? throw new DomainException(404, $"Room type '{request.RoomTypeCode}' does not exist.");

        var checkIn = request.CheckIn!.Value;
        var nights = (int)request.Nights;
        var rooms = (int)request.Rooms;
        var checkOut = checkIn.AddDays(nights);
        var openNights = await db.RoomNights.CountAsync(n => n.RoomTypeId == roomType.Id && n.Night >= checkIn && n.Night < checkOut, ct);
        if (openNights != nights)
            throw new DomainException(400, "The room type is not open for sale on every night of that stay.");

        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            RoomTypeId = roomType.Id,
            RoomTypeCode = roomType.Code,
            Reference = reference,
            CheckIn = checkIn,
            Nights = nights,
            Rooms = rooms,
            TotalCents = BookingRules.TotalCents(roomType.RateCents, nights, rooms),
            Status = BookingStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Bookings.Add(booking);
        db.Enqueue("bookings", Topology.ReserveKey, booking.Id, new ReserveRooms(booking.Id, roomType.Id, checkIn, nights, rooms, reference));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainException(409, $"Reference '{reference}' is already booked on {roomType.Code}.");
        }
        await tx.CommitAsync(ct);
        return booking;
    }

    /// <summary>Moves Held or Confirmed to Cancelled and asks Rooms to give the nights back. The voucher is not touched.</summary>
    public async Task<Booking> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var moved = await db.Bookings
            .Where(b => b.Id == id && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Confirmed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Cancelled)
                .SetProperty(b => b.Reason, "Cancelled by request.")
                .SetProperty(b => b.UpdatedAt, now), ct);
        if (moved != 1)
        {
            var current = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct)
                ?? throw new DomainException(404, "Booking not found.");
            throw new DomainException(409, $"A booking in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("bookings", Topology.ReleaseKey, id, new ReleaseRooms(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a booking forward. Each step runs only from the status it expects.</summary>
public class BookingsHandler(HotelDbContext db, IOptions<BookingOptions> options) : IMessageHandler
{
    public const string Name = "bookings";

    public Task HandleAsync(Delivery d, CancellationToken ct) => d.RoutingKey switch
    {
        Topology.HeldKey => MarkHeldAsync(d.MessageId, Json.Parse<RoomsHeld>(d.Body).BookingId, ct),
        Topology.ShortKey => MarkShortAsync(d.MessageId, Json.Parse<RoomsShort>(d.Body), ct),
        Topology.IssuedKey => MarkConfirmedAsync(d.MessageId, Json.Parse<VoucherIssued>(d.Body), ct),
        Topology.RejectedKey => MarkRejectedAsync(d.MessageId, Json.Parse<VoucherRejected>(d.Body), ct),
        Topology.ReleasedKey => MarkReleasedAsync(d.MessageId, Json.Parse<RoomsReleased>(d.Body), ct),
        _ => throw new InvalidOperationException($"Bookings does not handle '{d.RoutingKey}'."),
    };

    public Task<MarkOutcome> MarkHeldAsync(Guid messageId, Guid bookingId, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == bookingId && b.Status == BookingStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Held)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkShortAsync(Guid messageId, RoomsShort m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && b.Status == BookingStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Rejected)
                .SetProperty(b => b.Reason, $"Short: {m.RequestedRooms} room(s) for {m.Nights} night(s) wanted, {m.FewestRoomsLeft} left on the tightest night.")
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    /// <summary>The final mark. If a cancellation got there first the row is no longer Held and nothing changes.</summary>
    public async Task<MarkOutcome> MarkConfirmedAsync(Guid messageId, VoucherIssued m, CancellationToken ct = default)
    {
        if (options.Value.FinalMarkDelayMs > 0)
            await Task.Delay(options.Value.FinalMarkDelayMs, ct);

        return await StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && b.Status == BookingStatus.Held)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Confirmed)
                .SetProperty(b => b.VoucherNumber, m.Number)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct),
            bookingIdForOrderCheck: m.BookingId);
    }

    public Task<MarkOutcome> MarkRejectedAsync(Guid messageId, VoucherRejected m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Requested))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BookingStatus.Rejected)
                .SetProperty(b => b.Reason, m.Reason)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow), ct));

    public Task<MarkOutcome> MarkReleasedAsync(Guid messageId, RoomsReleased m, CancellationToken ct = default) =>
        StepAsync(messageId, ct, () => db.Bookings
            .Where(b => b.Id == m.BookingId && b.RoomsReleasedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.RoomsReleasedAt, DateTimeOffset.UtcNow), ct));

    private async Task<MarkOutcome> StepAsync(Guid messageId, CancellationToken ct, Func<Task<int>> update, Guid? bookingIdForOrderCheck = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return MarkOutcome.Duplicate;
        }

        var changed = await update();
        if (changed == 0 && bookingIdForOrderCheck is { } id)
        {
            // Skipped for a reason we can name? Requested means the "Held" mark has not landed yet: retry, do not drop.
            var status = await db.Bookings.AsNoTracking().Where(b => b.Id == id).Select(b => b.Status).FirstOrDefaultAsync(ct);
            if (status == BookingStatus.Requested)
                throw new OutOfOrderException("Final mark arrived before the Held mark.");
        }

        await tx.CommitAsync(ct);
        return changed == 1 ? MarkOutcome.Marked : MarkOutcome.Skipped;
    }
}
