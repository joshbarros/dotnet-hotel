using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hotel.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Hotel.IntegrationTests;

/// <summary>The real host, the real Postgres, the real auth pipeline. Only the brokers are off: nothing here needs Kafka or RabbitMQ.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "integration-test-signing-key-0123456789abcdef";
    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin-Passw0rd1";

    /// <summary>Cookies are sent by hand so each test controls exactly which refresh token it presents.</summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Hotel", TestDatabase.ConnectionString);
        builder.UseSetting("Messaging:Enabled", "false");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Identity:SeedAdmin:Email", AdminEmail);
        builder.UseSetting("Identity:SeedAdmin:Password", AdminPassword);
        builder.UseSetting("RateLimit:AuthPerMinute", "100000");
        builder.UseSetting("Docs:Public", "true");
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;

public sealed record SignedIn(string AccessToken, string RefreshCookie, Guid UserId, string Email, string[] Roles);

public static class ApiHelpers
{
    public static Dictionary<string, object?> Also(this Dictionary<string, object?> body, string key, object? value)
    {
        body[key] = value;
        return body;
    }

    public const string Password = "Passw0rd-Test1";

    public static HttpClient ClientFor(this ApiFactory factory, string? accessToken = null)
    {
        var client = factory.NewClient();
        if (accessToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@test.local";
    /// <summary>The body of POST /api/room-types. One place, so a system with a different shape changes one line.</summary>
    public static object RoomTypeBody(string code, int totalRooms) =>
        new { code, name = "Harbor Test", city = "Lisbon", rateCents = 5_000, totalRooms };

    /// <summary>The body of POST /api/bookings. Add fields to the dictionary; a system with a different shape changes one place.</summary>
    public static Dictionary<string, object?> BookingBody(string roomTypeCode, string reference, int nights = 1) =>
        new() { ["roomTypeCode"] = roomTypeCode, ["reference"] = reference, ["checkIn"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd"), ["nights"] = nights, ["rooms"] = 1 };

    public static string NewCode() => $"S{Guid.NewGuid():N}"[..14];

    public static async Task<SignedIn> RegisterAsync(this ApiFactory factory, string? email = null)
    {
        email ??= NewEmail();
        var response = await factory.NewClient().PostAsJsonAsync("/api/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadSessionAsync(response);
    }

    public static async Task<SignedIn> LoginAsync(this ApiFactory factory, string email, string password)
    {
        var response = await factory.NewClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadSessionAsync(response);
    }

    public static Task<SignedIn> AdminAsync(this ApiFactory factory) => factory.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);

    public static async Task<SignedIn> ReadSessionAsync(HttpResponseMessage response)
    {
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>());
        var user = json.GetProperty("user");
        return new SignedIn(
            json.GetProperty("accessToken").GetString()!,
            RefreshCookieFrom(response),
            user.GetProperty("id").GetGuid(),
            user.GetProperty("email").GetString()!,
            user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }

    public static string RefreshCookieFrom(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("hotel_refresh="));
        return setCookie["hotel_refresh=".Length..].Split(';')[0];
    }

    public static HttpRequestMessage RefreshRequest(string cookie, bool withHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"hotel_refresh={cookie}");
        if (withHeader)
            request.Headers.Add("X-Requested-With", "hotel-web");
        return request;
    }

    public static async Task<string> CreateRoomTypeAsync(this ApiFactory factory, SignedIn admin, int totalRooms = 10)
    {
        var code = NewCode();
        var response = await factory.ClientFor(admin.AccessToken).PostAsJsonAsync("/api/room-types", RoomTypeBody(code, totalRooms));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return code;
    }

    public static async Task<Guid> BookAsync(this ApiFactory factory, SignedIn who, string roomTypeCode, string? reference = null, int nights = 1)
    {
        var response = await factory.ClientFor(who.AccessToken).PostAsJsonAsync("/api/bookings",
            BookingBody(roomTypeCode, reference ?? $"GUEST-{Guid.NewGuid():N}"[..12], nights));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>An access token signed with the real key but confirmed in 2020, so it is long expired.</summary>
    public static string ExpiredToken(Guid userId)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return new TokenService(Options.Create(new JwtOptions { Key = ApiFactory.JwtKey }), clock)
            .CreateAccessToken(userId, "old@test.local", ["Customer"]).Value;
    }
}
