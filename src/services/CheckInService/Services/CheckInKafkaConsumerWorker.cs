using System.Text.Json;
using Confluent.Kafka;
using Exvo.CheckInService.Data;
using Exvo.CheckInService.Models.Events;
using Microsoft.EntityFrameworkCore;

namespace Exvo.CheckInService.Services;

public class CheckInKafkaConsumerWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CheckInKafkaConsumerWorker> _logger;

    public CheckInKafkaConsumerWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<CheckInKafkaConsumerWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var brokers = _configuration["KAFKA_BOOTSTRAP_SERVERS"];
        if (string.IsNullOrWhiteSpace(brokers))
        {
            _logger.LogInformation("KAFKA_BOOTSTRAP_SERVERS is unset; CheckInKafkaConsumerWorker is disabled.");
            return Task.CompletedTask;
        }

        return Task.Run(() => ConsumeLoop(brokers, stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(string brokers, CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = brokers,
            GroupId = "checkin-service-consumer-group-v1",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnablePartitionEof = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(new[] { "ticket.cancelled", "event.updated", "booking.confirmed.v1" });
        _logger.LogInformation("Subscribed to ticket.cancelled, event.updated, and booking.confirmed.v1 at {Brokers}.", brokers);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumeResult = consumer.Consume(stoppingToken);
                if (consumeResult?.Message == null) continue;

                var topic = consumeResult.Topic;
                var value = consumeResult.Message.Value;

                _logger.LogInformation("Received message on topic {Topic} at offset {Offset}.", topic, consumeResult.Offset);

                using (var scope = _serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();

                    if (topic == "ticket.cancelled")
                    {
                        var cancelledEvent = JsonSerializer.Deserialize<TicketCancelledEvent>(value);
                        if (cancelledEvent != null)
                        {
                            // Audit ticket cancellation
                            db.AuditLogs.Add(new Models.CheckInAuditLog
                            {
                                Action = "TicketCancelledReceived",
                                TicketId = cancelledEvent.TicketId,
                                EventId = cancelledEvent.EventId,
                                UserId = 0,
                                Details = $"Kafka event received: BookingId={cancelledEvent.BookingId}",
                                TimestampUtc = DateTime.UtcNow
                            });
                            db.SaveChanges();
                        }
                    }
                    else if (topic == "event.updated")
                    {
                        var updatedEvent = JsonSerializer.Deserialize<EventUpdatedEvent>(value);
                        if (updatedEvent != null)
                        {
                            db.AuditLogs.Add(new Models.CheckInAuditLog
                            {
                                Action = "EventUpdatedReceived",
                                EventId = updatedEvent.EventId,
                                UserId = 0,
                                Details = $"Event updated: {updatedEvent.Title}",
                                TimestampUtc = DateTime.UtcNow
                            });
                            db.SaveChanges();
                        }
                    }
                    else if (topic == "booking.confirmed.v1")
                    {
                        var bookingEvent = JsonSerializer.Deserialize<BookingConfirmedEvent>(value);
                        if (bookingEvent != null)
                        {
                            db.AuditLogs.Add(new Models.CheckInAuditLog
                            {
                                Action = "BookingConfirmedReceived",
                                TicketId = null,
                                EventId = bookingEvent.EventId,
                                UserId = bookingEvent.AttendeeUserId,
                                Details = $"Booking confirmed received: Ref={bookingEvent.BookingReference}, Amount={bookingEvent.TotalAmount} {bookingEvent.Currency}",
                                TimestampUtc = DateTime.UtcNow
                            });
                            db.SaveChanges();
                        }
                    }
                }

                consumer.Commit(consumeResult);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Kafka consumer message.");
                // Retry backoff delay
                Thread.Sleep(1000);
            }
        }

        consumer.Close();
    }
}
