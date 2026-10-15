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

// 3. Register Outbox Service, Analytics, Reports & Background Workers
builder.Services.AddSingleton<IRealtimeAnalyticsNotifier, RealtimeAnalyticsNotifier>();
builder.Services.AddScoped<IEventAnalyticsService, EventAnalyticsService>();
builder.Services.AddScoped<IEventReportService, EventReportService>();
builder.Services.AddScoped<ICheckInOutboxService, CheckInOutboxService>();
builder.Services.AddSingleton<CheckInConcurrencyGate>();
builder.Services.AddSingleton(TimeProvider.System);
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

    options.AddPolicy("TicketScanAccess", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("Organizer", "Company", "Admin", "Coordinator"));

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

static async Task<bool> IsEventExpiredAsync(
    int eventId,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider)
{
    try
    {
        var response = await httpClientFactory.CreateClient("CatalogService")
            .GetAsync($"/api/catalog/events/{eventId}/ticket-snapshot");
        if (!response.IsSuccessStatusCode) return false;

        var eventData = await response.Content.ReadFromJsonAsync<CatalogEventResponse>();
        if (eventData is null) return false;

        return ToAssignedEventDto(eventData, timeProvider.GetUtcNow().UtcDateTime).ScannerCategory
            == ScannerEventCategory.Past;
    }
    catch
    {
        return false;
    }
}

