namespace Exvo.CheckInService.Models;

public class CheckInAuditLog
{
    public long Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public int? TicketId { get; set; }
    public int EventId { get; set; }
    public int UserId { get; set; }
    public string Details { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
