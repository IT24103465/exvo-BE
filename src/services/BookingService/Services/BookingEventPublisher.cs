using System.Text.Json;
using Confluent.Kafka;

namespace Exvo.BookingService.Services;

public sealed record BookingConfirmedEvent(int BookingId, string BookingReference, int EventId, int AttendeeUserId, string AttendeeEmail, decimal TotalAmount, string Currency, DateTime ConfirmedAtUtc);

public sealed class BookingEventPublisher : IDisposable
{
    public const string Topic = "booking.confirmed.v1";
    private readonly IProducer<string, string>? producer;
    private readonly ILogger<BookingEventPublisher> logger;

    public BookingEventPublisher(IConfiguration configuration, ILogger<BookingEventPublisher> logger)
    {
        this.logger = logger;
        var brokers = configuration["KAFKA_BOOTSTRAP_SERVERS"];
        if (!string.IsNullOrWhiteSpace(brokers))
        {
            producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = brokers, Acks = Acks.All }).Build();
            logger.LogInformation("Booking Kafka publisher configured for {BootstrapServers}.", brokers);
        }
        else logger.LogWarning("KAFKA_BOOTSTRAP_SERVERS is unset; booking events will not be published.");
    }

    public async Task PublishAsync(BookingConfirmedEvent message, CancellationToken cancellationToken)
    {
        if (producer is null) return; // Kafka is optional for local runs without a broker.
        var result = await producer.ProduceAsync(Topic, new Message<string, string>
        {
            Key = message.BookingId.ToString(),
            Value = JsonSerializer.Serialize(message)
        }, cancellationToken);
        logger.LogInformation("Published {Topic} event for booking {BookingReference} to partition {Partition}, offset {Offset}.", Topic, message.BookingReference, result.Partition.Value, result.Offset.Value);
    }

    public void Dispose() => producer?.Dispose();
}
