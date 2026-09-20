using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Exvo.CatalogService;
using Exvo.CatalogService.Data;
using Exvo.CatalogService.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

public class CatalogFactory : WebApplicationFactory<Program>
{
    public const string Key = "Catalog_Tests_Signing_Key_Only_At_Least_32_Bytes!";
    private readonly string databaseName = Guid.NewGuid().ToString();
    public TestClock Clock { get; } = new();

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
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
                options.TokenValidationParameters.ValidIssuer = "ExvoAuthService";
                options.TokenValidationParameters.ValidAudience = "ExvoPlatform";
            });
            services.RemoveAll<DbContextOptions<CatalogDbContext>>();
            services.RemoveAll<CatalogDbContext>();
            services.AddDbContext<CatalogDbContext>(options => options.UseInMemoryDatabase(databaseName));
        });
    }

    public HttpClient Client(string? id = "11", string role = "Organizer", bool authenticated = true)
    {
        var client = CreateClient();
        if (!authenticated) return client;
        var claims = new List<Claim> { new(ClaimTypes.Role, role), new("name", "Shared company") };
        if (id is not null) claims.Add(new(JwtRegisteredClaimNames.Sub, id));
        var jwt = new JwtSecurityToken("ExvoAuthService", "ExvoPlatform", claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }

    public async Task Seed()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.Categories.Add(new Category { Id = 1, Name = "Concert" });
        db.Categories.Add(new Category { Id = 2, Name = "Festival" });
        db.Events.AddRange(
            new Event { Id = 1, OrganizerId = 11, OrganizerName = "Shared company", Title = "Mine", CategoryId = 1, EventDate = new DateTime(2099, 1, 1), UtcOffsetMinutes = 0 },
            new Event { Id = 2, OrganizerId = 22, OrganizerName = "Shared company", Title = "Other", CategoryId = 1, EventDate = new DateTime(2099, 1, 1), UtcOffsetMinutes = 0 },
            new Event { Id = 3, OrganizerId = 11, Title = "Hidden mine", CategoryId = 1, IsHidder = true, EventDate = new DateTime(2099, 1, 1), UtcOffsetMinutes = 0 });
        await db.SaveChangesAsync();
    }
}

public class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class EventApiTests
{
    private static object Payload(DateTime date, int? offset = 0) => new
    {
        title = "Updated event",
        organizerId = 22,
        eventDate = date,
        utcOffsetMinutes = offset,
        categoryId = 1,
        venue = "Colombo"
    };

    [Fact]
    public async Task OrganizerListsAreScopedByIdIncludingHiddenEventsAndIgnoreQueryIdentity()
    {
        await using var factory = new CatalogFactory();
        using var client = factory.Client();
        await factory.Seed();
        var mine = await client.GetFromJsonAsync<Event[]>("/api/catalog/events/my-events?organizerId=22");
        Assert.Equal(new[] { 1, 3 }, mine!.Select(e => e.Id).Order());
        var visible = await client.GetFromJsonAsync<Event[]>("/api/catalog/events");
        Assert.Equal(new[] { 1, 2 }, visible!.Select(e => e.Id).Order());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/events/2")).StatusCode);
        using var publicClient = factory.Client(authenticated: false);
        Assert.Equal(2, (await publicClient.GetFromJsonAsync<Event[]>("/api/catalog/events"))!.Length);
    }

    [Theory]
    [InlineData("Organizer", true)]
    [InlineData("Attendee", true)]
    [InlineData("Attendee", false)]
    public async Task PublicBrowsingShowsAllPublishedEventsRegardlessOfAccountRole(string role, bool authenticated)
    {
        await using var factory = new CatalogFactory();
        using var client = factory.Client("33", role, authenticated);
        await factory.Seed();
        var events = await client.GetFromJsonAsync<Event[]>("/api/catalog/events");
        Assert.Equal(new[] { 1, 2 }, events!.Select(e => e.Id).Order());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/events/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/catalog/events/3")).StatusCode);
        if (role == "Organizer")
        {
            Assert.Empty((await client.GetFromJsonAsync<Event[]>("/api/catalog/events/my-events"))!);
            var created = await client.PostAsJsonAsync("/api/catalog/events", Payload(new DateTime(2099, 1, 1)));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(3, (await client.GetFromJsonAsync<Event[]>("/api/catalog/events"))!.Length);
            Assert.Equal(33, Assert.Single((await client.GetFromJsonAsync<Event[]>("/api/catalog/events/my-events"))!).OrganizerId);
        }
    }

