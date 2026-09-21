using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Exvo.BookingService.Contracts;
using Exvo.BookingService.Data;
using Exvo.BookingService.Models;
using Exvo.BookingService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["EXVO_BOOKING_MYSQL_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost;Port=3306;Database=exvo_booking_db;Uid=root;Pwd=;SslMode=None;AllowPublicKeyRetrieval=True;";

builder.Services.AddDbContext<BookingDbContext>(options => options.UseMySql(
    connectionString, new MySqlServerVersion(new Version(8, 0, 30)), mysql => mysql.EnableRetryOnFailure()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("CatalogService", client =>
    client.BaseAddress = new Uri(builder.Configuration["CatalogService:BaseUrl"] ?? "http://localhost:5255"));
builder.Services.AddScoped<IEventOwnershipClient, EventOwnershipClient>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "ExvoAuthService",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "ExvoPlatform",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            builder.Configuration["Jwt:Key"] ?? "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!"))
    };
});
builder.Services.AddAuthorization(options => options.AddPolicy("Organizer",
    policy => policy.RequireAuthenticatedUser().RequireRole("Organizer", "Company")));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
    db.Database.GetService<IMigrator>().Migrate("20260920160843_InitialBookingSchema");

    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { service = "BookingService", status = "healthy" })).AllowAnonymous();

app.MapPost("/api/booking/events/{eventId:int}/seating-plan", SavePlan)
    .RequireAuthorization("Organizer");
app.MapPut("/api/booking/events/{eventId:int}/seating-plan", SavePlan)
    .RequireAuthorization("Organizer");
app.MapPatch("/api/booking/events/{eventId:int}/seating-plan/visibility", async (int eventId, VisibilityRequest request, ClaimsPrincipal user, BookingDbContext db, IEventOwnershipClient ownership, CancellationToken ct) =>
{
    if (!await ownership.IsOwnerAsync(eventId, user, ct)) return Results.NotFound();
    var plan = await db.SeatingPlans.SingleOrDefaultAsync(item => item.EventId == eventId, ct);
    if (plan is null) return Results.NotFound();
    plan.IsVisibleToAttendees = request.IsVisibleToAttendees;
    plan.Status = request.IsVisibleToAttendees ? SeatingPlanStatus.Published : SeatingPlanStatus.Draft;
    plan.UpdatedAtUtc = DateTime.UtcNow;
    plan.Version++;
    await db.SaveChangesAsync(ct);
    return Results.Ok(ToResponse(plan));
}).RequireAuthorization("Organizer");
app.MapGet("/api/booking/events/{eventId:int}/seating-plan", async (int eventId, BookingDbContext db, CancellationToken ct) =>
{
    var plan = await db.SeatingPlans.AsNoTracking().Include(item => item.Sections).Include(item => item.Seats)
        .SingleOrDefaultAsync(item => item.EventId == eventId && item.IsVisibleToAttendees && item.Status == SeatingPlanStatus.Published, ct);
    return plan is null ? Results.NotFound(new { message = "Seating plan is unavailable." }) : Results.Ok(ToResponse(plan));
});
app.MapGet("/api/booking/events/{eventId:int}/availability", async (int eventId, BookingDbContext db, CancellationToken ct) =>
{
    var planIsVisible = await db.SeatingPlans.AsNoTracking()
        .AnyAsync(plan => plan.EventId == eventId && plan.IsVisibleToAttendees && plan.Status == SeatingPlanStatus.Published, ct);
    if (!planIsVisible) return Results.NotFound(new { message = "Event availability is unavailable." });

    var seats = await db.Seats.AsNoTracking()
        .Where(seat => seat.SeatingPlan.EventId == eventId && seat.IsEnabled && seat.Status == SeatStatus.Available)
        .Select(seat => new { seat.TicketTierId, seat.Price })
        .ToListAsync(ct);
    return Results.Ok(new SeatingAvailabilityResponse(eventId, seats.Count, seats.GroupBy(seat => new { seat.TicketTierId, seat.Price })
        .Select(group => new TierAvailabilityResponse(group.Key.TicketTierId, group.Key.Price, group.Count())).ToList()));
});
app.MapGet("/api/booking/organizer/events/{eventId:int}/seating-plan", async (int eventId, ClaimsPrincipal user, BookingDbContext db, IEventOwnershipClient ownership, CancellationToken ct) =>
{
    if (!await ownership.IsOwnerAsync(eventId, user, ct)) return Results.NotFound();
    var plan = await db.SeatingPlans.AsNoTracking().Include(item => item.Sections).Include(item => item.Seats)
        .SingleOrDefaultAsync(item => item.EventId == eventId, ct);
    return plan is null ? Results.NotFound() : Results.Ok(ToResponse(plan));
}).RequireAuthorization("Organizer");

