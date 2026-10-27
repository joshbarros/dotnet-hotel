using System.Text.Json;

namespace Hotel.Shared.Messaging;

public record ReserveRooms(Guid BookingId, Guid OwnerId, Guid RoomTypeId, DateOnly CheckIn, int Nights, int Rooms, string Reference);
public record RoomsHeld(Guid BookingId, Guid OwnerId, Guid RoomTypeId, DateOnly CheckIn, int Nights, int Rooms, string Reference);
public record RoomsShort(Guid BookingId, string Reference, int RequestedRooms, int Nights, int FewestRoomsLeft);
public record ReleaseRooms(Guid BookingId, string Reason);
public record RoomsReleased(Guid BookingId, int Rooms, int Nights, string Reason);
public record VoucherIssued(Guid BookingId, string Number);
public record VoucherRejected(Guid BookingId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
