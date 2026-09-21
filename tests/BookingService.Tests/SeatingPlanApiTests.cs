using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Exvo.BookingService.Data;
using Exvo.BookingService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

public sealed class BookingFactory : WebApplicationFactory<Program>
{
    public const string Key = "Booking_Tests_Signing_Key_Only_At_Least_32_Bytes!";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var contentRoot = Environment.GetEnvironmentVariable("EXVO_BOOKING_TEST_CONTENT_ROOT")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/services/BookingService"));
        if (!Directory.Exists(contentRoot))
        {
            contentRoot = Path.GetFullPath("src/services/BookingService");
        }
        builder.UseContentRoot(contentRoot);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BookingDbContext>>();
            services.RemoveAll<BookingDbContext>();
            services.AddDbContext<BookingDbContext>(options => options.UseInMemoryDatabase("BookingServiceApiTests"));
            services.RemoveAll<IEventOwnershipClient>();
            services.AddScoped<IEventOwnershipClient, TestOwnershipClient>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
            });
        });
    }

    public HttpClient Client(string id = "11")
    {
        var client = CreateClient();
        var claims = new[] { new Claim(ClaimTypes.Role, "Organizer"), new Claim(JwtRegisteredClaimNames.Sub, id) };
        var token = new JwtSecurityToken("ExvoAuthService", "ExvoPlatform", claims,
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }
}

public sealed class TestOwnershipClient : IEventOwnershipClient
{
    public Task<bool> IsOwnerAsync(int eventId, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        Task.FromResult(eventId == 1 && user.FindFirstValue(ClaimTypes.NameIdentifier) == "11");
}

public class SeatingPlanApiTests
{
    private static object Request(bool visible = true) => new
    {
        name = "Main hall",
        isVisibleToAttendees = visible,
        sections = new[]
        {
            new { name = "Orchestra", rowCount = 2, seatsPerRow = 3, startingRowLabel = "A", startingSeatNumber = 1, ticketTierId = (int?)null, price = 2500m, displayOrder = 0 }
        }
    };

    [Fact]
    public async Task SavingAPlanGeneratesEverySeatAndAttendeesCanReadPublishedData()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        var saved = await organizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var plan = await saved.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.Equal(6, plan!.Sections.Single().Seats.Count);
        Assert.Equal("A-01", plan.Sections.Single().Seats[0].SeatCode);

        using var attendee = factory.CreateClient();
        var response = await attendee.GetAsync("/api/booking/events/1/seating-plan");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6, (await response.Content.ReadFromJsonAsync<PlanResponse>())!.Sections.Single().Seats.Count);
        var availability = await attendee.GetFromJsonAsync<AvailabilityResponse>("/api/booking/events/1/availability");
        Assert.Equal(6, availability!.AvailableSeatCount);
    }

    [Fact]
    public async Task HiddenPlansAreUnavailableAndNonOwnersCannotSave()
    {
        await using var factory = new BookingFactory();
        using var owner = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request(false))).StatusCode);
        using var attendee = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await attendee.GetAsync("/api/booking/events/1/seating-plan")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await attendee.GetAsync("/api/booking/events/1/availability")).StatusCode);
        using var otherOrganizer = factory.Client("22");
        Assert.Equal(HttpStatusCode.NotFound, (await otherOrganizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request())).StatusCode);
    }

    private sealed record PlanResponse(int Id, int EventId, string Name, bool IsVisibleToAttendees, string Status, int Version, List<SectionResponse> Sections);
    private sealed record SectionResponse(int Id, string Name, int RowCount, int SeatsPerRow, string StartingRowLabel, int StartingSeatNumber, int? TicketTierId, decimal Price, int DisplayOrder, List<SeatResponse> Seats);
    private sealed record SeatResponse(string SeatCode, string RowLabel, int SeatNumber, int? TicketTierId, decimal Price, bool IsEnabled, string Status);
    private sealed record AvailabilityResponse(int EventId, int AvailableSeatCount, List<TierAvailabilityResponse> Tiers);
    private sealed record TierAvailabilityResponse(int? TicketTierId, decimal Price, int AvailableQuantity);
}