app.Run();

static async Task<IResult> SavePlan(int eventId, SeatingPlanRequest request, ClaimsPrincipal user, BookingDbContext db, IEventOwnershipClient ownership, CancellationToken ct)
{
    if (!await ownership.IsOwnerAsync(eventId, user, ct)) return Results.NotFound();
    if (request.Sections is null || request.Sections.Count == 0 || request.Sections.Any(section => section.RowCount < 1 || section.SeatsPerRow < 1 || string.IsNullOrWhiteSpace(section.Name)))
        return Results.BadRequest(new { message = "At least one valid seating section is required." });
    var organizerId = int.Parse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")!.Value);
    var plan = await db.SeatingPlans.Include(item => item.Sections).SingleOrDefaultAsync(item => item.EventId == eventId, ct);
    var now = DateTime.UtcNow;
    if (plan is null)
    {
        plan = new SeatingPlan { EventId = eventId, OrganizerId = organizerId, CreatedAtUtc = now };
        db.SeatingPlans.Add(plan);
    }
    plan.Name = string.IsNullOrWhiteSpace(request.Name) ? "Seating plan" : request.Name.Trim();
    plan.IsVisibleToAttendees = request.IsVisibleToAttendees;
    plan.Status = request.IsVisibleToAttendees ? SeatingPlanStatus.Published : SeatingPlanStatus.Draft;
    plan.UpdatedAtUtc = now;
    plan.Version++;
    var requestedIds = request.Sections.Where(section => section.Id.HasValue).Select(section => section.Id!.Value).ToHashSet();
    foreach (var existing in plan.Sections.Where(section => section.Id > 0 && !requestedIds.Contains(section.Id)).ToList()) db.SeatingSections.Remove(existing);
    plan.Sections = request.Sections.Select((section, index) =>
    {
        var entity = plan.Sections.FirstOrDefault(item => item.Id == section.Id) ?? new SeatingSection { SeatingPlan = plan, CreatedAtUtc = now };
        entity.Name = section.Name.Trim(); entity.RowCount = section.RowCount; entity.SeatsPerRow = section.SeatsPerRow;
        entity.StartingRowLabel = section.StartingRowLabel.Trim().ToUpperInvariant(); entity.StartingSeatNumber = section.StartingSeatNumber;
        entity.TicketTierId = section.TicketTierId; entity.Price = section.Price; entity.DisplayOrder = section.DisplayOrder == 0 ? index : section.DisplayOrder; entity.UpdatedAtUtc = now;
        return entity;
    }).ToList();
    await db.SaveChangesAsync(ct);
    await SeatGenerator.RegenerateAsync(db, plan, ct);
    var disabledCodes = request.Sections.SelectMany(section => section.DisabledSeatCodes ?? [])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var generatedSeats = await db.Seats.Where(seat => seat.SeatingPlanId == plan.Id).ToListAsync(ct);
    foreach (var seat in generatedSeats)
    {
        seat.IsEnabled = !disabledCodes.Contains(seat.SeatCode);
        seat.UpdatedAtUtc = now;
    }
    await db.SaveChangesAsync(ct);
    await db.Entry(plan).Collection(item => item.Sections).LoadAsync(ct);
    await db.Entry(plan).Collection(item => item.Seats).LoadAsync(ct);
    return Results.Ok(ToResponse(plan));
}

static SeatingPlanResponse ToResponse(SeatingPlan plan) => new(plan.Id, plan.EventId, plan.Name, plan.IsVisibleToAttendees, plan.Status.ToString(), plan.Version,
    plan.Sections.OrderBy(section => section.DisplayOrder).Select(section => new SeatingSectionResponse(section.Id, section.Name, section.RowCount, section.SeatsPerRow, section.StartingRowLabel, section.StartingSeatNumber, section.TicketTierId, section.Price, section.DisplayOrder,
        plan.Seats.Where(seat => seat.SeatingSectionId == section.Id).OrderBy(seat => seat.RowLabel).ThenBy(seat => seat.SeatNumber).Select(seat => new SeatResponse(seat.SeatCode, seat.RowLabel, seat.SeatNumber, seat.TicketTierId, seat.Price, seat.IsEnabled, seat.Status.ToString())).ToList())).ToList());

public record VisibilityRequest(bool IsVisibleToAttendees);
public partial class Program { }
