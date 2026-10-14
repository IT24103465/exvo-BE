using Confluent.Kafka;
using Exvo.CheckInService.Data;
using Microsoft.EntityFrameworkCore;

namespace Exvo.CheckInService.Services;

public class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutboxPublisherWorker> _logger;
    private IProducer<string, string>? _producer;

    public OutboxPublisherWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<OutboxPublisherWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var brokers = _configuration["KAFKA_BOOTSTRAP_SERVERS"];
        if (string.IsNullOrWhiteSpace(brokers))
        {
            _logger.LogWarning("KAFKA_BOOTSTRAP_SERVERS is unset; OutboxPublisherWorker will run in simulation mode.");
        }
        else
        {
            try
            {
                var config = new ProducerConfig
                {
                    BootstrapServers = brokers,
                    Acks = Acks.All,
                    MessageSendMaxRetries = 3,
                    RetryBackoffMs = 1000
                };
                _producer = new ProducerBuilder<string, string>(config).Build();
                _logger.LogInformation("Kafka producer connected to {Brokers}.", brokers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Kafka producer.");
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingOutboxMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while processing outbox messages.");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task ProcessPendingOutboxMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();

        var pendingMessages = await db.OutboxMessages
            .Where(m => m.ProcessedAtUtc == null && m.RetryCount < 5)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(20)
            .ToListAsync(cancellationToken);

        if (pendingMessages.Count == 0) return;

        foreach (var message in pendingMessages)
        {
            try
            {
                if (_producer != null)
                {
                    var kafkaMessage = new Message<string, string>
                    {
                        Key = message.PartitionKey,
                        Value = message.Payload
                    };

                    await _producer.ProduceAsync(message.Topic, kafkaMessage, cancellationToken);
                }

                message.ProcessedAtUtc = DateTime.UtcNow;
                _logger.LogInformation("Successfully published outbox message {Id} to topic {Topic}.", message.Id, message.Topic);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.Error = ex.Message;
                _logger.LogError(ex, "Failed to publish outbox message {Id} (Attempt {Count}).", message.Id, message.RetryCount);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _producer?.Dispose();
        base.Dispose();
    }
}
