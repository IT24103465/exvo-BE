using Confluent.Kafka;
using Exvo.TicketService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Exvo.TicketService.Tests;

public class BookingConfirmedConsumerTests
{
    private static IConfiguration Config(string? brokers) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["KAFKA_BOOTSTRAP_SERVERS"] = brokers }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Missing_brokers_disable_consumer(string? brokers)
    {
        var logger = new RecordingLogger();
        using var worker = new BookingConfirmedConsumer(Config(brokers), logger, _ => throw new Exception("Must not connect"));
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(logger.Messages, entry => entry.Level == LogLevel.Information && entry.Message.Contains("disabled"));
    }

    [Theory]
    [InlineData("{\"BookingReference\":\"EXVO-123\",\"EventId\":9}", false)]
    [InlineData("null", false)]
    [InlineData("malformed-json", true)]
    public async Task Consumer_subscribes_handles_messages_commits_success_and_closes(string payload, bool invalid)
    {
        using var cancellation = new CancellationTokenSource();
        var record = new ConsumeResult<string, string>
        {
            Topic = "booking.confirmed.v1",
            Partition = 0,
            Offset = 7,
            Message = new Message<string, string> { Key = "123", Value = payload }
        };
        var consumer = new Mock<IConsumer<string, string>>(MockBehavior.Strict);
        consumer.Setup(c => c.Subscribe("booking.confirmed.v1"));
        consumer.Setup(c => c.Commit(record));
        consumer.Setup(c => c.Close());
        consumer.Setup(c => c.Dispose());
        var reads = 0;
        consumer.Setup(c => c.Consume(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (reads++ == 0) return record;
            cancellation.Cancel();
            throw new OperationCanceledException();
        });
        ConsumerConfig? captured = null;
        var logger = new RecordingLogger();
        using var worker = new BookingConfirmedConsumer(Config("test-broker:9092"), logger, config =>
        {
            captured = config;
            return consumer.Object;
        });
        await worker.StartAsync(cancellation.Token);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(captured);
        Assert.Equal("test-broker:9092", captured.BootstrapServers);
        Assert.Equal("ticket-service-v1", captured.GroupId);
        Assert.Equal(AutoOffsetReset.Earliest, captured.AutoOffsetReset);
        Assert.False(captured.EnableAutoCommit);
        consumer.Verify(c => c.Subscribe("booking.confirmed.v1"), Times.Once);
        consumer.Verify(c => c.Commit(record), invalid ? Times.Never() : Times.Once());
        consumer.Verify(c => c.Close(), Times.Once);
        consumer.Verify(c => c.Dispose(), Times.Once);
        Assert.Equal(2, reads);
        Assert.Contains(logger.Messages, e => e.Level == LogLevel.Information &&
            e.Message == "Subscribed to booking.confirmed.v1 at test-broker:9092.");
        if (invalid) Assert.Contains(logger.Messages, e => e.Level == LogLevel.Error &&
            e.Error is System.Text.Json.JsonException && e.Message == "Failed processing booking.confirmed.v1 event.");
        else if (payload == "null") Assert.Contains(logger.Messages, e => e.Level == LogLevel.Warning && e.Message.Contains("7"));
        else Assert.Contains(logger.Messages, e => e.Level == LogLevel.Information && e.Message.Contains("EXVO-123") && e.Message.Contains("9"));
    }

    private sealed class RecordingLogger : ILogger<BookingConfirmedConsumer>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((logLevel, formatter(state, exception), exception));
    }
}
