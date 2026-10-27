using Hotel.Shared;
using Hotel.Shared.Auth;
using Hotel.Shared.Contracts;
using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hotel.Bookings;

/// <summary>HTTP side of the saga: writes the booking and its first outbox row in one local transaction.</summary>
public class BookingService(BookingsDbContext db, IRoomTypeCatalog roomTypes, ICurrentUser user)
{
    public async Task<Booking> CreateAsync(CreateBookingRequest request, CancellationToken ct)
    {
        var ownerId = user.Id ?? throw new DomainException(401, "Sign in to book.");
        if (string.IsNullOrWhiteSpace(request.RoomTypeCode))
            throw new DomainException(400, "Room type code is required.");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var error = BookingRules.Validate(request.Reference, request.CheckIn, request.Nights, request.Rooms, today);
        if (error is not null)
            throw new DomainException(400, error);

        var roomType = await roomTypes.FindAsync(request.RoomTypeCode.Trim(), ct)
            ?? throw new DomainException(404, $"RoomType '{request.RoomTypeCode}' does not exist.");

        var checkIn = request.CheckIn!.Value;
        var nights = (int)request.Nights;
        var rooms = (int)request.Rooms;
        var openNights = await roomTypes.OpenNightsAsync(roomType.Id, checkIn, nights, ct);
        if (openNights != nights)
            throw new DomainException(400, "The room type is not open for sale on every night of that stay.");

        var reference = request.Reference!.Trim();
        var now = DateTimeOffset.UtcNow;
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId,
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
        db.Enqueue("bookings", Topology.ReserveKey, booking.Id, new ReserveRooms(booking.Id, ownerId, roomType.Id, checkIn, nights, rooms, reference));
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

    /// <summary>The owner or staff only. Anyone else gets a 404, so ids cannot be probed.</summary>
    public async Task<Booking> GetAsync(Guid id, CancellationToken ct)
    {
        var booking = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (booking is null || !OwnerAccess.CanAccess(user, booking.OwnerId))
            throw new DomainException(404, "Booking not found.");
        return booking;
    }

    public async Task<List<Booking>> ListAsync(string? roomTypeCode, CancellationToken ct)
    {
        var query = db.Bookings.AsNoTracking();
        if (!user.IsStaff)
        {
            var me = user.Id ?? throw new DomainException(401, "Sign in to see bookings.");
            query = query.Where(b => b.OwnerId == me);
        }
        if (!string.IsNullOrWhiteSpace(roomTypeCode))
            query = query.Where(b => b.RoomTypeCode == roomTypeCode);
        return await query.OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct);
    }

    /// <summary>Moves Held or Confirmed to Cancelled and asks Rooms to give the nights back. The voucher is not touched.</summary>
    public async Task<Booking> CancelAsync(Guid id, CancellationToken ct)
    {
        await GetAsync(id, ct); // 404 unless the caller may see it

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
            var current = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id, ct);
            throw new DomainException(409, $"A booking in status {current.Status} cannot be cancelled.");
        }

        db.Enqueue("bookings", Topology.ReleaseKey, id, new ReleaseRooms(id, "cancellation"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == id, ct);
    }
}
