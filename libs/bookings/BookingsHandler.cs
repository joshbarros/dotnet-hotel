using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hotel.Bookings;

public enum MarkOutcome { Marked, Skipped, Duplicate }

/// <summary>Consumes the events that move a booking forward. Each step runs only from the status it expects.</summary>
public class BookingsHandler(BookingsDbContext db, IOptions<BookingOptions> options) : IMessageHandler
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
