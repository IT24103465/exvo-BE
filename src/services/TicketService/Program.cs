using Confluent.Kafka;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<BookingConfirmedConsumer>();

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.Run();

sealed class BookingConfirmedConsumer(IConfiguration configuration, ILogger<BookingConfirmedConsumer> logger) : BackgroundService
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
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = brokers,
            GroupId = "ticket-service-v1",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
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

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
