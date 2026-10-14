using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Exvo.CheckInService.Data;
using Exvo.CheckInService.Models;
using Exvo.CheckInService.Models.DTOs;
using Exvo.CheckInService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Exvo.CheckInService.Tests;

public class MockReportsHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.ToString() ?? string.Empty;

        // Mock Event 10 (Summer Festival, Organizer 100)
        if (uri.Contains("/api/catalog/events/10/ticket-snapshot"))
        {
            var json = """
            {
                "id": 10,
                "title": "Summer Festival",
                "organizerId": 100,
                "organizerName": "Festival Corp",
                "venue": "Galle Face Green",
                "location": "Colombo, Sri Lanka",
                "availableTickets": 100,
                "ticketTiersJson": "[{\"id\":1,\"name\":\"VIP\",\"price\":10000.00,\"quantity\":20},{\"id\":2,\"name\":\"General Admission\",\"price\":3750.00,\"quantity\":80}]",
                "eventDate": "2026-11-20T10:00:00Z"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/events/10/metrics"))
        {
            var json = """
            {
                "eventId": 10,
                "totalTicketsSold": 50,
                "totalRevenue": 250000.00,
                "currency": "LKR",
                "confirmedBookingsCount": 20,
                "cancelledBookingsCount": 2,
                "cancelledTicketsCount": 4,
                "cancelledAmount": 20000.00,
                "tierMetrics": [
                    {
                        "ticketTierId": 1,
                        "tierName": "VIP",
                        "unitPrice": 10000.00,
                        "soldQuantity": 10,
                        "revenue": 100000.00
                    },
                    {
                        "ticketTierId": 2,
                        "tierName": "General Admission",
                        "unitPrice": 3750.00,
                        "soldQuantity": 40,
                        "revenue": 150000.00
                    }
                ]
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        // Mock Event 20 (Zero sales and zero venue capacity event)
        if (uri.Contains("/api/catalog/events/20/ticket-snapshot"))
        {
            var json = """
            {
                "id": 20,
                "title": "Empty Workshop",
                "organizerId": 100,
                "organizerName": "Workshop Organizers",
                "venue": "Room 0",
                "location": "Colombo",
                "availableTickets": 0,
                "ticketTiersJson": "[]",
                "eventDate": "2026-12-01T09:00:00Z"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/events/20/metrics"))
        {
            var json = """
            {
                "eventId": 20,
                "totalTicketsSold": 0,
                "totalRevenue": 0.00,
                "currency": "LKR",
                "confirmedBookingsCount": 0,
                "cancelledBookingsCount": 0,
                "cancelledTicketsCount": 0,
                "cancelledAmount": 0.00,
                "tierMetrics": []
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        // Mock ticket scan lookup
        if (uri.Contains("/api/booking/internal/tickets/TICKET-LIVE-1"))
        {
            var json = """
            {
                "ticketCode": "TICKET-LIVE-1",
                "ticketId": 701,
                "eventId": 10,
                "bookingId": 801,
                "attendeeId": 901,
                "attendeeName": "Live Attendee",
                "ticketType": "VIP",
                "seatCode": "VIP-1",
                "status": "Confirmed"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class RealtimeAnalyticsAndReportsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string JwtSecret = "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!";

    public RealtimeAnalyticsAndReportsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddHttpClient("CatalogService", client =>
                {
                    client.BaseAddress = new Uri("http://localhost:5255");
                }).ConfigurePrimaryHttpMessageHandler(() => new MockReportsHttpMessageHandler());

                services.AddHttpClient("BookingService", client =>
                {
                    client.BaseAddress = new Uri("http://localhost:5063");
                }).ConfigurePrimaryHttpMessageHandler(() => new MockReportsHttpMessageHandler());
            });
        });
    }

    private string GenerateJwt(int userId, string role)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(JwtSecret);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role),
                new Claim("role", role),
                new Claim("sub", userId.ToString())
            }),
            Expires = DateTime.UtcNow.AddHours(2),
            Issuer = "ExvoAuthService",
            Audience = "ExvoPlatform",
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    // =========================================================================
    // SCRUM-36: Real-Time Check-In Analytics Tests
    // =========================================================================

    [Fact]
    public async Task GetRealtimeAnalytics_AuthorizedOrganizer_ReturnsAccurateMetricsAndOccupancy()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        // Preload database with some check-ins for event 10
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();
            if (!await db.ScannedTickets.AnyAsync(t => t.TicketId == 601))
            {
                db.ScannedTickets.Add(new ScannedTicket
                {
                    TicketId = 601,
                    EventId = 10,
                    BookingId = 1001,
                    AttendeeId = 2001,
                    ScannedBy = 100,
                    ScannedAt = DateTime.UtcNow,
                    Status = ScannedTicketStatus.CheckedIn,
                    QrCodeHash = "hash-601",
                    TicketType = "VIP",
                    AttendeeName = "Attendee 601"
                });
                await db.SaveChangesAsync();
            }
        }

        var response = await client.GetAsync("/api/checkin/analytics/event/10/realtime");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var data = await response.Content.ReadFromJsonAsync<EventRealtimeAnalyticsResponse>();
        Assert.NotNull(data);
        Assert.Equal(10, data.EventId);
        Assert.Equal("Summer Festival", data.Title);
        Assert.Equal(50, data.TotalSoldTickets);
        Assert.True(data.VenueCapacity >= 50);
        Assert.True(data.TotalUniqueCheckedInTickets >= 1);
        Assert.Equal(data.TotalSoldTickets - data.TotalUniqueCheckedInTickets, data.RemainingExpectedAttendees);
        Assert.True(data.CheckInRate > 0.0);
        Assert.True(data.OccupancyPercentage > 0.0);
        Assert.Equal(data.TotalUniqueCheckedInTickets, data.CurrentInside);
        Assert.Contains("Physical exit tracking is not active", data.OccupancyNote);
        Assert.Equal(data.TotalUniqueCheckedInTickets, data.TotalCheckedIn);
        Assert.Equal(data.TotalUniqueCheckedInTickets, data.TotalCheckedInAttendees);
        Assert.Equal(data.CurrentInside, data.OccupancyCount);
        Assert.Equal(data.CheckInRate, data.CheckInRatePercentage);

        // Also verify root route and alias route
        var rootResp = await client.GetAsync("/api/checkin/analytics/event/10");
        Assert.Equal(HttpStatusCode.OK, rootResp.StatusCode);

        var aliasResp = await client.GetAsync("/api/checkin/event/10/realtime-analytics");
        Assert.Equal(HttpStatusCode.OK, aliasResp.StatusCode);
    }

    [Fact]
    public async Task GetRealtimeAnalytics_DuplicateCheckIns_DoesNotAffectStatistics()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        // Insert distinct ticket 602
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();
            if (!await db.ScannedTickets.AnyAsync(t => t.TicketId == 602))
            {
                db.ScannedTickets.Add(new ScannedTicket
                {
                    TicketId = 602,
                    EventId = 10,
                    BookingId = 1002,
                    AttendeeId = 2002,
                    ScannedBy = 100,
                    ScannedAt = DateTime.UtcNow,
                    Status = ScannedTicketStatus.CheckedIn,
                    QrCodeHash = "hash-602",
                    TicketType = "General Admission"
                });
                await db.SaveChangesAsync();
            }
        }

        var firstResp = await client.GetAsync("/api/checkin/analytics/event/10/realtime");
        var firstData = await firstResp.Content.ReadFromJsonAsync<EventRealtimeAnalyticsResponse>();
        Assert.NotNull(firstData);
        var initialUnique = firstData.TotalUniqueCheckedInTickets;

        // Even if an extra rejected scan occurs for ticket 602
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();
            db.ScannedTickets.Add(new ScannedTicket
            {
                TicketId = 602,
                EventId = 10,
                BookingId = 1002,
                AttendeeId = 2002,
                ScannedBy = 100,
                ScannedAt = DateTime.UtcNow,
                Status = ScannedTicketStatus.Rejected,
                QrCodeHash = "hash-602-dup",
                RejectionReason = "Duplicate scan"
            });
            await db.SaveChangesAsync();
        }

        var secondResp = await client.GetAsync("/api/checkin/analytics/event/10/realtime");
        var secondData = await secondResp.Content.ReadFromJsonAsync<EventRealtimeAnalyticsResponse>();
        Assert.NotNull(secondData);

        // Unique tickets checked in should NOT increase from duplicate/rejected attempts
        Assert.Equal(initialUnique, secondData.TotalUniqueCheckedInTickets);
        Assert.True(secondData.TotalRejectedScans >= 1);
    }

    [Fact]
    public async Task GetRealtimeAnalytics_ZeroSalesAndCapacity_HandledSafelyWithoutZeroDivision()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/analytics/event/20/realtime");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var data = await response.Content.ReadFromJsonAsync<EventRealtimeAnalyticsResponse>();
        Assert.NotNull(data);
        Assert.Equal(20, data.EventId);
        Assert.Equal(0, data.TotalSoldTickets);
        Assert.Equal(0, data.VenueCapacity);
        Assert.Equal(0, data.TotalUniqueCheckedInTickets);
        Assert.Equal(0, data.RemainingExpectedAttendees);
        Assert.Equal(0.0, data.CheckInRate);
        Assert.Equal(0.0, data.OccupancyPercentage);
    }

    [Fact]
    public async Task GetRealtimeAnalytics_ETagCaching_Returns304NotModified()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var firstResp = await client.GetAsync("/api/checkin/analytics/event/10/realtime");
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
        var etag = firstResp.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        // Send second request with If-None-Match
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/checkin/analytics/event/10/realtime");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var secondResp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotModified, secondResp.StatusCode);
    }

    [Fact]
    public async Task GetRealtimeAnalytics_Security_ValidatesAuthAndRoles()
    {
        var client = _factory.CreateClient();

        // 1. Unauthenticated -> 401
        var unauthResp = await client.GetAsync("/api/checkin/analytics/event/10/realtime");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // 2. Unauthorized organizer (User 999 does not own event 10) -> 403
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(999, "Organizer"));
        var forbidResp = await client.GetAsync("/api/checkin/analytics/event/10/realtime");
        Assert.Equal(HttpStatusCode.Forbidden, forbidResp.StatusCode);

        // 3. Admin user -> 200 OK regardless of ownership
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(999, "Admin"));
        var adminResp = await client.GetAsync("/api/checkin/analytics/event/10/realtime");
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);

        // 4. Invalid event ID (0 or negative) -> 400
        var badResp = await client.GetAsync("/api/checkin/analytics/event/0/realtime");
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);

        // 5. Non-existent event ID -> 404
        var notFoundResp = await client.GetAsync("/api/checkin/analytics/event/9999/realtime");
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
    }

    [Fact]
    public async Task LiveStreamAnalytics_EmitsEventStreamContentType()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var response = await client.GetAsync("/api/checkin/analytics/event/10/live", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) { }
    }

    // =========================================================================
    // SCRUM-37: Dynamic Sales & Attendance Reports Tests
    // =========================================================================

    [Fact]
    public async Task GetSalesReport_Json_ReturnsAccurateFinancialsAndTiers()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/reports/event/10/sales");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<EventSalesReportResponse>();
        Assert.NotNull(report);
        Assert.Equal(10, report.EventId);
        Assert.Equal("Summer Festival", report.Title);
        Assert.Equal(50, report.TotalSoldTickets);
        Assert.Equal(250000.00m, report.TotalRevenue);
        Assert.Equal("LKR", report.Currency);
        Assert.Equal(20, report.ConfirmedBookingsCount);
        Assert.Equal(2, report.CancelledBookingsCount);
        Assert.Equal(4, report.RefundedTicketsCount);
        Assert.Equal(20000.00m, report.RefundedAmount);
        Assert.NotNull(report.TierSales);
        Assert.Equal(2, report.TierSales.Count);
    }

    [Fact]
    public async Task GetSalesReport_Csv_ReturnsValidCsvFileWithBomAndHeaders()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/reports/event/10/sales/csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/csv", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("sales-report-event-10.csv", response.Content.Headers.ContentDisposition?.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 3);
        // Verify UTF-8 BOM
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("EXVO SALES & REVENUE REPORT", text);
        Assert.Contains("Summer Festival", text);
        Assert.Contains("KEY SALES METRICS", text);
        Assert.Contains("TICKET TIER SALES BREAKDOWN", text);
        Assert.Contains("VIP", text);
        Assert.Contains("LKR", text);
    }

    [Fact]
    public async Task GetSalesReport_Pdf_ReturnsValidPdfStructure()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/reports/event/10/sales/pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sales-report-event-10.pdf", response.Content.Headers.ContentDisposition?.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);

        var header = Encoding.ASCII.GetString(bytes, 0, 8);
        Assert.StartsWith("%PDF-1.4", header);

        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - 10, 10);
        Assert.Contains("%%EOF", tail);
    }

    [Fact]
    public async Task GetAttendanceReport_Json_ReturnsAttendanceAndDisclaimers()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/reports/event/10/attendance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<EventAttendanceReportResponse>();
        Assert.NotNull(report);
        Assert.Equal(10, report.EventId);
        Assert.Equal("Summer Festival", report.Title);
        Assert.Equal(50, report.TotalSoldTickets);
        Assert.True(report.TotalUniqueCheckedIn >= 0);
        Assert.Equal(report.TotalSoldTickets - report.TotalUniqueCheckedIn, report.RemainingExpectedAttendees);
        Assert.False(report.ExitTrackingActive);
        Assert.Contains("Physical exit tracking is not active", report.OccupancyNote);
        Assert.NotNull(report.TierAttendance);
    }

    [Fact]
    public async Task GetAttendanceReport_Csv_ReturnsValidCsvFileWithHeadersAndTimeline()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/reports/event/10/attendance/csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/csv", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("attendance-report-event-10.csv", response.Content.Headers.ContentDisposition?.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("EXVO ATTENDANCE & CHECK-IN REPORT", text);
        Assert.Contains("KEY ATTENDANCE METRICS", text);
        Assert.Contains("TIER ATTENDANCE BREAKDOWN", text);
        Assert.Contains("Physical exit tracking is not active", text);
    }

    [Fact]
    public async Task GetAttendanceReport_Pdf_ReturnsValidPdfStructure()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/reports/event/10/attendance/pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attendance-report-event-10.pdf", response.Content.Headers.ContentDisposition?.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);

        var header = Encoding.ASCII.GetString(bytes, 0, 8);
        Assert.StartsWith("%PDF-1.4", header);

        var tail = Encoding.ASCII.GetString(bytes, bytes.Length - 10, 10);
        Assert.Contains("%%EOF", tail);
    }

    [Fact]
    public async Task Reports_ContentNegotiation_FormatQueryParameterReturnsAppropriateType()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        // Format query parameter = csv
        var csvResp = await client.GetAsync("/api/checkin/reports/event/10/sales?format=csv");
        Assert.Equal(HttpStatusCode.OK, csvResp.StatusCode);
        Assert.Contains("text/csv", csvResp.Content.Headers.ContentType?.ToString());

        // Format query parameter = pdf
        var pdfResp = await client.GetAsync("/api/checkin/reports/event/10/sales?format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdfResp.StatusCode);
        Assert.Equal("application/pdf", pdfResp.Content.Headers.ContentType?.MediaType);

        // Format query parameter = csv on attendance
        var attCsvResp = await client.GetAsync("/api/checkin/reports/event/10/attendance?format=csv");
        Assert.Equal(HttpStatusCode.OK, attCsvResp.StatusCode);
        Assert.Contains("text/csv", attCsvResp.Content.Headers.ContentType?.ToString());

        // Format query parameter = pdf on attendance
        var attPdfResp = await client.GetAsync("/api/checkin/reports/event/10/attendance?format=pdf");
        Assert.Equal(HttpStatusCode.OK, attPdfResp.StatusCode);
        Assert.Equal("application/pdf", attPdfResp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RealtimeNotifier_PulseAndBroadcast_WorksCorrectly()
    {
        var notifier = new RealtimeAnalyticsNotifier();
        using var sub = notifier.Subscribe(42);

        // Signal notification
        notifier.NotifyEventUpdated(42);

        // Wait with timeout
        var received = await sub.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(received);

        notifier.Unsubscribe(42, sub);
    }
}
