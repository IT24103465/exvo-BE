using System.Text.Json;
using Confluent.Kafka;

namespace Exvo.TicketService;

public sealed class BookingConfirmedConsumer(
    IConfiguration configuration,
    ILogger<BookingConfirmedConsumer> logger,
    Func<ConsumerConfig, IConsumer<string, string>>? consumerFactory = null) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var brokers = configuration["KAFKA_BOOTSTRAP_SERVERS"];
        if (string.IsNullOrWhiteSpace(brokers))
        {
            logger.LogInformation("Kafka is not configured; ticket service event consumer is disabled.");
            return Task.CompletedTask;
        }
        return Task.Run(() => Consume(brokers, stoppingToken), stoppingToken);
    }

    private void Consume(string brokers, CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = brokers,
            GroupId = "ticket-service-v1",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        using var consumer = consumerFactory is null
            ? new ConsumerBuilder<string, string>(config).Build()
            : consumerFactory(config);
        consumer.Subscribe("booking.confirmed.v1");
        logger.LogInformation("Subscribed to booking.confirmed.v1 at {BootstrapServers}.", brokers);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var record = consumer.Consume(stoppingToken);
                var booking = JsonSerializer.Deserialize<BookingConfirmedEvent>(record.Message.Value);
                if (booking is null)
                {
                    logger.LogWarning("Skipping invalid booking.confirmed.v1 message at offset {Offset}.", record.Offset.Value);
                    consumer.Commit(record);
                    continue;
                }
                logger.LogInformation("TicketService received booking {BookingReference} for event {EventId}; ticket issuance can proceed.", booking.BookingReference, booking.EventId);
                consumer.Commit(record);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Failed processing booking.confirmed.v1 event."); }
        }
        consumer.Close();
    }

    private sealed record BookingConfirmedEvent(int BookingId, string BookingReference, int EventId, int AttendeeUserId, string AttendeeEmail, decimal TotalAmount, string Currency, DateTime ConfirmedAtUtc);
}
