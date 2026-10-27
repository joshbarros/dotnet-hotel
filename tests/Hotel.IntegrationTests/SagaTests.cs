using Hotel.Bookings;
using Hotel.Vouchers;
using Hotel.Shared;
using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hotel.IntegrationTests;

public class SagaTests
{
    private static BookingsHandler Handler(BookingsDbContext db) => new(db, Options.Create(new BookingOptions()));

    [Fact]
    public async Task PoisonReference_IsCommittedRejected_ThenCompensatingCommandIsQueued()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        var booking = await TestDatabase.AddBookingAsync(roomType, "GUEST-POISON-1", 2, BookingStatus.Held);
        var held = TestDatabase.HeldMessage(booking, roomType);

        await using var docs = TestDatabase.Vouchers();
        var ex = await Assert.ThrowsAsync<PoisonMessageException>(() =>
            new VouchersHandler(docs).HandleAsync(new Delivery(Guid.CreateVersion7(), Topology.HeldKey, Json.Serialize(held)), default));
        Assert.Contains("poison", ex.Message, StringComparison.OrdinalIgnoreCase);

        await using var check = TestDatabase.Vouchers();
        var doc = await check.Vouchers.SingleAsync(d => d.BookingId == booking.Id);
        Assert.Equal(VoucherStatus.Rejected, doc.Status); // committed before the dead-letter
        Assert.Equal(booking.OwnerId, doc.OwnerId);
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ReleaseKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.RejectedKey));
        Assert.Equal(0, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.IssuedKey));
    }

    [Fact]
    public async Task Cancellation_ThenFinalMark_LeavesBookingCancelledAndVoucherIssued()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        var owner = Guid.CreateVersion7();
        var booking = await TestDatabase.AddBookingAsync(roomType, "GUEST-1", 2, BookingStatus.Held, owner);

        // The voucher is issued; the final mark has not landed yet.
        await using (var docs = TestDatabase.Vouchers())
            await new VouchersHandler(docs).IssueAsync(Guid.CreateVersion7(), TestDatabase.HeldMessage(booking, roomType, owner));

        // The owner reverses first.
        await using (var bookings = TestDatabase.Bookings())
            await new BookingService(bookings, new FixedCatalog(roomType), new TestUser(owner)).CancelAsync(booking.Id, default);

        await using var late = TestDatabase.Bookings();
        var mark = await Handler(late).MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(booking.Id, "VC-TEST"));

        await using var check = TestDatabase.Bookings();
        await using var checkDocs = TestDatabase.Vouchers();
        Assert.Equal(MarkOutcome.Skipped, mark);
        Assert.Equal(BookingStatus.Cancelled, (await check.Bookings.SingleAsync()).Status);
        Assert.Equal(VoucherStatus.Issued, (await checkDocs.Vouchers.SingleAsync()).Status);
    }

    [Fact]
    public async Task FinalMark_RunsOnlyFromHeld()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        var held = await TestDatabase.AddBookingAsync(roomType, "GUEST-A", 1, BookingStatus.Held);
        var rejected = await TestDatabase.AddBookingAsync(roomType, "GUEST-B", 1, BookingStatus.Rejected);
        var requested = await TestDatabase.AddBookingAsync(roomType, "GUEST-C", 1, BookingStatus.Requested);

        await using (var a = TestDatabase.Bookings())
            Assert.Equal(MarkOutcome.Marked, await Handler(a).MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(held.Id, "VC-1")));
        await using (var b = TestDatabase.Bookings())
            Assert.Equal(MarkOutcome.Skipped, await Handler(b).MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(rejected.Id, "VC-2")));
        // Not Held yet: the message is retried later instead of being dropped.
        await using (var c = TestDatabase.Bookings())
            await Assert.ThrowsAsync<OutOfOrderException>(() => Handler(c).MarkConfirmedAsync(Guid.CreateVersion7(), new VoucherIssued(requested.Id, "VC-3")));

        await using var check = TestDatabase.Bookings();
        var statuses = (await check.Bookings.ToListAsync()).ToDictionary(b => b.Reference, b => b.Status);
        Assert.Equal(BookingStatus.Confirmed, statuses["GUEST-A"]);
        Assert.Equal(BookingStatus.Rejected, statuses["GUEST-B"]);
        Assert.Equal(BookingStatus.Requested, statuses["GUEST-C"]);
    }

    [Fact]
    public async Task Cancellation_OfARejectedBooking_Conflicts()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        var owner = Guid.CreateVersion7();
        var booking = await TestDatabase.AddBookingAsync(roomType, "GUEST-1", 1, BookingStatus.Rejected, owner);

        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(roomType), new TestUser(owner)).CancelAsync(booking.Id, default));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Cancellation_ByAnotherCustomer_IsNotFound_AndChangesNothing()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        var booking = await TestDatabase.AddBookingAsync(roomType, "GUEST-1", 1, BookingStatus.Held);

        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(roomType), new TestUser(Guid.NewGuid())).CancelAsync(booking.Id, default));

        Assert.Equal(404, ex.Status);
        await using var check = TestDatabase.Bookings();
        Assert.Equal(BookingStatus.Held, (await check.Bookings.SingleAsync()).Status);
        Assert.Equal(0, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Staff_CanCancelAnyonesBooking()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        var booking = await TestDatabase.AddBookingAsync(roomType, "GUEST-1", 1, BookingStatus.Held);

        await using var db = TestDatabase.Bookings();
        var cancelled = await new BookingService(db, new FixedCatalog(roomType), new TestUser(Guid.NewGuid(), staff: true)).CancelAsync(booking.Id, default);
        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Create_RecordsTheCallerAsTheOwner_AndQueuesTheReservation()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        var owner = Guid.CreateVersion7();

        await using var db = TestDatabase.Bookings();
        var booking = await new BookingService(db, new FixedCatalog(roomType), new TestUser(owner))
            .CreateAsync(TestDatabase.BookingRequest(roomType.Code, "GUEST-1", 2), default);

        await using var check = TestDatabase.Bookings();
        Assert.Equal(owner, (await check.Bookings.SingleAsync()).OwnerId);
        var message = await check.Outbox.SingleAsync();
        Assert.Equal(Topology.ReserveKey, message.RoutingKey);
        Assert.Equal(owner, Json.Parse<ReserveRooms>(message.Payload).OwnerId);
        Assert.Equal(BookingStatus.Requested, booking.Status);
    }

    [Fact]
    public async Task Create_WithoutASignedInUser_IsRefused()
    {
        await TestDatabase.OpenAsync();
        var roomType = await TestDatabase.AddRoomTypeAsync(5);
        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(roomType), new TestUser(null)).CreateAsync(TestDatabase.BookingRequest(roomType.Code, "GUEST-1", 1), default));
        Assert.Equal(401, ex.Status);
    }

    [Fact]
    public async Task Create_ForAnUnknownRoomType_IsNotFound()
    {
        await TestDatabase.OpenAsync();
        await using var db = TestDatabase.Bookings();
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            new BookingService(db, new FixedCatalog(), new TestUser(Guid.NewGuid())).CreateAsync(TestDatabase.BookingRequest("NOPE", "GUEST-1", 1), default));
        Assert.Equal(404, ex.Status);
    }
}
