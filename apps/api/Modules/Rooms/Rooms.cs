namespace Hotel.Api.Modules.Rooms;

public class RoomType
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public int TotalRooms { get; set; }
    public long RateCents { get; set; }
    public DateOnly FirstNight { get; set; }
    public int OpenNights { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One row per room type per night. RoomsLeft is the scarce number; only RoomsHandler writes it.</summary>
public class RoomNight
{
    public Guid RoomTypeId { get; set; }
    public DateOnly Night { get; set; }
    public int TotalRooms { get; set; }
    public int RoomsLeft { get; set; }
}

public enum HoldStatus { Held, Released }

public class RoomHold
{
    public Guid BookingId { get; set; }
    public Guid RoomTypeId { get; set; }
    public DateOnly CheckIn { get; set; }
    public int Nights { get; set; }
    public int Rooms { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
