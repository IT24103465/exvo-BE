using Exvo.CatalogService;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Exvo.CatalogService.Data;
using Exvo.CatalogService.Models;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Context
var connectionString = builder.Configuration["EXVO_CATALOG_MYSQL_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Server=localhost;Port=3306;Database=exvo_catalog_db;Uid=root;Pwd=;SslMode=None;AllowPublicKeyRetrieval=True;";

if (!connectionString.Contains("AllowPublicKeyRetrieval", StringComparison.OrdinalIgnoreCase))
{
    connectionString += ";AllowPublicKeyRetrieval=True;";
}

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 30)), mySqlOptions => mySqlOptions.EnableRetryOnFailure()));
builder.Services.AddSingleton(TimeProvider.System);
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<ExpiredEventVisibilityWorker>();

// 2. JWT Authentication Setup
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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Organizer", policy => policy.RequireAuthenticatedUser().RequireRole("Organizer", "Company"));
});

// 3. Swagger / OpenAPI with Bearer Auth
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Exvo Catalog API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer' followed by your token. Example: 'Bearer eyJhbGci...'"
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

// 4. CORS
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

// Preserve existing IDs, seed categories, and patch DB schema for TicketTiersJson & IsHidder on startup.
if (!app.Environment.IsEnvironment("Testing"))
    using (var scope = app.Services.CreateScope())
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await CategorySeeder.SeedAsync(db);
            // Additive upgrade, matching the existing startup schema upgrades. Null preserves
            // old records and lets EventSchedule apply the agreed Sri Lanka offset.
            try
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Events ADD COLUMN UtcOffsetMinutes int NULL;");
            }
            catch (MySqlException ex) when (ex.Number == 1060) { } // Column already exists.
            try
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE Events ADD COLUMN TicketTiersJson longtext NULL;");
            }
            catch { }
            try
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE Events ADD COLUMN IsHidder tinyint(1) NOT NULL DEFAULT 0;");
            }
            catch { }
            try
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE Events ADD COLUMN ArtistOrOrganizer varchar(200) NULL;");
            }
            catch { }
            try
            {
                db.Database.ExecuteSqlRaw("UPDATE Events SET EventDate = STR_TO_DATE(LEFT(EventDate, 19), '%Y-%m-%dT%H:%i:%s') WHERE EventDate LIKE '%T%';");
            }
            catch { }
            try
            {
                db.Database.ExecuteSqlRaw("UPDATE Events SET CreatedAt = NOW() WHERE CreatedAt LIKE '%T%';");
            }
            catch { }
        }
        catch { }
    }

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "CatalogService", status = "healthy" }))
    .AllowAnonymous();

// --- PUBLIC CATEGORIES API ---

app.MapGet("/api/catalog/categories", async (CatalogDbContext db) =>
{
    var categories = await db.Categories.ToListAsync();
    return Results.Ok(categories);
})
.WithName("GetCategories")
.WithOpenApi();

app.MapPost("/api/catalog/categories", async (Category category, CatalogDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(category.Name))
    {
        return Results.BadRequest(new { Message = "Category name is required." });
    }

    db.Categories.Add(category);
    await db.SaveChangesAsync();

    return Results.Created($"/api/catalog/categories/{category.Id}", category);
})
.WithName("CreateCategory")
.WithOpenApi();

// --- PUBLIC EVENTS SEARCH / BROWSING API ---

app.MapGet("/api/catalog/events", async (int? categoryId, CatalogDbContext db, TimeProvider clock) =>
{
    await EventSchedule.HideExpiredAsync(db, clock.GetUtcNow().UtcDateTime);
    var query = EventSchedule.Upcoming(db.Events.Include(e => e.Category), clock.GetUtcNow().UtcDateTime);

    // Filter out hidden events from public browsing
    query = query.Where(e => !e.IsHidder);

    if (categoryId.HasValue)
    {
        query = query.Where(e => e.CategoryId == categoryId.Value);
    }

    var events = await query.ToListAsync();

    var result = events.Select(e => new
    {
        e.Id,
        e.Title,
        e.Description,
        e.Location,
        e.Venue,
        e.Price,
        e.EventDate,
        e.UtcOffsetMinutes,
        StartsAtUtc = EventSchedule.StartsAtUtc(e),
        e.CategoryId,
        CategoryName = e.Category != null ? e.Category.Name : "Music & Concerts",
        Category = e.Category != null ? e.Category.Name : "Music & Concerts",
        e.OrganizerId,
        e.OrganizerName,
        e.ArtistOrOrganizer,
        e.ImageUrl,
        CoverImage = e.ImageUrl,
        e.AvailableTickets,
        e.TicketTiersJson,
        e.CreatedAt,
        e.IsHidder,
        IsHidden = e.IsHidder
    });

    return Results.Ok(result);
})
.WithName("GetEvents")
.WithOpenApi();

