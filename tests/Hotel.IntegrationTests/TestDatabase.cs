using Hotel.Bookings;
using Hotel.Rooms;
using Hotel.Vouchers;
using Hotel.Shared.Auth;
using Hotel.Shared.Contracts;
using Hotel.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Hotel.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("HOTEL_TEST_DB")
        ?? "Host=localhost;Port=5476;Database=hotel_test;Username=hotel;Password=hotel";

    public static readonly DateOnly FirstNight = new(2030, 1, 1);

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static BookingsDbContext Bookings() => new(Options<BookingsDbContext>(BookingsDbContext.SchemaName));
    public static RoomsDbContext Rooms() => new(Options<RoomsDbContext>(RoomsDbContext.SchemaName));
    public static VouchersDbContext Vouchers() => new(Options<VouchersDbContext>(VouchersDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var bookings = Bookings();
        await using var rooms = Rooms();
        await using var vouchers = Vouchers();
        await bookings.Database.MigrateAsync();
        await rooms.Database.MigrateAsync();
        await vouchers.Database.MigrateAsync();
        await bookings.Database.ExecuteSqlRawAsync(
            "TRUNCATE bookings.\"InboxMessages\", bookings.\"OutboxMessages\", bookings.\"Bookings\", " +
            "rooms.\"InboxMessages\", rooms.\"OutboxMessages\", rooms.\"RoomHolds\", rooms.\"RoomNights\", rooms.\"RoomTypes\", " +
            "vouchers.\"InboxMessages\", vouchers.\"OutboxMessages\", vouchers.\"Vouchers\" RESTART IDENTITY CASCADE;");
    }

    /// <summary>A room type open for sale on <paramref name="openNights"/> nights starting at <see cref="FirstNight"/>.</summary>
    public static async Task<RoomType> AddRoomTypeAsync(int totalRooms, int openNights = 7, long rateCents = 12_000)
    {
        await using var db = Rooms();
        var roomType = new RoomType
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Room",
            City = "Lisbon",
            TotalRooms = totalRooms,
            RateCents = rateCents,
            FirstNight = FirstNight,
            OpenNights = openNights,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RoomTypes.Add(roomType);
        for (var i = 0; i < openNights; i++)
            db.RoomNights.Add(new RoomNight { RoomTypeId = roomType.Id, Night = FirstNight.AddDays(i), TotalRooms = totalRooms, RoomsLeft = totalRooms });
        await db.SaveChangesAsync();
        return roomType;
    }

    /// <summary>The request the booking service receives. One place to change for a system whose booking has a different shape.</summary>
    public static CreateBookingRequest BookingRequest(string roomTypeCode, string reference, int nights) =>
        new(roomTypeCode, reference, FirstNight, nights, 1);

    /// <summary>What Rooms tells the rest of the saga after it holds a stay.</summary>
    public static RoomsHeld HeldMessage(Booking booking, RoomType roomType, Guid? ownerId = null) =>
        new(booking.Id, ownerId ?? booking.OwnerId, roomType.Id, booking.CheckIn, booking.Nights, booking.Rooms, booking.Reference);

    public static async Task<Booking> AddBookingAsync(RoomType roomType, string reference, int rooms, BookingStatus status, Guid? ownerId = null, int nights = 2)
    {
        await using var db = Bookings();
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            RoomTypeId = roomType.Id,
            RoomTypeCode = roomType.Code,
            Reference = reference,
            CheckIn = FirstNight,
            Nights = nights,
            Rooms = rooms,
            TotalCents = BookingRules.TotalCents(roomType.RateCents, nights, rooms),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    public static async Task<int[]> RoomsLeftAsync(Guid roomTypeId, int nights)
    {
        await using var db = Rooms();
        return (await db.RoomNights.AsNoTracking()
            .Where(n => n.RoomTypeId == roomTypeId)
            .OrderBy(n => n.Night)
            .Select(n => n.RoomsLeft)
            .ToListAsync()).Take(nights).ToArray();
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

/// <summary>A catalog over fixed room types. Every night counts as open, so the stay is bookable.</summary>
public sealed class FixedCatalog(params RoomType[] roomTypes) : IRoomTypeCatalog
{
    public Task<RoomTypeInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(roomTypes.Where(r => r.Code == code).Select(r => new RoomTypeInfo(r.Id, r.Code, r.RateCents)).FirstOrDefault());

    public Task<int> OpenNightsAsync(Guid roomTypeId, DateOnly checkIn, int nights, CancellationToken ct) => Task.FromResult(nights);
}