    [Theory]
    [InlineData(null, "Organizer", false, HttpStatusCode.Unauthorized)]
    [InlineData("11", "Attendee", true, HttpStatusCode.Forbidden)]
    [InlineData(null, "Organizer", true, HttpStatusCode.Forbidden)]
    [InlineData("bad", "Organizer", true, HttpStatusCode.Forbidden)]
    [InlineData("0", "Organizer", true, HttpStatusCode.Forbidden)]
    public async Task InvalidIdentityCannotListOrMutate(string? id, string role, bool authenticated, HttpStatusCode expected)
    {
        await using var factory = new CatalogFactory();
        using var client = factory.Client(id, role, authenticated);
        Assert.Equal(expected, (await client.GetAsync("/api/catalog/events/my-events")).StatusCode);
        Assert.Equal(expected, (await client.PostAsJsonAsync("/api/catalog/events", Payload(DateTime.Today.AddDays(2)))).StatusCode);
        Assert.Equal(expected, (await client.PutAsJsonAsync("/api/catalog/events/1", Payload(DateTime.Today.AddDays(2)))).StatusCode);
        Assert.Equal(expected, (await client.DeleteAsync("/api/catalog/events/1")).StatusCode);
        Assert.Equal(expected, (await client.PatchAsJsonAsync("/api/catalog/events/1/visibility", new { isHidden = true })).StatusCode);
    }

    [Fact]
    public async Task WritesUseAuthenticatedOwnerAndUpdatesPersistLocalDate()
    {
        await using var factory = new CatalogFactory();
        using var client = factory.Client();
        await factory.Seed();
        var date = new DateTime(2099, 8, 12, 23, 45, 0);
        var created = await client.PostAsJsonAsync("/api/catalog/events", Payload(date, 330));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(11, (await created.Content.ReadFromJsonAsync<Event>())!.OrganizerId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/catalog/events/2", Payload(date))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/catalog/events/2")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/catalog/events/1", Payload(date, 330))).StatusCode);
        var updated = await client.GetFromJsonAsync<Event>("/api/catalog/events/1");
        Assert.Equal(date, updated!.EventDate);
        Assert.Equal(330, updated.UtcOffsetMinutes);
        Assert.Equal(11, updated.OrganizerId);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/catalog/events/1")).StatusCode);
    }

    [Fact]
    public async Task CreateAndUpdatePersistArtistAndResolveCategoryName()
    {
        await using var factory = new CatalogFactory();
        using var client = factory.Client();
        await factory.Seed();
        var date = new DateTime(2099, 8, 12, 23, 45, 0);

        var created = await client.PostAsJsonAsync("/api/catalog/events", new
        {
            title = "Festival night",
            eventDate = date,
            utcOffsetMinutes = 330,
            categoryName = "Festival",
            artistOrOrganizer = "The Signal",
            venue = "Colombo"
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdEvent = await created.Content.ReadFromJsonAsync<Event>();
        Assert.Equal("The Signal", createdEvent!.ArtistOrOrganizer);
        Assert.Equal(2, createdEvent.CategoryId);

        var updated = await client.PutAsJsonAsync("/api/catalog/events/1", new
        {
            title = "Mine",
            eventDate = date,
            utcOffsetMinutes = 330,
            categoryName = "Festival",
            artistOrOrganizer = "Updated Artist",
            venue = "Colombo"
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var eventAfterUpdate = await client.GetFromJsonAsync<Event>("/api/catalog/events/1");
        Assert.Equal("Updated Artist", eventAfterUpdate!.ArtistOrOrganizer);
        Assert.Equal(2, eventAfterUpdate.CategoryId);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, null)]
    [InlineData(1, 841)]
    public async Task InvalidSchedulesAreRejectedOnCreateAndUpdate(int days, int? offset)
    {
        await using var factory = new CatalogFactory();
        using var client = factory.Client();
        await factory.Seed();
        var payload = Payload(DateTime.SpecifyKind(DateTime.UtcNow.AddDays(days), DateTimeKind.Unspecified), offset);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/catalog/events", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/catalog/events/1", payload)).StatusCode);
    }

