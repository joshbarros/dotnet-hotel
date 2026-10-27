using Hotel.Shared;
using Hotel.Shared.Auth;
using Hotel.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hotel.Rooms;

public record CreateRoomTypeRequest(string? Code, string? Name, string? City, long TotalRooms, long RateCents, DateOnly? FirstNight, long? OpenNights);

public record RoomTypeDto(Guid Id, string Code, string Name, string City, int TotalRooms, long RateCents, DateOnly FirstNight, int OpenNights)
{
    public static RoomTypeDto From(RoomType r) => new(r.Id, r.Code, r.Name, r.City, r.TotalRooms, r.RateCents, r.FirstNight, r.OpenNights);
}

public record NightDto(DateOnly Night, int TotalRooms, int RoomsLeft);

/// <summary>What other modules may ask Rooms, over Rooms's own tables.</summary>
public sealed class RoomTypeCatalog(RoomsDbContext db) : IRoomTypeCatalog
{
    public async Task<RoomTypeInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.RoomTypes.AsNoTracking().Where(r => r.Code == code).Select(r => new RoomTypeInfo(r.Id, r.Code, r.RateCents)).FirstOrDefaultAsync(ct);

    public Task<int> OpenNightsAsync(Guid roomTypeId, DateOnly checkIn, int nights, CancellationToken ct)
    {
        var checkOut = checkIn.AddDays(nights);
        return db.RoomNights.CountAsync(n => n.RoomTypeId == roomTypeId && n.Night >= checkIn && n.Night < checkOut, ct);
    }
}

[ApiController]
[Route("api/room-types")]
[Authorize]
public class RoomTypesController(RoomsDbContext db) : ControllerBase
{
    public const int MaxTotalRooms = 500;
    public const long MaxRateCents = 1_000_000;
    public const int MaxOpenNights = 365;

    [HttpGet]
    public async Task<IEnumerable<RoomTypeDto>> List(CancellationToken ct) =>
        (await db.RoomTypes.AsNoTracking().OrderBy(r => r.City).ThenBy(r => r.Code).ToListAsync(ct)).Select(RoomTypeDto.From);

    [HttpGet("{code}")]
    public async Task<ActionResult<RoomTypeDto>> Get(string code, CancellationToken ct)
    {
        var roomType = await db.RoomTypes.AsNoTracking().FirstOrDefaultAsync(r => r.Code == code, ct);
        return roomType is null ? NotFound() : RoomTypeDto.From(roomType);
    }

    /// <summary>Rooms left on each night from a date. Defaults to the first open night and 14 nights.</summary>
    [HttpGet("{code}/availability")]
    public async Task<ActionResult<IEnumerable<NightDto>>> Availability(string code, [FromQuery] DateOnly? from, [FromQuery] int days = 14, CancellationToken ct = default)
    {
        var roomType = await db.RoomTypes.AsNoTracking().FirstOrDefaultAsync(r => r.Code == code, ct);
        if (roomType is null)
            return NotFound();
        days = Math.Clamp(days, 1, 60);
        var start = from ?? roomType.FirstNight;
        var end = start.AddDays(days);
        var nights = await db.RoomNights.AsNoTracking()
            .Where(n => n.RoomTypeId == roomType.Id && n.Night >= start && n.Night < end)
            .OrderBy(n => n.Night)
            .Select(n => new NightDto(n.Night, n.TotalRooms, n.RoomsLeft))
            .ToListAsync(ct);
        return nights;
    }

    /// <summary>Opens a room type for sale on a run of nights. Staff only. This is the only place RoomsLeft is initialised; after that only RoomsHandler writes it.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<RoomTypeDto>> Create(CreateRoomTypeRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.City))
            throw new DomainException(400, "Name and city are required.");
        if (request.TotalRooms < 1 || request.TotalRooms > MaxTotalRooms)
            throw new DomainException(400, $"Total rooms must be between 1 and {MaxTotalRooms}.");
        if (request.RateCents < 1 || request.RateCents > MaxRateCents)
            throw new DomainException(400, $"Rate must be between 1 and {MaxRateCents} cents per night.");
        var openNights = request.OpenNights ?? 30;
        if (openNights < 1 || openNights > MaxOpenNights)
            throw new DomainException(400, $"Open nights must be between 1 and {MaxOpenNights}.");
        if (await db.RoomTypes.AnyAsync(r => r.Code == code, ct))
            throw new DomainException(409, $"Room type '{code}' already exists.");

        var first = request.FirstNight ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var roomType = new RoomType
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            City = request.City.Trim(),
            TotalRooms = (int)request.TotalRooms,
            RateCents = request.RateCents,
            FirstNight = first,
            OpenNights = (int)openNights,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RoomTypes.Add(roomType);
        for (var i = 0; i < roomType.OpenNights; i++)
            db.RoomNights.Add(new RoomNight { RoomTypeId = roomType.Id, Night = first.AddDays(i), TotalRooms = roomType.TotalRooms, RoomsLeft = roomType.TotalRooms });
        await db.SaveChangesAsync(ct);
        return Created($"/api/room-types/{roomType.Code}", RoomTypeDto.From(roomType));
    }
}
