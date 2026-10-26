using System.Collections.Concurrent;
using Hotel.Api.Messaging;
using Hotel.Api.Modules.Rooms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hotel.Api.Tests;

public class RoomsConcurrencyTests
{
    private static ReserveRooms Stay(Guid bookingId, Guid roomTypeId, int fromNight, int nights, int rooms, string reference) =>
        new(bookingId, roomTypeId, TestDatabase.FirstNight.AddDays(fromNight), nights, rooms, reference);

    [Fact]
    public async Task LastRoom_TwoConcurrentStays_OneHeldOneShortNoHoldForTheLoser()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, totalRooms: 1);

        var outcomes = new ConcurrentBag<(Guid Booking, ReserveOutcome Outcome)>();
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            var bookingId = Guid.CreateVersion7();
            var outcome = await new RoomsHandler(TestDatabase.Create())
                .ReserveAsync(Guid.CreateVersion7(), Stay(bookingId, roomType.Id, 0, 3, 1, $"GUEST-{n}"));
            outcomes.Add((bookingId, outcome));
        })).ToArray();
        gate.SetResult();
        await Task.WhenAll(tasks);

        var check = TestDatabase.Create();
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
            var db = await TestDatabase.OpenAsync();
            var roomType = await TestDatabase.AddRoomTypeAsync(db, totalRooms: 1, openNights: 5);
            var a = Guid.CreateVersion7();
            var b = Guid.CreateVersion7();

            var gate = new TaskCompletionSource();
            var first = Task.Run(async () =>
            {
                await gate.Task;
                return await new RoomsHandler(TestDatabase.Create()).ReserveAsync(Guid.CreateVersion7(), Stay(a, roomType.Id, 0, 3, 1, "GUEST-A"));
            });
            var second = Task.Run(async () =>
            {
                await gate.Task;
                return await new RoomsHandler(TestDatabase.Create()).ReserveAsync(Guid.CreateVersion7(), Stay(b, roomType.Id, 2, 3, 1, "GUEST-B"));
            });
            gate.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, results.Count(r => r == ReserveOutcome.Held));
            Assert.Equal(1, results.Count(r => r == ReserveOutcome.Short));
            var left = await TestDatabase.RoomsLeftAsync(roomType.Id, 5);
            Assert.Equal(results[0] == ReserveOutcome.Held ? new[] { 0, 0, 0, 1, 1 } : new[] { 1, 1, 0, 0, 0 }, left);
            Assert.Single(await TestDatabase.Create().Holds.ToListAsync());
        }
    }

    [Fact]
    public async Task StayRunningPastTheLastOpenNight_IsShortAndDecrementsNothing()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, totalRooms: 2, openNights: 3);

        var outcome = await new RoomsHandler(TestDatabase.Create())
            .ReserveAsync(Guid.CreateVersion7(), Stay(Guid.CreateVersion7(), roomType.Id, 2, 3, 1, "GUEST-1"));

        Assert.Equal(ReserveOutcome.Short, outcome);
        Assert.Equal(new[] { 2, 2, 2 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 3));
        Assert.Empty(await TestDatabase.Create().Holds.ToListAsync());
    }

    [Fact]
    public async Task Release_GivesEveryNightBackExactlyOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, totalRooms: 2);
        var bookingId = Guid.CreateVersion7();
        await new RoomsHandler(TestDatabase.Create())
            .ReserveAsync(Guid.CreateVersion7(), Stay(bookingId, roomType.Id, 1, 3, 2, "GUEST-1"));
        Assert.Equal(new[] { 2, 0, 0, 0, 2, 2, 2 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 7));

        // Two different commands (poison compensation and a cancellation) both ask for the same rooms back.
        var first = await new RoomsHandler(TestDatabase.Create()).ReleaseAsync(Guid.CreateVersion7(), new ReleaseRooms(bookingId, "poison"));
        var second = await new RoomsHandler(TestDatabase.Create()).ReleaseAsync(Guid.CreateVersion7(), new ReleaseRooms(bookingId, "cancellation"));

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(new[] { 2, 2, 2, 2, 2, 2, 2 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 7));
    }

    [Fact]
    public async Task SameMessageDeliveredTwice_IsClaimedOnce()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, totalRooms: 5);
        var messageId = Guid.CreateVersion7();
        var command = Stay(Guid.CreateVersion7(), roomType.Id, 0, 2, 2, "GUEST-1");

        var first = await new RoomsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);
        var second = await new RoomsHandler(TestDatabase.Create()).ReserveAsync(messageId, command);

        Assert.Equal(ReserveOutcome.Held, first);
        Assert.Equal(ReserveOutcome.Duplicate, second);
        Assert.Equal(new[] { 3, 3, 5, 5, 5, 5, 5 }, await TestDatabase.RoomsLeftAsync(roomType.Id, 7));
    }
}
