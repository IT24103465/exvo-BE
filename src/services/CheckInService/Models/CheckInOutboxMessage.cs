namespace Exvo.CheckInService.Models;

public class CheckInOutboxMessage
{
    public long Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
}
