using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Exvo.CheckInService.Data;
using Exvo.CheckInService.Models;
using Exvo.CheckInService.Models.DTOs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Exvo.CheckInService.Tests;

public class MockCatalogHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.ToString() ?? string.Empty;

        if (uri.Contains("/api/catalog/events/1/ticket-snapshot"))
        {
            var json = """
            {
                "id": 1,
                "title": "Tech Conference 2026",
                "description": "Tech event",
                "location": "Colombo",
                "venue": "Hall A",
                "price": 50.0,
                "eventDate": "2026-11-15T09:00:00Z",
                "utcOffsetMinutes": 330,
                "categoryId": 1,
                "organizerId": 11,
                "organizerName": "TechOrg",
                "imageUrl": "http://img.jpg",
                "availableTickets": 100,
                "ticketTiersJson": "[]",
                "createdAt": "2026-01-01T00:00:00Z",
                "isHidder": false
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/catalog/events/2/ticket-snapshot"))
        {
            var json = """
            {
                "id": 2,
                "title": "Music Fest 2026",
                "description": "Music fest",
                "location": "Kandy",
                "venue": "Stadium",
                "price": 25.0,
                "eventDate": "2026-12-01T18:00:00Z",
                "utcOffsetMinutes": 330,
                "categoryId": 2,
                "organizerId": 11,
                "organizerName": "TechOrg",
                "imageUrl": "http://img2.jpg",
                "availableTickets": 200,
                "ticketTiersJson": "[]",
                "createdAt": "2026-01-01T00:00:00Z",
                "isHidder": false
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        if (uri.Contains("/api/catalog/events/99/ticket-snapshot"))
        {
            var json = """
            {
                "id": 99,
                "title": "Other Org Event",
                "description": "Other event",
                "location": "Galle",
                "venue": "Fort",
                "price": 10.0,
                "eventDate": "2026-12-10T18:00:00Z",
                "utcOffsetMinutes": 330,
                "categoryId": 1,
                "organizerId": 999,
                "organizerName": "OtherOrg",
                "imageUrl": "http://img3.jpg",
                "availableTickets": 50,
                "ticketTiersJson": "[]",
                "createdAt": "2026-01-01T00:00:00Z",
                "isHidder": false
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

public sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public MutableTimeProvider(DateTimeOffset now) => Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class CheckInFactory : WebApplicationFactory<Program>
{
    public const string Key = "CheckIn_Tests_Signing_Key_Only_At_Least_32_Bytes!";
    private readonly string _databaseName = Guid.NewGuid().ToString();

    // Default: 2026-11-15 09:30 Sri Lanka time (+05:30) = event 1's day.
    public static readonly DateTimeOffset DefaultNow = new(2026, 11, 15, 4, 0, 0, TimeSpan.Zero);
    public MutableTimeProvider Clock { get; } = new(DefaultNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = Key,
            ["Jwt:Issuer"] = "ExvoAuthService",
            ["Jwt:Audience"] = "ExvoPlatform"
        }));

        builder.ConfigureServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
                options.TokenValidationParameters.ValidIssuer = "ExvoAuthService";
                options.TokenValidationParameters.ValidAudience = "ExvoPlatform";
            });

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<DbContextOptions<CheckInDbContext>>();
            services.RemoveAll<CheckInDbContext>();
            services.AddDbContext<CheckInDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            services.AddHttpClient("CatalogService", client =>
            {
                client.BaseAddress = new Uri("http://localhost:5255");
            }).ConfigurePrimaryHttpMessageHandler(() => new MockCatalogHandler());
        });
    }

    public HttpClient CreateUserClient(string? id = "50", string role = "Coordinator", bool authenticated = true)
    {
        var client = CreateClient();
        if (!authenticated) return client;

        var claims = new List<Claim> { new(ClaimTypes.Role, role), new("role", role) };
        if (id is not null)
        {
            claims.Add(new(ClaimTypes.NameIdentifier, id));
            claims.Add(new(JwtRegisteredClaimNames.Sub, id));
        }

        var jwt = new JwtSecurityToken(
            "ExvoAuthService",
            "ExvoPlatform",
            claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
                SecurityAlgorithms.HmacSha256));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }

    public async Task SeedAssignmentsIfNeededAsync()
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();
            if (await db.CoordinatorAssignments.AnyAsync(a => a.Id == 1)) return;
        }
        await SeedAssignmentsAsync();
    }

    public async Task SeedAssignmentsAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();

        // Coordinator 50 is assigned to Event 1 (Active)
        db.CoordinatorAssignments.Add(new CoordinatorAssignment
        {
            Id = 1,
            CoordinatorUserId = 50,
            EventId = 1,
            Status = AssignmentStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        });

        // Coordinator 50 has a revoked assignment for Event 2
        db.CoordinatorAssignments.Add(new CoordinatorAssignment
        {
            Id = 2,
            CoordinatorUserId = 50,
            EventId = 2,
            Status = AssignmentStatus.Revoked,
            CreatedAtUtc = DateTime.UtcNow
        });

        // Coordinator 60 is assigned to Event 2 (Active)
        db.CoordinatorAssignments.Add(new CoordinatorAssignment
        {
            Id = 3,
            CoordinatorUserId = 60,
            EventId = 2,
            Status = AssignmentStatus.Active,
            CreatedAtUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync();
    }
}

public class CoordinatorApiTests : IClassFixture<CheckInFactory>
{
    private readonly CheckInFactory _factory;

    public CoordinatorApiTests(CheckInFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAssignedEvents_Unauthenticated_Returns401()
    {
        var client = _factory.CreateUserClient(authenticated: false);

        var response = await client.GetAsync("/api/checkin/coordinator/events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAssignedEvents_NonCoordinatorRole_Returns403()
    {
        var attendeeClient = _factory.CreateUserClient(id: "10", role: "Attendee");
        var organizerClient = _factory.CreateUserClient(id: "11", role: "Organizer");

        var response1 = await attendeeClient.GetAsync("/api/checkin/coordinator/events");
        var response2 = await organizerClient.GetAsync("/api/checkin/coordinator/events");

        Assert.Equal(HttpStatusCode.Forbidden, response1.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response2.StatusCode);
    }

    [Fact]
    public async Task GetAssignedEvents_CoordinatorWithAssignments_ReturnsActiveEvents()
    {
        await _factory.SeedAssignmentsIfNeededAsync();
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");

        var response = await client.GetAsync("/api/checkin/coordinator/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<AssignedEventDto>>();
        Assert.NotNull(events);
        Assert.Single(events);
        Assert.Equal(1, events[0].Id);
        Assert.Equal("Tech Conference 2026", events[0].Title);
        Assert.Equal("Hall A", events[0].Venue);
    }

    [Fact]
    public async Task GetAssignedEvents_DirectPath_ReturnsSuccess()
    {
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");

        var response = await client.GetAsync("/api/coordinator/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAssignedEvents_CoordinatorWithNoAssignments_ReturnsEmptyList()
    {
        var client = _factory.CreateUserClient(id: "999", role: "Coordinator");

        var response = await client.GetAsync("/api/checkin/coordinator/events");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await response.Content.ReadFromJsonAsync<List<AssignedEventDto>>();
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    [Fact]
    public async Task GetEventAccess_AssignedCoordinator_Returns200WithAccessGranted()
    {
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");

        var response = await client.GetAsync("/api/checkin/coordinator/events/1/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ScannerAccessDto>();
        Assert.NotNull(result);
        Assert.Equal(1, result.EventId);
        Assert.True(result.CanAccessScanner);
        Assert.NotNull(result.Event);
        Assert.Equal("Tech Conference 2026", result.Event.Title);
    }

    [Fact]
    public async Task GetEventAccess_DirectPath_Returns200WithAccessGranted()
    {
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");

        var response = await client.GetAsync("/api/coordinator/events/1/access");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ScannerAccessDto>();
        Assert.NotNull(result);
        Assert.True(result.CanAccessScanner);
    }

    [Fact]
    public async Task GetEventAccess_UnassignedEvent_Returns403WithAccessDenied_IdorProtection()
    {
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");

        // Event 99 exists in catalog but coordinator 50 is not assigned to it
        var response = await client.GetAsync("/api/checkin/coordinator/events/99/access");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(error);
        Assert.Equal("Access denied.", error.Message);
    }

    [Fact]
    public async Task GetEventAccess_RevokedAssignment_Returns403WithAccessDenied()
    {
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");

        // Coordinator 50 assignment to Event 2 is Revoked
        var response = await client.GetAsync("/api/checkin/coordinator/events/2/access");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.NotNull(error);
        Assert.Equal("Access denied.", error.Message);
    }

    [Fact]
    public async Task Organizer_CanAssignCoordinator_AndCoordinatorGainsAccess()
    {
        var organizerClient = _factory.CreateUserClient(id: "11", role: "Organizer");
        var newCoordId = 77;

        // Assign coordinator 77 to event 1
        var assignResponse = await organizerClient.PostAsJsonAsync("/api/checkin/assignments",
            new AssignCoordinatorRequest(newCoordId, 1));

        Assert.True(assignResponse.StatusCode == HttpStatusCode.Created || assignResponse.StatusCode == HttpStatusCode.OK);

        // Verify coordinator 77 now has access
        var coordClient = _factory.CreateUserClient(id: newCoordId.ToString(), role: "Coordinator");
        var accessResponse = await coordClient.GetAsync("/api/checkin/coordinator/events/1/access");

        Assert.Equal(HttpStatusCode.OK, accessResponse.StatusCode);
    }

    [Fact]
    public async Task Organizer_CannotAssignCoordinatorToOtherOrganizersEvent_Returns403()
    {
        var organizerClient = _factory.CreateUserClient(id: "11", role: "Organizer");

        // Event 99 belongs to organizer 999, not 11
        var response = await organizerClient.PostAsJsonAsync("/api/checkin/assignments",
            new AssignCoordinatorRequest(88, 99));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NonOrganizer_CannotManageAssignments_Returns403()
    {
        var coordClient = _factory.CreateUserClient(id: "50", role: "Coordinator");

        var response = await coordClient.PostAsJsonAsync("/api/checkin/assignments",
            new AssignCoordinatorRequest(50, 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Organizer_CanRevokeAssignment_AndCoordinatorLosesAccess()
    {
        var organizerClient = _factory.CreateUserClient(id: "11", role: "Organizer");
        var coordClient = _factory.CreateUserClient(id: "60", role: "Coordinator");

        // Move clock to Event 2's day (2026-12-01 local)
        _factory.Clock.Now = new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero);
        try
        {
            // Coordinator 60 initially has access to Event 2
            var initialAccess = await coordClient.GetAsync("/api/checkin/coordinator/events/2/access");
            Assert.Equal(HttpStatusCode.OK, initialAccess.StatusCode);

            // Revoke assignment 3 (Coordinator 60, Event 2)
            var revokeResponse = await organizerClient.DeleteAsync("/api/checkin/assignments/3");
            Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

            // Verify access is now denied
            var revokedAccess = await coordClient.GetAsync("/api/checkin/coordinator/events/2/access");
            Assert.Equal(HttpStatusCode.Forbidden, revokedAccess.StatusCode);
        }
        finally
        {
            _factory.Clock.Now = CheckInFactory.DefaultNow;
        }
    }

    [Fact]
    public async Task EventDay_ListMarksEventAsToday_AndScannerCanLaunch()
    {
        await _factory.SeedAssignmentsIfNeededAsync();
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");

        var events = await client.GetFromJsonAsync<List<AssignedEventDto>>("/api/checkin/coordinator/events");
        var ev = Assert.Single(events!, e => e.Id == 1);
        Assert.Equal(ScannerEventCategory.Today, ev.ScannerCategory);
        Assert.True(ev.CanLaunchScanner);

        var access = await client.GetAsync("/api/checkin/coordinator/events/1/access");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
    }

    [Fact]
    public async Task BeforeEventDay_EventIsUpcoming_AndScannerLaunchDenied()
    {
        await _factory.SeedAssignmentsIfNeededAsync();
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");
        _factory.Clock.Now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero); // Nov 14 17:30 local
        try
        {
            var events = await client.GetFromJsonAsync<List<AssignedEventDto>>("/api/checkin/coordinator/events");
            var ev = Assert.Single(events!, e => e.Id == 1);
            Assert.Equal(ScannerEventCategory.Upcoming, ev.ScannerCategory);
            Assert.False(ev.CanLaunchScanner);

            var access = await client.GetAsync("/api/coordinator/events/1/access");
            Assert.Equal(HttpStatusCode.Forbidden, access.StatusCode);
            var error = await access.Content.ReadFromJsonAsync<ErrorDto>();
            Assert.Equal("The scanner is only available on the event day.", error!.Message);
        }
        finally
        {
            _factory.Clock.Now = CheckInFactory.DefaultNow;
        }
    }

    [Fact]
    public async Task AfterEventDay_EventIsPast_AndScannerLaunchDenied()
    {
        await _factory.SeedAssignmentsIfNeededAsync();
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");
        _factory.Clock.Now = new DateTimeOffset(2026, 11, 15, 19, 0, 0, TimeSpan.Zero); // Nov 16 00:30 local
        try
        {
            var events = await client.GetFromJsonAsync<List<AssignedEventDto>>("/api/checkin/coordinator/events");
            var ev = Assert.Single(events!, e => e.Id == 1);
            Assert.Equal(ScannerEventCategory.Past, ev.ScannerCategory);
            Assert.False(ev.CanLaunchScanner);

            var access = await client.GetAsync("/api/checkin/coordinator/events/1/access");
            Assert.Equal(HttpStatusCode.Forbidden, access.StatusCode);
            var error = await access.Content.ReadFromJsonAsync<ErrorDto>();
            Assert.Equal("This event has ended. The scanner is no longer available.", error!.Message);
        }
        finally
        {
            _factory.Clock.Now = CheckInFactory.DefaultNow;
        }
    }

    [Fact]
    public async Task EventDay_UsesVenueLocalDate_NotUtcDate()
    {
        await _factory.SeedAssignmentsIfNeededAsync();
        var client = _factory.CreateUserClient(id: "50", role: "Coordinator");
        // 2026-11-14 19:00 UTC is already 2026-11-15 00:30 in Sri Lanka (+05:30)
        _factory.Clock.Now = new DateTimeOffset(2026, 11, 14, 19, 0, 0, TimeSpan.Zero);
        try
        {
            var access = await client.GetAsync("/api/checkin/coordinator/events/1/access");
            Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        }
        finally
        {
            _factory.Clock.Now = CheckInFactory.DefaultNow;
        }
    }
}
