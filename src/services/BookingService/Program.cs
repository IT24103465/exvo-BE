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
    db.Database.GetService<IMigrator>().Migrate();

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
    await ExpireHoldsAsync(db, DateTime.UtcNow, ct);
    var plan = await db.SeatingPlans.AsNoTracking().Include(item => item.Sections).Include(item => item.Seats)
        .SingleOrDefaultAsync(item => item.EventId == eventId && item.IsVisibleToAttendees && item.Status == SeatingPlanStatus.Published, ct);
    return plan is null ? Results.NotFound(new { message = "Seating plan is unavailable." }) : Results.Ok(ToResponse(plan));
});
app.MapGet("/api/booking/events/{eventId:int}/availability", async (int eventId, BookingDbContext db, CancellationToken ct) =>
{
    await ExpireHoldsAsync(db, DateTime.UtcNow, ct);
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
app.MapPost("/api/booking/events/{eventId:int}/seat-holds", async (int eventId, SeatHoldRequest request, ClaimsPrincipal user, BookingDbContext db, CancellationToken ct) =>
{
    var attendeeUserId = GetUserId(user);
    var requestedCodes = request.SeatCodes?.Where(code => !string.IsNullOrWhiteSpace(code))
        .Select(code => code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    if (requestedCodes.Length == 0 || requestedCodes.Length > 10)
        return Results.BadRequest(new { message = "Select between 1 and 10 seats." });

    var now = DateTime.UtcNow;
    await ExpireHoldsAsync(db, now, ct);
    var plan = await db.SeatingPlans.SingleOrDefaultAsync(item => item.EventId == eventId && item.IsVisibleToAttendees && item.Status == SeatingPlanStatus.Published, ct);
    if (plan is null) return Results.NotFound(new { message = "Seating plan is unavailable." });
    var seats = await db.Seats.Where(seat => seat.SeatingPlanId == plan.Id && requestedCodes.Contains(seat.SeatCode)).ToListAsync(ct);
    if (seats.Count != requestedCodes.Length || seats.Any(seat => !seat.IsEnabled || seat.Status != SeatStatus.Available))
        return Results.Conflict(new { message = "One or more selected seats are no longer available." });

    var expiresAt = now.AddMinutes(5);
    var hold = new SeatHold { EventId = eventId, AttendeeUserId = attendeeUserId, CreatedAtUtc = now, ExpiresAtUtc = expiresAt };
    db.SeatHolds.Add(hold);
    foreach (var seat in seats)
    {
        seat.Status = SeatStatus.Held;
        seat.HoldExpiresAtUtc = expiresAt;
        seat.UpdatedAtUtc = now;
        seat.Version++;
        hold.Items.Add(new SeatHoldItem { Seat = seat });
    }
    try
    {
        await db.SaveChangesAsync(ct);
        foreach (var seat in seats) seat.CurrentHoldId = hold.Id;
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new { message = "One or more selected seats are no longer available." });
    }
    return Results.Ok(new SeatHoldResponse(hold.Id, eventId, seats.Select(seat => seat.SeatCode).ToList(), expiresAt));
}).RequireAuthorization();
app.MapPost("/api/booking/events/{eventId:int}/seat-holds/{holdId:int}/confirm", async (int eventId, int holdId, ClaimsPrincipal user, BookingDbContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    var now = DateTime.UtcNow;
    await ExpireHoldsAsync(db, now, ct);
    var attendeeUserId = GetUserId(user);
    var hold = await db.SeatHolds.Include(item => item.Items)
        .SingleOrDefaultAsync(item => item.Id == holdId && item.EventId == eventId && item.AttendeeUserId == attendeeUserId, ct);
    if (hold is null) return Results.NotFound(new { message = "Seat hold was not found." });
    if (hold.Status != SeatHoldStatus.Active || hold.ExpiresAtUtc <= now)
        return Results.Conflict(new { message = "This seat hold has expired or is no longer active." });

    var seatIds = hold.Items.Select(item => item.SeatId).ToArray();
    var seats = await db.Seats.Where(seat => seatIds.Contains(seat.Id)).ToListAsync(ct);
    if (seats.Count != seatIds.Length || seats.Any(seat => seat.Status != SeatStatus.Held || seat.CurrentHoldId != hold.Id || seat.HoldExpiresAtUtc <= now))
        return Results.Conflict(new { message = "The required seats are no longer available." });

    var booking = new Booking
    {
        BookingReference = $"EXVO-{Guid.NewGuid():N}".ToUpperInvariant(),
        EventId = eventId,
        AttendeeUserId = attendeeUserId,
        Status = BookingStatus.Confirmed,
        TotalAmount = seats.Sum(seat => seat.Price),
        CreatedAtUtc = now,
        ConfirmedAtUtc = now,
        UpdatedAtUtc = now,
        Items = seats.Select(seat => new BookingItem
        {
            SeatId = seat.Id,
            SeatCode = seat.SeatCode,
            TicketTierId = seat.TicketTierId,
            UnitPrice = seat.Price,
            Quantity = 1
        }).ToList()
    };
    db.Bookings.Add(booking);
    hold.Status = SeatHoldStatus.Converted;
    hold.ReleasedAtUtc = now;
    foreach (var seat in seats)
    {
        seat.Status = SeatStatus.Booked;
        seat.HoldExpiresAtUtc = null;
        seat.UpdatedAtUtc = now;
        seat.Version++;
    }
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new { message = "The required seats are no longer available." });
    }
    catch (DbUpdateException exception)
    {
        logger.LogError(exception, "Could not confirm seat hold {HoldId} for event {EventId}.", holdId, eventId);
        return Results.Problem("The reservation could not be saved. Check the BookingService database migration.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new BookingConfirmationResponse(booking.Id, booking.BookingReference, eventId, booking.TotalAmount, booking.Currency,
        seats.Select(seat => seat.SeatCode).ToList()));
}).RequireAuthorization();
app.MapDelete("/api/booking/events/{eventId:int}/seat-holds/{holdId:int}", async (int eventId, int holdId, ClaimsPrincipal user, BookingDbContext db, CancellationToken ct) =>
{
    var attendeeUserId = GetUserId(user);
    var hold = await db.SeatHolds.Include(item => item.Items).SingleOrDefaultAsync(item => item.Id == holdId && item.EventId == eventId && item.AttendeeUserId == attendeeUserId && item.Status == SeatHoldStatus.Active, ct);
    if (hold is null) return Results.NotFound();
    await ReleaseHoldAsync(db, hold, DateTime.UtcNow, SeatHoldStatus.Released, ct);
    return Results.NoContent();
}).RequireAuthorization();
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

