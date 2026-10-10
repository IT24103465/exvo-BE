namespace Exvo.CheckInService.Models.DTOs;

public record VerifyTicketRequest(
    string QrCode,
    int EventId
);

public record VerifyTicketResponse(
    bool IsValid,
    string Status, // "Valid", "AlreadyCheckedIn", "Cancelled", "InvalidEvent", "NotFound"
    string Message,
    int? TicketId,
    int? EventId,
    int? BookingId,
    int? AttendeeId,
    string? AttendeeName,
    string? TicketType,
    string? SeatCode,
    DateTime? ScannedAt
);

public record ScanTicketRequest(
    string QrCode,
    int EventId
);

public record ScanTicketResponse(
    bool Success,
    string Status, // "CheckedIn", "Duplicate", "Rejected", "Invalid"
    string Message,
    ScannedTicketDto? ScannedTicket
);

public record ScannedTicketDto(
    int Id,
    int TicketId,
    int EventId,
    int BookingId,
    int AttendeeId,
    int ScannedBy,
    DateTime ScannedAt,
    string Status,
    string? AttendeeName,
    string? TicketType,
    string? SeatCode
);

public record AttendanceStatsResponse(
    int EventId,
    int TotalCheckedIn,
    int TotalRejected,
    int TotalScanned,
    DateTime LastUpdatedUtc
);