app.MapGet("/api/catalog/events/my-events", async (ClaimsPrincipal claimsPrincipal, CatalogDbContext db, TimeProvider clock) =>
{
    var organizerId = EventAccess.OrganizerId(claimsPrincipal);
    if (organizerId is null) return Results.Forbid();

    await EventSchedule.HideExpiredAsync(db, clock.GetUtcNow().UtcDateTime);
    var query = db.Events.Include(e => e.Category)
        .Where(e => e.OrganizerId == organizerId.Value);

    var events = await query.ToListAsync();

    var result = events.Select(e => new
    {
        e.Id,
        e.Title,
        e.Description,
        e.Location,
        e.Venue,
        e.Price,
        e.EventDate,
        e.UtcOffsetMinutes,
        StartsAtUtc = EventSchedule.StartsAtUtc(e),
        e.CategoryId,
        CategoryName = e.Category != null ? e.Category.Name : "Music & Concerts",
        Category = e.Category != null ? e.Category.Name : "Music & Concerts",
        e.OrganizerId,
        e.OrganizerName,
        e.ArtistOrOrganizer,
        e.ImageUrl,
        CoverImage = e.ImageUrl,
        e.AvailableTickets,
        e.TicketTiersJson,
        e.CreatedAt,
        e.IsHidder,
        IsHidden = e.IsHidder
    });

    return Results.Ok(result);
})
.RequireAuthorization("Organizer")
.WithName("GetMyEvents")
.WithOpenApi();

app.MapGet("/api/catalog/events/{id:int}", async (int id, ClaimsPrincipal user, CatalogDbContext db, TimeProvider clock) =>
{
    await EventSchedule.HideExpiredAsync(db, clock.GetUtcNow().UtcDateTime);
    var evt = await EventSchedule.Upcoming(db.Events.Include(e => e.Category), clock.GetUtcNow().UtcDateTime)
        .FirstOrDefaultAsync(e => e.Id == id);
    if (evt == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    if (evt.IsHidder && evt.OrganizerId != EventAccess.OrganizerId(user))
        return Results.NotFound(new { Message = "Event not found." });

    var result = new
    {
        evt.Id,
        evt.Title,
        evt.Description,
        evt.Location,
        evt.Venue,
        evt.Price,
        evt.EventDate,
        evt.UtcOffsetMinutes,
        StartsAtUtc = EventSchedule.StartsAtUtc(evt),
        evt.CategoryId,
        CategoryName = evt.Category != null ? evt.Category.Name : "Music & Concerts",
        Category = evt.Category != null ? evt.Category.Name : "Music & Concerts",
        evt.OrganizerId,
        evt.OrganizerName,
        evt.ArtistOrOrganizer,
        evt.ImageUrl,
        CoverImage = evt.ImageUrl,
        evt.AvailableTickets,
        evt.TicketTiersJson,
        evt.CreatedAt,
        evt.IsHidder,
        IsHidden = evt.IsHidder
    };

    return Results.Ok(result);
})
.WithName("GetEventById")
.WithOpenApi();

// --- EVENT CREATION API ---

app.MapPost("/api/catalog/events", async (ClaimsPrincipal claimsPrincipal, Event evt, CatalogDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(evt.Title))
    {
        return Results.BadRequest(new { Message = "Event title is required." });
    }

    var organizerId = EventAccess.OrganizerId(claimsPrincipal);
    if (organizerId is null) return Results.Forbid();
    evt.OrganizerId = organizerId.Value;
    evt.Id = 0;

    var scheduleError = EventAccess.ValidateSchedule(evt.EventDate, evt.UtcOffsetMinutes, DateTimeOffset.UtcNow);
    if (scheduleError is not null) return Results.BadRequest(new { Message = scheduleError });

    if (string.IsNullOrWhiteSpace(evt.OrganizerName))
    {
        var nameClaim = claimsPrincipal.FindFirst(ClaimTypes.Name)?.Value
                        ?? claimsPrincipal.FindFirst("name")?.Value
                        ?? claimsPrincipal.FindFirst("companyName")?.Value;

        if (!string.IsNullOrWhiteSpace(nameClaim))
        {
            evt.OrganizerName = nameClaim;
        }
        else
        {
            evt.OrganizerName = "EXVO Organizer";
        }
    }

    if (string.IsNullOrWhiteSpace(evt.ArtistOrOrganizer))
    {
        evt.ArtistOrOrganizer = evt.OrganizerName;
    }

    evt.CategoryId = await CategoryResolver.ResolveCategoryIdAsync(db, evt.CategoryId, evt.CategoryName);

    db.Events.Add(evt);
    await db.SaveChangesAsync();

    return Results.Created($"/api/catalog/events/{evt.Id}", evt);
})
.RequireAuthorization("Organizer")
.WithOpenApi();

// --- EVENT UPDATE API ---

