using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Exvo.CheckInService.Data;
using Exvo.CheckInService.Models;
using Exvo.CheckInService.Models.DTOs;
using Exvo.CheckInService.Models.Events;
using Exvo.CheckInService.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Context
var connectionString = builder.Configuration["EXVO_CHECKIN_MYSQL_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost;Port=3306;Database=exvo_checkin_db;Uid=root;Pwd=;SslMode=None;AllowPublicKeyRetrieval=True;";

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<CheckInDbContext>(options =>
        options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 30)),
            mySqlOptions => mySqlOptions.EnableRetryOnFailure()));
}
else
{
    builder.Services.AddDbContext<CheckInDbContext>(options =>
        options.UseInMemoryDatabase("CheckInSharedTestingDb"));
}

// 2. HTTP clients for service-to-service communication
builder.Services.AddHttpClient("CatalogService", client =>
    client.BaseAddress = new Uri(builder.Configuration["CatalogService:BaseUrl"] ?? "http://localhost:5255"));

builder.Services.AddHttpClient("BookingService", client =>
    client.BaseAddress = new Uri(builder.Configuration["BookingService:BaseUrl"] ?? "http://localhost:5063"));

// 3. Register Outbox Service & Background Workers
builder.Services.AddScoped<ICheckInOutboxService, CheckInOutboxService>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<CheckInKafkaConsumerWorker>();

// 4. JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ExvoAuthService";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "ExvoPlatform";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

// 5. Authorization policies — Allow Organizer, Company, Admin
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CheckInScannerAccess", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Organizer", "Company", "Admin"));

    options.AddPolicy("CoordinatorScannerAccess", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Coordinator"));

    options.AddPolicy("OrganizerOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Organizer", "Company", "Admin"));
});

// 6. Swagger configuration
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Exvo Check-In & Ticket Scanning API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer' followed by your token."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// 7. CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CheckInDbContext>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch
    {
        // Database will be initialized when connection is ready
    }
}

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

// Health check (anonymous)
app.MapGet("/health", () => Results.Ok(new { service = "CheckInService", status = "healthy" }))
    .AllowAnonymous();

// --- Helper methods ---
static int? GetUserId(ClaimsPrincipal user)
{
    if (user.Identity?.IsAuthenticated != true)
        return null;

    var value = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value;

    return int.TryParse(value, out var id) && id > 0 ? id : null;
}

static bool IsAdmin(ClaimsPrincipal user)
{
    return user.IsInRole("Admin");
}

static async Task<bool> ValidateEventOwnershipAsync(
    int eventId,
    ClaimsPrincipal user,
    IHttpClientFactory httpClientFactory)
{
    if (IsAdmin(user)) return true;

    var userId = GetUserId(user);
    if (userId is null) return false;

    var catalogClient = httpClientFactory.CreateClient("CatalogService");
    try
    {
        var response = await catalogClient.GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot");
        if (!response.IsSuccessStatusCode) return false;

        var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
        return eventData != null && eventData.OrganizerId == userId.Value;
    }
    catch
    {
        return false;
    }
}

