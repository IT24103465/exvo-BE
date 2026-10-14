using System.Net.Http.Json;
using System.Text.Json;
using Exvo.CheckInService.Data;
using Exvo.CheckInService.Models;
using Exvo.CheckInService.Models.DTOs;
using Microsoft.EntityFrameworkCore;

namespace Exvo.CheckInService.Services;

public class EventAnalyticsService : IEventAnalyticsService
{
    private readonly CheckInDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EventAnalyticsService> _logger;

    public const string OccupancyDisclaimer = "Physical exit tracking is not active. Occupancy percentage is calculated using cumulative verified unique check-in entries against venue capacity.";

    public EventAnalyticsService(
        CheckInDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<EventAnalyticsService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CatalogEventResponse?> GetCatalogEventAsync(int eventId, CancellationToken ct = default)
    {
        var catalogClient = _httpClientFactory.CreateClient("CatalogService");
        try
        {
            var response = await catalogClient.GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<CatalogEventResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve event #{EventId} from CatalogService.", eventId);
            return null;
        }
    }

    public async Task<EventBookingMetricsResponse?> GetBookingMetricsAsync(int eventId, CancellationToken ct = default)
    {
        var bookingClient = _httpClientFactory.CreateClient("BookingService");
        try
        {
            var response = await bookingClient.GetAsync($"/api/booking/internal/events/{eventId}/metrics", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<EventBookingMetricsResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve booking metrics for event #{EventId} from BookingService.", eventId);
            return null;
        }
    }

    public static int ResolveVenueCapacity(CatalogEventResponse eventData, int totalSoldTickets)
    {
        int tierCapacitySum = 0;
        if (!string.IsNullOrWhiteSpace(eventData.TicketTiersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(eventData.TicketTiersJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tier in doc.RootElement.EnumerateArray())
                    {
                        if (tier.TryGetProperty("quantity", out var qProp) && qProp.TryGetInt32(out var q) && q > 0)
                        {
                            tierCapacitySum += q;
                        }
                    }
                }
            }
            catch { }
        }

        var baseCapacity = tierCapacitySum > 0 ? tierCapacitySum : eventData.AvailableTickets;
        return Math.Max(baseCapacity, totalSoldTickets);
    }

    public async Task<EventAnalyticsSummaryResponse?> GetEventAnalyticsSummaryAsync(int eventId, CancellationToken ct = default)
    {
        var eventData = await GetCatalogEventAsync(eventId, ct);
        if (eventData is null) return null;

        var bookingMetrics = await GetBookingMetricsAsync(eventId, ct);

        var totalTicketsSold = bookingMetrics?.TotalTicketsSold ?? 0;
        var totalRevenue = bookingMetrics?.TotalRevenue ?? 0m;
        var currency = bookingMetrics?.Currency ?? "LKR";
        var totalRefundedTickets = bookingMetrics?.CancelledTicketsCount ?? 0;
        var totalRefundedAmount = bookingMetrics?.CancelledAmount ?? 0m;

        var totalCapacity = ResolveVenueCapacity(eventData, totalTicketsSold);
        var remainingTickets = Math.Max(0, totalCapacity - totalTicketsSold);

        // Committed check-ins query with distinct TicketId to prevent duplicates from affecting statistics
        var totalUniqueCheckedIn = await _db.ScannedTickets
            .AsNoTracking()
            .Where(t => t.EventId == eventId && t.Status == ScannedTicketStatus.CheckedIn)
            .Select(t => t.TicketId)
            .Distinct()
            .CountAsync(ct);

        var totalAttendeesExpected = totalTicketsSold;
        var attendancePercentage = totalAttendeesExpected > 0
            ? Math.Round((double)totalUniqueCheckedIn / totalAttendeesExpected * 100.0, 2)
            : 0.0;

        return new EventAnalyticsSummaryResponse(
            EventId: eventId,
            Title: eventData.Title ?? $"Event #{eventId}",
            TotalCapacity: totalCapacity,
            TotalTicketsSold: totalTicketsSold,
            RemainingTickets: remainingTickets,
            TotalRevenue: totalRevenue,
            Currency: currency,
            TotalCheckedIn: totalUniqueCheckedIn,
            TotalAttendeesExpected: totalAttendeesExpected,
            AttendancePercentage: attendancePercentage,
            TotalRefundedTickets: totalRefundedTickets,
            TotalRefundedAmount: totalRefundedAmount,
            LastUpdatedUtc: DateTime.UtcNow
        );
    }

    public async Task<EventSalesAnalyticsResponse?> GetEventSalesAnalyticsAsync(int eventId, CancellationToken ct = default)
    {
        var eventData = await GetCatalogEventAsync(eventId, ct);
        if (eventData is null) return null;

        var bookingMetrics = await GetBookingMetricsAsync(eventId, ct);

        var totalTicketsSold = bookingMetrics?.TotalTicketsSold ?? 0;
        var totalRevenue = bookingMetrics?.TotalRevenue ?? 0m;
        var currency = bookingMetrics?.Currency ?? "LKR";
        var confirmedBookingsCount = bookingMetrics?.ConfirmedBookingsCount ?? 0;
        var cancelledBookingsCount = bookingMetrics?.CancelledBookingsCount ?? 0;
        var refundedTicketsCount = bookingMetrics?.CancelledTicketsCount ?? 0;
        var refundedAmount = bookingMetrics?.CancelledAmount ?? 0m;

        var totalCapacity = ResolveVenueCapacity(eventData, totalTicketsSold);
        var remainingTickets = Math.Max(0, totalCapacity - totalTicketsSold);

        var tierSalesList = new List<TierSalesMetricsDto>();
        if (bookingMetrics?.TierMetrics != null && bookingMetrics.TierMetrics.Count > 0)
        {
            foreach (var tm in bookingMetrics.TierMetrics)
            {
                tierSalesList.Add(new TierSalesMetricsDto(
                    TicketTierId: tm.TicketTierId,
                    TierName: tm.TierName,
                    UnitPrice: tm.UnitPrice,
                    SoldQuantity: tm.SoldQuantity,
                    TotalCapacity: tm.SoldQuantity,
                    RemainingQuantity: 0,
                    Revenue: tm.Revenue
                ));
            }
        }
        else if (totalTicketsSold > 0)
        {
            tierSalesList.Add(new TierSalesMetricsDto(
                TicketTierId: null,
                TierName: "General Admission",
                UnitPrice: eventData.Price,
                SoldQuantity: totalTicketsSold,
                TotalCapacity: totalCapacity,
                RemainingQuantity: remainingTickets,
                Revenue: totalRevenue
            ));
        }

        return new EventSalesAnalyticsResponse(
            EventId: eventId,
            TotalTicketsSold: totalTicketsSold,
            TotalCapacity: totalCapacity,
            RemainingTickets: remainingTickets,
            TotalRevenue: totalRevenue,
            Currency: currency,
            ConfirmedBookingsCount: confirmedBookingsCount,
            CancelledBookingsCount: cancelledBookingsCount,
            RefundedTicketsCount: refundedTicketsCount,
            RefundedAmount: refundedAmount,
            TierSales: tierSalesList,
            LastUpdatedUtc: DateTime.UtcNow
        );
    }

    public async Task<EventAttendanceAnalyticsResponse?> GetEventAttendanceAnalyticsAsync(int eventId, CancellationToken ct = default)
    {
        var bookingMetrics = await GetBookingMetricsAsync(eventId, ct);
        var totalTicketsSold = bookingMetrics?.TotalTicketsSold ?? 0;

        var scannedTickets = await _db.ScannedTickets
            .AsNoTracking()
            .Where(t => t.EventId == eventId)
            .ToListAsync(ct);

        var checkedInList = scannedTickets
            .Where(t => t.Status == ScannedTicketStatus.CheckedIn)
            .ToList();

        // Unique tickets checked in
        var totalUniqueCheckedIn = checkedInList
            .Select(t => t.TicketId)
            .Distinct()
            .Count();

        var totalRejectedScans = scannedTickets.Count(t => t.Status == ScannedTicketStatus.Rejected);
        var totalNotCheckedIn = Math.Max(0, totalTicketsSold - totalUniqueCheckedIn);

        var attendancePercentage = totalTicketsSold > 0
            ? Math.Round((double)totalUniqueCheckedIn / totalTicketsSold * 100.0, 2)
            : 0.0;

        var hourlyCheckIns = checkedInList
            .GroupBy(t => new DateTime(t.ScannedAt.Year, t.ScannedAt.Month, t.ScannedAt.Day, t.ScannedAt.Hour, 0, 0, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new HourlyCheckInDto(
                HourUtc: g.Key,
                CheckInCount: g.Select(x => x.TicketId).Distinct().Count()
            ))
            .ToList();

        var tierAttendanceList = new List<TierAttendanceDto>();
        var checkedInByTier = checkedInList
            .GroupBy(t => string.IsNullOrWhiteSpace(t.TicketType) ? "General Admission" : t.TicketType)
            .ToDictionary(g => g.Key, g => g.Select(x => x.TicketId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);

        if (bookingMetrics?.TierMetrics != null && bookingMetrics.TierMetrics.Count > 0)
        {
            foreach (var tm in bookingMetrics.TierMetrics)
            {
                var tierName = tm.TierName;
                var checkedInCount = checkedInByTier.TryGetValue(tierName, out var count) ? count : 0;
                var tierAttendancePct = tm.SoldQuantity > 0
                    ? Math.Round((double)checkedInCount / tm.SoldQuantity * 100.0, 2)
                    : 0.0;

                tierAttendanceList.Add(new TierAttendanceDto(
                    TicketType: tierName,
                    TotalSold: tm.SoldQuantity,
                    CheckedInCount: checkedInCount,
                    AttendancePercentage: tierAttendancePct
                ));
            }
        }
        else
        {
            tierAttendanceList.Add(new TierAttendanceDto(
                TicketType: "General Admission",
                TotalSold: totalTicketsSold,
                CheckedInCount: totalUniqueCheckedIn,
                AttendancePercentage: attendancePercentage
            ));
        }

        return new EventAttendanceAnalyticsResponse(
            EventId: eventId,
            TotalTicketsSold: totalTicketsSold,
            TotalCheckedIn: totalUniqueCheckedIn,
            TotalNotCheckedIn: totalNotCheckedIn,
            TotalRejectedScans: totalRejectedScans,
            AttendancePercentage: attendancePercentage,
            HourlyCheckIns: hourlyCheckIns,
            TierAttendance: tierAttendanceList,
            LastUpdatedUtc: DateTime.UtcNow
        );
    }

    public async Task<EventRealtimeAnalyticsResponse?> GetEventRealtimeAnalyticsAsync(int eventId, CancellationToken ct = default)
    {
        var eventData = await GetCatalogEventAsync(eventId, ct);
        if (eventData is null) return null;

        var bookingMetrics = await GetBookingMetricsAsync(eventId, ct);
        var totalTicketsSold = bookingMetrics?.TotalTicketsSold ?? 0;

        var venueCapacity = ResolveVenueCapacity(eventData, totalTicketsSold);

        var scannedTickets = await _db.ScannedTickets
            .AsNoTracking()
            .Where(t => t.EventId == eventId)
            .ToListAsync(ct);

        var checkedInList = scannedTickets
            .Where(t => t.Status == ScannedTicketStatus.CheckedIn)
            .ToList();

        // Cumulative unique tickets checked in
        var uniqueCheckedInTickets = checkedInList
            .Select(t => t.TicketId)
            .Distinct()
            .Count();

        var totalRejectedScans = scannedTickets.Count(t => t.Status == ScannedTicketStatus.Rejected);
        var remainingExpectedAttendees = Math.Max(0, totalTicketsSold - uniqueCheckedInTickets);

        // Safe division handling for zero sold tickets and zero venue capacity
        var checkInRate = totalTicketsSold > 0
            ? Math.Round((double)uniqueCheckedInTickets / totalTicketsSold * 100.0, 2)
            : 0.0;

        var occupancyPercentage = venueCapacity > 0
            ? Math.Round((double)uniqueCheckedInTickets / venueCapacity * 100.0, 2)
            : 0.0;

        var hourlyCheckIns = checkedInList
            .GroupBy(t => new DateTime(t.ScannedAt.Year, t.ScannedAt.Month, t.ScannedAt.Day, t.ScannedAt.Hour, 0, 0, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new HourlyCheckInDto(
                HourUtc: g.Key,
                CheckInCount: g.Select(x => x.TicketId).Distinct().Count()
            ))
            .ToList();

        return new EventRealtimeAnalyticsResponse(
            EventId: eventId,
            Title: eventData.Title ?? $"Event #{eventId}",
            VenueCapacity: venueCapacity,
            TotalSoldTickets: totalTicketsSold,
            TotalUniqueCheckedInTickets: uniqueCheckedInTickets,
            RemainingExpectedAttendees: remainingExpectedAttendees,
            CheckInRate: checkInRate,
            OccupancyPercentage: occupancyPercentage,
            CurrentInside: uniqueCheckedInTickets,
            TotalRejectedScans: totalRejectedScans,
            ExitTrackingActive: false,
            OccupancyNote: OccupancyDisclaimer,
            HourlyCheckIns: hourlyCheckIns,
            LastUpdatedUtc: DateTime.UtcNow
        );
    }
}
