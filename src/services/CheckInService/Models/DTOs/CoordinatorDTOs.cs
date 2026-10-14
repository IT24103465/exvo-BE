namespace Exvo.CheckInService.Models.DTOs;

/// <summary>Minimal event info returned to coordinators.</summary>
public record AssignedEventDto(
    int Id,
    string Title,
    DateTime EventDate,
    string? Venue,
    string? Location,
    bool IsHidden,
    string ScannerCategory = ScannerEventCategory.Upcoming, // TODAY | UPCOMING | PAST
    bool CanLaunchScanner = false
);

/// <summary>Scanner portal categories based on the event's local calendar day.</summary>
public static class ScannerEventCategory
{
    public const string Today = "TODAY";
    public const string Upcoming = "UPCOMING";
    public const string Past = "PAST";
}

/// <summary>Scanner access check result for a specific event.</summary>
public record ScannerAccessDto(
    int EventId,
    bool CanAccessScanner,
    AssignedEventDto? Event
);

/// <summary>Request to assign a coordinator to an event.</summary>
public record AssignCoordinatorRequest(
    int CoordinatorUserId,
    int EventId
);

/// <summary>Response representing a coordinator assignment.</summary>
public record CoordinatorAssignmentDto(
    int Id,
    int CoordinatorUserId,
    int EventId,
    string Status,
    DateTime CreatedAtUtc
);

/// <summary>Standard error response matching project conventions.</summary>
public record ErrorDto(string Message);

/// <summary>Request body for POST /api/coordinator/events/{eventId}/check-in.</summary>
public record CheckInRequest(string? TicketCode = null, string? Code = null)
{
    public string EffectiveCode => !string.IsNullOrWhiteSpace(TicketCode) ? TicketCode.Trim() : (Code?.Trim() ?? string.Empty);
}

/// <summary>Result returned after scanning a ticket.</summary>
public record CheckInResultDto(
    string Status,           // "VALID" | "DUPLICATE" | "INVALID"
    string TicketCode,
    int EventId,
    string? AttendeeName,
    string? TicketType,
    string? SeatCode,
    DateTime? FirstCheckedInAt
);

/// <summary>Internal response from BookingService ticket lookup.</summary>
public record TicketLookupResponse(
    string TicketCode,
    int? TicketId,
    int EventId,
    int BookingId,
    int? AttendeeId,
    string AttendeeName,
    string TicketType,
    string SeatCode,
    string Status
);

