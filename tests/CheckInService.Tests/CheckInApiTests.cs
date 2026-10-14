using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Exvo.CheckInService.Data;
using Exvo.CheckInService.Models;
using Exvo.CheckInService.Models.DTOs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Exvo.CheckInService.Tests;

public class MockHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.ToString() ?? string.Empty;

        // Mock CatalogService event snapshot
        if (uri.Contains("/api/catalog/events/10/ticket-snapshot"))
        {
            var json = """
            {
                "id": 10,
                "title": "Summer Festival",
                "organizerId": 100,
                "eventDate": "2026-11-20T10:00:00Z"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        // Mock BookingService internal ticket lookup
        if (uri.Contains("/api/booking/internal/tickets/VALID-TICKET-1"))
        {
            var json = """
            {
                "ticketCode": "VALID-TICKET-1",
                "ticketId": 101,
                "eventId": 10,
                "bookingId": 501,
                "attendeeId": 201,
                "attendeeName": "John Doe",
                "ticketType": "VIP",
                "seatCode": "A-12",
                "status": "Confirmed"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/tickets/VALID-TICKET-SCAN"))
        {
            var json = """
            {
                "ticketCode": "VALID-TICKET-SCAN",
                "ticketId": 105,
                "eventId": 10,
                "bookingId": 505,
                "attendeeId": 205,
                "attendeeName": "Bob Scan",
                "ticketType": "VIP",
                "seatCode": "B-01",
                "status": "Confirmed"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/tickets/VALID-TICKET-DUP"))
        {
            var json = """
            {
                "ticketCode": "VALID-TICKET-DUP",
                "ticketId": 106,
                "eventId": 10,
                "bookingId": 506,
                "attendeeId": 206,
                "attendeeName": "Charlie Dup",
                "ticketType": "General",
                "seatCode": "C-01",
                "status": "Confirmed"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/tickets/CONCURRENT-TICKET"))
        {
            var json = """
            {
                "ticketCode": "CONCURRENT-TICKET",
                "ticketId": 108,
                "eventId": 10,
                "bookingId": 508,
                "attendeeId": 208,
                "attendeeName": "Race Attendee",
                "ticketType": "VIP",
                "seatCode": "R-01",
                "status": "Confirmed"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/tickets/VALID-TICKET-STATS"))
        {
            var json = """
            {
                "ticketCode": "VALID-TICKET-STATS",
                "ticketId": 107,
                "eventId": 10,
                "bookingId": 507,
                "attendeeId": 207,
                "attendeeName": "Diana Stats",
                "ticketType": "General",
                "seatCode": "D-01",
                "status": "Confirmed"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/tickets/CANCELLED-TICKET"))
        {
            var json = """
            {
                "ticketCode": "CANCELLED-TICKET",
                "ticketId": 102,
                "eventId": 10,
                "bookingId": 502,
                "attendeeId": 202,
                "attendeeName": "Jane Doe",
                "ticketType": "General",
                "seatCode": "GA",
                "status": "Cancelled"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/tickets/WRONG-EVENT-TICKET"))
        {
            var json = """
            {
                "ticketCode": "WRONG-EVENT-TICKET",
                "ticketId": 103,
                "eventId": 99,
                "bookingId": 503,
                "attendeeId": 203,
                "attendeeName": "Alice",
                "ticketType": "General",
                "seatCode": "GA",
                "status": "Confirmed"
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/catalog/events/12/ticket-snapshot"))
        {
            var json = """{ "id": 12, "title": "Past Show", "organizerId": 100, "eventDate": "2026-01-01T10:00:00Z" }""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/booking/internal/tickets/EXPIRED-EVENT-TICKET"))
        {
            var json = """{ "ticketCode": "EXPIRED-EVENT-TICKET", "ticketId": 120, "eventId": 12, "bookingId": 520, "attendeeId": 220, "attendeeName": "Old", "ticketType": "GA", "seatCode": "GA", "status": "Confirmed" }""";
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

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class CheckInApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string JwtSecret = "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!";

    public CheckInApiTests(WebApplicationFactory<Program> factory)
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
                }).ConfigurePrimaryHttpMessageHandler(() => new MockHttpMessageHandler());

                services.AddHttpClient("BookingService", client =>
                {
                    client.BaseAddress = new Uri("http://localhost:5063");
                }).ConfigurePrimaryHttpMessageHandler(() => new MockHttpMessageHandler());
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

    [Fact]
    public async Task Verify_ValidTicket_ReturnsValid()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/verify", new VerifyTicketRequest("VALID-TICKET-1", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<VerifyTicketResponse>();
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("Valid", result.Status);
        Assert.Equal(101, result.TicketId);
        Assert.Equal("John Doe", result.AttendeeName);
    }

    [Fact]
    public async Task Verify_CancelledTicket_ReturnsCancelled()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/verify", new VerifyTicketRequest("CANCELLED-TICKET", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<VerifyTicketResponse>();
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Equal("Cancelled", result.Status);
    }

    [Fact]
    public async Task Verify_WrongEventTicket_ReturnsInvalidEvent()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/verify", new VerifyTicketRequest("WRONG-EVENT-TICKET", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<VerifyTicketResponse>();
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Equal("InvalidEvent", result.Status);
    }

    [Fact]
    public async Task Validate_ValidTicket_ReturnsVALID_AndDoesNotConsumeTicket()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("VALID-TICKET-1", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ValidateTicketResponse>();
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal(TicketValidationStatus.Valid, result.Status);
        Assert.Equal(101, result.TicketId);
        Assert.Equal("John Doe", result.AttendeeName);
        Assert.Null(result.ScannedAt);

        // Verification that ticket was not consumed / marked as used in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();
        var scanRecord = await db.ScannedTickets.FirstOrDefaultAsync(s => s.TicketId == 101 && s.Status == ScannedTicketStatus.CheckedIn);
        Assert.Null(scanRecord);
    }

    [Fact]
    public async Task Validate_NonExistentTicket_ReturnsINVALID()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("NON-EXISTENT-CODE", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ValidateTicketResponse>();
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Equal(TicketValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Validate_WrongEventTicket_ReturnsWRONG_EVENT()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("WRONG-EVENT-TICKET", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ValidateTicketResponse>();
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Equal(TicketValidationStatus.WrongEvent, result.Status);
    }

    [Fact]
    public async Task Validate_CancelledTicket_ReturnsCANCELLED()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("CANCELLED-TICKET", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ValidateTicketResponse>();
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Equal(TicketValidationStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Validate_AlreadyUsedTicket_ReturnsALREADY_USED()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        // First scan to check in VALID-TICKET-DUP
        var scanRes = await client.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-DUP", 10));
        Assert.Equal(HttpStatusCode.OK, scanRes.StatusCode);

        // Now validate the ticket
        var valRes = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("VALID-TICKET-DUP", 10));
        Assert.Equal(HttpStatusCode.OK, valRes.StatusCode);
        var result = await valRes.Content.ReadFromJsonAsync<ValidateTicketResponse>();
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.Equal(TicketValidationStatus.AlreadyUsed, result.Status);
        Assert.NotNull(result.ScannedAt);
    }

    [Fact]
    public async Task Validate_UnauthorizedEventOwner_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        // Organizer ID 999 does not own event 10 (owner is 100)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(999, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("VALID-TICKET-1", 10));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Validate_InvalidRequestBody_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("", 10));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var response2 = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("VALID-TICKET-1", 0));
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
    }

    [Fact]
    public async Task Scan_ValidTicket_SucceedsAndPersistsOutboxAndScannedTicket()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-SCAN", 10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ScanTicketResponse>();
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal("CheckedIn", result.Status);
        Assert.NotNull(result.ScannedTicket);
        Assert.Equal(105, result.ScannedTicket.TicketId);

        // Verify outbox message was persisted
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();
        var outbox = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Topic == "ticket.checked-in");
        Assert.NotNull(outbox);
        Assert.Equal("10", outbox.PartitionKey);
    }

    [Fact]
    public async Task Scan_DuplicateTicket_ReturnsDuplicate()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        // First scan
        await client.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-DUP", 10));

        // Second duplicate scan
        var secondResponse = await client.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-DUP", 10));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var result = await secondResponse.Content.ReadFromJsonAsync<ScanTicketResponse>();
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("Duplicate", result.Status);
    }

    [Fact]
    public async Task Scan_UnauthorizedEventOwner_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        // Organizer ID 999 does not own event 10 (owner is 100)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(999, "Organizer"));

        var response = await client.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-1", 10));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        // Perform scan
        await client.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-STATS", 10));

        var statsResponse = await client.GetAsync("/api/checkin/event/10/stats");
        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);
        var stats = await statsResponse.Content.ReadFromJsonAsync<AttendanceStatsResponse>();
        Assert.NotNull(stats);
        Assert.Equal(10, stats.EventId);
        Assert.True(stats.TotalCheckedIn >= 1);
        Assert.True(stats.TotalScanned >= 1);
    }

    [Fact]
    public async Task GetEventAnalyticsSummary_AuthorizedOwner_ReturnsAccurateSummary()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/analytics/event/10/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<EventAnalyticsSummaryResponse>();
        Assert.NotNull(summary);
        Assert.Equal(10, summary.EventId);
        Assert.Equal("Summer Festival", summary.Title);
        Assert.Equal(50, summary.TotalTicketsSold);
        Assert.Equal(250000.00m, summary.TotalRevenue);
        Assert.Equal("LKR", summary.Currency);
        Assert.Equal(4, summary.TotalRefundedTickets);
        Assert.Equal(20000.00m, summary.TotalRefundedAmount);
        Assert.True(summary.AttendancePercentage >= 0.0);
    }

    [Fact]
    public async Task GetEventAnalyticsSummary_UnauthorizedOwner_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        // User 999 does not own event 10
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(999, "Organizer"));

        var response = await client.GetAsync("/api/checkin/analytics/event/10/summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetEventSalesAnalytics_AuthorizedOwner_ReturnsCorrectSalesAndTierBreakdown()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/analytics/event/10/sales");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sales = await response.Content.ReadFromJsonAsync<EventSalesAnalyticsResponse>();
        Assert.NotNull(sales);
        Assert.Equal(10, sales.EventId);
        Assert.Equal(50, sales.TotalTicketsSold);
        Assert.Equal(250000.00m, sales.TotalRevenue);
        Assert.Equal(20, sales.ConfirmedBookingsCount);
        Assert.Equal(2, sales.CancelledBookingsCount);
        Assert.Equal(4, sales.RefundedTicketsCount);
        Assert.Equal(20000.00m, sales.RefundedAmount);
        Assert.Equal(2, sales.TierSales.Count);
        Assert.Equal("VIP", sales.TierSales[0].TierName);
        Assert.Equal(100000.00m, sales.TierSales[0].Revenue);
        Assert.Equal(10, sales.TierSales[0].SoldQuantity);
    }

    [Fact]
    public async Task GetEventAttendanceAnalytics_AuthorizedOwner_ReturnsCheckInBreakdownAndTimeline()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        var response = await client.GetAsync("/api/checkin/analytics/event/10/attendance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var attendance = await response.Content.ReadFromJsonAsync<EventAttendanceAnalyticsResponse>();
        Assert.NotNull(attendance);
        Assert.Equal(10, attendance.EventId);
        Assert.Equal(50, attendance.TotalTicketsSold);
        Assert.True(attendance.AttendancePercentage >= 0.0);
        Assert.NotNull(attendance.HourlyCheckIns);
        Assert.NotNull(attendance.TierAttendance);
    }

    [Fact]
    public async Task GetEventAttendanceAnalytics_UnauthorizedOwner_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(999, "Organizer"));

        var response = await client.GetAsync("/api/checkin/analytics/event/10/attendance");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Scan_ConcurrentScans_AtomicallyAllowsOnlyOneSuccess()
    {
        var client1 = _factory.CreateClient();
        var client2 = _factory.CreateClient();
        var token = GenerateJwt(100, "Organizer");
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Initiate 2 parallel scan requests for the same ticket
        var task1 = client1.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("CONCURRENT-TICKET", 10));
        var task2 = client2.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("CONCURRENT-TICKET", 10));

        var responses = await Task.WhenAll(task1, task2);
        var results = new List<ScanTicketResponse>();
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var res = await r.Content.ReadFromJsonAsync<ScanTicketResponse>();
            Assert.NotNull(res);
            results.Add(res);
        }

        // Exactly one scan must succeed with "CheckedIn", and the other must be flagged as "Duplicate"
        Assert.Single(results, r => r.Success && r.Status == "CheckedIn");
        Assert.Single(results, r => !r.Success && r.Status == "Duplicate");
    }

    [Fact]
    public async Task Validate_Performance_UnderLowLatencyThreshold()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GenerateJwt(100, "Organizer"));

        // Warmup
        await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("VALID-TICKET-1", 10));

        // Measure batch of validation calls
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            var response = await client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("VALID-TICKET-1", 10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        sw.Stop();

        // 20 requests in test server should comfortably run under 2000ms
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Total execution time was {sw.ElapsedMilliseconds}ms, expected under 2000ms");
    }
}
