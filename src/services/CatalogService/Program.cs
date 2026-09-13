using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Exvo.CatalogService.Data;
using Exvo.CatalogService.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Context
var connectionString = builder.Configuration["EXVO_CATALOG_MYSQL_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("No Catalog Service MySQL connection string is configured.");

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// 2. JWT Authentication Setup
var jwtKey = builder.Configuration["Jwt:Key"] ?? "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ExvoAuthService";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "ExvoPlatform";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

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

// Preserve existing IDs, seed categories, and patch DB schema for TicketTiersJson on startup.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await CategorySeeder.SeedAsync(db);
        try
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Events ADD COLUMN TicketTiersJson longtext NULL;");
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

app.MapGet("/api/catalog/events", async (int? categoryId, CatalogDbContext db) =>
{
    var query = db.Events.Include(e => e.Category).AsQueryable();

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
        e.CategoryId,
        CategoryName = e.Category != null ? e.Category.Name : "Music & Concerts",
        Category = e.Category != null ? e.Category.Name : "Music & Concerts",
        e.OrganizerId,
        e.OrganizerName,
        e.ImageUrl,
        CoverImage = e.ImageUrl,
        e.AvailableTickets,
        e.TicketTiersJson,
        e.CreatedAt
    });

    return Results.Ok(result);
})
.WithName("GetEvents")
.WithOpenApi();

app.MapGet("/api/catalog/events/my-events", async (ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var userIdClaim = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? claimsPrincipal.FindFirst("sub")?.Value;

    int.TryParse(userIdClaim, out int organizerId);

    var nameClaim = claimsPrincipal.FindFirst(ClaimTypes.Name)?.Value 
                    ?? claimsPrincipal.FindFirst("name")?.Value
                    ?? claimsPrincipal.FindFirst("companyName")?.Value;

    var query = db.Events.Include(e => e.Category).AsQueryable();

    if (organizerId > 0)
    {
        query = query.Where(e => e.OrganizerId == organizerId);
    }
    else if (!string.IsNullOrWhiteSpace(nameClaim))
    {
        query = query.Where(e => e.OrganizerName == nameClaim);
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
        e.CategoryId,
        CategoryName = e.Category != null ? e.Category.Name : "Music & Concerts",
        Category = e.Category != null ? e.Category.Name : "Music & Concerts",
        e.OrganizerId,
        e.OrganizerName,
        e.ImageUrl,
        CoverImage = e.ImageUrl,
        e.AvailableTickets,
        e.TicketTiersJson,
        e.CreatedAt
    });

    return Results.Ok(result);
})
.WithName("GetMyEvents")
.WithOpenApi();

app.MapGet("/api/catalog/events/{id:int}", async (int id, CatalogDbContext db) =>
{
    var evt = await db.Events.Include(e => e.Category).FirstOrDefaultAsync(e => e.Id == id);
    if (evt == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    var result = new
    {
        evt.Id,
        evt.Title,
        evt.Description,
        evt.Location,
        evt.Venue,
        evt.Price,
        evt.EventDate,
        evt.CategoryId,
        CategoryName = evt.Category != null ? evt.Category.Name : "Music & Concerts",
        Category = evt.Category != null ? evt.Category.Name : "Music & Concerts",
        evt.OrganizerId,
        evt.OrganizerName,
        evt.ImageUrl,
        CoverImage = evt.ImageUrl,
        evt.AvailableTickets,
        evt.TicketTiersJson,
        evt.CreatedAt
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

    var userIdClaim = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? claimsPrincipal.FindFirst("sub")?.Value;

    if (int.TryParse(userIdClaim, out int organizerId) && organizerId > 0)
    {
        evt.OrganizerId = organizerId;
    }

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

    var categoryExists = await db.Categories.AnyAsync(c => c.Id == evt.CategoryId);
    if (!categoryExists)
    {
        evt.CategoryId = 1;
    }

    db.Events.Add(evt);
    await db.SaveChangesAsync();

    return Results.Created($"/api/catalog/events/{evt.Id}", evt);
})
.WithOpenApi();

// --- EVENT UPDATE API ---

app.MapPut("/api/catalog/events/{id:int}", async (int id, Event updatedEvt, ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var evt = await db.Events.FindAsync(id);
    if (evt == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

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

    if (updatedEvt.CategoryId > 0)
        evt.CategoryId = updatedEvt.CategoryId;

    if (!string.IsNullOrWhiteSpace(updatedEvt.OrganizerName))
        evt.OrganizerName = updatedEvt.OrganizerName;

    if (!string.IsNullOrWhiteSpace(updatedEvt.ImageUrl))
        evt.ImageUrl = updatedEvt.ImageUrl;

    if (updatedEvt.AvailableTickets > 0)
        evt.AvailableTickets = updatedEvt.AvailableTickets;

    if (updatedEvt.TicketTiersJson != null)
        evt.TicketTiersJson = updatedEvt.TicketTiersJson;

    await db.SaveChangesAsync();

    return Results.Ok(new { Message = "Event updated successfully.", Id = id });
})
.WithOpenApi();

// --- EVENT DELETION API ---

app.MapDelete("/api/catalog/events/{id:int}", async (int id, ClaimsPrincipal claimsPrincipal, CatalogDbContext db) =>
{
    var evt = await db.Events.FindAsync(id);
    if (evt == null)
    {
        return Results.NotFound(new { Message = "Event not found." });
    }

    db.Events.Remove(evt);
    await db.SaveChangesAsync();

    return Results.Ok(new { Message = "Event deleted successfully.", Id = id });
})
.WithOpenApi();

app.Run();