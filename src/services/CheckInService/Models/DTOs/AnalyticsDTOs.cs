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