app.MapPut("/api/catalog/events/{id:int}", async (int id, Event updatedEvt, ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var organizerId = EventAccess.OrganizerId(claimsPrincipal);
    if (organizerId is null) return Results.Forbid();
    var evt = await db.Events.FirstOrDefaultAsync(e => e.Id == id && e.OrganizerId == organizerId.Value);
    if (evt == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    var scheduleError = EventAccess.ValidateSchedule(updatedEvt.EventDate, updatedEvt.UtcOffsetMinutes, DateTimeOffset.UtcNow);
    if (scheduleError is not null) return Results.BadRequest(new { Message = scheduleError });
    evt.EventDate = updatedEvt.EventDate;
    evt.UtcOffsetMinutes = updatedEvt.UtcOffsetMinutes;

    if (!string.IsNullOrWhiteSpace(updatedEvt.Title))
        evt.Title = updatedEvt.Title;

    if (!string.IsNullOrWhiteSpace(updatedEvt.Description))
        evt.Description = updatedEvt.Description;

    if (!string.IsNullOrWhiteSpace(updatedEvt.Location))
        evt.Location = updatedEvt.Location;

    if (!string.IsNullOrWhiteSpace(updatedEvt.Venue))
        evt.Venue = updatedEvt.Venue;

    if (updatedEvt.Price > 0)
        evt.Price = updatedEvt.Price;

    evt.CategoryId = await CategoryResolver.ResolveCategoryIdAsync(db, updatedEvt.CategoryId, updatedEvt.CategoryName, evt.CategoryId);

    if (!string.IsNullOrWhiteSpace(updatedEvt.OrganizerName))
        evt.OrganizerName = updatedEvt.OrganizerName;

    if (!string.IsNullOrWhiteSpace(updatedEvt.ArtistOrOrganizer))
        evt.ArtistOrOrganizer = updatedEvt.ArtistOrOrganizer;

    if (!string.IsNullOrWhiteSpace(updatedEvt.ImageUrl))
        evt.ImageUrl = updatedEvt.ImageUrl;

    if (updatedEvt.AvailableTickets > 0)
        evt.AvailableTickets = updatedEvt.AvailableTickets;

    if (updatedEvt.TicketTiersJson != null)
        evt.TicketTiersJson = updatedEvt.TicketTiersJson;

    // Update IsHidder in Database
    evt.IsHidder = updatedEvt.IsHidder;

    await db.SaveChangesAsync();

    return Results.Ok(new { Message = "Event updated successfully.", Id = id });
})
.RequireAuthorization("Organizer")
.WithOpenApi();

// Visibility does not change the schedule, so past events can still be hidden.
app.MapPatch("/api/catalog/events/{id:int}/visibility", async (int id, EventVisibility request, ClaimsPrincipal user, CatalogDbContext db, TimeProvider clock) =>
{
    var organizerId = EventAccess.OrganizerId(user);
    if (organizerId is null) return Results.Forbid();
    var evt = await db.Events.FirstOrDefaultAsync(e => e.Id == id && e.OrganizerId == organizerId.Value);
    if (evt is null) return Results.NotFound(new { Message = "Event not found." });
    if (!request.IsHidden && !await EventSchedule.Upcoming(db.Events, clock.GetUtcNow().UtcDateTime).AnyAsync(e => e.Id == id))
        return Results.BadRequest(new { Message = "Past events cannot be made visible. Please choose a future event date and time." });
    evt.IsHidder = request.IsHidden;
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.RequireAuthorization("Organizer")
.WithOpenApi();

// --- EVENT DELETION API ---

app.MapDelete("/api/catalog/events/{id:int}", async (int id, ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var organizerId = EventAccess.OrganizerId(claimsPrincipal);
    if (organizerId is null) return Results.Forbid();
    var evt = await db.Events.FirstOrDefaultAsync(e => e.Id == id && e.OrganizerId == organizerId.Value);
    if (evt == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    db.Events.Remove(evt);
    await db.SaveChangesAsync();

    return Results.Ok(new { Message = "Event deleted successfully.", Id = id });
})
.RequireAuthorization("Organizer")
.WithOpenApi();

app.Run();

public static class CategoryResolver
{
    public static async Task<int> ResolveCategoryIdAsync(CatalogDbContext db, int requestedCategoryId, string? requestedCategoryName, int fallbackCategoryId = 1)
    {
        if (!string.IsNullOrWhiteSpace(requestedCategoryName))
        {
            var normalizedName = requestedCategoryName.Trim();
            var matchedCategory = await db.Categories
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Name.ToLower() == normalizedName.ToLower());

            if (matchedCategory is not null)
            {
                return matchedCategory.Id;
            }
        }

        if (requestedCategoryId > 0 && await db.Categories.AnyAsync(c => c.Id == requestedCategoryId))
        {
            return requestedCategoryId;
        }

        return await db.Categories.AnyAsync(c => c.Id == fallbackCategoryId) ? fallbackCategoryId : 1;
    }
}

public partial class Program { }

public record EventVisibility(bool IsHidden);
