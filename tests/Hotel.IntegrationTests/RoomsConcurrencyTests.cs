using System.Collections.Concurrent;
using Hotel.Rooms;
using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hotel.IntegrationTests;

public class RoomsConcurrencyTests
{
    private static ReserveRooms Stay(Guid bookingId, Guid roomTypeId, int fromNight, int nights, int rooms, string reference) =>
        new(bookingId, Guid.NewGuid(), roomTypeId, TestDatabase.FirstNight.AddDays(fromNight), nights, rooms, reference);

    private static async Task<ReserveOutcome> ReserveAsync(Guid messageId, ReserveRooms command)
    {
        await using var db = TestDatabase.Rooms();
        return await new RoomsHandler(db).ReserveAsync(messageId, command);
    }

    [Fact]
    public async Task LastRoom_TwoConcurrentStays_OneHeldOneShortNoHoldForTheLoser()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(totalRooms: 1);

        var outcomes = new ConcurrentBag<(Guid Booking, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var bookingId = Guid.CreateVersion7();
            var outcome = await ReserveAsync(Guid.CreateVersion7(), Stay(bookingId, roomType.Id, 0, 3, 1, $"GUEST-{n}"));
            outcomes.Add((bookingId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        await using var check = TestDatabase.Rooms();
        var holds = await check.Holds.ToListAsync();
        var loser = outcomes.Single(o => o.Outcome == ReserveOutcome.Short).Booking;

        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Held);
        Assert.Single(outcomes, o => o.Outcome == ReserveOutcome.Short);
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1, 1 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 7));
        Assert.Single(holds);
        Assert.DoesNotContain(holds, h => h.BookingId == loser);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey));
    }

    [Fact]
    public async Task OverlappingStays_NeverLeaveAPartialDecrement()
    {
        // Nights 0-2 and nights 2-4 share night 2, which has one room. Whoever loses must change nothing.
        for (var round = 0; round < 5; round++)
        {
            await TestDatabase.OpenAsync();
            var roomType = await TestDatabase.AddRoomTypeAsync(totalRooms: 1, openNights: 5);
            var a = Guid.CreateVersion7();
            var b = Guid.CreateVersion7();

            var gate = new TaskCompletionSource();
            var first = Task.Run(async () =>
            {
                await gate.Task;
                return await ReserveAsync(Guid.CreateVersion7(), Stay(a, roomType.Id, 0, 3, 1, "GUEST-A"));
            });
            var second = Task.Run(async () =>
            {
                await gate.Task;
                return await ReserveAsync(Guid.CreateVersion7(), Stay(b, roomType.Id, 2, 3, 1, "GUEST-B"));
            });
            gate.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, results.Count(r => r == ReserveOutcome.Held));
            Assert.Equal(1, results.Count(r => r == ReserveOutcome.Short));
            var left = await TestDatabase.RoomsLeftAsync(roomType.Id, 5);
            Assert.Equal(results[0] == ReserveOutcome.Held ? new[] { 0, 0, 0, 1, 1 } : new[] { 1, 1, 0, 0, 0 }, left);
            await using var check = TestDatabase.Rooms();
            Assert.Single(await check.Holds.ToListAsync());
        }
    }

    [Fact]
    public async Task StayRunningPastTheLastOpenNight_IsShortAndDecrementsNothing()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(totalRooms: 2, openNights: 3);

        var outcome = await ReserveAsync(Guid.CreateVersion7(), Stay(Guid.CreateVersion7(), roomType.Id, 2, 3, 1, "GUEST-1"));

        Assert.Equal(ReserveOutcome.Short, outcome);
        Assert.Equal(new[] { 2, 2, 2 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 3));
        await using var check = TestDatabase.Rooms();
        Assert.Empty(await check.Holds.ToListAsync());
    }

    [Fact]
    public async Task Release_GivesEveryNightBackExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(totalRooms: 2);
        var bookingId = Guid.CreateVersion7();
        await ReserveAsync(Guid.CreateVersion7(), Stay(bookingId, roomType.Id, 1, 3, 2, "GUEST-1"));
        Assert.Equal(new[] { 2, 0, 0, 0, 2, 2, 2 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 7));

        // Two different commands (poison compensation and a cancellation) both ask for the same rooms back.
        await using var d1 = TestDatabase.Rooms();
        await using var d2 = TestDatabase.Rooms();
        var first = await new RoomsHandler(d1).ReleaseAsync(Guid.CreateVersion7(), new ReleaseRooms(bookingId, "poison"));
        var second = await new RoomsHandler(d2).ReleaseAsync(Guid.CreateVersion7(), new ReleaseRooms(bookingId, "cancellation"));

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(new[] { 2, 2, 2, 2, 2, 2, 2 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 7));
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(totalRooms: 5);
        var messageId = Guid.CreateVersion7();
        var command = Stay(Guid.CreateVersion7(), roomType.Id, 0, 2, 2, "GUEST-1");

        var first = await ReserveAsync(messageId, command);
        var second = await ReserveAsync(messageId, command);

        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(new[] { 3, 3, 5, 5, 5, 5, 5 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 7));
    }
}
