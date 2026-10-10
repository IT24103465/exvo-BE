namespace Exvo.CheckInService.Models;

/// <summary>
/// Records a successful ticket scan/check-in for an event.
/// </summary>
public class CheckInRecord
{
    public int Id { get; set; }

    /// <summary>The ticket code scanned (format: {BookingRef}-{ItemId}).</summary>
    public string TicketCode { get; set; } = string.Empty;

    /// <summary>The event this ticket belongs to.</summary>
    public int EventId { get; set; }

    /// <summary>The coordinator who performed the scan.</summary>
    public int ScannedByCoordinatorId { get; set; }

    /// <summary>UTC timestamp of first scan.</summary>
    public DateTime CheckedInAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Attendee name from booking (cached for display).</summary>
    public string? AttendeeName { get; set; }

    /// <summary>Ticket tier/type name.</summary>
    public string? TicketType { get; set; }

    /// <summary>Seat code (e.g. A-1 or GA).</summary>
    public string? SeatCode { get; set; }
}
