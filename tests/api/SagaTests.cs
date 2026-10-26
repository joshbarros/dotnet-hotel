using Hotel.Api.Messaging;
using Hotel.Api.Modules.Bookings;
using Hotel.Api.Modules.Rooms;
using Hotel.Api.Modules.Vouchers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hotel.Api.Tests;

public class SagaTests
{
    private static BookingsHandler Bookings() =>
        new(TestDatabase.Create(), Options.Create(new BookingOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, 5);
        var booking = await TestDatabase.AddBookingAsync(db, roomType, "GUEST-POISON-1", 2, BookingStatus.Held);
        var held = new RoomsHeld(booking.Id, roomType.Id, booking.CheckIn, booking.Nights, booking.Rooms, booking.Reference);

        var handler = new VouchersHandler(TestDatabase.Create());
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            handler.HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        var check = TestDatabase.Create();
        var doc = await check.Vouchers.SingleAsync(d => d.BookingId == booking.Id);
        Assert.Equal(VoucherStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesBookingCancelledAndVoucherIssued()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, 5);
        var booking = await TestDatabase.AddBookingAsync(db, roomType, "GUEST-1", 2, BookingStatus.Held);

        // The voucher is issued; the final mark has not landed yet.
        var number = "VC-TEST";
        await new VouchersHandler(TestDatabase.Create()).IssueAsync(
            Guid.CreateVersion7(), new RoomsHeld(booking.Id, roomType.Id, booking.CheckIn, booking.Nights, booking.Rooms, booking.Reference));

        // The cancellation gets there first.
        await new BookingService(TestDatabase.Create()).CancelAsync(booking.Id, default);

        var mark = await Bookings().MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(booking.Id, number));

        var check = TestDatabase.Create();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(BookingStatus.Cancelled, (await check.Bookings.SingleAsync()).Status);
        Assert.Equal(VoucherStatus.Issued, (await check.Vouchers.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, 5);
        var held = await TestDatabase.AddBookingAsync(db, roomType, "GUEST-A", 1, BookingStatus.Held);
        var rejected = await TestDatabase.AddBookingAsync(db, roomType, "GUEST-B", 1, BookingStatus.Rejected);
        var requested = await TestDatabase.AddBookingAsync(db, roomType, "GUEST-C", 1, BookingStatus.Requested);

        Assert.Equal(MarkOutcome.Marked, await Bookings().MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(held.Id, "VC-1")));
        Assert.Equal(MarkOutcome.Skipped, await Bookings().MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(rejected.Id, "VC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await Assert.ThrowsAsync<OutOfOrderException>(() =>
            Bookings().MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(requested.Id, "VC-3")));

        var statuses = (await TestDatabase.Create().Bookings.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(BookingStatus.Confirmed, statuses["GUEST-A"]);
        Assert.Equal(BookingStatus.Rejected, statuses["GUEST-B"]);
        Assert.Equal(BookingStatus.Requested, statuses["GUEST-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedBooking_Conflicts()
    {
        var db = await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(db, 5);
        var booking = await TestDatabase.AddBookingAsync(db, roomType, "GUEST-1", 1, BookingStatus.Rejected);

        var ex = await Assert.ThrowsAsync<DomainException>(() => new BookingService(TestDatabase.Create()).CancelAsync(booking.Id, default));
        Assert.Equal(409, ex.Status);
    }
}