static async Task<bool> CanScanEventAsync(
    int eventId,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory)
{
    if (user.IsInRole("Coordinator"))
    {
        var userId = GetUserId(user);
        if (userId is null) return false;
        return await db.CoordinatorAssignments.AsNoTracking()
            .AnyAsync(a => a.CoordinatorUserId == userId.Value
                        && a.EventId == eventId
                        && a.Status == AssignmentStatus.Active);
    }
    return await ValidateEventOwnershipAsync(eventId, user, httpClientFactory);
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
    if (!await CanScanEventAsync(request.EventId, user, db, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var qrCode = request.QrCode.Trim();
    var qrHash = outboxService.ComputeSha256Hash(qrCode);

    // Call BookingService internal endpoint
    var bookingClient = httpClientFactory.CreateClient("BookingService");
    TicketLookupResponse? ticketInfo = null;
    try
    {
        var response = await bookingClient.GetAsync(
            $"/api/booking/internal/tickets/{Uri.EscapeDataString(qrCode)}");

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

    if (!string.Equals(ticketInfo.Status, "Confirmed", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ticketInfo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new VerifyTicketResponse(
            IsValid: false,
            Status: "NotEligible",
            Message: $"Ticket is not eligible for check-in (Status: {ticketInfo.Status}).",
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
.RequireAuthorization("TicketScanAccess")
.WithName("VerifyTicket")
.WithOpenApi();

// =============================================================================
// 1b. POST /api/checkin/validate – SCRUM-31 QR Ticket Validation
// Validates ticket authenticity, event matching, eligibility, and prior usage
// without consuming or marking the ticket as used.
// Returns: VALID, INVALID, WRONG_EVENT, ALREADY_USED, NOT_ELIGIBLE
// =============================================================================
app.MapPost("/api/checkin/validate", async (
    ValidateTicketRequest request,
    ClaimsPrincipal user,
    CheckInDbContext db,
    ICheckInOutboxService outboxService,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) =>
{
    var eventIdValue = request.EventId ?? 0;
    if (string.IsNullOrWhiteSpace(request.EffectiveCode) || eventIdValue <= 0)
        return Results.BadRequest(new ErrorDto("Code and valid EventId are required."));

    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    // Enforce JWT authentication, Organizer/Admin authorization & event ownership
    if (!await CanScanEventAsync(eventIdValue, user, db, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var code = request.EffectiveCode;

    // Call BookingService internal endpoint to validate ticket code / existence
    var bookingClient = httpClientFactory.CreateClient("BookingService");
    TicketLookupResponse? ticketInfo = null;
    try
    {
        var response = await bookingClient.GetAsync(
            $"/api/booking/internal/tickets/{Uri.EscapeDataString(code)}");

        if (response.IsSuccessStatusCode)
        {
            ticketInfo = await response.Content.ReadFromJsonAsync<TicketLookupResponse>();
        }
    }
    catch
    {
        return Results.Json(new ErrorDto("Booking service temporarily unavailable."), statusCode: 503);
    }

    // 1. Not found / malformed / not authentic -> INVALID
    if (ticketInfo is null || ticketInfo.TicketId is null)
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.Invalid,
            Message: "Ticket was not found or the validation code is invalid.",
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

    // 2. Belongs to different event -> WRONG_EVENT
    if (ticketInfo.EventId != request.EventId)
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.WrongEvent,
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

    // 2b. Cancelled -> CANCELLED
    if (string.Equals(ticketInfo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.Cancelled,
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

    // 2c. Event ended -> EXPIRED
    if (await IsEventExpiredAsync(eventIdValue, httpClientFactory, timeProvider))
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.Expired,
            Message: "This event has ended. The ticket has expired.",
            TicketId: ticketInfo.TicketId,
            EventId: eventIdValue,
            BookingId: ticketInfo.BookingId,
            AttendeeId: ticketInfo.AttendeeId,
            AttendeeName: ticketInfo.AttendeeName,
            TicketType: ticketInfo.TicketType,
            SeatCode: ticketInfo.SeatCode,
            ScannedAt: null
        ));
    }

    // 3. Status not confirmed (e.g., Pending, Refunded) -> NOT_ELIGIBLE
    if (!string.Equals(ticketInfo.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.NotEligible,
            Message: $"Ticket is not eligible for check-in (Status: {ticketInfo.Status}).",
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

    // 4. Already checked in -> ALREADY_USED
    var existingScan = await db.ScannedTickets
        .AsNoTracking()
        .FirstOrDefaultAsync(t => t.TicketId == ticketInfo.TicketId && t.Status == ScannedTicketStatus.CheckedIn);

    if (existingScan is not null)
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.AlreadyUsed,
            Message: $"Ticket has already been used and checked in at {existingScan.ScannedAt:u}.",
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

    // 5. Eligible, authentic, unused -> VALID (Validation does not mark ticket as used)
    return Results.Ok(new ValidateTicketResponse(
        IsValid: true,
        Status: TicketValidationStatus.Valid,
        Message: "Ticket is valid for admission.",
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
.RequireAuthorization("TicketScanAccess")
.WithName("ValidateTicket")
.WithOpenApi();

// =============================================================================
// 2. POST /api/checkin/scan – Scan and check in a ticket
// =============================================================================
app.MapPost("/api/checkin/scan", async (
    ScanTicketRequest request,
    ClaimsPrincipal user,
    CheckInDbContext db,
    ICheckInOutboxService outboxService,
    IHttpClientFactory httpClientFactory,
    IRealtimeAnalyticsNotifier realtimeNotifier,
    CheckInConcurrencyGate concurrencyGate,
    TimeProvider timeProvider) =>
{
    if (string.IsNullOrWhiteSpace(request.QrCode) || request.EventId <= 0)
        return Results.BadRequest(new ErrorDto("QrCode and valid EventId are required."));

    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    // Verify event ownership for non-admin
    if (!await CanScanEventAsync(request.EventId, user, db, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var qrCode = request.QrCode.Trim();
    var qrHash = outboxService.ComputeSha256Hash(qrCode);

    // 1. Call BookingService to validate authenticity and booking status
    var bookingClient = httpClientFactory.CreateClient("BookingService");
    TicketLookupResponse? ticketInfo = null;
    try
    {
        var response = await bookingClient.GetAsync(
            $"/api/booking/internal/tickets/{Uri.EscapeDataString(qrCode)}");

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
            // Unique negative id: ScannedTickets.TicketId has a unique index, so rejected rows must not collide.
            TicketId = -Random.Shared.Next(1, int.MaxValue),
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

    // Server-side revalidation: expired events cannot be checked in
    if (await IsEventExpiredAsync(request.EventId, httpClientFactory, timeProvider))
    {
        return Results.Ok(new ScanTicketResponse(
            Success: false,
            Status: "Rejected",
            Message: "This event has ended. The ticket has expired.",
            ScannedTicket: null
        ));
    }

    if (!string.Equals(ticketInfo.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new ScanTicketResponse(
            Success: false,
            Status: "Rejected",
            Message: $"Ticket is not eligible for check-in (Status: {ticketInfo.Status}).",
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

    // Guard atomic scan with in-process lock per ticket ID
    using (await concurrencyGate.LockAsync(ticketId))
    {
        // 2. Prevent duplicate scan using DB query first
        var existingScan = await db.ScannedTickets
            .AsNoTracking()
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
            realtimeNotifier.NotifyEventUpdated(request.EventId);
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
    }
})
.RequireAuthorization("TicketScanAccess")
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

    if (!await CanScanEventAsync(eventId, user, db, httpClientFactory))
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
.RequireAuthorization("TicketScanAccess")
.WithName("GetAttendanceStats")
.WithOpenApi();

// =============================================================================
// 5. GET /api/checkin/analytics/event/{eventId}/summary – Event Analytics Summary
// =============================================================================
app.MapGet("/api/checkin/analytics/event/{eventId:int}/summary", async (
    int eventId,
    ClaimsPrincipal user,
    IEventAnalyticsService analyticsService,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var summary = await analyticsService.GetEventAnalyticsSummaryAsync(eventId, ct);
    if (summary is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    return Results.Ok(summary);
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("GetEventAnalyticsSummary")
.WithOpenApi();

// =============================================================================
// 6. GET /api/checkin/analytics/event/{eventId}/sales – Event Sales & Revenue Analytics
// =============================================================================
app.MapGet("/api/checkin/analytics/event/{eventId:int}/sales", async (
    int eventId,
    ClaimsPrincipal user,
    IEventAnalyticsService analyticsService,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var sales = await analyticsService.GetEventSalesAnalyticsAsync(eventId, ct);
    if (sales is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    return Results.Ok(sales);
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("GetEventSalesAnalytics")
.WithOpenApi();

// =============================================================================
// 7. GET /api/checkin/analytics/event/{eventId}/attendance – Event Attendance Analytics
// =============================================================================
app.MapGet("/api/checkin/analytics/event/{eventId:int}/attendance", async (
    int eventId,
    ClaimsPrincipal user,
    IEventAnalyticsService analyticsService,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var attendance = await analyticsService.GetEventAttendanceAnalyticsAsync(eventId, ct);
    if (attendance is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    return Results.Ok(attendance);
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("GetEventAttendanceAnalytics")
.WithOpenApi();

// =============================================================================
// 8. SCRUM-36: Real-Time Event Check-In Analytics REST API
// =============================================================================
static async Task<IResult> HandleRealtimeAnalytics(
    int eventId,
    HttpContext httpContext,
    ClaimsPrincipal user,
    IEventAnalyticsService analyticsService,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct)
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var realtime = await analyticsService.GetEventRealtimeAnalyticsAsync(eventId, ct);
    if (realtime is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    // Efficient polling with stable ETag based on check-in state
    var etag = $"\"{eventId}-{realtime.VenueCapacity}-{realtime.TotalSoldTickets}-{realtime.TotalUniqueCheckedInTickets}-{realtime.TotalRejectedScans}\"";
    if (httpContext.Request.Headers.IfNoneMatch == etag)
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    httpContext.Response.Headers.ETag = etag;
    httpContext.Response.Headers.CacheControl = "private, max-age=2";

    return Results.Ok(realtime);
}

// =============================================================================
// GET /api/analytics/events/{eventId:int} – Full Attendance Dashboard Analytics REST API
// Accessible to Organizer and Admin roles, returning comprehensive real-time metrics
// =============================================================================
static async Task<IResult> HandleFullAttendanceDashboardAnalytics(
    int eventId,
    HttpContext httpContext,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IEventAnalyticsService analyticsService,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct)
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    // 1. Fetch Catalog Event info & ticket tiers
    var eventData = await analyticsService.GetCatalogEventAsync(eventId, ct);
    if (eventData is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    // 2. Fetch Booking Metrics & tier metrics
    var bookingMetrics = await analyticsService.GetBookingMetricsAsync(eventId, ct);
    var totalTicketsSold = bookingMetrics?.TotalTicketsSold ?? 0;
    var totalRevenue = bookingMetrics?.TotalRevenue ?? 0m;

    // Resolve venue capacity from tiers or snapshot
    int totalCapacity = 0;
    var tierDefinitions = new List<(int? Id, string Name, decimal Price, int Capacity)>();

    if (!string.IsNullOrWhiteSpace(eventData.TicketTiersJson))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(eventData.TicketTiersJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var tierEl in doc.RootElement.EnumerateArray())
                {
                    int? tierId = tierEl.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var tid) ? tid : null;
                    string name = tierEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "General" : "General";
                    decimal price = tierEl.TryGetProperty("price", out var priceProp) && priceProp.TryGetDecimal(out var p) ? p : 0m;
                    int quantity = tierEl.TryGetProperty("quantity", out var qProp) && qProp.TryGetInt32(out var q) ? q : 0;
                    tierDefinitions.Add((tierId, name, price, quantity));
                    totalCapacity += quantity;
                }
            }
        }
        catch
        {
            // fallback
        }
    }

    if (totalCapacity == 0)
    {
        totalCapacity = eventData.AvailableTickets > 0 ? eventData.AvailableTickets : Math.Max(totalTicketsSold, 100);
    }

    // 3. Fetch check-in records from CheckInDbContext
    var scannedTickets = await db.ScannedTickets
        .AsNoTracking()
        .Where(t => t.EventId == eventId)
        .ToListAsync(ct);

    var checkedInList = scannedTickets
        .Where(t => t.Status == ScannedTicketStatus.CheckedIn)
        .ToList();

    var checkedInAttendees = checkedInList
        .Select(t => t.TicketId)
        .Distinct()
        .Count();

    // Also support fallback from CheckInRecords table if ScannedTickets count is smaller
    var checkInRecordsCount = await db.CheckInRecords
        .AsNoTracking()
        .Where(r => r.EventId == eventId)
        .Select(r => r.TicketCode)
        .Distinct()
        .CountAsync(ct);

    if (checkInRecordsCount > checkedInAttendees)
    {
        checkedInAttendees = checkInRecordsCount;
    }

    var remainingAttendees = Math.Max(0, totalTicketsSold - checkedInAttendees);
    var attendancePercentage = totalTicketsSold > 0
        ? Math.Round(((double)checkedInAttendees / totalTicketsSold) * 100.0, 2)
        : 0.0;
    var checkInRate = attendancePercentage;
    var venueOccupancy = checkedInAttendees;
    var venueOccupancyPercentage = totalCapacity > 0
        ? Math.Round(((double)checkedInAttendees / totalCapacity) * 100.0, 2)
        : 0.0;
    var salesFillRate = totalCapacity > 0
        ? Math.Round(((double)totalTicketsSold / totalCapacity) * 100.0, 2)
        : 0.0;

    // 4. Compute tierBreakdowns: [{ id, name, price, capacity, sold, available, revenue }]
    var tierBreakdowns = new List<object>();
    if (tierDefinitions.Count > 0)
    {
        foreach (var def in tierDefinitions)
        {
            var tierMetric = bookingMetrics?.TierMetrics?.FirstOrDefault(m =>
                (def.Id.HasValue && m.TicketTierId.HasValue && m.TicketTierId == def.Id) ||
                string.Equals(m.TierName, def.Name, StringComparison.OrdinalIgnoreCase));

            var sold = tierMetric?.SoldQuantity ?? 0;
            var rev = tierMetric?.Revenue ?? (sold * def.Price);
            var avail = Math.Max(0, def.Capacity - sold);

            tierBreakdowns.Add(new
            {
                id = def.Id?.ToString() ?? def.Name,
                name = def.Name,
                price = def.Price,
                capacity = def.Capacity,
                sold = sold,
                available = avail,
                revenue = rev
            });
        }
    }
    else if (bookingMetrics?.TierMetrics != null && bookingMetrics.TierMetrics.Count > 0)
    {
        foreach (var tm in bookingMetrics.TierMetrics)
        {
            var cap = tm.SoldQuantity;
            tierBreakdowns.Add(new
            {
                id = tm.TicketTierId?.ToString() ?? tm.TierName,
                name = tm.TierName,
                price = tm.UnitPrice,
                capacity = cap,
                sold = tm.SoldQuantity,
                available = 0,
                revenue = tm.Revenue
            });
        }
    }
    else
    {
        tierBreakdowns.Add(new
        {
            id = "ga",
            name = "General Admission",
            price = eventData.Price,
            capacity = totalCapacity,
            sold = totalTicketsSold,
            available = Math.Max(0, totalCapacity - totalTicketsSold),
            revenue = totalRevenue
        });
    }

    // 5. Compute hourlyAttendance: [{ hour, count }]
    var hourlyAttendance = checkedInList
        .GroupBy(t => t.ScannedAt.ToString("HH:00"))
        .OrderBy(g => g.Key)
        .Select(g => new
        {
            hour = g.Key,
            count = g.Select(x => x.TicketId).Distinct().Count()
        })
        .ToList();

    if (hourlyAttendance.Count == 0)
    {
        var nowHour = DateTime.UtcNow.ToString("HH:00");
        hourlyAttendance.Add(new { hour = nowHour, count = checkedInAttendees });
    }

    // 6. Compute salesTimeline: last 7 days with [{ date, label, ticketsSold, revenue }]
    var nowUtc = DateTime.UtcNow.Date;
    var salesTimeline = new List<object>();
    for (int i = 6; i >= 0; i--)
    {
        var dayDate = nowUtc.AddDays(-i);
        var dateStr = dayDate.ToString("yyyy-MM-dd");
        var labelStr = dayDate.ToString("MMM d");

        // Distribute or calculate actual daily sales
        var daySold = totalTicketsSold > 0 ? (int)Math.Max(0, Math.Round((double)totalTicketsSold / 7.0 * (0.6 + (6 - i) * 0.08))) : 0;
        if (i == 0 && totalTicketsSold > 0)
        {
            daySold = Math.Max(daySold, totalTicketsSold - (totalTicketsSold / 7 * 6));
        }
        var dayRev = totalTicketsSold > 0 ? Math.Round((totalRevenue / totalTicketsSold) * daySold, 2) : 0m;

        salesTimeline.Add(new
        {
            date = dateStr,
            label = labelStr,
            ticketsSold = daySold,
            revenue = dayRev
        });
    }

    var response = new
    {
        eventId,
        title = eventData.Title ?? $"Event #{eventId}",
        totalCapacity,
        totalTicketsSold,
        totalRevenue,
        checkedInAttendees,
        remainingAttendees,
        attendancePercentage,
        checkInRate,
        venueOccupancy,
        venueOccupancyPercentage,
        salesFillRate,
        tierBreakdowns,
        hourlyAttendance,
        salesTimeline
    };

    return Results.Ok(response);
}

app.MapGet("/api/analytics/events/{eventId:int}", HandleFullAttendanceDashboardAnalytics)
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetFullAttendanceEventAnalytics")
    .WithOpenApi();

app.MapGet("/api/checkin/analytics/events/{eventId:int}", HandleFullAttendanceDashboardAnalytics)
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetFullAttendanceCheckinEventAnalytics")
    .WithOpenApi();

app.MapGet("/api/checkin/analytics/event/{eventId:int}/realtime", HandleRealtimeAnalytics)
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventRealtimeAnalytics")
    .WithOpenApi();

app.MapGet("/api/checkin/event/{eventId:int}/realtime-analytics", HandleRealtimeAnalytics)
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventRealtimeAnalyticsAlias")
    .WithOpenApi();

app.MapGet("/api/checkin/analytics/event/{eventId:int}", HandleRealtimeAnalytics)
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventAnalyticsDefault")
    .WithOpenApi();

// =============================================================================
// 9. SCRUM-36: Server-Sent Events (SSE) Live Stream Endpoint
// =============================================================================
app.MapGet("/api/checkin/analytics/event/{eventId:int}/live", async (
    int eventId,
    HttpContext httpContext,
    ClaimsPrincipal user,
    IEventAnalyticsService analyticsService,
    IRealtimeAnalyticsNotifier notifier,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var initial = await analyticsService.GetEventRealtimeAnalyticsAsync(eventId, ct);
    if (initial is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    httpContext.Response.Headers.Append("Content-Type", "text/event-stream");
    httpContext.Response.Headers.Append("Cache-Control", "no-cache, no-transform");
    httpContext.Response.Headers.Append("Connection", "keep-alive");

    var responseStream = httpContext.Response.Body;
    var clientToken = httpContext.RequestAborted;
    var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    // Initial snapshot
    var initialJson = System.Text.Json.JsonSerializer.Serialize(initial, jsonOptions);
    await responseStream.WriteAsync(Encoding.UTF8.GetBytes($"data: {initialJson}\n\n"), clientToken);
    await responseStream.FlushAsync(clientToken);

    using var subscriber = notifier.Subscribe(eventId);
    try
    {
        while (!clientToken.IsCancellationRequested)
        {
            var hasUpdate = await subscriber.WaitAsync(TimeSpan.FromSeconds(5), clientToken);
            if (clientToken.IsCancellationRequested) break;

            if (hasUpdate)
            {
                var updated = await analyticsService.GetEventRealtimeAnalyticsAsync(eventId, clientToken);
                if (updated != null)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(updated, jsonOptions);
                    await responseStream.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"), clientToken);
                    await responseStream.FlushAsync(clientToken);
                }
            }
            else
            {
                // Heartbeat keepalive comment
                await responseStream.WriteAsync(Encoding.UTF8.GetBytes(": keepalive\n\n"), clientToken);
                await responseStream.FlushAsync(clientToken);
            }
        }
    }
    finally
    {
        notifier.Unsubscribe(eventId, subscriber);
    }

    return Results.Empty;
})
.RequireAuthorization("CheckInScannerAccess")
.WithName("LiveStreamEventAnalytics")
.WithOpenApi();

// =============================================================================
// 10. SCRUM-37: Dynamic Sales & Attendance Reports APIs (JSON, CSV, PDF)
// =============================================================================
static async Task<IResult> HandleSalesReport(
    int eventId,
    string? format,
    HttpContext httpContext,
    ClaimsPrincipal user,
    IEventReportService reportService,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct)
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var report = await reportService.GenerateSalesReportAsync(eventId, ct);
    if (report is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    var accept = httpContext.Request.Headers.Accept.ToString();
    var fmt = (format ?? string.Empty).Trim().ToLowerInvariant();

    if (fmt == "csv" || accept.Contains("text/csv"))
    {
        var csvBytes = CsvReportGenerator.GenerateSalesReportCsv(report);
        return Results.File(csvBytes, "text/csv; charset=utf-8", $"sales-report-event-{eventId}.csv");
    }

    if (fmt == "pdf" || accept.Contains("application/pdf"))
    {
        var pdfBytes = PdfReportGenerator.GenerateSalesReportPdf(report);
        return Results.File(pdfBytes, "application/pdf", $"sales-report-event-{eventId}.pdf");
    }

    return Results.Ok(report);
}

static async Task<IResult> HandleAttendanceReport(
    int eventId,
    string? format,
    HttpContext httpContext,
    ClaimsPrincipal user,
    IEventReportService reportService,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct)
{
    if (eventId <= 0) return Results.BadRequest(new ErrorDto("Valid EventId is required."));

    if (!await ValidateEventOwnershipAsync(eventId, user, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You do not own this event."), statusCode: 403);

    var report = await reportService.GenerateAttendanceReportAsync(eventId, ct);
    if (report is null)
        return Results.NotFound(new ErrorDto("Event not found."));

    var accept = httpContext.Request.Headers.Accept.ToString();
    var fmt = (format ?? string.Empty).Trim().ToLowerInvariant();

    if (fmt == "csv" || accept.Contains("text/csv"))
    {
        var csvBytes = CsvReportGenerator.GenerateAttendanceReportCsv(report);
        return Results.File(csvBytes, "text/csv; charset=utf-8", $"attendance-report-event-{eventId}.csv");
    }

    if (fmt == "pdf" || accept.Contains("application/pdf"))
    {
        var pdfBytes = PdfReportGenerator.GenerateAttendanceReportPdf(report);
        return Results.File(pdfBytes, "application/pdf", $"attendance-report-event-{eventId}.pdf");
    }

    return Results.Ok(report);
}

// Sales Report Endpoints
app.MapGet("/api/checkin/reports/event/{eventId:int}/sales", (int eventId, string? format, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleSalesReport(eventId, format, ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventSalesReport")
    .WithOpenApi();

app.MapGet("/api/checkin/reports/event/{eventId:int}/sales/csv", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleSalesReport(eventId, "csv", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventSalesReportCsv")
    .WithOpenApi();

app.MapGet("/api/checkin/reports/event/{eventId:int}/sales/pdf", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleSalesReport(eventId, "pdf", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventSalesReportPdf")
    .WithOpenApi();

// Attendance Report Endpoints
app.MapGet("/api/checkin/reports/event/{eventId:int}/attendance", (int eventId, string? format, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleAttendanceReport(eventId, format, ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventAttendanceReport")
    .WithOpenApi();

app.MapGet("/api/checkin/reports/event/{eventId:int}/attendance/csv", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleAttendanceReport(eventId, "csv", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventAttendanceReportCsv")
    .WithOpenApi();

app.MapGet("/api/checkin/reports/event/{eventId:int}/attendance/pdf", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleAttendanceReport(eventId, "pdf", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess")
    .WithName("GetEventAttendanceReportPdf")
    .WithOpenApi();

// Report Aliases for Gateway & Analytics compatibility
app.MapGet("/api/checkin/analytics/event/{eventId:int}/sales/export", (int eventId, string? format, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleSalesReport(eventId, format ?? "csv", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");

app.MapGet("/api/checkin/analytics/event/{eventId:int}/attendance/export", (int eventId, string? format, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleAttendanceReport(eventId, format ?? "csv", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");

app.MapGet("/api/reports/event/{eventId:int}/sales", (int eventId, string? format, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleSalesReport(eventId, format, ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");

app.MapGet("/api/reports/event/{eventId:int}/sales/csv", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleSalesReport(eventId, "csv", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");

app.MapGet("/api/reports/event/{eventId:int}/sales/pdf", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleSalesReport(eventId, "pdf", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");

app.MapGet("/api/reports/event/{eventId:int}/attendance", (int eventId, string? format, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleAttendanceReport(eventId, format, ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");

app.MapGet("/api/reports/event/{eventId:int}/attendance/csv", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleAttendanceReport(eventId, "csv", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");

app.MapGet("/api/reports/event/{eventId:int}/attendance/pdf", (int eventId, HttpContext ctx, ClaimsPrincipal u, IEventReportService rs, IHttpClientFactory hcf, CancellationToken ct) =>
    HandleAttendanceReport(eventId, "pdf", ctx, u, rs, hcf, ct))
    .RequireAuthorization("CheckInScannerAccess");


// =============================================================================
// Backward compatibility coordinator endpoints & assignments
// =============================================================================

// Classifies an event by its local calendar day (EventDate is UTC; UtcOffsetMinutes
// gives the venue offset). The scanner may only be launched on the event day.
static AssignedEventDto ToAssignedEventDto(CatalogEventResponse e, DateTime nowUtc)
{
    var offset = TimeSpan.FromMinutes(e.UtcOffsetMinutes ?? 0);
    var eventUtc = e.EventDate.Kind == DateTimeKind.Local ? e.EventDate.ToUniversalTime() : e.EventDate;
    var eventDay = DateOnly.FromDateTime(eventUtc + offset);
    var today = DateOnly.FromDateTime(nowUtc + offset);

    var category = eventDay == today ? ScannerEventCategory.Today
        : eventDay > today ? ScannerEventCategory.Upcoming
        : ScannerEventCategory.Past;

    return new AssignedEventDto(
        e.Id,
        e.Title ?? string.Empty,
        e.EventDate,
        e.Venue,
        e.Location,
        e.IsHidder,
        category,
        category == ScannerEventCategory.Today);
}

// Today first, then upcoming (soonest first), then past (most recent first).
static List<AssignedEventDto> OrderForScannerPortal(List<AssignedEventDto> events) =>
    events.Where(e => e.ScannerCategory == ScannerEventCategory.Today).OrderBy(e => e.EventDate)
        .Concat(events.Where(e => e.ScannerCategory == ScannerEventCategory.Upcoming).OrderBy(e => e.EventDate))
        .Concat(events.Where(e => e.ScannerCategory == ScannerEventCategory.Past).OrderByDescending(e => e.EventDate))
        .ToList();
app.MapGet("/api/checkin/coordinator/events", async (
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) =>
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
                    assignedEvents.Add(ToAssignedEventDto(eventData, timeProvider.GetUtcNow().UtcDateTime));
                }
            }
        }
        catch { }
    }

    return Results.Ok(OrderForScannerPortal(assignedEvents));
})
.RequireAuthorization("CoordinatorScannerAccess")
.WithName("GetCoordinatorAssignedEvents")
.WithOpenApi();

app.MapGet("/api/coordinator/events", async (
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) =>
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
                    assignedEvents.Add(ToAssignedEventDto(eventData, timeProvider.GetUtcNow().UtcDateTime));
                }
            }
        }
        catch { }
    }

    return Results.Ok(OrderForScannerPortal(assignedEvents));
})
.RequireAuthorization("CoordinatorScannerAccess")
.WithName("GetCoordinatorAssignedEventsDirect")
.WithOpenApi();

app.MapGet("/api/checkin/coordinator/events/{eventId:int}/access", async (
    int eventId,
    ClaimsPrincipal user,
    CheckInDbContext db,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) =>
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

        var eventDto = ToAssignedEventDto(eventData, timeProvider.GetUtcNow().UtcDateTime);
        if (!eventDto.CanLaunchScanner)
        {
            var reason = eventDto.ScannerCategory == ScannerEventCategory.Past
                ? "This event has ended. The scanner is no longer available."
                : "The scanner is only available on the event day.";
            return Results.Json(new ErrorDto(reason), statusCode: 403);
        }

        return Results.Ok(new ScannerAccessDto(
            EventId: eventId,
            CanAccessScanner: true,
            Event: eventDto
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
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) =>
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

        var eventDto = ToAssignedEventDto(eventData, timeProvider.GetUtcNow().UtcDateTime);
        if (!eventDto.CanLaunchScanner)
        {
            var reason = eventDto.ScannerCategory == ScannerEventCategory.Past
                ? "This event has ended. The scanner is no longer available."
                : "The scanner is only available on the event day.";
            return Results.Json(new ErrorDto(reason), statusCode: 403);
        }

        return Results.Ok(new ScannerAccessDto(
            EventId: eventId,
            CanAccessScanner: true,
            Event: eventDto
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

app.MapPost("/api/coordinator/events/{eventId:int}/validate", async (
    int eventId,
    ValidateTicketRequest request,
    ClaimsPrincipal user,
    CheckInDbContext db,
    ICheckInOutboxService outboxService,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider) =>
{
    var ticketCode = request.EffectiveCode;
    if (string.IsNullOrWhiteSpace(ticketCode) || eventId <= 0)
        return Results.BadRequest(new ErrorDto("Ticket code and valid eventId are required."));

    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    if (!await CanScanEventAsync(eventId, user, db, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You are not authorized to scan for this event."), statusCode: 403);

    // Call BookingService internal endpoint to validate ticket code & existence
    var bookingClient = httpClientFactory.CreateClient("BookingService");
    TicketLookupResponse? ticketInfo = null;
    try
    {
        var response = await bookingClient.GetAsync(
            $"/api/booking/internal/tickets/{Uri.EscapeDataString(ticketCode)}");

        if (response.IsSuccessStatusCode)
        {
            ticketInfo = await response.Content.ReadFromJsonAsync<TicketLookupResponse>();
        }
    }
    catch
    {
        return Results.Json(new ErrorDto("Booking service temporarily unavailable."), statusCode: 503);
    }

    // 1. Not found / malformed / not authentic -> INVALID
    if (ticketInfo is null || ticketInfo.TicketId is null)
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.Invalid,
            Message: "Ticket was not found or the validation code is invalid.",
            TicketId: null,
            EventId: eventId,
            BookingId: null,
            AttendeeId: null,
            AttendeeName: null,
            TicketType: null,
            SeatCode: null,
            ScannedAt: null
        ));
    }

    // 2. Belongs to different event -> WRONG_EVENT
    if (ticketInfo.EventId != eventId)
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.WrongEvent,
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

    // 2b. Cancelled -> CANCELLED
    if (string.Equals(ticketInfo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.Cancelled,
            Message: "This ticket has been cancelled.",
            TicketId: ticketInfo.TicketId,
            EventId: eventId,
            BookingId: ticketInfo.BookingId,
            AttendeeId: ticketInfo.AttendeeId,
            AttendeeName: ticketInfo.AttendeeName,
            TicketType: ticketInfo.TicketType,
            SeatCode: ticketInfo.SeatCode,
            ScannedAt: null
        ));
    }

    // 2c. Event ended -> EXPIRED
    if (await IsEventExpiredAsync(eventId, httpClientFactory, timeProvider))
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.Expired,
            Message: "This event has ended. The ticket has expired.",
            TicketId: ticketInfo.TicketId,
            EventId: eventId,
            BookingId: ticketInfo.BookingId,
            AttendeeId: ticketInfo.AttendeeId,
            AttendeeName: ticketInfo.AttendeeName,
            TicketType: ticketInfo.TicketType,
            SeatCode: ticketInfo.SeatCode,
            ScannedAt: null
        ));
    }

    // 3. Status not confirmed -> NOT_ELIGIBLE
    if (!string.Equals(ticketInfo.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.NotEligible,
            Message: $"Ticket is not eligible for check-in (Status: {ticketInfo.Status}).",
            TicketId: ticketInfo.TicketId,
            EventId: eventId,
            BookingId: ticketInfo.BookingId,
            AttendeeId: ticketInfo.AttendeeId,
            AttendeeName: ticketInfo.AttendeeName,
            TicketType: ticketInfo.TicketType,
            SeatCode: ticketInfo.SeatCode,
            ScannedAt: null
        ));
    }

    // 4. Already checked in -> ALREADY_USED
    var existingScan = await db.ScannedTickets
        .AsNoTracking()
        .FirstOrDefaultAsync(t => t.TicketId == ticketInfo.TicketId && t.Status == ScannedTicketStatus.CheckedIn);

    if (existingScan is not null)
    {
        return Results.Ok(new ValidateTicketResponse(
            IsValid: false,
            Status: TicketValidationStatus.AlreadyUsed,
            Message: $"Ticket has already been used and checked in at {existingScan.ScannedAt:u}.",
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

    // 5. Eligible, authentic, unused -> VALID (Validation without check-in)
    return Results.Ok(new ValidateTicketResponse(
        IsValid: true,
        Status: TicketValidationStatus.Valid,
        Message: "Ticket is valid for admission.",
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
.RequireAuthorization("TicketScanAccess")
.WithName("ValidateCoordinatorTicket")
.WithOpenApi();

app.MapPost("/api/coordinator/events/{eventId:int}/check-in", async (
    int eventId,
    CheckInRequest request,
    ClaimsPrincipal user,
    CheckInDbContext db,
    ICheckInOutboxService outboxService,
    IHttpClientFactory httpClientFactory,
    IRealtimeAnalyticsNotifier realtimeNotifier,
    CheckInConcurrencyGate concurrencyGate,
    TimeProvider timeProvider) =>
{
    var ticketCode = request.EffectiveCode;
    if (string.IsNullOrWhiteSpace(ticketCode) || eventId <= 0)
        return Results.BadRequest(new ErrorDto("Ticket code and valid eventId are required."));

    var userId = GetUserId(user);
    if (userId is null) return Results.Forbid();

    if (!await CanScanEventAsync(eventId, user, db, httpClientFactory))
        return Results.Json(new ErrorDto("Access denied. You are not authorized to scan for this event."), statusCode: 403);

    var qrHash = outboxService.ComputeSha256Hash(ticketCode);

    // Call BookingService internal endpoint to get authentic ticket info
    var bookingClient = httpClientFactory.CreateClient("BookingService");
    TicketLookupResponse? ticketInfo = null;
    try
    {
        var response = await bookingClient.GetAsync(
            $"/api/booking/internal/tickets/{Uri.EscapeDataString(ticketCode)}");

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
        var rejectedTicket = new ScannedTicket
        {
            TicketId = -Random.Shared.Next(1, int.MaxValue),
            EventId = eventId,
            BookingId = 0,
            AttendeeId = 0,
            ScannedBy = userId.Value,
            ScannedAt = DateTime.UtcNow,
            Status = ScannedTicketStatus.Rejected,
            QrCodeHash = qrHash,
            RejectionReason = "Ticket not found or invalid"
        };
        db.ScannedTickets.Add(rejectedTicket);
        await db.SaveChangesAsync();

        return Results.BadRequest(new ErrorDto("Invalid or unrecognized ticket code."));
    }

    var ticketId = ticketInfo.TicketId.Value;

    if (string.Equals(ticketInfo.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new ErrorDto("This ticket has been cancelled."));
    }

    if (await IsEventExpiredAsync(eventId, httpClientFactory, timeProvider))
    {
        return Results.BadRequest(new ErrorDto("This event has ended. The ticket has expired."));
    }

    if (!string.Equals(ticketInfo.Status, "Confirmed", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new ErrorDto($"Ticket is not eligible for check-in (Status: {ticketInfo.Status})."));
    }

    if (ticketInfo.EventId != eventId)
    {
        return Results.BadRequest(new ErrorDto("Ticket belongs to a different event."));
    }

    using (await concurrencyGate.LockAsync(ticketId))
    {
        var existingScan = await db.ScannedTickets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TicketId == ticketId && t.Status == ScannedTicketStatus.CheckedIn);

        if (existingScan is not null)
        {
            return Results.Conflict(new ErrorDto($"Ticket was already checked in at {existingScan.ScannedAt:u}."));
        }

        var scannedAt = DateTime.UtcNow;
        var newScan = new ScannedTicket
        {
            TicketId = ticketId,
            EventId = eventId,
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

        // Also add to legacy CheckInRecords for table consistency
        var existingCheckInRecord = await db.CheckInRecords
            .FirstOrDefaultAsync(r => r.TicketCode == ticketCode && r.EventId == eventId);

        if (existingCheckInRecord is null)
        {
            db.CheckInRecords.Add(new CheckInRecord
            {
                TicketCode = ticketCode,
                EventId = eventId,
                ScannedByCoordinatorId = userId.Value,
                CheckedInAtUtc = scannedAt,
                AttendeeName = ticketInfo.AttendeeName,
                TicketType = ticketInfo.TicketType,
                SeatCode = ticketInfo.SeatCode
            });
        }

        db.AuditLogs.Add(new CheckInAuditLog
        {
            TicketId = ticketId,
            EventId = eventId,
            Action = "CoordinatorTicketCheckedIn",
            UserId = userId.Value,
            TimestampUtc = scannedAt,
            Details = $"Ticket {ticketCode} checked in by coordinator {userId.Value}"
        });

        await outboxService.QueueEventAsync("ticket.checked-in", eventId.ToString(), new TicketCheckedInEvent(
            TicketId: ticketId,
            EventId: eventId,
            BookingId: ticketInfo.BookingId,
            AttendeeId: ticketInfo.AttendeeId ?? 0,
            ScannedBy: userId.Value,
            ScannedAt: scannedAt
        ));

        await db.SaveChangesAsync();

        // Broadcast real-time analytics
        realtimeNotifier.NotifyEventUpdated(eventId);

        return Results.Ok(new
        {
            isValid = true,
            status = "CHECKED_IN",
            ticketId = ticketId,
            ticketCode = ticketCode,
            eventId = eventId,
            attendeeName = ticketInfo.AttendeeName,
            ticketType = ticketInfo.TicketType,
            seatCode = ticketInfo.SeatCode,
            checkedInAt = scannedAt,
            message = "Check-in confirmed successfully!"
        });
    }
})
.RequireAuthorization("TicketScanAccess")
.WithName("ConfirmCoordinatorTicketCheckIn")
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

public partial class Program { }

/// <summary>
/// Thread-safe in-process concurrency gate to prevent race conditions during ticket scan/check-in.
/// </summary>
public sealed class CheckInConcurrencyGate
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _ticketLocks = new();

    public async Task<IDisposable> LockAsync(int ticketId, CancellationToken cancellationToken = default)
    {
        var sem = _ticketLocks.GetOrAdd(ticketId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken);
        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _semaphore.Release();
            }
        }
    }
}
