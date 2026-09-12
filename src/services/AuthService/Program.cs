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
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// 2. Register Token Service
builder.Services.AddScoped<TokenService>();

// 3. Configure JWT Authentication
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

// 4. Configure Swagger with JWT Authorize Button
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Exvo Platform API", Version = "v1" });
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

// Automatically ensure DB tables are created / migrated
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}
catch (Exception ex)
{
    Console.WriteLine($"DB Initialization note: {ex.Message}");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

// Helper to get authenticated user ID from Claims
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


// ══════════════════════════════════════════════════════
// ── AUTHENTICATION & PROFILE ENDPOINTS ──
// ══════════════════════════════════════════════════════

// POST /api/auth/register
app.MapPost("/api/auth/register", async (RegisterRequest request, AppDbContext db, TokenService tokenService) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { Message = "Email and password are required." });
    }

    var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
    if (existingUser != null)
    {
        return Results.Conflict(new { Message = "An account with this email already exists!" });
    }

    var hashedPassword = BCrypt.Net.BCrypt.HashPassword(request.Password);

    var user = new User
    {
        FullName = request.FullName?.Trim() ?? string.Empty,
        Email = request.Email.Trim(),
        PasswordHash = hashedPassword,
        Role = request.Role?.Trim() ?? "Attendee",
        CompanyName = request.CompanyName?.Trim(),
        CompanyRegNumber = request.CompanyRegNumber?.Trim(),
        ContactNumber = request.ContactNumber?.Trim(),
        CreatedAt = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    var profile = new Profile
    {
        UserId = user.Id,
        Name = user.FullName,
        Email = user.Email,
        PhoneNumber = user.ContactNumber,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    db.Profiles.Add(profile);
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
        Token: token,
        Message: "Registration successful!",
        ProfilePicture: profile.ProfilePicture,
        Address: profile.Address
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

    var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
    if (profile == null)
    {
        profile = new Profile
        {
            UserId = user.Id,
            Name = user.FullName,
            Email = user.Email,
            PhoneNumber = user.ContactNumber,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
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
        Token: token,
        Message: "Login successful!",
        ProfilePicture: profile.ProfilePicture,
        Address: profile.Address
    ));
})
.WithName("Login")
.WithOpenApi();

// GET /api/auth/profile
var handleGetProfile = async (ClaimsPrincipal claimsPrincipal, AppDbContext db) =>
{
    var userId = GetUserIdFromClaims(claimsPrincipal);
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.FindAsync(userId.Value);
    if (user == null)
    {
        return Results.NotFound(new { Message = "User not found." });
    }

    var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
    if (profile == null)
    {
        profile = new Profile
        {
            UserId = user.Id,
            Name = user.FullName,
            Email = user.Email,
            PhoneNumber = user.ContactNumber,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync();
    }

    return Results.Ok(new ProfileResponse(
        profile.Id,
        user.Id,
        profile.Name,
        profile.Email,
        profile.Address,
        profile.PhoneNumber,
        profile.ProfilePicture,
        user.Role,
        user.CompanyName,
        user.CompanyRegNumber,
        profile.CreatedAt,
        profile.UpdatedAt
    ));
};

app.MapGet("/api/auth/profile", handleGetProfile).RequireAuthorization().WithName("GetProfile").WithOpenApi();
app.MapGet("/api/profile", handleGetProfile).RequireAuthorization().WithName("GetProfileAlias").WithOpenApi();

// PUT /api/auth/profile
var handleUpdateProfile = async (UpdateProfileRequest request, ClaimsPrincipal claimsPrincipal, AppDbContext db) =>
{
    var userId = GetUserIdFromClaims(claimsPrincipal);
    if (userId == null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new { Message = "Name and Email are required." });
    }

    var user = await db.Users.FindAsync(userId.Value);
    if (user == null)
    {
        return Results.NotFound(new { Message = "User not found." });
    }

    if (!string.Equals(user.Email, request.Email, StringComparison.OrdinalIgnoreCase))
    {
        var emailExists = await db.Users.AnyAsync(u => u.Email == request.Email && u.Id != user.Id);
        if (emailExists)
        {
            return Results.Conflict(new { Message = "This email is already in use by another account." });
        }
    }

    var profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Id);
    if (profile == null)
    {
        profile = new Profile
        {
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow
        };
        db.Profiles.Add(profile);
    }

    profile.Name = request.Name.Trim();
    profile.Email = request.Email.Trim();
    profile.Address = request.Address?.Trim();
    profile.PhoneNumber = request.PhoneNumber?.Trim();
    if (request.ProfilePicture != null)
    {
        profile.ProfilePicture = request.ProfilePicture;
    }
    profile.UpdatedAt = DateTime.UtcNow;

    user.FullName = profile.Name;
    user.Email = profile.Email;
    user.ContactNumber = profile.PhoneNumber;

    await db.SaveChangesAsync();

    return Results.Ok(new ProfileResponse(
        profile.Id,
        user.Id,
        profile.Name,
        profile.Email,
        profile.Address,
        profile.PhoneNumber,
        profile.ProfilePicture,
        user.Role,
        user.CompanyName,
        user.CompanyRegNumber,
        profile.CreatedAt,
        profile.UpdatedAt
    ));
};

app.MapPut("/api/auth/profile", handleUpdateProfile).RequireAuthorization().WithName("UpdateProfile").WithOpenApi();
app.MapPut("/api/profile", handleUpdateProfile).RequireAuthorization().WithName("UpdateProfileAlias").WithOpenApi();

app.Run();
