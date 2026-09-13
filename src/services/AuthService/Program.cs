using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using ExvoAuthService.Data;
using ExvoAuthService.Models;
using ExvoAuthService.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Database Context
var connectionString = builder.Configuration["EXVO_AUTH_MYSQL_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("No Auth Service MySQL connection string is configured.");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// 2. Register Token Service
builder.Services.AddScoped<TokenService>();

// 3. Configure JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromMinutes(5)
        };
    });

builder.Services.AddAuthorization();

// 4. Configure Swagger with JWT Authorize Button
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Exvo Auth API", Version = "v1" });
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

// 5. CORS Policy
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

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

// Auto-patch Database Schema if missing columns (e.g. Address)
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN Address longtext NULL;");
    }
    catch
    {
        // Column already exists or error ignored
    }
}

// Helper to resolve user from ClaimsPrincipal
static async Task<User?> GetUserFromClaimsAsync(ClaimsPrincipal claimsPrincipal, AppDbContext db)
{
    var userIdClaim = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? claimsPrincipal.FindFirst("sub")?.Value
                      ?? claimsPrincipal.FindFirst("nameid")?.Value
                      ?? claimsPrincipal.Claims.FirstOrDefault(c => c.Type.EndsWith("nameidentifier", StringComparison.OrdinalIgnoreCase))?.Value;

    if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int userId))
    {
        var userById = await db.Users.FindAsync(userId);
        if (userById != null) return userById;
    }

    var userEmailClaim = claimsPrincipal.FindFirst(ClaimTypes.Email)?.Value 
                         ?? claimsPrincipal.FindFirst("email")?.Value;

    if (!string.IsNullOrEmpty(userEmailClaim))
    {
        return await db.Users.FirstOrDefaultAsync(u => u.Email == userEmailClaim);
    }

    return null;
}

// POST /api/auth/register
app.MapPost("/api/auth/register", async (RegisterRequest request, AppDbContext db, TokenService tokenService) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { Message = "Email and password are required." });
    }

    var isOrganizer = string.Equals(request.Role, "Organizer", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(request.Role, "Company", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(request.Role, "Business", StringComparison.OrdinalIgnoreCase) ||
                      !string.IsNullOrWhiteSpace(request.CompanyRegNumber);

    var companyName = !string.IsNullOrWhiteSpace(request.CompanyName) 
        ? request.CompanyName 
        : (isOrganizer ? request.FullName : null);

    if (isOrganizer && string.IsNullOrWhiteSpace(companyName))
    {
        return Results.BadRequest(new { Message = "Company name is required for company registration." });
    }

    var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
    if (existingUser != null)
    {
        return Results.Conflict(new { Message = "A user with this email already exists." });
    }

    var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
    var resolvedRole = isOrganizer ? "Organizer" : "Attendee";
    var resolvedName = isOrganizer ? companyName! : request.FullName;

    var user = new User
    {
        FullName = resolvedName,
        Email = request.Email,
        PasswordHash = passwordHash,
        Role = resolvedRole,
        CompanyName = isOrganizer ? companyName : null,
        CompanyRegNumber = isOrganizer ? request.CompanyRegNumber : null,
        ContactNumber = request.ContactNumber,
        CreatedAt = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    var token = tokenService.GenerateToken(user);

    return Results.Created($"/api/auth/users/{user.Id}", new AuthResponse(
        user.Id,
        user.FullName,
        user.Email,
        user.Role,
        user.CompanyName,
        user.CompanyRegNumber,
        user.ContactNumber,
        user.ProfilePicture,
        Token: token,
        Message: "Registration successful!",
        Address: user.Address
    ));
})
.WithName("Register")
.WithOpenApi();

// POST /api/auth/login
app.MapPost("/api/auth/login", async (LoginRequest request, AppDbContext db, TokenService tokenService) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { Message = "Email and password are required." });
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
    if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
    {
        return Results.Json(new { Message = "Invalid email or password!" }, statusCode: 401);
    }

    var token = tokenService.GenerateToken(user);

    return Results.Ok(new AuthResponse(
        user.Id,
        user.FullName,
        user.Email,
        user.Role,
        user.CompanyName,
        user.CompanyRegNumber,
        user.ContactNumber,
        user.ProfilePicture,
        Token: token,
        Message: "Login successful!",
        Address: user.Address
    ));
})
.WithName("Login")
.WithOpenApi();

// GET /api/auth/me (Protected Endpoint)
app.MapGet("/api/auth/me", async (ClaimsPrincipal claimsPrincipal, AppDbContext db) =>
{
    var user = await GetUserFromClaimsAsync(claimsPrincipal, db);
    if (user == null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        user.Id,
        user.FullName,
        name = user.FullName,
        user.Email,
        user.Role,
        user.CompanyName,
        user.CompanyRegNumber,
        user.ContactNumber,
        phoneNumber = user.ContactNumber,
        user.Address,
        user.ProfilePicture,
        user.CreatedAt
    });
})
.RequireAuthorization()
.WithName("GetCurrentUser")
.WithOpenApi();

// PUT /api/auth/profile (Protected - Update profile details + photo)
app.MapPut("/api/auth/profile", async (UpdateProfileRequest request, ClaimsPrincipal claimsPrincipal, AppDbContext db) =>
{
    var user = await GetUserFromClaimsAsync(claimsPrincipal, db);
    if (user == null)
    {
        return Results.Unauthorized();
    }

    var newName = !string.IsNullOrWhiteSpace(request.FullName) ? request.FullName : request.Name;
    if (!string.IsNullOrWhiteSpace(newName))
    {
        user.FullName = newName.Trim();
    }

    if (!string.IsNullOrWhiteSpace(request.Email))
    {
        user.Email = request.Email.Trim();
    }

    var newPhone = request.ContactNumber ?? request.PhoneNumber;
    if (newPhone != null)
    {
        user.ContactNumber = newPhone.Trim();
    }

    if (request.Address != null)
    {
        user.Address = request.Address.Trim();
    }

    if (request.ProfilePicture != null)
    {
        user.ProfilePicture = request.ProfilePicture;
    }

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        user.Id,
        user.FullName,
        name = user.FullName,
        user.Email,
        user.Role,
        user.CompanyName,
        user.CompanyRegNumber,
        user.ContactNumber,
        phoneNumber = user.ContactNumber,
        user.Address,
        user.ProfilePicture,
        user.CreatedAt,
        Message = "Profile updated successfully."
    });
})
.RequireAuthorization()
.WithName("UpdateProfile")
.WithOpenApi();

// DELETE /api/auth/profile (Protected - Permanently Delete Account)
app.MapDelete("/api/auth/profile", async (ClaimsPrincipal claimsPrincipal, AppDbContext db) =>
{
    var user = await GetUserFromClaimsAsync(claimsPrincipal, db);
    if (user == null)
    {
        return Results.Unauthorized();
    }

    db.Users.Remove(user);
    await db.SaveChangesAsync();

    return Results.Ok(new { Message = "Account deleted successfully." });
})
.RequireAuthorization()
.WithName("DeleteAccount")
.WithOpenApi();

app.Run();