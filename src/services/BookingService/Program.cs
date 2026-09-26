using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Exvo.BookingService.Contracts;
using Exvo.BookingService.Data;
using Exvo.BookingService.Models;
using Exvo.BookingService.Notifications;
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
builder.Services.AddScoped<IEventInventoryCatalogClient, EventInventoryCatalogClient>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<IBookingNotificationQueue, BookingNotificationQueue>();
builder.Services.AddScoped<IBookingEmailSender, BookingEmailSender>();
builder.Services.AddHostedService<BookingNotificationWorker>();
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
app.MapGet("/api/booking/events/{eventId:int}/availability", async (int eventId, BookingDbContext db, IEventInventoryCatalogClient catalog, CancellationToken ct) =>
{
    await ExpireHoldsAsync(db, DateTime.UtcNow, ct);
    var planIsVisible = await db.SeatingPlans.AsNoTracking()
        .AnyAsync(plan => plan.EventId == eventId && plan.IsVisibleToAttendees && plan.Status == SeatingPlanStatus.Published, ct);
    if (planIsVisible)
    {
        var seats = await db.Seats.AsNoTracking()
            .Where(seat => seat.SeatingPlan.EventId == eventId && seat.IsEnabled)
            .Select(seat => new { seat.TicketTierId, seat.Price, seat.Status })
            .ToListAsync(ct);
        var seatedTiers = seats.GroupBy(seat => new { seat.TicketTierId, seat.Price })
                .Select(group => new TierAvailabilityResponse(
                    group.Key.TicketTierId,
                    group.Key.Price,
                    group.Count(seat => seat.Status == SeatStatus.Available),
                    group.Count(),
                    group.Count(seat => seat.Status == SeatStatus.Held),
                    group.Count(seat => seat.Status == SeatStatus.Booked)))
                .ToList();
        var seatedEventTickets = await catalog.GetTicketCatalogAsync(eventId, ct);
        if (seatedEventTickets is not null)
        {
            var seatedConfirmedCounts = await ConfirmedGeneralCountsAsync(db, eventId, ct);
            var seatedTierKeys = seatedTiers
                .Select(tier => new { tier.TicketTierId, tier.Price })
                .ToList();
            seatedTiers.AddRange(seatedEventTickets.Tiers
                .Where(tier => !seatedTierKeys.Any(key => TierMatchesAvailability(key.TicketTierId, key.Price, tier)))
                .Select(tier =>
                {
                    var booked = ConfirmedCountForTier(seatedConfirmedCounts, tier);
                    var available = Math.Max(0, tier.Quantity - booked);
                    return new TierAvailabilityResponse(tier.TicketTierId, tier.Price, available, tier.Quantity, 0, booked);
                }));
        }

        var seatedAvailable = seatedTiers.Sum(tier => tier.AvailableQuantity);
        var seatedTotal = seatedTiers.Sum(tier => tier.TotalQuantity);
        var seatedHeld = seatedTiers.Sum(tier => tier.HeldQuantity);
        var seatedBooked = seatedTiers.Sum(tier => tier.BookedQuantity);
        return Results.Ok(new SeatingAvailabilityResponse(eventId, seatedAvailable, seatedTotal, seatedHeld, seatedBooked, InventoryStatus(seatedAvailable, seatedHeld, seatedTotal), seatedTiers));
    }

    var eventTickets = await catalog.GetTicketCatalogAsync(eventId, ct);
    if (eventTickets is null) return Results.NotFound(new { message = "Event availability is unavailable." });
    var confirmedCounts = await ConfirmedGeneralCountsAsync(db, eventId, ct);
    var tiers = eventTickets.Tiers.Select(tier =>
    {
        var booked = ConfirmedCountForTier(confirmedCounts, tier);
        var available = Math.Max(0, tier.Quantity - booked);
        return new TierAvailabilityResponse(tier.TicketTierId, tier.Price, available, tier.Quantity, 0, booked);
    }).ToList();
    var total = tiers.Sum(tier => tier.TotalQuantity);
    var totalAvailable = tiers.Sum(tier => tier.AvailableQuantity);
    var totalBooked = tiers.Sum(tier => tier.BookedQuantity);
    return Results.Ok(new SeatingAvailabilityResponse(eventId, totalAvailable, total, 0, totalBooked, InventoryStatus(totalAvailable, 0, total), tiers));
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
app.MapPost("/api/booking/events/{eventId:int}/seat-holds/{holdId:int}/confirm", async (int eventId, int holdId, ConfirmSeatHoldRequest? request, ClaimsPrincipal user, BookingDbContext db, IEventInventoryCatalogClient catalog, ILogger<Program> logger, CancellationToken ct) =>
{
    var now = DateTime.UtcNow;
    await ExpireHoldsAsync(db, now, ct);
    var attendeeUserId = GetUserId(user);
    var attendeeEmail = GetUserEmail(user);
    var hold = await db.SeatHolds.Include(item => item.Items)
        .SingleOrDefaultAsync(item => item.Id == holdId && item.EventId == eventId && item.AttendeeUserId == attendeeUserId, ct);
    if (hold is null) return Results.NotFound(new { message = "Seat hold was not found." });
    if (hold.Status != SeatHoldStatus.Active || hold.ExpiresAtUtc <= now)
        return Results.Conflict(new { message = "This seat hold has expired or is no longer active." });

    var seatIds = hold.Items.Select(item => item.SeatId).ToArray();
    var seats = await db.Seats.Where(seat => seatIds.Contains(seat.Id)).ToListAsync(ct);
    if (seats.Count != seatIds.Length || seats.Any(seat => seat.Status != SeatStatus.Held || seat.CurrentHoldId != hold.Id || seat.HoldExpiresAtUtc <= now))
        return Results.Conflict(new { message = "The required seats are no longer available." });

    var requestedGeneralSelections = request?.Tickets?
        .Where(ticket => ticket.Quantity > 0 && !string.IsNullOrWhiteSpace(ticket.Name) && ticket.UnitPrice >= 0)
        .Select(ticket => ticket with
        {
            Name = ticket.Name.Trim(),
            Quantity = Math.Min(ticket.Quantity, 10)
        })
        .ToList() ?? [];
    var totalGeneralQuantity = requestedGeneralSelections.Sum(ticket => ticket.Quantity);
    var confirmedGeneralSelections = new List<GeneralTicketSelectionRequest>();
    if (totalGeneralQuantity > 0)
    {
        if (seats.Count + totalGeneralQuantity > 10)
            return Results.BadRequest(new { message = "Select between 1 and 10 tickets." });

        var eventTickets = await catalog.GetTicketCatalogAsync(eventId, ct);
        if (eventTickets is null) return Results.NotFound(new { message = "Event availability is unavailable." });
        var seatedTierIds = seats.Select(seat => seat.TicketTierId).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        var seatedPrices = seats.Select(seat => seat.Price).ToHashSet();
        confirmedGeneralSelections = requestedGeneralSelections
            .Select(ticket => MatchRequestedTier(eventTickets, ticket))
            .Where(ticket => ticket is not null)
            .Select(ticket => ticket!)
            .Where(ticket => !(ticket.TicketTierId.HasValue && seatedTierIds.Contains(ticket.TicketTierId.Value)) && !seatedPrices.Contains(ticket.UnitPrice))
            .GroupBy(ticket => new { ticket.TicketTierId, ticket.Name, ticket.UnitPrice })
            .Select(group => new GeneralTicketSelectionRequest(group.Key.TicketTierId, group.Key.Name, group.Key.UnitPrice, group.Sum(ticket => ticket.Quantity)))
            .ToList();
        if (confirmedGeneralSelections.Count == 0 || confirmedGeneralSelections.Sum(ticket => ticket.Quantity) != totalGeneralQuantity)
            return Results.BadRequest(new { message = "One or more selected ticket tiers are unavailable." });

        var confirmedCounts = await ConfirmedGeneralCountsAsync(db, eventId, ct);
        foreach (var selection in confirmedGeneralSelections)
        {
            var tier = eventTickets.Tiers.First(item => TierMatchesSelection(item, selection));
            var booked = ConfirmedCountForTier(confirmedCounts, tier);
            var remaining = Math.Max(0, tier.Quantity - booked);
            if (selection.Quantity > remaining)
                return Results.Conflict(new { message = remaining > 0 ? $"Only {remaining} tickets remain for {tier.Name}." : $"{tier.Name} is sold out." });
        }
    }

    var generalItems = confirmedGeneralSelections.SelectMany(ticket => Enumerable.Range(1, ticket.Quantity).Select(_ => new BookingItem
    {
        SeatId = null,
        SeatCode = ShortTicketName(ticket.Name),
        TicketTierId = ticket.TicketTierId,
        UnitPrice = ticket.UnitPrice,
        Quantity = 1
    })).ToList();

    var booking = new Booking
    {
        BookingReference = $"EXVO-{Guid.NewGuid():N}".ToUpperInvariant(),
        EventId = eventId,
        AttendeeUserId = attendeeUserId,
        AttendeeEmail = attendeeEmail,
        Status = BookingStatus.Confirmed,
        TotalAmount = seats.Sum(seat => seat.Price) + generalItems.Sum(item => item.UnitPrice),
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
        }).Concat(generalItems).ToList()
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
    var confirmedSeatSelections = seats
        .GroupBy(seat => new { seat.TicketTierId, seat.Price })
        .Select(group => new ConfirmedTicketSelectionResponse(group.Key.TicketTierId, "Reserved Seat", group.Key.Price, group.Count()))
        .ToList();
    confirmedSeatSelections.AddRange(confirmedGeneralSelections.Select(selection => new ConfirmedTicketSelectionResponse(selection.TicketTierId, selection.Name, selection.UnitPrice, selection.Quantity)));
    return Results.Ok(new BookingConfirmationResponse(booking.Id, booking.BookingReference, eventId, booking.TotalAmount, booking.Currency,
        seats.Select(seat => seat.SeatCode).Concat(generalItems.Select(item => item.SeatCode)).ToList(), confirmedSeatSelections));
}).RequireAuthorization();
app.MapPost("/api/booking/events/{eventId:int}/general-bookings", async (int eventId, GeneralBookingRequest request, ClaimsPrincipal user, BookingDbContext db, IEventInventoryCatalogClient catalog, ILogger<Program> logger, CancellationToken ct) =>
{
    var requestedSelections = request.Tickets?
        .Where(ticket => ticket.Quantity > 0 && !string.IsNullOrWhiteSpace(ticket.Name) && ticket.UnitPrice >= 0)
        .Select(ticket => ticket with
        {
            Name = ticket.Name.Trim(),
            Quantity = Math.Min(ticket.Quantity, 10)
        })
        .ToList() ?? [];
    var totalQuantity = requestedSelections.Sum(ticket => ticket.Quantity);
    if (totalQuantity is < 1 or > 10) return Results.BadRequest(new { message = "Select between 1 and 10 tickets." });

    var attendeeUserId = GetUserId(user);
    var attendeeEmail = GetUserEmail(user);

    var strategy = db.Database.CreateExecutionStrategy();
    return await strategy.ExecuteAsync(async () =>
    {
        var now = DateTime.UtcNow;
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var eventTickets = await catalog.GetTicketCatalogAsync(eventId, ct);
        if (eventTickets is null) return Results.NotFound(new { message = "Event availability is unavailable." });

        var selections = requestedSelections.Select(ticket => MatchRequestedTier(eventTickets, ticket))
            .Where(ticket => ticket is not null)
            .Select(ticket => ticket!)
            .GroupBy(ticket => new { ticket.TicketTierId, ticket.Name, ticket.UnitPrice })
            .Select(group => new GeneralTicketSelectionRequest(group.Key.TicketTierId, group.Key.Name, group.Key.UnitPrice, group.Sum(ticket => ticket.Quantity)))
            .ToList();
        if (selections.Count == 0 || selections.Sum(ticket => ticket.Quantity) != totalQuantity)
            return Results.BadRequest(new { message = "One or more selected ticket tiers are unavailable." });

        var confirmedCounts = await ConfirmedGeneralCountsAsync(db, eventId, ct);
        foreach (var selection in selections)
        {
            var tier = eventTickets.Tiers.First(item => TierMatchesSelection(item, selection));
            var booked = ConfirmedCountForTier(confirmedCounts, tier);
            var remaining = Math.Max(0, tier.Quantity - booked);
            if (selection.Quantity > remaining)
                return Results.Conflict(new { message = remaining > 0 ? $"Only {remaining} tickets remain for {tier.Name}." : $"{tier.Name} is sold out." });
        }

        var items = selections.SelectMany(ticket => Enumerable.Range(1, ticket.Quantity).Select(_ => new BookingItem
        {
            SeatId = null,
            SeatCode = ShortTicketName(ticket.Name),
            TicketTierId = ticket.TicketTierId,
            UnitPrice = ticket.UnitPrice,
            Quantity = 1
        })).ToList();
        var booking = new Booking
        {
            BookingReference = $"EXVO-{Guid.NewGuid():N}".ToUpperInvariant(),
            EventId = eventId,
            AttendeeUserId = attendeeUserId,
            AttendeeEmail = attendeeEmail,
            Status = BookingStatus.Confirmed,
            TotalAmount = items.Sum(item => item.UnitPrice),
            CreatedAtUtc = now,
            ConfirmedAtUtc = now,
            UpdatedAtUtc = now,
            Items = items
        };
        db.Bookings.Add(booking);
        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(exception, "Could not confirm general admission booking for event {EventId}.", eventId);
            return Results.Problem("The reservation could not be saved. Check the BookingService database migration.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new BookingConfirmationResponse(booking.Id, booking.BookingReference, eventId, booking.TotalAmount, booking.Currency,
            items.Select(item => item.SeatCode).ToList(),
            selections.Select(selection => new ConfirmedTicketSelectionResponse(selection.TicketTierId, selection.Name, selection.UnitPrice, selection.Quantity)).ToList()));
    });
}).RequireAuthorization();
app.MapPost("/api/booking/{bookingId:int}/ticket-images", async (int bookingId, TicketImagesRequest request, ClaimsPrincipal user, BookingDbContext db, IBookingNotificationQueue queue, CancellationToken ct) =>
{
    var attendeeUserId = GetUserId(user);
    var booking = await db.Bookings.Include(item => item.Items)
        .SingleOrDefaultAsync(item => item.Id == bookingId && item.AttendeeUserId == attendeeUserId && item.Status == BookingStatus.Confirmed, ct);
    if (booking is null) return Results.NotFound(new { message = "Confirmed booking was not found." });
    if (request.Tickets is null || request.Tickets.Count != booking.Items.Count)
        return Results.BadRequest(new { message = "Provide one rendered image for each ticket." });
    if (request.Tickets.Select(ticket => ticket.BookingItemId).Distinct().Count() != booking.Items.Count)
        return Results.BadRequest(new { message = "Each booking ticket must have exactly one rendered image." });

    var rendered = new List<TicketAttachment>();
    foreach (var ticket in request.Tickets)
    {
        var item = booking.Items.SingleOrDefault(candidate => candidate.Id == ticket.BookingItemId);
        var expectedCode = item is null ? string.Empty : $"{booking.BookingReference}-{item.Id}";
        if (item is null || !string.Equals(ticket.TicketCode, expectedCode, StringComparison.Ordinal) ||
            !ticket.Image.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal))
            return Results.BadRequest(new { message = "Ticket image details do not match this booking." });

        byte[] bytes;
        try { bytes = Convert.FromBase64String(ticket.Image["data:image/jpeg;base64,".Length..]); }
        catch (FormatException) { return Results.BadRequest(new { message = "Ticket image data is invalid." }); }
        if (bytes.Length < 4 || bytes.Length > 8_000_000 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[^2] != 0xFF || bytes[^1] != 0xD9)
            return Results.BadRequest(new { message = "Ticket image must be a valid JPEG under 8 MB." });
        rendered.Add(new TicketAttachment($"{expectedCode}.jpg", "image/jpeg", bytes));
    }

    await queue.QueueAsync(new BookingNotification(booking.Id, rendered), ct);
    return Results.Ok(new { queued = true });
}).RequireAuthorization();
app.MapGet("/api/booking/my-tickets", async (ClaimsPrincipal user, BookingDbContext db, CancellationToken ct) =>
{
    var attendeeUserId = GetUserId(user);
    var bookings = await db.Bookings.AsNoTracking()
        .Include(booking => booking.Items)
            .ThenInclude(item => item.Seat)
                .ThenInclude(seat => seat!.SeatingSection)
        .Where(booking => booking.AttendeeUserId == attendeeUserId && booking.Status == BookingStatus.Confirmed)
        .OrderByDescending(booking => booking.ConfirmedAtUtc ?? booking.CreatedAtUtc)
        .ToListAsync(ct);

    return Results.Ok(bookings.Select(booking => new AttendeeBookingResponse(
        booking.Id,
        booking.BookingReference,
        booking.EventId,
        booking.Status.ToString(),
        booking.TotalAmount,
        booking.Currency,
        booking.CreatedAtUtc,
        booking.ConfirmedAtUtc,
        booking.Items.OrderBy(item => item.SeatCode).Select(item => new AttendeeTicketResponse(
            item.Id,
            $"{booking.BookingReference}-{item.Id}",
            item.Seat != null ? item.Seat.SeatCode : item.SeatCode,
            item.Seat != null ? item.Seat.RowLabel : "GA",
            item.Seat != null ? item.Seat.SeatNumber : item.Id,
            item.Seat != null ? item.Seat.SeatingSection.Name : item.SeatCode,
            item.TicketTierId,
            item.UnitPrice,
            item.Seat != null)).ToList())).ToList());
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
    var protectedSectionIds = plan.Id > 0
        ? (await db.Seats.AsNoTracking()
            .Where(seat => seat.SeatingPlanId == plan.Id &&
                (seat.Status == SeatStatus.Held ||
                 seat.Status == SeatStatus.Booked ||
                 db.BookingItems.Any(item => item.SeatId == seat.Id) ||
                 db.SeatHoldItems.Any(item => item.SeatId == seat.Id)))
            .Select(seat => seat.SeatingSectionId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet()
        : new HashSet<int>();
    var existingSections = plan.Sections.ToList();
    var retainedLegacySections = new List<SeatingSection>();
    foreach (var existing in existingSections.Where(section => section.Id > 0 && !requestedIds.Contains(section.Id)).ToList())
    {
        if (protectedSectionIds.Contains(existing.Id))
        {
            existing.DisplayOrder = request.Sections.Count + retainedLegacySections.Count;
            existing.UpdatedAtUtc = now;
            retainedLegacySections.Add(existing);
        }
        else
        {
            db.SeatingSections.Remove(existing);
        }
    }
    var activeSections = request.Sections.Select((section, index) =>
    {
        var entity = existingSections.FirstOrDefault(item => item.Id == section.Id) ?? new SeatingSection { SeatingPlan = plan, CreatedAtUtc = now };
        entity.Name = section.Name.Trim(); entity.RowCount = section.RowCount; entity.SeatsPerRow = section.SeatsPerRow;
        entity.StartingRowLabel = section.StartingRowLabel.Trim().ToUpperInvariant(); entity.StartingSeatNumber = section.StartingSeatNumber;
        entity.TicketTierId = section.TicketTierId; entity.Price = section.Price; entity.DisplayOrder = section.DisplayOrder == 0 ? index : section.DisplayOrder; entity.UpdatedAtUtc = now;
        return entity;
    }).ToList();
    plan.Sections = activeSections.Concat(retainedLegacySections).ToList();
    await db.SaveChangesAsync(ct);
    var activeSectionIds = activeSections.Select(section => section.Id).ToHashSet();
    await SeatGenerator.RegenerateAsync(db, plan, activeSectionIds, ct);
    var disabledCodes = request.Sections.SelectMany(section => section.DisabledSeatCodes ?? [])
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var generatedSeats = await db.Seats.Where(seat => seat.SeatingPlanId == plan.Id).ToListAsync(ct);
    foreach (var seat in generatedSeats)
    {
        seat.IsEnabled = activeSectionIds.Contains(seat.SeatingSectionId) && !disabledCodes.Contains(seat.SeatCode);
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

static string GetUserEmail(ClaimsPrincipal user) =>
    user.FindFirst(ClaimTypes.Email)?.Value
    ?? user.FindFirst(JwtRegisteredClaimNames.Email)?.Value
    ?? user.FindFirst("email")?.Value
    ?? throw new UnauthorizedAccessException("The attendee email claim is required to send booking confirmations.");

static string InventoryStatus(int available, int held, int total)
{
    if (available > 0) return "Available";
    if (held > 0) return "TemporarilyHeld";
    return "SoldOut";
}

static async Task<List<ConfirmedGeneralTicketCount>> ConfirmedGeneralCountsAsync(BookingDbContext db, int eventId, CancellationToken ct) =>
    await db.BookingItems.AsNoTracking()
        .Where(item => item.SeatId == null && item.Booking.EventId == eventId && item.Booking.Status == BookingStatus.Confirmed)
        .GroupBy(item => new { item.TicketTierId, item.SeatCode, item.UnitPrice })
        .Select(group => new ConfirmedGeneralTicketCount(group.Key.TicketTierId, group.Key.SeatCode, group.Key.UnitPrice, group.Count()))
        .ToListAsync(ct);

static int ConfirmedCountForTier(List<ConfirmedGeneralTicketCount> confirmedCounts, CatalogTicketTier tier) =>
    confirmedCounts
        .Where(count => tier.TicketTierId.HasValue
            ? count.TicketTierId == tier.TicketTierId
            : count.TicketTierId == null && string.Equals(count.Name, ShortTicketName(tier.Name), StringComparison.OrdinalIgnoreCase) && count.Price == tier.Price)
        .Sum(count => count.Count);

static GeneralTicketSelectionRequest? MatchRequestedTier(EventTicketCatalog catalog, GeneralTicketSelectionRequest request)
{
    var tier = catalog.Tiers.FirstOrDefault(item => TierMatchesSelection(item, request));
    return tier is null ? null : new GeneralTicketSelectionRequest(tier.TicketTierId, tier.Name, tier.Price, request.Quantity);
}

static bool TierMatchesSelection(CatalogTicketTier tier, GeneralTicketSelectionRequest selection) =>
    selection.TicketTierId.HasValue
        ? tier.TicketTierId == selection.TicketTierId
        : string.Equals(tier.Name, selection.Name, StringComparison.OrdinalIgnoreCase) && tier.Price == selection.UnitPrice;

static bool TierMatchesAvailability(int? ticketTierId, decimal price, CatalogTicketTier tier) =>
    ticketTierId.HasValue
        ? tier.TicketTierId == ticketTierId
        : tier.TicketTierId == null && tier.Price == price;

static string ShortTicketName(string name) => name.Length > 50 ? name[..50] : name;

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
public record ConfirmedGeneralTicketCount(int? TicketTierId, string Name, decimal Price, int Count);
public partial class Program { }
