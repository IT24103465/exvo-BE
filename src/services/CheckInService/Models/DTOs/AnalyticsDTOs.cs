namespace Exvo.CheckInService.Models.DTOs;

/// <summary>
/// Comprehensive event analytics summary combining ticket sales, revenue, inventory, and check-in attendance.
/// </summary>
public record EventAnalyticsSummaryResponse(
    int EventId,
    string Title,
    int TotalCapacity,
    int TotalTicketsSold,
    int RemainingTickets,
    decimal TotalRevenue,
    string Currency,
    int TotalCheckedIn,
    int TotalAttendeesExpected,
    double AttendancePercentage,
    int TotalRefundedTickets,
    decimal TotalRefundedAmount,
    DateTime LastUpdatedUtc
);

/// <summary>
/// Sales and revenue analytics for an event, broken down by ticket tier/type and status.
/// </summary>
public record EventSalesAnalyticsResponse(
    int EventId,
    int TotalTicketsSold,
    int TotalCapacity,
    int RemainingTickets,
    decimal TotalRevenue,
    string Currency,
    int ConfirmedBookingsCount,
    int CancelledBookingsCount,
    int RefundedTicketsCount,
    decimal RefundedAmount,
    IReadOnlyList<TierSalesMetricsDto> TierSales,
    DateTime LastUpdatedUtc
);

/// <summary>
/// Tier-level sales and revenue breakdown.
/// </summary>
public record TierSalesMetricsDto(
    int? TicketTierId,
    string TierName,
    decimal UnitPrice,
    int SoldQuantity,
    int TotalCapacity,
    int RemainingQuantity,
    decimal Revenue
);

/// <summary>
/// Detailed attendance analytics for an event, including check-in progress, check-in timeline, and scanner stats.
/// </summary>
public record EventAttendanceAnalyticsResponse(
    int EventId,
    int TotalTicketsSold,
    int TotalCheckedIn,
    int TotalNotCheckedIn,
    int TotalRejectedScans,
    double AttendancePercentage,
    IReadOnlyList<HourlyCheckInDto> HourlyCheckIns,
    IReadOnlyList<TierAttendanceDto> TierAttendance,
    DateTime LastUpdatedUtc
);

/// <summary>
/// Hourly check-in aggregate for time-series attendance monitoring.
/// </summary>
public record HourlyCheckInDto(
    DateTime HourUtc,
    int CheckInCount
);

/// <summary>
/// Attendance breakdown by ticket tier/type.
/// </summary>
public record TierAttendanceDto(
    string TicketType,
    int TotalSold,
    int CheckedInCount,
    double AttendancePercentage
);

/// <summary>
/// Internal DTO representing booking metrics fetched from BookingService.
/// </summary>
public record EventBookingMetricsResponse(
    int EventId,
    int TotalTicketsSold,
    decimal TotalRevenue,
    string Currency,
    int ConfirmedBookingsCount,
    int CancelledBookingsCount,
    int CancelledTicketsCount,
    decimal CancelledAmount,
    IReadOnlyList<BookingTierMetricDto> TierMetrics
);

/// <summary>
/// Internal DTO representing tier breakdown from BookingService.
/// </summary>
public record BookingTierMetricDto(
    int? TicketTierId,
    string TierName,
    decimal UnitPrice,
    int SoldQuantity,
    decimal Revenue
);

/// <summary>
/// Real-time event check-in analytics (SCRUM-36).
/// Includes unique entries, remaining attendees, check-in rate, and occupancy calculation.
/// </summary>
public record EventRealtimeAnalyticsResponse(
    int EventId,
    string Title,
    int VenueCapacity,
    int TotalSoldTickets,
    int TotalUniqueCheckedInTickets,
    int RemainingExpectedAttendees,
    double CheckInRate,
    double OccupancyPercentage,
    int CurrentInside,
    int TotalRejectedScans,
    bool ExitTrackingActive,
    string OccupancyNote,
    IReadOnlyList<HourlyCheckInDto> HourlyCheckIns,
    DateTime LastUpdatedUtc
)
{
    public int TotalCheckedIn => TotalUniqueCheckedInTickets;
    public int TotalCheckedInAttendees => TotalUniqueCheckedInTickets;
    public int OccupancyCount => CurrentInside;
    public double CheckInRatePercentage => CheckInRate;
}

/// <summary>
/// Dynamic sales and revenue report for an event (SCRUM-37).
/// </summary>
public record EventSalesReportResponse(
    int EventId,
    string Title,
    DateTime EventDate,
    string Venue,
    string Location,
    string OrganizerName,
    int TotalCapacity,
    int TotalSoldTickets,
    int RemainingTickets,
    decimal TotalRevenue,
    decimal NetRevenue,
    string Currency,
    int ConfirmedBookingsCount,
    int CancelledBookingsCount,
    int RefundedTicketsCount,
    decimal RefundedAmount,
    IReadOnlyList<TierSalesMetricsDto> TierSales,
    DateTime GeneratedAtUtc
)
{
    public int AvailableTickets => RemainingTickets;
    public decimal GrossRevenue => TotalRevenue + RefundedAmount;
}

/// <summary>
/// Dynamic attendance report for an event (SCRUM-37).
/// </summary>
public record EventAttendanceReportResponse(
    int EventId,
    string Title,
    DateTime EventDate,
    string Venue,
    string Location,
    string OrganizerName,
    int VenueCapacity,
    int TotalSoldTickets,
    int TotalUniqueCheckedIn,
    int RemainingExpectedAttendees,
    double CheckInRate,
    double OccupancyPercentage,
    int TotalRejectedScans,
    bool ExitTrackingActive,
    string OccupancyNote,
    IReadOnlyList<TierAttendanceDto> TierAttendance,
    IReadOnlyList<HourlyCheckInDto> HourlyCheckIns,
    DateTime GeneratedAtUtc
)
{
    public int TotalCheckedIn => TotalUniqueCheckedIn;
    public int TotalCheckedInAttendees => TotalUniqueCheckedIn;
    public int OccupancyCount => TotalUniqueCheckedIn;
    public double CheckInRatePercentage => CheckInRate;
}


/// <summary>
/// CatalogService event response DTO for internal analytics and snapshot consumption.
/// </summary>
public record CatalogEventResponse(
    int Id,
    string? Title,
    string? Description,
    string? Location,
    string? Venue,
    decimal Price,
    DateTime EventDate,
    int? UtcOffsetMinutes,
    int CategoryId,
    int OrganizerId,
    string? OrganizerName,
    string? ImageUrl,
    int AvailableTickets,
    string? TicketTiersJson,
    DateTime CreatedAt,
    bool IsHidder
);


