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
    private readonly string databaseName = $"BookingServiceApiTests-{Guid.NewGuid()}";

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
            services.AddDbContext<BookingDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.RemoveAll<IEventOwnershipClient>();
            services.AddScoped<IEventOwnershipClient, TestOwnershipClient>();
            services.RemoveAll<IEventInventoryCatalogClient>();
            services.AddScoped<IEventInventoryCatalogClient, TestInventoryCatalogClient>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
            });
        });
    }

    public HttpClient Client(string id = "11", string role = "Organizer")
    {
        var client = CreateClient();
        var claims = new[] { new Claim(ClaimTypes.Role, role), new Claim(JwtRegisteredClaimNames.Sub, id) };
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
        Task.FromResult((eventId == 1 || eventId == 2) && user.FindFirstValue(ClaimTypes.NameIdentifier) == "11");
}

public sealed class TestInventoryCatalogClient : IEventInventoryCatalogClient
{
    public Task<EventTicketCatalog?> GetTicketCatalogAsync(int eventId, CancellationToken cancellationToken)
    {
        EventTicketCatalog? catalog = eventId switch
        {
            2 => new EventTicketCatalog(2, 10, [
                new CatalogTicketTier(7, "Reserved Seat", 2500m, 6),
                new CatalogTicketTier(8, "General Admission", 1500m, 4)
            ]),
            99 => new EventTicketCatalog(99, 2, [new CatalogTicketTier(7, "General Admission", 2500m, 2)]),
            _ => null
        };
        return Task.FromResult(catalog);
    }
}