    [Theory]
    [InlineData(330)]
    [InlineData(-420)]
    [InlineData(840)]
    public void ScheduleValidationUsesExplicitOffsetAcrossLocalMidnight(int offset)
    {
        var now = new DateTimeOffset(2026, 9, 20, 23, 59, 30, TimeSpan.Zero);
        var local = now.ToOffset(TimeSpan.FromMinutes(offset)).DateTime;
        Assert.NotNull(EventAccess.ValidateSchedule(local.AddSeconds(-1), offset, now));
        Assert.Null(EventAccess.ValidateSchedule(local.AddMinutes(1), offset, now));
        Assert.NotNull(EventAccess.ValidateSchedule(DateTime.SpecifyKind(local, DateTimeKind.Utc), offset, now));
    }

    [Fact]
    public async Task VisibilityChangeRequiresOwnershipAndDoesNotChangePastSchedule()
    {
        await using var factory = new CatalogFactory();
        using var client = factory.Client();
        await factory.Seed();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var pastDate = DateTime.UtcNow.AddDays(-1);
        (await db.Events.FindAsync(1))!.EventDate = pastDate;
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync("/api/catalog/events/2/visibility", new { isHidden = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync("/api/catalog/events/1/visibility", new { isHidden = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/catalog/events/1")).StatusCode);
        db.ChangeTracker.Clear();
        var mine = await db.Events.FindAsync(1);
        Assert.True(mine!.IsHidden);
        Assert.Equal(pastDate, mine.EventDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(330)]
    [InlineData(-420)]
    public async Task EventsExpireForEveryoneAtTheStoredInstantWithoutRestartOrDeletion(int? offset)
    {
        await using var factory = new CatalogFactory();
        factory.Clock.Now = new DateTimeOffset(2026, 9, 20, 23, 59, 0, TimeSpan.Zero);
        using var owner = factory.Client();
        using var attendee = factory.Client("33", "Attendee");
        using var visitor = factory.Client(authenticated: false);
        await factory.Seed();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var expiry = factory.Clock.Now.AddMinutes(1);
        var evt = (await db.Events.FindAsync(1))!;
        evt.EventDate = expiry.ToOffset(TimeSpan.FromMinutes(offset ?? 330)).DateTime;
        evt.UtcOffsetMinutes = offset;
        await db.SaveChangesAsync();
        foreach (var client in new[] { owner, attendee, visitor })
            Assert.Contains((await client.GetFromJsonAsync<Event[]>("/api/catalog/events?categoryId=1"))!, e => e.Id == 1);

        factory.Clock.Now = expiry;
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/catalog/events/1")).StatusCode);
        factory.Clock.Now = expiry.AddTicks(1);
        foreach (var client in new[] { owner, attendee, visitor })
        {
            Assert.DoesNotContain((await client.GetFromJsonAsync<Event[]>("/api/catalog/events?categoryId=1"))!, e => e.Id == 1);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/catalog/events/1")).StatusCode);
        }
        var expiredOrganizerEvent = Assert.Single(
            (await owner.GetFromJsonAsync<Event[]>("/api/catalog/events/my-events"))!, e => e.Id == 1);
        Assert.True(expiredOrganizerEvent.IsHidden);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync("/api/catalog/events/1/visibility", new { isHidden = false })).StatusCode);
        db.ChangeTracker.Clear();
        var persistedEvent = await db.Events.SingleAsync(e => e.Id == 1);
        Assert.True(persistedEvent.IsHidder);
    }

    [Fact]
    public void ExpiryQueryIsTranslatedByTheProductionMySqlProvider()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseMySql("Server=localhost;Database=test;Uid=test;Pwd=test", new MySqlServerVersion(new Version(8, 0, 30))).Options;
        using var db = new CatalogDbContext(options);
        var sql = EventSchedule.Upcoming(db.Events, DateTime.UtcNow).ToQueryString();
        Assert.Contains("DATE_ADD", sql);
        Assert.Contains("UtcOffsetMinutes", sql);
        Assert.Contains("330", sql);
    }
}