// =============================================================================
// 1. POST /api/checkin/verify – Verify QR ticket
// =============================================================================
app.MapPost("/api/checkin/verify", async (
    VerifyTicketRequest request,
    ClaimsPrincipal user,
    CheckInDbContext db,
    ICheckInOutboxService outboxService,
    IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.QrCode) || request.EventId <= 0)
        return Results.BadRequest(new ErrorDto("QrCode and valid EventId are required."));

    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    // Verify event ownership for non-admin
    if (!await ValidateEventOwnershipAsync(request.EventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var qrCode = request.QrCode.Trim();
    var qrHash = outboxService.ComputeSha256Hash(qrCode);

    // Call BookingService internal endpoint
    var bookingClient = httpClientFactory.CreateClient("BookingService");
    TicketLookupResponse? ticketInfo = null;
    try
    {
        var response = await bookingClient.GetAsync(
            $"/api/booking/internal/tickets/{Uri.EscapeDataString(qrCode)}?eventId={request.EventId}");

        if (response.IsSuccessStatusCode)
        {
            ticketInfo = await response.Content.ReadFromJsonAsync<TicketLookupResponse>();
        }
    }
    catch
    {
        return Results.Json(new ErrorDto("Booking service temporarily unavailable."), statusCode: 503);
    }

    if (ticketInfo is null)
    {
        // Record verification failure outbox event
        await outboxService.QueueEventAsync("ticket.verification-failed", request.EventId.ToString(), new TicketVerificationFailedEvent(
            QrCode: qrCode,
            EventId: request.EventId,
            ScannedBy: userId.Value,
            Reason: "Ticket not found or invalid",
            AttemptedAt: DateTime.UtcNow
        ));
        await db.SaveChangesAsync();

        return Results.Ok(new VerifyTicketResponse(
            IsValid: false,
            Status: "NotFound",
            Message: "Ticket was not found or is invalid.",
            TicketId: null,
            EventId: request.EventId,
            BookingId: null,
            AttendeeId: null,
            AttendeeName: null,
            TicketType: null,
            SeatCode: null,
            ScannedAt: null
        ));
    }

    // Check if ticket is cancelled
    if (string.Equals(ticketInfo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new VerifyTicketResponse(
            IsValid: false,
            Status: "Cancelled",
            Message: "This ticket has been cancelled.",
            TicketId: ticketInfo.TicketId,
            EventId: request.EventId,
            BookingId: ticketInfo.BookingId,
            AttendeeId: ticketInfo.AttendeeId,
            AttendeeName: ticketInfo.AttendeeName,
            TicketType: ticketInfo.TicketType,
            SeatCode: ticketInfo.SeatCode,
            ScannedAt: null
        ));
    }

    // Check if ticket belongs to the requested event
    if (ticketInfo.EventId != request.EventId)
    {
        return Results.Ok(new VerifyTicketResponse(
            IsValid: false,
            Status: "InvalidEvent",
            Message: "Ticket belongs to a different event.",
            TicketId: ticketInfo.TicketId,
            EventId: ticketInfo.EventId,
            BookingId: ticketInfo.BookingId,
            AttendeeId: ticketInfo.AttendeeId,
            AttendeeName: ticketInfo.AttendeeName,
            TicketType: ticketInfo.TicketType,
            SeatCode: ticketInfo.SeatCode,
            ScannedAt: null
        ));
    }

    // Check if already checked in in ScannedTickets
    var existingScan = await db.ScannedTickets
        .AsNoTracking()
        .FirstOrDefaultAsync(t => t.TicketId == ticketInfo.TicketId && t.Status == ScannedTicketStatus.CheckedIn);

    if (existingScan is not null)
    {
        return Results.Ok(new VerifyTicketResponse(
            IsValid: false,
            Status: "AlreadyCheckedIn",
            Message: $"Ticket was already checked in at {existingScan.ScannedAt:u}.",
            TicketId: existingScan.TicketId,
            EventId: existingScan.EventId,
            BookingId: existingScan.BookingId,
            AttendeeId: existingScan.AttendeeId,
            AttendeeName: existingScan.AttendeeName,
            TicketType: existingScan.TicketType,
            SeatCode: existingScan.SeatCode,
            ScannedAt: existingScan.ScannedAt
        ));
    }

    return Results.Ok(new VerifyTicketResponse(
        IsValid: true,
        Status: "Valid",
        Message: "Ticket is valid for check-in.",
        TicketId: ticketInfo.TicketId,
        EventId: ticketInfo.EventId,
        BookingId: ticketInfo.BookingId,
        AttendeeId: ticketInfo.AttendeeId,
        AttendeeName: ticketInfo.AttendeeName,
        TicketType: ticketInfo.TicketType,
        SeatCode: ticketInfo.SeatCode,
        ScannedAt: null
    ));
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("VerifyTicket")
.WithOpenApi();

// =============================================================================
// 2. POST /api/checkin/scan – Scan and check in a ticket
// =============================================================================
app.MapPost("/api/checkin/scan", async (
    ScanTicketRequest request,
    ClaimsPrincipal user,
    CheckInDbContext db,
    ICheckInOutboxService outboxService,
    IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.QrCode) || request.EventId <= 0)
        return Results.BadRequest(new ErrorDto("QrCode and valid EventId are required."));

    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    // Verify event ownership for non-admin
    if (!await ValidateEventOwnershipAsync(request.EventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var qrCode = request.QrCode.Trim();
    var qrHash = outboxService.ComputeSha256Hash(qrCode);

    // 1. Call BookingService to validate authenticity and booking status
    var bookingClient = httpClientFactory.CreateClient("BookingService");
    TicketLookupResponse? ticketInfo = null;
    try
    {
        var response = await bookingClient.GetAsync(
            $"/api/booking/internal/tickets/{Uri.EscapeDataString(qrCode)}?eventId={request.EventId}");

        if (response.IsSuccessStatusCode)
        {
            ticketInfo = await response.Content.ReadFromJsonAsync<TicketLookupResponse>();
        }
    }
    catch
    {
        return Results.Json(new ErrorDto("Booking service temporarily unavailable."), statusCode: 503);
    }

    if (ticketInfo is null || ticketInfo.TicketId is null)
    {
        // Record rejected scan
        var rejectedTicket = new ScannedTicket
        {
            TicketId = 0,
            EventId = request.EventId,
            BookingId = 0,
            AttendeeId = 0,
            ScannedBy = userId.Value,
            ScannedAt = DateTime.UtcNow,
            Status = ScannedTicketStatus.Rejected,
            QrCodeHash = qrHash,
            RejectionReason = "Ticket not found or invalid"
        };
        db.ScannedTickets.Add(rejectedTicket);

        await outboxService.QueueEventAsync("ticket.verification-failed", request.EventId.ToString(), new TicketVerificationFailedEvent(
            QrCode: qrCode,
            EventId: request.EventId,
            ScannedBy: userId.Value,
            Reason: "Ticket not found or invalid",
            AttemptedAt: DateTime.UtcNow
        ));
        await db.SaveChangesAsync();

        return Results.Ok(new ScanTicketResponse(
            Success: false,
            Status: "Invalid",
            Message: "Invalid ticket QR code.",
            ScannedTicket: null
        ));
    }

    var ticketId = ticketInfo.TicketId.Value;

    // Check cancellation
    if (string.Equals(ticketInfo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new ScanTicketResponse(
            Success: false,
            Status: "Rejected",
            Message: "This ticket has been cancelled and cannot be used.",
            ScannedTicket: null
        ));
    }

    // Check event match
    if (ticketInfo.EventId != request.EventId)
    {
        return Results.Ok(new ScanTicketResponse(
            Success: false,
            Status: "Invalid",
            Message: "Ticket belongs to a different event.",
            ScannedTicket: null
        ));
    }

    // 2. Prevent duplicate scan using DB query first
    var existingScan = await db.ScannedTickets
        .FirstOrDefaultAsync(t => t.TicketId == ticketId && t.Status == ScannedTicketStatus.CheckedIn);

    if (existingScan is not null)
    {
        return Results.Ok(new ScanTicketResponse(
            Success: false,
            Status: "Duplicate",
            Message: $"Duplicate scan: ticket was already checked in at {existingScan.ScannedAt:u}.",
            ScannedTicket: new ScannedTicketDto(
                existingScan.Id,
                existingScan.TicketId,
                existingScan.EventId,
                existingScan.BookingId,
                existingScan.AttendeeId,
                existingScan.ScannedBy,
                existingScan.ScannedAt,
                existingScan.Status.ToString(),
                existingScan.AttendeeName,
                existingScan.TicketType,
                existingScan.SeatCode
            )
        ));
    }

    // 3. Atomic transaction to insert ScannedTicket and OutboxMessage
    var scannedAt = DateTime.UtcNow;
    var newScan = new ScannedTicket
    {
        TicketId = ticketId,
        EventId = request.EventId,
        BookingId = ticketInfo.BookingId,
        AttendeeId = ticketInfo.AttendeeId ?? 0,
        ScannedBy = userId.Value,
        ScannedAt = scannedAt,
        Status = ScannedTicketStatus.CheckedIn,
        QrCodeHash = qrHash,
        AttendeeName = ticketInfo.AttendeeName,
        TicketType = ticketInfo.TicketType,
        SeatCode = ticketInfo.SeatCode
    };

    db.ScannedTickets.Add(newScan);

    // Audit log
    db.AuditLogs.Add(new CheckInAuditLog
    {
        Action = "TicketCheckedIn",
        TicketId = ticketId,
        EventId = request.EventId,
        UserId = userId.Value,
        Details = $"Ticket checked in by User #{userId.Value}",
        TimestampUtc = scannedAt
    });

    // Enqueue transactional outbox event
    await outboxService.QueueEventAsync("ticket.checked-in", request.EventId.ToString(), new TicketCheckedInEvent(
        TicketId: ticketId,
        EventId: request.EventId,
        BookingId: ticketInfo.BookingId,
        AttendeeId: ticketInfo.AttendeeId ?? 0,
        ScannedBy: userId.Value,
        ScannedAt: scannedAt
    ));

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        // Concurrency / duplicate constraint hit
        var raceScan = await db.ScannedTickets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TicketId == ticketId && t.Status == ScannedTicketStatus.CheckedIn);

        return Results.Ok(new ScanTicketResponse(
            Success: false,
            Status: "Duplicate",
            Message: "Duplicate scan: ticket was already checked in.",
            ScannedTicket: raceScan != null ? new ScannedTicketDto(
                raceScan.Id,
                raceScan.TicketId,
                raceScan.EventId,
                raceScan.BookingId,
                raceScan.AttendeeId,
                raceScan.ScannedBy,
                raceScan.ScannedAt,
                raceScan.Status.ToString(),
                raceScan.AttendeeName,
                raceScan.TicketType,
                raceScan.SeatCode
            ) : null
        ));
    }

    return Results.Ok(new ScanTicketResponse(
        Success: true,
        Status: "CheckedIn",
        Message: "Ticket successfully checked in.",
        ScannedTicket: new ScannedTicketDto(
            newScan.Id,
            newScan.TicketId,
            newScan.EventId,
            newScan.BookingId,
            newScan.AttendeeId,
            newScan.ScannedBy,
            newScan.ScannedAt,
            newScan.Status.ToString(),
            newScan.AttendeeName,
            newScan.TicketType,
            newScan.SeatCode
        )
    ));
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("ScanTicket")
.WithOpenApi();

// =============================================================================
// 3. GET /api/checkin/event/{eventId} – Retrieve scanned tickets for an event
// =============================================================================
app.MapGet("/api/checkin/event/{eventId:int}", async (
    int eventId,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var scannedTickets = await db.ScannedTickets
        .AsNoTracking()
        .Where(t => t.EventId == eventId)
        .OrderByDescending(t => t.ScannedAt)
        .Select(t => new ScannedTicketDto(
            t.Id,
            t.TicketId,
            t.EventId,
            t.BookingId,
            t.AttendeeId,
            t.ScannedBy,
            t.ScannedAt,
            t.Status.ToString(),
            t.AttendeeName,
            t.TicketType,
            t.SeatCode
        ))
        .ToListAsync();

    return Results.Ok(scannedTickets);
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("GetScannedTicketsByEvent")
.WithOpenApi();

// =============================================================================
// 4. GET /api/checkin/event/{eventId}/stats – Attendance statistics
// =============================================================================
app.MapGet("/api/checkin/event/{eventId:int}/stats", async (
    int eventId,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var totalCheckedIn = await db.ScannedTickets
        .AsNoTracking()
        .CountAsync(t => t.EventId == eventId && t.Status == ScannedTicketStatus.CheckedIn);

    var totalRejected = await db.ScannedTickets
        .AsNoTracking()
        .CountAsync(t => t.EventId == eventId && t.Status == ScannedTicketStatus.Rejected);

    return Results.Ok(new AttendanceStatsResponse(
        EventId: eventId,
        TotalCheckedIn: totalCheckedIn,
        TotalRejected: totalRejected,
        TotalScanned: totalCheckedIn + totalRejected,
        LastUpdatedUtc: DateTime.UtcNow
    ));
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("GetAttendanceStats")
.WithOpenApi();

// =============================================================================
// Backward compatibility coordinator endpoints & assignments
// =============================================================================
app.MapGet("/api/checkin/coordinator/events", async (
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    var activeAssignments = await db.CoordinatorAssignments
        .AsNoTracking()
        .Where(a => a.CoordinatorUserId == userId.Value && a.Status == AssignmentStatus.Active)
        .Select(a => a.EventId)
        .ToListAsync();

    if (activeAssignments.Count == 0)
        return Results.Ok(Array.Empty<AssignedEventDto>());

    var catalogClient = httpClientFactory.CreateClient("CatalogService");
    var assignedEvents = new List<AssignedEventDto>();

    foreach (var eventId in activeAssignments)
    {
        try
        {
            var response = await catalogClient.GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot");
            if (response.IsSuccessStatusCode)
            {
                var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
                if (eventData is not null)
                {
                    assignedEvents.Add(new AssignedEventDto(
                        eventData.Id,
                        eventData.Title ?? string.Empty,
                        eventData.EventDate,
                        eventData.Venue,
                        eventData.Location,
                        eventData.IsHidder
                    ));
                }
            }
        }
        catch { }
    }

    return Results.Ok(assignedEvents);
})
.RequireAuthorization("CoordinatorScannerAccess")
.WithName("GetCoordinatorAssignedEvents")
.WithOpenApi();

app.MapGet("/api/coordinator/events", async (
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    var activeAssignments = await db.CoordinatorAssignments
        .AsNoTracking()
        .Where(a => a.CoordinatorUserId == userId.Value && a.Status == AssignmentStatus.Active)
        .Select(a => a.EventId)
        .ToListAsync();

    if (activeAssignments.Count == 0)
        return Results.Ok(Array.Empty<AssignedEventDto>());

    var catalogClient = httpClientFactory.CreateClient("CatalogService");
    var assignedEvents = new List<AssignedEventDto>();

    foreach (var eventId in activeAssignments)
    {
        try
        {
            var response = await catalogClient.GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot");
            if (response.IsSuccessStatusCode)
            {
                var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
                if (eventData is not null)
                {
                    assignedEvents.Add(new AssignedEventDto(
                        eventData.Id,
                        eventData.Title ?? string.Empty,
                        eventData.EventDate,
                        eventData.Venue,
                        eventData.Location,
                        eventData.IsHidder
                    ));
                }
            }
        }
        catch { }
    }

    return Results.Ok(assignedEvents);
})
.RequireAuthorization("CoordinatorScannerAccess")
.WithName("GetCoordinatorAssignedEventsDirect")
.WithOpenApi();

app.MapGet("/api/checkin/coordinator/events/{eventId:int}/access", async (
    int eventId,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    var hasActiveAssignment = await db.CoordinatorAssignments
        .AsNoTracking()
        .AnyAsync(a => a.CoordinatorUserId == userId.Value
                    && a.EventId == eventId
                    && a.Status == AssignmentStatus.Active);

    if (!hasActiveAssignment)
        return Results.Json(new ErrorDto("Access denied."), statusCode: 403);

    var catalogClient = httpClientFactory.CreateClient("CatalogService");
    try
    {
        var response = await catalogClient.GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot");
        if (!response.IsSuccessStatusCode)
            return Results.Json(new ErrorDto("Access denied."), statusCode: 403);

        var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
        if (eventData is null)
            return Results.Json(new ErrorDto("Access denied."), statusCode: 403);

        return Results.Ok(new ScannerAccessDto(
            EventId: eventId,
            CanAccessScanner: true,
            Event: new AssignedEventDto(
                eventData.Id,
                eventData.Title ?? string.Empty,
                eventData.EventDate,
                eventData.Venue,
                eventData.Location,
                eventData.IsHidder
            )
        ));
    }
    catch
    {
        return Results.Json(new ErrorDto("Service temporarily unavailable."), statusCode: 503);
    }
})
.RequireAuthorization("CoordinatorScannerAccess")
.WithName("GetCoordinatorEventAccess")
.WithOpenApi();

app.MapGet("/api/coordinator/events/{eventId:int}/access", async (
    int eventId,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    var hasActiveAssignment = await db.CoordinatorAssignments
        .AsNoTracking()
        .AnyAsync(a => a.CoordinatorUserId == userId.Value
                    && a.EventId == eventId
                    && a.Status == AssignmentStatus.Active);

    if (!hasActiveAssignment)
        return Results.Json(new ErrorDto("Access denied."), statusCode: 403);

    var catalogClient = httpClientFactory.CreateClient("CatalogService");
    try
    {
        var response = await catalogClient.GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot");
        if (!response.IsSuccessStatusCode)
            return Results.Json(new ErrorDto("Access denied."), statusCode: 403);

        var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
        if (eventData is null)
            return Results.Json(new ErrorDto("Access denied."), statusCode: 403);

        return Results.Ok(new ScannerAccessDto(
            EventId: eventId,
            CanAccessScanner: true,
            Event: new AssignedEventDto(
                eventData.Id,
                eventData.Title ?? string.Empty,
                eventData.EventDate,
                eventData.Venue,
                eventData.Location,
                eventData.IsHidder
            )
        ));
    }
    catch
    {
        return Results.Json(new ErrorDto("Service temporarily unavailable."), statusCode: 503);
    }
})
.RequireAuthorization("CoordinatorScannerAccess")
.WithName("GetCoordinatorEventAccessDirect")
.WithOpenApi();

app.MapPost("/api/checkin/assignments", async (
    AssignCoordinatorRequest request,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    var organizerId = GetUserId(user);
    if (organizerId is null) return Results.Forbid();

    if (request.CoordinatorUserId <= 0 || request.EventId <= 0)
        return Results.BadRequest(new ErrorDto("Valid CoordinatorUserId and EventId are required."));

    var catalogClient = httpClientFactory.CreateClient("CatalogService");
    try
    {
        var response = await catalogClient.GetAsync($"/api/catalog/events/{request.EventId}/ticket-snapshot");
        if (!response.IsSuccessStatusCode) return Results.NotFound(new ErrorDto("Event not found."));

        var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
        if (eventData is null) return Results.NotFound(new ErrorDto("Event not found."));

        if (eventData.OrganizerId != organizerId.Value) return Results.Forbid();
    }
    catch
    {
        return Results.Json(new ErrorDto("Service temporarily unavailable."), statusCode: 503);
    }

    var existing = await db.CoordinatorAssignments
        .FirstOrDefaultAsync(a => a.CoordinatorUserId == request.CoordinatorUserId && a.EventId == request.EventId);

    if (existing is not null)
    {
        if (existing.Status == AssignmentStatus.Active)
        {
            return Results.Ok(new CoordinatorAssignmentDto(
                existing.Id, existing.CoordinatorUserId, existing.EventId,
                existing.Status.ToString(), existing.CreatedAtUtc));
        }

        existing.Status = AssignmentStatus.Active;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok(new CoordinatorAssignmentDto(
            existing.Id, existing.CoordinatorUserId, existing.EventId,
            existing.Status.ToString(), existing.CreatedAtUtc));
    }

    var assignment = new CoordinatorAssignment
    {
        CoordinatorUserId = request.CoordinatorUserId,
        EventId = request.EventId,
        Status = AssignmentStatus.Active,
        CreatedAtUtc = DateTime.UtcNow
    };

    db.CoordinatorAssignments.Add(assignment);
    await db.SaveChangesAsync();

    return Results.Created($"/api/checkin/assignments/{assignment.Id}", new CoordinatorAssignmentDto(
        assignment.Id, assignment.CoordinatorUserId, assignment.EventId,
        assignment.Status.ToString(), assignment.CreatedAtUtc));
})
.RequireAuthorization("OrganizerOnly")
.WithName("AssignCoordinator")
.WithOpenApi();

app.MapDelete("/api/checkin/assignments/{id:int}", async (
    int id,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory) =>
{
    var organizerId = GetUserId(user);
    if (organizerId is null) return Results.Forbid();

    var assignment = await db.CoordinatorAssignments.FindAsync(id);
    if (assignment is null) return Results.NotFound(new ErrorDto("Assignment not found."));

    var catalogClient = httpClientFactory.CreateClient("CatalogService");
    try
    {
        var response = await catalogClient.GetAsync($"/api/catalog/events/{assignment.EventId}/ticket-snapshot");
        if (response.IsSuccessStatusCode)
        {
            var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
            if (eventData is not null && eventData.OrganizerId != organizerId.Value)
                return Results.Forbid();
        }
    }
    catch
    {
        return Results.Json(new ErrorDto("Service temporarily unavailable."), statusCode: 503);
    }

    assignment.Status = AssignmentStatus.Revoked;
    assignment.UpdatedAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.RequireAuthorization("OrganizerOnly")
.WithName("RevokeAssignment")
.WithOpenApi();

app.Run();

// --- Internal DTO for CatalogService responses ---
internal record CatalogEventResponse(
    int Id,
    string? Title,
    string? Description,
    string? Location,
    string? Venue,
    decimal Price,
    DateTime EventDate,
    int? UtcOffsetMinutes,
    int CategoryId,
    int OrganizerId,
    string? OrganizerName,
    string? ImageUrl,
    int AvailableTickets,
    string? TicketTiersJson,
    DateTime CreatedAt,
    bool IsHidder
);

public partial class Program { }
