using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Exvo.CheckInService.Models.DTOs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Exvo.CheckInService.Tests;

public class ScanValidationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private const string JwtSecret = "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!";

    public ScanValidationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddHttpClient("CatalogService", c => c.BaseAddress = new Uri("http://localhost:5255"))
                    .ConfigurePrimaryHttpMessageHandler(() => new MockHttpMessageHandler());
                services.AddHttpClient("BookingService", c => c.BaseAddress = new Uri("http://localhost:5063"))
                    .ConfigurePrimaryHttpMessageHandler(() => new MockHttpMessageHandler());
            });
        });
    }

    private HttpClient ClientFor(int userId, string role)
    {
        var key = Encoding.UTF8.GetBytes(JwtSecret);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role),
                new Claim("sub", userId.ToString())
            }),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = "ExvoAuthService",
            Audience = "ExvoPlatform",
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        var handler = new JwtSecurityTokenHandler();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", handler.WriteToken(handler.CreateToken(descriptor)));
        return client;
    }

    [Fact]
    public async Task Validate_CancelledTicket_ReturnsCancelled()
    {
        var response = await ClientFor(100, "Organizer").PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("CANCELLED-TICKET", 10));
        var result = await response.Content.ReadFromJsonAsync<ValidateTicketResponse>();
        Assert.False(result!.IsValid);
        Assert.Equal(TicketValidationStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Validate_TicketOfEndedEvent_ReturnsExpired()
    {
        var response = await ClientFor(1, "Admin").PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("EXPIRED-EVENT-TICKET", 12));
        var result = await response.Content.ReadFromJsonAsync<ValidateTicketResponse>();
        Assert.False(result!.IsValid);
        Assert.Equal(TicketValidationStatus.Expired, result.Status);
    }

    [Fact]
    public async Task Scan_TicketOfEndedEvent_IsRejectedAndNotCheckedIn()
    {
        var response = await ClientFor(1, "Admin").PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("EXPIRED-EVENT-TICKET", 12));
        var result = await response.Content.ReadFromJsonAsync<ScanTicketResponse>();
        Assert.False(result!.Success);
        Assert.Equal("Rejected", result.Status);
    }

    [Fact]
    public async Task Scan_UnknownTicketTwice_BothRejectedWithoutServerError()
    {
        var client = ClientFor(100, "Organizer");
        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("UNKNOWN-CODE", 10));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ScanTicketResponse>();
            Assert.Equal("Invalid", result!.Status);
        }
    }

    [Fact]
    public async Task Scan_UnassignedCoordinator_IsForbidden()
    {
        var response = await ClientFor(555, "Coordinator").PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-1", 10));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Scan_AttendeeRole_IsForbidden()
    {
        var response = await ClientFor(9, "Attendee").PostAsJsonAsync("/api/checkin/scan", new ScanTicketRequest("VALID-TICKET-1", 10));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Performance_ConcurrentValidations_CompleteQuickly()
    {
        var client = ClientFor(100, "Organizer");
        var stopwatch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            client.PostAsJsonAsync("/api/checkin/validate", new ValidateTicketRequest("VALID-TICKET-1", 10))));
        stopwatch.Stop();

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"100 validations took {stopwatch.Elapsed}");
    }
}
