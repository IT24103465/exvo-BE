namespace Exvo.CheckInService.Models;

public enum ScannedTicketStatus
{
    CheckedIn,
    Rejected
}

public class ScannedTicket
{
    public int Id { get; set; }

    /// <summary>Unique identifier of the ticket (e.g. BookingItem Id or unique Ticket ID).</summary>
    public int TicketId { get; set; }

    /// <summary>ID of the event this ticket belongs to.</summary>
    public int EventId { get; set; }

    /// <summary>ID of the associated booking.</summary>
    public int BookingId { get; set; }

    /// <summary>ID of the attendee.</summary>
    public int AttendeeId { get; set; }

    /// <summary>Organizer or User ID who performed the scan.</summary>
    public int ScannedBy { get; set; }

    /// <summary>UTC timestamp when the ticket was scanned.</summary>
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Scan status: CheckedIn or Rejected.</summary>
    public ScannedTicketStatus Status { get; set; } = ScannedTicketStatus.CheckedIn;

    /// <summary>Hash or representation of the scanned QR code.</summary>
    public string QrCodeHash { get; set; } = string.Empty;

    /// <summary>Optional reason if rejected.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>Attendee name cached for quick display.</summary>
    public string? AttendeeName { get; set; }

    /// <summary>Ticket tier or type name.</summary>
    public string? TicketType { get; set; }

    /// <summary>Seat code (e.g. A-1 or GA).</summary>
    public string? SeatCode { get; set; }
}
