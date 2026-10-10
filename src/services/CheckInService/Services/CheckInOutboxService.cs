using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exvo.CheckInService.Data;
using Exvo.CheckInService.Models;
using Exvo.CheckInService.Models.Events;

namespace Exvo.CheckInService.Services;

public interface ICheckInOutboxService
{
    Task QueueEventAsync<T>(string topic, string partitionKey, T payload, CancellationToken cancellationToken = default) where T : class;
    string ComputeSha256Hash(string rawData);
}

public class CheckInOutboxService : ICheckInOutboxService
{
    private readonly CheckInDbContext _db;

    public CheckInOutboxService(CheckInDbContext db)
    {
        _db = db;
    }

    public async Task QueueEventAsync<T>(string topic, string partitionKey, T payload, CancellationToken cancellationToken = default) where T : class
    {
        var json = JsonSerializer.Serialize(payload);
        var outboxMessage = new CheckInOutboxMessage
        {
            Topic = topic,
            PartitionKey = partitionKey,
            Payload = json,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.OutboxMessages.Add(outboxMessage);
        await Task.CompletedTask;
    }

    public string ComputeSha256Hash(string rawData)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
