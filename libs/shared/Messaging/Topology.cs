using RabbitMQ.Client;

namespace Hotel.Shared.Messaging;

public static class Topology
{
    public const string Exchange = "hotel.work";
    public const string DeadLetterExchange = "hotel.dlx";
    public const string RequeueExchange = "hotel.requeue";
    public const string KafkaTopic = "hotel.events";

    public const int RetryDelayMs = 2000;
    public const int MaxAttempts = 3;

    public const string ReserveKey = "rooms.reserve";
    public const string ReleaseKey = "rooms.release";
    public const string HeldKey = "rooms.held";
    public const string ShortKey = "rooms.short";
    public const string ReleasedKey = "rooms.released";
    public const string IssuedKey = "voucher.issued";
    public const string RejectedKey = "voucher.rejected";

    public const string RoomsQueue = "rooms.commands";
    public const string VouchersQueue = "vouchers.work";
    public const string BookingsQueue = "bookings.events";

    public static async Task DeclareAsync(IChannel channel, string queue, string[] routingKeys, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(RequeueExchange, ExchangeType.Direct, durable: true, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync($"{queue}.dlq", DeadLetterExchange, queue, cancellationToken: ct);

        await channel.QueueDeclareAsync($"{queue}.retry", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = RetryDelayMs,
                ["x-dead-letter-exchange"] = RequeueExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue,
            }, cancellationToken: ct);
        await channel.QueueBindAsync(queue, RequeueExchange, queue, cancellationToken: ct);
        foreach (var key in routingKeys)
            await channel.QueueBindAsync(queue, Exchange, key, cancellationToken: ct);
    }
}
