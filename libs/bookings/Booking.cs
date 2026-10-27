namespace Hotel.Bookings;

public enum BookingStatus { Requested, Held, Confirmed, Cancelled, Rejected }

public class Booking
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid RoomTypeId { get; set; }
    public string RoomTypeCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public DateOnly CheckIn { get; set; }
    public int Nights { get; set; }
    public int Rooms { get; set; }
    public long TotalCents { get; set; }
    public BookingStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? VoucherNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RoomsReleasedAt { get; set; }
}

public class BookingOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the cancellation race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateBookingRequest(string? RoomTypeCode, string? Reference, DateOnly? CheckIn, long Nights, long Rooms);

public record BookingDto(
    Guid Id, Guid OwnerId, string RoomTypeCode, string Reference, DateOnly CheckIn, DateOnly CheckOut, int Nights, int Rooms,
    long TotalCents, string Status, string? Reason, string? VoucherNumber, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? RoomsReleasedAt)
{
    public static BookingDto From(Booking b) => new(
        b.Id, b.OwnerId, b.RoomTypeCode, b.Reference, b.CheckIn, b.CheckIn.AddDays(b.Nights), b.Nights, b.Rooms,
        b.TotalCents, b.Status.ToString(), b.Reason, b.VoucherNumber, b.CreatedAt, b.UpdatedAt, b.RoomsReleasedAt);
}
