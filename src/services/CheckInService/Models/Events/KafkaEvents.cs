namespace Exvo.CheckInService.Models.Events;

public sealed record TicketCheckedInEvent(
    int TicketId,
    int EventId,
    int BookingId,
    int AttendeeId,
    int ScannedBy,
    DateTime ScannedAt
);

public sealed record TicketVerificationFailedEvent(
    string QrCode,
    int EventId,
    int ScannedBy,
    string Reason,
    DateTime AttemptedAt
);

public sealed record TicketCancelledEvent(
    int TicketId,
    int BookingId,
    int EventId,
    DateTime CancelledAt
);

public sealed record EventUpdatedEvent(
    int EventId,
    string? Title,
    DateTime? EventDate,
    string? Status,
    DateTime UpdatedAt
);