public class SeatingPlanApiTests
{
    private static object Request(bool visible = true, int? ticketTierId = null, decimal price = 2500m) => new
    {
        name = "Main hall",
        isVisibleToAttendees = visible,
        sections = new[]
        {
            new { name = "Orchestra", rowCount = 2, seatsPerRow = 3, startingRowLabel = "A", startingSeatNumber = 1, ticketTierId, price, displayOrder = 0 }
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
        Assert.Equal(6, availability.TotalSeatCount);
        Assert.Equal("Available", availability.InventoryStatus);
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

    [Fact]
    public async Task AttendeeCanHoldSeatsAndAnotherAttendeeCannotTakeThem()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await organizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request())).StatusCode);
        using var attendee = factory.Client("42", "Attendee");
        var held = await attendee.PostAsJsonAsync("/api/booking/events/1/seat-holds", new { seatCodes = new[] { "A-01" } });
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        var hold = await held.Content.ReadFromJsonAsync<HoldResponse>();
        Assert.Equal(new[] { "A-01" }, hold!.SeatCodes);
        Assert.Equal(5, (hold.ExpiresAtUtc - DateTime.UtcNow).TotalMinutes, precision: 0);

        using var otherAttendee = factory.Client("43", "Attendee");
        var conflict = await otherAttendee.PostAsJsonAsync("/api/booking/events/1/seat-holds", new { seatCodes = new[] { "A-01" } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await attendee.DeleteAsync($"/api/booking/events/1/seat-holds/{hold.HoldId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherAttendee.PostAsJsonAsync("/api/booking/events/1/seat-holds", new { seatCodes = new[] { "A-01" } })).StatusCode);
    }

    [Fact]
    public async Task AttendeeCanConfirmHoldAndSeatBecomesBooked()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await organizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request())).StatusCode);
        using var attendee = factory.Client("42", "Attendee");
        var hold = await (await attendee.PostAsJsonAsync("/api/booking/events/1/seat-holds", new { seatCodes = new[] { "A-01" } }))
            .Content.ReadFromJsonAsync<HoldResponse>();

        var response = await attendee.PostAsync($"/api/booking/events/1/seat-holds/{hold!.HoldId}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var confirmation = await response.Content.ReadFromJsonAsync<BookingConfirmationResponse>();
        Assert.StartsWith("EXVO-", confirmation!.BookingReference);
        Assert.Equal(new[] { "A-01" }, confirmation.SeatCodes);

        var plan = await attendee.GetFromJsonAsync<PlanResponse>("/api/booking/events/1/seating-plan");
        Assert.Equal("Booked", plan!.Sections.Single().Seats.Single(seat => seat.SeatCode == "A-01").Status);
    }

    [Fact]
    public async Task OrganizerCanEditPlanAfterSeatIsBookedWithoutDeletingTicketSeat()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await organizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request())).StatusCode);
        using var attendee = factory.Client("42", "Attendee");
        var hold = await (await attendee.PostAsJsonAsync("/api/booking/events/1/seat-holds", new { seatCodes = new[] { "A-01" } }))
            .Content.ReadFromJsonAsync<HoldResponse>();
        Assert.Equal(HttpStatusCode.OK, (await attendee.PostAsync($"/api/booking/events/1/seat-holds/{hold!.HoldId}/confirm", null)).StatusCode);

        var edited = await organizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", new
        {
            name = "Revised hall",
            isVisibleToAttendees = true,
            sections = new[]
            {
                new { name = "Balcony", rowCount = 1, seatsPerRow = 2, startingRowLabel = "C", startingSeatNumber = 1, ticketTierId = (int?)null, price = 3000m, displayOrder = 0 }
            }
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var plan = await attendee.GetFromJsonAsync<PlanResponse>("/api/booking/events/1/seating-plan");
        var bookedSeat = plan!.Sections.SelectMany(section => section.Seats).Single(seat => seat.SeatCode == "A-01");
        Assert.False(bookedSeat.IsEnabled);
        Assert.Equal("Booked", bookedSeat.Status);
        Assert.Contains(plan.Sections.SelectMany(section => section.Seats), seat => seat.SeatCode == "C-01" && seat.IsEnabled);

        var mine = await attendee.GetFromJsonAsync<List<AttendeeBookingResponse>>("/api/booking/my-tickets");
        Assert.NotNull(mine);
        Assert.Single(mine);
        Assert.Single(mine[0].Tickets);
        Assert.Equal("A-01", mine[0].Tickets[0].SeatCode);
        Assert.True(mine[0].Tickets[0].HasSeat);
    }

    [Fact]
    public async Task AvailabilityDistinguishesTemporaryHoldsFromSoldOut()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await organizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request())).StatusCode);
        using var attendee = factory.Client("42", "Attendee");
        var held = await attendee.PostAsJsonAsync("/api/booking/events/1/seat-holds", new { seatCodes = new[] { "A-01", "A-02", "A-03", "B-01", "B-02", "B-03" } });
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);

        using var otherAttendee = factory.Client("43", "Attendee");
        var availability = await otherAttendee.GetFromJsonAsync<AvailabilityResponse>("/api/booking/events/1/availability");
        Assert.Equal(0, availability!.AvailableSeatCount);
        Assert.Equal(6, availability.HeldSeatCount);
        Assert.Equal("TemporarilyHeld", availability.InventoryStatus);
    }

    [Fact]
    public async Task SeatedTierCanBeUnavailableWhileCatalogOnlyTierRemainsAvailable()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await organizer.PostAsJsonAsync("/api/booking/events/2/seating-plan", Request(ticketTierId: 7))).StatusCode);
        using var attendee = factory.Client("42", "Attendee");
        var held = await attendee.PostAsJsonAsync("/api/booking/events/2/seat-holds", new { seatCodes = new[] { "A-01", "A-02", "A-03", "B-01", "B-02", "B-03" } });
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);

        using var otherAttendee = factory.Client("43", "Attendee");
        var availability = await otherAttendee.GetFromJsonAsync<AvailabilityResponse>("/api/booking/events/2/availability");
        Assert.Equal("Available", availability!.InventoryStatus);
        Assert.Equal(4, availability.AvailableSeatCount);
        Assert.Equal(10, availability.TotalSeatCount);
        Assert.Equal(0, availability.Tiers.Single(tier => tier.TicketTierId == 7).AvailableQuantity);
        Assert.Equal(6, availability.Tiers.Single(tier => tier.TicketTierId == 7).HeldQuantity);
        Assert.Equal(4, availability.Tiers.Single(tier => tier.TicketTierId == 8).AvailableQuantity);
    }

    [Fact]
    public async Task ConfirmingHoldCanIncludeGeneralTicketsInSameBooking()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await organizer.PostAsJsonAsync("/api/booking/events/2/seating-plan", Request(ticketTierId: 7))).StatusCode);
        using var attendee = factory.Client("42", "Attendee");
        var hold = await (await attendee.PostAsJsonAsync("/api/booking/events/2/seat-holds", new { seatCodes = new[] { "A-01" } }))
            .Content.ReadFromJsonAsync<HoldResponse>();

        var response = await attendee.PostAsJsonAsync($"/api/booking/events/2/seat-holds/{hold!.HoldId}/confirm", new
        {
            tickets = new[]
            {
                new { ticketTierId = (int?)8, name = "General Admission", unitPrice = 1500m, quantity = 2 }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var confirmation = await response.Content.ReadFromJsonAsync<BookingConfirmationResponse>();
        Assert.Equal(3, confirmation!.SeatCodes.Length);
        Assert.Contains(confirmation.Tickets, ticket => ticket.TicketTierId == 8 && ticket.Quantity == 2);

        var mine = await attendee.GetFromJsonAsync<List<AttendeeBookingResponse>>("/api/booking/my-tickets");
        Assert.Single(mine!);
        Assert.Equal(3, mine![0].Tickets.Count);
        Assert.Single(mine[0].Tickets.Where(ticket => ticket.HasSeat));
        Assert.Equal(2, mine[0].Tickets.Count(ticket => !ticket.HasSeat && ticket.SectionName == "General Admission"));
    }

    [Fact]
    public async Task AttendeeCanListOnlyTheirConfirmedTickets()
    {
        await using var factory = new BookingFactory();
        using var organizer = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await organizer.PostAsJsonAsync("/api/booking/events/1/seating-plan", Request())).StatusCode);
        using var attendee = factory.Client("42", "Attendee");
        var hold = await (await attendee.PostAsJsonAsync("/api/booking/events/1/seat-holds", new { seatCodes = new[] { "A-01", "A-02" } }))
            .Content.ReadFromJsonAsync<HoldResponse>();
        Assert.Equal(HttpStatusCode.OK, (await attendee.PostAsync($"/api/booking/events/1/seat-holds/{hold!.HoldId}/confirm", null)).StatusCode);

        var mine = await attendee.GetFromJsonAsync<List<AttendeeBookingResponse>>("/api/booking/my-tickets");
        Assert.Single(mine!);
        Assert.Equal(2, mine![0].Tickets.Count);
        Assert.All(mine[0].Tickets, ticket => Assert.StartsWith(mine[0].BookingReference, ticket.TicketCode));
        Assert.Equal("Orchestra", mine[0].Tickets[0].SectionName);

        using var otherAttendee = factory.Client("43", "Attendee");
        var theirs = await otherAttendee.GetFromJsonAsync<List<AttendeeBookingResponse>>("/api/booking/my-tickets");
        Assert.Empty(theirs!);
    }

    [Fact]
    public async Task AttendeeCanConfirmGeneralAdmissionTicketsWithoutSeats()
    {
        await using var factory = new BookingFactory();
        using var attendee = factory.Client("42", "Attendee");

        var response = await attendee.PostAsJsonAsync("/api/booking/events/99/general-bookings", new
        {
            tickets = new[]
            {
                new { ticketTierId = (int?)7, name = "General Admission", unitPrice = 2500m, quantity = 2 }
            }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var confirmation = await response.Content.ReadFromJsonAsync<BookingConfirmationResponse>();
        Assert.StartsWith("EXVO-", confirmation!.BookingReference);
        Assert.Equal(2, confirmation.SeatCodes.Length);
        Assert.Single(confirmation.Tickets);
        Assert.Equal("General Admission", confirmation.Tickets[0].Name);
        Assert.Equal(2, confirmation.Tickets[0].Quantity);

        var mine = await attendee.GetFromJsonAsync<List<AttendeeBookingResponse>>("/api/booking/my-tickets");
        Assert.Single(mine!);
        Assert.Equal(2, mine![0].Tickets.Count);
        Assert.All(mine[0].Tickets, ticket =>
        {
            Assert.Equal("GA", ticket.RowLabel);
            Assert.Equal("General Admission", ticket.SectionName);
            Assert.Equal(2500m, ticket.Price);
        });
    }

    [Fact]
    public async Task GeneralAdmissionTicketsCannotBeOverbooked()
    {
        await using var factory = new BookingFactory();
        using var attendee = factory.Client("42", "Attendee");
        var first = await attendee.PostAsJsonAsync("/api/booking/events/99/general-bookings", new
        {
            tickets = new[]
            {
                new { ticketTierId = (int?)7, name = "General Admission", unitPrice = 2500m, quantity = 2 }
            }
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var otherAttendee = factory.Client("43", "Attendee");
        var second = await otherAttendee.PostAsJsonAsync("/api/booking/events/99/general-bookings", new
        {
            tickets = new[]
            {
                new { ticketTierId = (int?)7, name = "General Admission", unitPrice = 2500m, quantity = 1 }
            }
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var availability = await otherAttendee.GetFromJsonAsync<AvailabilityResponse>("/api/booking/events/99/availability");
        Assert.Equal(0, availability!.AvailableSeatCount);
        Assert.Equal(2, availability.BookedSeatCount);
        Assert.Equal("SoldOut", availability.InventoryStatus);
    }

    private sealed record PlanResponse(int Id, int EventId, string Name, bool IsVisibleToAttendees, string Status, int Version, List<SectionResponse> Sections);
    private sealed record SectionResponse(int Id, string Name, int RowCount, int SeatsPerRow, string StartingRowLabel, int StartingSeatNumber, int? TicketTierId, decimal Price, int DisplayOrder, List<SeatResponse> Seats);
    private sealed record SeatResponse(string SeatCode, string RowLabel, int SeatNumber, int? TicketTierId, decimal Price, bool IsEnabled, string Status);
    private sealed record AvailabilityResponse(int EventId, int AvailableSeatCount, int TotalSeatCount, int HeldSeatCount, int BookedSeatCount, string InventoryStatus, List<TierAvailabilityResponse> Tiers);
    private sealed record TierAvailabilityResponse(int? TicketTierId, decimal Price, int AvailableQuantity, int TotalQuantity, int HeldQuantity, int BookedQuantity);
    private sealed record HoldResponse(int HoldId, int EventId, string[] SeatCodes, DateTime ExpiresAtUtc);
    private sealed record BookingConfirmationResponse(int BookingId, string BookingReference, int EventId, decimal TotalAmount, string Currency, string[] SeatCodes, List<ConfirmedTicketSelectionResponse> Tickets);
    private sealed record ConfirmedTicketSelectionResponse(int? TicketTierId, string Name, decimal UnitPrice, int Quantity);
    private sealed record AttendeeBookingResponse(int BookingId, string BookingReference, int EventId, string Status, decimal TotalAmount, string Currency, DateTime CreatedAtUtc, DateTime? ConfirmedAtUtc, List<AttendeeTicketResponse> Tickets);
    private sealed record AttendeeTicketResponse(int BookingItemId, string TicketCode, string SeatCode, string RowLabel, int SeatNumber, string SectionName, int? TicketTierId, decimal Price, bool HasSeat);
}
