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
}
