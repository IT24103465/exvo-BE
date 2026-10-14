using Exvo.CheckInService.Models.DTOs;

namespace Exvo.CheckInService.Services;

public interface IEventReportService
{
    Task<EventSalesReportResponse?> GenerateSalesReportAsync(int eventId, CancellationToken ct = default);
    Task<EventAttendanceReportResponse?> GenerateAttendanceReportAsync(int eventId, CancellationToken ct = default);
}

public class EventReportService : IEventReportService
{
    private readonly IEventAnalyticsService _analyticsService;

    public EventReportService(IEventAnalyticsService analyticsService)
    {
        _analyticsService = analyticsService;
    }

    public async Task<EventSalesReportResponse?> GenerateSalesReportAsync(int eventId, CancellationToken ct = default)
    {
        var eventData = await _analyticsService.GetCatalogEventAsync(eventId, ct);
        if (eventData is null) return null;

        var salesAnalytics = await _analyticsService.GetEventSalesAnalyticsAsync(eventId, ct);
        if (salesAnalytics is null) return null;

        return new EventSalesReportResponse(
            EventId: eventId,
            Title: eventData.Title ?? $"Event #{eventId}",
            EventDate: eventData.EventDate,
            Venue: eventData.Venue ?? "Main Venue",
            Location: eventData.Location ?? "Colombo, Sri Lanka",
            OrganizerName: eventData.OrganizerName ?? $"Organizer #{eventData.OrganizerId}",
            TotalCapacity: salesAnalytics.TotalCapacity,
            TotalSoldTickets: salesAnalytics.TotalTicketsSold,
            RemainingTickets: salesAnalytics.RemainingTickets,
            TotalRevenue: salesAnalytics.TotalRevenue,
            NetRevenue: salesAnalytics.TotalRevenue,
            Currency: salesAnalytics.Currency,
            ConfirmedBookingsCount: salesAnalytics.ConfirmedBookingsCount,
            CancelledBookingsCount: salesAnalytics.CancelledBookingsCount,
            RefundedTicketsCount: salesAnalytics.RefundedTicketsCount,
            RefundedAmount: salesAnalytics.RefundedAmount,
            TierSales: salesAnalytics.TierSales,
            GeneratedAtUtc: DateTime.UtcNow
        );
    }

    public async Task<EventAttendanceReportResponse?> GenerateAttendanceReportAsync(int eventId, CancellationToken ct = default)
    {
        var eventData = await _analyticsService.GetCatalogEventAsync(eventId, ct);
        if (eventData is null) return null;

        var realtime = await _analyticsService.GetEventRealtimeAnalyticsAsync(eventId, ct);
        if (realtime is null) return null;

        var attendanceAnalytics = await _analyticsService.GetEventAttendanceAnalyticsAsync(eventId, ct);

        return new EventAttendanceReportResponse(
            EventId: eventId,
            Title: eventData.Title ?? $"Event #{eventId}",
            EventDate: eventData.EventDate,
            Venue: eventData.Venue ?? "Main Venue",
            Location: eventData.Location ?? "Colombo, Sri Lanka",
            OrganizerName: eventData.OrganizerName ?? $"Organizer #{eventData.OrganizerId}",
            VenueCapacity: realtime.VenueCapacity,
            TotalSoldTickets: realtime.TotalSoldTickets,
            TotalUniqueCheckedIn: realtime.TotalUniqueCheckedInTickets,
            RemainingExpectedAttendees: realtime.RemainingExpectedAttendees,
            CheckInRate: realtime.CheckInRate,
            OccupancyPercentage: realtime.OccupancyPercentage,
            TotalRejectedScans: realtime.TotalRejectedScans,
            ExitTrackingActive: realtime.ExitTrackingActive,
            OccupancyNote: realtime.OccupancyNote,
            TierAttendance: attendanceAnalytics?.TierAttendance ?? Array.Empty<TierAttendanceDto>(),
            HourlyCheckIns: realtime.HourlyCheckIns,
            GeneratedAtUtc: DateTime.UtcNow
        );
    }
}
