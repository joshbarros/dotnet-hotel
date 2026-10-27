using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hotel.Bookings;
using Hotel.Vouchers;
using Xunit;

namespace Hotel.IntegrationTests;

[Collection("api")]
public class AuthorizationApiTests(ApiFactory factory)
{
    [Theory]
    [InlineData("/api/room-types")]
    [InlineData("/api/bookings")]
    [InlineData("/api/vouchers/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/bookings/11111111-1111-1111-1111-111111111111/events")]
    public async Task EverythingButHealthAndAuth_RequiresASignedInUser(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.NewClient().GetAsync(path)).StatusCode);

    [Fact]
    public async Task WritesRequireASignedInUser()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/room-types", new { code = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/bookings", new { roomTypeCode = "X" })).StatusCode);
    }

    [Fact]
    public async Task Health_IsOpen_AndReadinessChecksTheDatabases()
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CannotOpenARoomType_ButAnAdministratorCan()
    {
        var customer = await factory.RegisterAsync();
        var admin = await factory.AdminAsync();
        var body = ApiHelpers.RoomTypeBody(ApiHelpers.NewCode(), 10);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(customer.AccessToken).PostAsJsonAsync("/api/room-types", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/room-types", body)).StatusCode);
    }

    [Fact]
    public async Task ACustomer_CanReadTheCatalog()
    {
        var admin = await factory.AdminAsync();
        var code = await factory.CreateRoomTypeAsync(admin);
        var customer = await factory.RegisterAsync();

        var roomType = await factory.ClientFor(customer.AccessToken).GetAsync($"/api/room-types/{code}");
        Assert.Equal(HttpStatusCode.OK, roomType.StatusCode);
    }

    [Fact]
    public async Task ABooking_BelongsToItsOwner()
    {
        var admin = await factory.AdminAsync();
        var roomType = await factory.CreateRoomTypeAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, roomType);

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/bookings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/bookings/{id}")).StatusCode);
        // Another customer learns nothing, not even that the id exists.
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/bookings/{id}")).StatusCode);
    }

    [Fact]
    public async Task AnotherCustomer_CannotCancelSomeoneElsesBooking()
    {
        var admin = await factory.AdminAsync();
        var roomType = await factory.CreateRoomTypeAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, roomType);

        var attempt = await factory.ClientFor(bruno.AccessToken).PostAsync($"/api/bookings/{id}/cancellation", null);
        Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
    }

    [Fact]
    public async Task TheList_ShowsACustomerOnlyTheirOwnBookings_AndStaffEverything()
    {
        var admin = await factory.AdminAsync();
        var roomType = await factory.CreateRoomTypeAsync(admin);
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var anas = await factory.BookAsync(ana, roomType);
        var brunos = await factory.BookAsync(bruno, roomType);

        var seenByAna = await factory.ClientFor(ana.AccessToken).GetFromJsonAsync<JsonElement>($"/api/bookings?roomTypeCode={roomType}");
        var seenByStaff = await factory.ClientFor(admin.AccessToken).GetFromJsonAsync<JsonElement>($"/api/bookings?roomTypeCode={roomType}");

        Assert.Equal([anas], seenByAna.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
        Assert.Equivalent(new[] { anas, brunos }, seenByStaff.EnumerateArray().Select(b => b.GetProperty("id").GetGuid()).ToArray());
    }

    [Fact]
    public async Task TheBookingOwnerIsTheSignedInUser_NotWhatTheBodySays()
    {
        var admin = await factory.AdminAsync();
        var roomType = await factory.CreateRoomTypeAsync(admin);
        var ana = await factory.RegisterAsync();

        var response = await factory.ClientFor(ana.AccessToken).PostAsJsonAsync("/api/bookings",
            ApiHelpers.BookingBody(roomType, "GUEST-OWNER-1", 1).Also("ownerId", Guid.NewGuid()));

        var booking = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ana.UserId, booking.GetProperty("ownerId").GetGuid());
    }

    [Fact]
    public async Task TheEventLog_IsForStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var roomType = await factory.CreateRoomTypeAsync(admin);
        var ana = await factory.RegisterAsync();
        var id = await factory.BookAsync(ana, roomType);

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/bookings/{id}/events")).StatusCode);
    }

    [Fact]
    public async Task AVoucher_IsVisibleToItsOwnerAndStaffOnly()
    {
        var admin = await factory.AdminAsync();
        var ana = await factory.RegisterAsync();
        var bruno = await factory.RegisterAsync();
        var bookingId = Guid.CreateVersion7();
        await using (var db = TestDatabase.Vouchers())
        {
            db.Vouchers.Add(new Voucher { BookingId = bookingId, OwnerId = ana.UserId, Number = "VC-X", Status = VoucherStatus.Issued, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(ana.AccessToken).GetAsync($"/api/vouchers/{bookingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientFor(admin.AccessToken).GetAsync($"/api/vouchers/{bookingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.ClientFor(bruno.AccessToken).GetAsync($"/api/vouchers/{bookingId}")).StatusCode);
    }

    [Fact]
    public async Task Validation_StillAppliesToSignedInUsers()
    {
        var admin = await factory.AdminAsync();
        var roomType = await factory.CreateRoomTypeAsync(admin);
        var ana = await factory.RegisterAsync();
        var client = factory.ClientFor(ana.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/bookings", ApiHelpers.BookingBody(roomType, "", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/bookings", ApiHelpers.BookingBody(roomType, "GUEST", BookingRules.MaxNights + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/bookings", ApiHelpers.BookingBody("NOPE", "GUEST", 1))).StatusCode);
    }

    [Fact]
    public async Task EveryResponse_CarriesSecurityHeaders_AndEchoesTheCorrelationId()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/room-types");
        request.Headers.Add("X-Correlation-Id", "trace-abc-123");

        var response = await factory.NewClient().SendAsync(request);

        Assert.Equal("trace-abc-123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task ProblemResponses_AreProblemJson_WithTheCorrelationId()
    {
        var admin = await factory.AdminAsync();
        var response = await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/room-types", new { code = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("correlationId", out _));
    }
}
