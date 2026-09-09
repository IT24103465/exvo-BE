using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Exvo.CatalogService.Data;
using Exvo.CatalogService.Models;
using Exvo.CatalogService.Models.DTOs;

var builder = WebApplication.CreateBuilder(args);

// ══════════════════════════════════════════════════════
// ── SERVICE CONFIGURATION ──
// ══════════════════════════════════════════════════════

// 1. Database Context — own database: exvo_event_catalog_db
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36))));

// 2. JWT Authentication — same key/issuer/audience as AuthService
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

builder.Services.AddAuthorization();

// 3. Swagger with JWT Authorize button
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Exvo Catalog Service API", Version = "v1" });
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

// ══════════════════════════════════════════════════════
// ── DATABASE AUTO-INITIALIZATION ──
// ══════════════════════════════════════════════════════

try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    db.Database.EnsureCreated();
    Console.WriteLine("✅ CatalogService DB initialized: exvo_event_catalog_db");
}
catch (Exception ex)
{
    Console.WriteLine($"⚠️ CatalogService DB initialization note: {ex.Message}");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

// ══════════════════════════════════════════════════════
// ── HELPER FUNCTIONS ──
// ══════════════════════════════════════════════════════

static int? GetUserIdFromClaims(ClaimsPrincipal claimsPrincipal)
{
    var userIdClaim = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? claimsPrincipal.FindFirst("sub")?.Value
                      ?? claimsPrincipal.FindFirst("nameid")?.Value;

    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
    {
        return null;
    }
    return userId;
}

static EventResponse MapEventToResponse(Event ev)
{
    var tiers = ev.TicketTiers
        .OrderBy(t => t.SortOrder)
        .Select(t => new TicketTierDto(t.TierId, t.Name, t.Price, t.Quantity))
        .ToList();

    return new EventResponse(
        ev.Id,
        ev.UserId,
        ev.OrganizerName,
        ev.Title,
        ev.ArtistOrOrganizer,
        ev.CategoryName,
        ev.EventDate,
        ev.EventTime,
        ev.VenueName,
        tiers,
        ev.MinPrice,
        ev.TotalCapacity,
        ev.CoverImage,
        ev.Description,
        ev.Status,
        ev.CreatedAt,
        ev.UpdatedAt
    );
}

// ══════════════════════════════════════════════════════
// ── EVENT ENDPOINTS ──
// ══════════════════════════════════════════════════════

// GET /api/events — all published events (Public)
app.MapGet("/api/events", async (CatalogDbContext db) =>
{
    var events = await db.Events
        .Include(e => e.TicketTiers)
        .Where(e => e.Status == "Published")
        .OrderByDescending(e => e.CreatedAt)
        .ToListAsync();

    var result = events.Select(MapEventToResponse).ToList();
    return Results.Ok(result);
})
.WithName("GetAllEvents")
.WithOpenApi();

// GET /api/events/{id} — single event (Public)
app.MapGet("/api/events/{id:int}", async (int id, CatalogDbContext db) =>
{
    var ev = await db.Events
        .Include(e => e.TicketTiers)
        .FirstOrDefaultAsync(e => e.Id == id);

    if (ev == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    return Results.Ok(MapEventToResponse(ev));
})
.WithName("GetEventById")
.WithOpenApi();

// GET /api/events/my-events — organizer's own events (Protected)
app.MapGet("/api/events/my-events", async (ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var userId = GetUserIdFromClaims(claimsPrincipal);
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    var events = await db.Events
        .Include(e => e.TicketTiers)
        .Where(e => e.UserId == userId.Value)
        .OrderByDescending(e => e.CreatedAt)
        .ToListAsync();

    var result = events.Select(MapEventToResponse).ToList();
    return Results.Ok(result);
})
.RequireAuthorization()
.WithName("GetMyEvents")
.WithOpenApi();

// POST /api/events — create event (Protected)
app.MapPost("/api/events", async (CreateEventRequest request, ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var userId = GetUserIdFromClaims(claimsPrincipal);
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Date) || string.IsNullOrWhiteSpace(request.Venue))
    {
        return Results.BadRequest(new { Message = "Title, Date, and Venue are required." });
    }

    // Get organizer name from JWT claims
    var organizerName = claimsPrincipal.FindFirst("fullName")?.Value ?? "Exvo Organizer";

    var tiers = request.TicketTiers ?? new List<TicketTierDto>();
    decimal minPrice = tiers.Count > 0 ? tiers.Min(t => t.Price) : 0;
    int totalCap = tiers.Count > 0 ? tiers.Sum(t => t.Quantity) : 500;

    // Resolve category (find or create)
    var categoryName = string.IsNullOrWhiteSpace(request.Category) ? "Concert" : request.Category.Trim();
    var category = await db.Categories.FirstOrDefaultAsync(c => c.Name == categoryName);
    if (category == null)
    {
        category = new Category { Name = categoryName, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
    }

    // Resolve venue (find or create)
    var venueName = request.Venue.Trim();
    var venue = await db.Venues.FirstOrDefaultAsync(v => v.Name == venueName);
    if (venue == null)
    {
        venue = new Venue { Name = venueName, TotalCapacity = totalCap, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Venues.Add(venue);
        await db.SaveChangesAsync();
    }

    var newEvent = new Event
    {
        UserId = userId.Value,
        OrganizerName = string.IsNullOrWhiteSpace(request.ArtistOrOrganizer) ? organizerName : request.ArtistOrOrganizer.Trim(),
        Title = request.Title.Trim(),
        ArtistOrOrganizer = request.ArtistOrOrganizer?.Trim(),
        CategoryId = category.Id,
        CategoryName = categoryName,
        VenueId = venue.Id,
        VenueName = venueName,
        EventDate = request.Date.Trim(),
        EventTime = string.IsNullOrWhiteSpace(request.Time) ? "19:00" : request.Time.Trim(),
        MinPrice = minPrice,
        TotalCapacity = totalCap,
        CoverImage = request.CoverImage,
        Description = request.Description?.Trim(),
        Status = "Published",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Events.Add(newEvent);
    await db.SaveChangesAsync();

    // Add ticket tiers
    for (int i = 0; i < tiers.Count; i++)
    {
        var tier = tiers[i];
        db.TicketTiers.Add(new TicketTier
        {
            EventId = newEvent.Id,
            TierId = tier.Id,
            Name = tier.Name,
            Price = tier.Price,
            Quantity = tier.Quantity,
            SortOrder = i,
            CreatedAt = DateTime.UtcNow
        });
    }
    await db.SaveChangesAsync();

    // Reload with tiers for response
    var created = await db.Events
        .Include(e => e.TicketTiers)
        .FirstAsync(e => e.Id == newEvent.Id);

    return Results.Created($"/api/events/{created.Id}", MapEventToResponse(created));
})
.RequireAuthorization()
.WithName("CreateEvent")
.WithOpenApi();

// PUT /api/events/{id} — update event (Protected)
app.MapPut("/api/events/{id:int}", async (int id, UpdateEventRequest request, ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var userId = GetUserIdFromClaims(claimsPrincipal);
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    var ev = await db.Events
        .Include(e => e.TicketTiers)
        .FirstOrDefaultAsync(e => e.Id == id);

    if (ev == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    if (ev.UserId != userId.Value)
    {
        return Results.Forbid();
    }

    var tiers = request.TicketTiers ?? new List<TicketTierDto>();
    decimal minPrice = tiers.Count > 0 ? tiers.Min(t => t.Price) : ev.MinPrice;
    int totalCap = tiers.Count > 0 ? tiers.Sum(t => t.Quantity) : ev.TotalCapacity;

    // Update category
    var categoryName = request.Category?.Trim() ?? ev.CategoryName;
    if (categoryName != ev.CategoryName)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Name == categoryName);
        if (category == null)
        {
            category = new Category { Name = categoryName, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
        }
        ev.CategoryId = category.Id;
        ev.CategoryName = categoryName;
    }

    // Update venue
    var venueName = request.Venue.Trim();
    if (venueName != ev.VenueName)
    {
        var venue = await db.Venues.FirstOrDefaultAsync(v => v.Name == venueName);
        if (venue == null)
        {
            venue = new Venue { Name = venueName, TotalCapacity = totalCap, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Venues.Add(venue);
            await db.SaveChangesAsync();
        }
        ev.VenueId = venue.Id;
        ev.VenueName = venueName;
    }

    ev.Title = request.Title.Trim();
    ev.ArtistOrOrganizer = request.ArtistOrOrganizer?.Trim();
    ev.EventDate = request.Date.Trim();
    ev.EventTime = request.Time?.Trim() ?? ev.EventTime;
    ev.MinPrice = minPrice;
    ev.TotalCapacity = totalCap;
    if (request.CoverImage != null)
    {
        ev.CoverImage = request.CoverImage;
    }
    ev.Description = request.Description?.Trim();
    if (!string.IsNullOrWhiteSpace(request.Status))
    {
        ev.Status = request.Status.Trim();
    }
    ev.UpdatedAt = DateTime.UtcNow;

    // Replace ticket tiers
    db.TicketTiers.RemoveRange(ev.TicketTiers);
    for (int i = 0; i < tiers.Count; i++)
    {
        var tier = tiers[i];
        db.TicketTiers.Add(new TicketTier
        {
            EventId = ev.Id,
            TierId = tier.Id,
            Name = tier.Name,
            Price = tier.Price,
            Quantity = tier.Quantity,
            SortOrder = i,
            CreatedAt = DateTime.UtcNow
        });
    }

    await db.SaveChangesAsync();

    // Reload with updated tiers
    var updated = await db.Events
        .Include(e => e.TicketTiers)
        .FirstAsync(e => e.Id == ev.Id);

    return Results.Ok(MapEventToResponse(updated));
})
.RequireAuthorization()
.WithName("UpdateEvent")
.WithOpenApi();

// DELETE /api/events/{id} — delete event (Protected)
app.MapDelete("/api/events/{id:int}", async (int id, ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var userId = GetUserIdFromClaims(claimsPrincipal);
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    var ev = await db.Events.FindAsync(id);
    if (ev == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    if (ev.UserId != userId.Value)
    {
        return Results.Forbid();
    }

    db.Events.Remove(ev);
    await db.SaveChangesAsync();

    return Results.Ok(new { Message = "Event deleted successfully." });
})
.RequireAuthorization()
.WithName("DeleteEvent")
.WithOpenApi();

// ══════════════════════════════════════════════════════
// ── CATALOG REFERENCE ENDPOINTS ──
// ══════════════════════════════════════════════════════

// GET /api/catalog/categories — all categories (Public)
app.MapGet("/api/catalog/categories", async (CatalogDbContext db) =>
{
    var categories = await db.Categories
        .OrderBy(c => c.Name)
        .Select(c => new CategoryResponse(c.Id, c.Name, c.Description, c.IconUrl))
        .ToListAsync();

    return Results.Ok(categories);
})
.WithName("GetAllCategories")
.WithOpenApi();

// GET /api/catalog/venues — all venues (Public)
app.MapGet("/api/catalog/venues", async (CatalogDbContext db) =>
{
    var venues = await db.Venues
        .OrderBy(v => v.Name)
        .Select(v => new VenueResponse(v.Id, v.Name, v.Address, v.City, v.TotalCapacity, v.Description))
        .ToListAsync();

    return Results.Ok(venues);
})
.WithName("GetAllVenues")
.WithOpenApi();

app.Run();