static int GetUserId(ClaimsPrincipal user) => int.Parse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? throw new UnauthorizedAccessException());

static async Task ExpireHoldsAsync(BookingDbContext db, DateTime now, CancellationToken ct)
{
    var holds = await db.SeatHolds.Include(item => item.Items)
        .Where(item => item.Status == SeatHoldStatus.Active && item.ExpiresAtUtc <= now).ToListAsync(ct);
    foreach (var hold in holds) await ReleaseHoldAsync(db, hold, now, SeatHoldStatus.Expired, ct, save: false);
    var orphanedExpiredSeats = await db.Seats
        .Where(seat => seat.Status == SeatStatus.Held && seat.HoldExpiresAtUtc <= now && seat.CurrentHoldId == null)
        .ToListAsync(ct);
    foreach (var seat in orphanedExpiredSeats)
    {
        seat.Status = SeatStatus.Available;
        seat.HoldExpiresAtUtc = null;
        seat.UpdatedAtUtc = now;
        seat.Version++;
    }
    if (holds.Count > 0 || orphanedExpiredSeats.Count > 0) await db.SaveChangesAsync(ct);
}

static async Task ReleaseHoldAsync(BookingDbContext db, SeatHold hold, DateTime now, SeatHoldStatus status, CancellationToken ct, bool save = true)
{
    hold.Status = status;
    hold.ReleasedAtUtc = now;
    var seatIds = hold.Items.Select(item => item.SeatId).ToArray();
    var seats = await db.Seats.Where(seat => seatIds.Contains(seat.Id) && seat.CurrentHoldId == hold.Id).ToListAsync(ct);
    foreach (var seat in seats)
    {
        seat.Status = SeatStatus.Available;
        seat.CurrentHoldId = null;
        seat.HoldExpiresAtUtc = null;
        seat.UpdatedAtUtc = now;
        seat.Version++;
    }
    if (save) await db.SaveChangesAsync(ct);
}

public record VisibilityRequest(bool IsVisibleToAttendees);
public partial class Program { }
