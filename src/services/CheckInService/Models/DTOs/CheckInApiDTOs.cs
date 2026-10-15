namespace Exvo.CheckInService.Models.DTOs;

public record VerifyTicketRequest(
    string QrCode,
    int EventId
);

public static class TicketValidationStatus
{
    public const string Valid = "VALID";
    public const string Invalid = "INVALID";
    public const string WrongEvent = "WRONG_EVENT";
    public const string AlreadyUsed = "ALREADY_USED";
    public const string NotEligible = "NOT_ELIGIBLE";
    public const string Cancelled = "CANCELLED";
    public const string Expired = "EXPIRED";
}

public record ValidateTicketRequest(
    string? Code = null,
    int? EventId = null,
    string? TicketCode = null
)
{
    public string EffectiveCode => !string.IsNullOrWhiteSpace(TicketCode) ? TicketCode.Trim() : (Code?.Trim() ?? string.Empty);
}

public record ValidateTicketResponse(
    bool IsValid,
    string Status, // VALID, INVALID, WRONG_EVENT, ALREADY_USED, NOT_ELIGIBLE
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
