using Hotel.Api.Data;
using Hotel.Api.Modules.Bookings;
using Hotel.Api.Modules.Rooms;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Api.Tests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("HOTEL_TEST_DB")
        ?? "Host=localhost;Port=5476;Database=hotel_test;Username=hotel;Password=hotel";

    public static readonly DateOnly FirstNight = new(2030, 1, 1);

    private static readonly DbContextOptions<HotelDbContext> Options =
        new DbContextOptionsBuilder<HotelDbContext>().UseNpgsql(ConnectionString).Options;

    public static HotelDbContext Create() => new(Options);

    public static async Task<HotelDbContext> OpenAsync()
    {
        var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE \"InboxMessages\", \"OutboxMessages\", \"Vouchers\", \"RoomHolds\", \"Bookings\", \"RoomNights\", \"RoomTypes\" RESTART IDENTITY CASCADE;");
        return db;
    }

    /// <summary>A room type open for sale on <paramref name="openNights"/> nights starting at <see cref="FirstNight"/>.</summary>
    public static async Task<RoomType> AddRoomTypeAsync(HotelDbContext db, int totalRooms, int openNights = 7, long rateCents = 12_000)
    {
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

    public static async Task<Booking> AddBookingAsync(HotelDbContext db, RoomType roomType, string reference, int rooms, BookingStatus status, int nights = 2)
    {
        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
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

    public static async Task<int[]> RoomsLeftAsync(Guid roomTypeId, int nights) =>
        (await Create().RoomNights.AsNoTracking()
            .Where(n => n.RoomTypeId == roomTypeId)
            .OrderBy(n => n.Night)
            .Select(n => n.RoomsLeft)
            .ToListAsync()).Take(nights).ToArray();
}
