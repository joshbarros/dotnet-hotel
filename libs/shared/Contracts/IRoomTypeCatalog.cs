namespace Hotel.Shared.Contracts;

public record RoomTypeInfo(Guid Id, string Code, long RateCents);

/// <summary>
/// What Bookings may ask Rooms, without referencing it. Rooms implements it over its own tables;
/// Bookings never reads another module's schema.
/// </summary>
public interface IRoomTypeCatalog
{
    Task<RoomTypeInfo?> FindAsync(string code, CancellationToken ct);

    /// <summary>How many nights of a stay are open for sale. A stay is bookable only when every night is.</summary>
    Task<int> OpenNightsAsync(Guid roomTypeId, DateOnly checkIn, int nights, CancellationToken ct);
}
