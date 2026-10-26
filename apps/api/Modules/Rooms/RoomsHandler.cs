using Hotel.Api.Data;
using Hotel.Api.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Api.Modules.Rooms;

public enum ReserveOutcome { Held, Short, Duplicate }

/// <summary>
/// The only writer of RoomNight.RoomsLeft and of room holds.
/// A stay is all-or-nothing: one conditional UPDATE decrements every night of the stay that still has enough rooms,
/// and the hold is kept only if it touched exactly as many rows as the stay has nights. Anything less is rolled back.
/// </summary>
public class RoomsHandler(HotelDbContext db) : IMessageHandler
{
    public const string Name = "rooms";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => ReserveAsync(delivery.MessageId, Json.Parse<ReserveRooms>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleaseRooms>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Rooms does not handle '{delivery.RoutingKey}'."),
    };

    public async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveRooms command, CancellationToken ct = default)
    {
        var checkOut = command.CheckIn.AddDays(command.Nights);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await db.ClaimAsync(Name, messageId, ct))
            {
                await tx.CommitAsync(ct);
                return ReserveOutcome.Duplicate;
            }

            var updated = await db.RoomNights
                .Where(n => n.RoomTypeId == command.RoomTypeId
                    && n.Night >= command.CheckIn && n.Night < checkOut
                    && n.RoomsLeft >= command.Rooms)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.RoomsLeft, n => n.RoomsLeft - command.Rooms), ct);

            if (updated == command.Nights)
            {
                db.Holds.Add(new RoomHold
                {
                    BookingId = command.BookingId,
                    RoomTypeId = command.RoomTypeId,
                    CheckIn = command.CheckIn,
                    Nights = command.Nights,
                    Rooms = command.Rooms,
                    Status = HoldStatus.Held,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.Enqueue("rooms", Topology.HeldKey, command.BookingId,
                    new RoomsHeld(command.BookingId, command.RoomTypeId, command.CheckIn, command.Nights, command.Rooms, command.Reference));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ReserveOutcome.Held;
            }

            // At least one night was short (or missing): undo the nights that did match, and the inbox claim with them.
            await tx.RollbackAsync(ct);
        }

        db.ChangeTracker.Clear();
        await using var shortTx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await shortTx.CommitAsync(ct);
            return ReserveOutcome.Duplicate;
        }

        var fewest = await db.RoomNights
            .Where(n => n.RoomTypeId == command.RoomTypeId && n.Night >= command.CheckIn && n.Night < checkOut)
            .Select(n => (int?)n.RoomsLeft)
            .MinAsync(ct) ?? 0;
        db.Enqueue("rooms", Topology.ShortKey, command.BookingId,
            new RoomsShort(command.BookingId, command.Reference, command.Rooms, command.Nights, fewest));
        await db.SaveChangesAsync(ct);
        await shortTx.CommitAsync(ct);
        return ReserveOutcome.Short;
    }

    /// <summary>Gives the held rooms back on every night of the stay, exactly once.</summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleaseRooms command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.BookingId == command.BookingId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.BookingId == command.BookingId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                var checkOut = hold.CheckIn.AddDays(hold.Nights);
                await db.RoomNights
                    .Where(n => n.RoomTypeId == hold.RoomTypeId
                        && n.Night >= hold.CheckIn && n.Night < checkOut
                        && n.RoomsLeft + hold.Rooms <= n.TotalRooms)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.RoomsLeft, n => n.RoomsLeft + hold.Rooms), ct);
                db.Enqueue("rooms", Topology.ReleasedKey, command.BookingId,
                    new RoomsReleased(command.BookingId, hold.Rooms, hold.Nights, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
