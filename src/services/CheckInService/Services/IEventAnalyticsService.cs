using Exvo.CheckInService.Models.DTOs;

namespace Exvo.CheckInService.Services;

public interface IEventAnalyticsService
{
    Task<CatalogEventResponse?> GetCatalogEventAsync(int eventId, CancellationToken ct = default);
    Task<EventBookingMetricsResponse?> GetBookingMetricsAsync(int eventId, CancellationToken ct = default);
    Task<EventAnalyticsSummaryResponse?> GetEventAnalyticsSummaryAsync(int eventId, CancellationToken ct = default);
    Task<EventSalesAnalyticsResponse?> GetEventSalesAnalyticsAsync(int eventId, CancellationToken ct = default);
    Task<EventAttendanceAnalyticsResponse?> GetEventAttendanceAnalyticsAsync(int eventId, CancellationToken ct = default);
    Task<EventRealtimeAnalyticsResponse?> GetEventRealtimeAnalyticsAsync(int eventId, CancellationToken ct = default);
}
