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
        return Results.Conflict(new { Message = "A user with this email already exists." });
    }

    var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

    var user = new User
    {
        FullName = request.FullName,
        Email = request.Email,
        PasswordHash = passwordHash,
        
        // SECURITY FIX: Hardcoded to "Attendee". The user can no longer self-assign "Organizer" or "Admin".
        Role = "Attendee", 
        
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
        Token: token,
        Message: "Registration successful!"
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
        Token: token,
        Message: "Login successful!"
    ));
})
.WithName("Login")
.WithOpenApi();

// GET /api/auth/me (Protected Endpoint)
app.MapGet("/api/auth/me", async (ClaimsPrincipal claimsPrincipal, AppDbContext db) =>
{
    var userIdClaim = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? claimsPrincipal.FindFirst("sub")?.Value;

    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
    {
        return Results.Unauthorized();
    }

    var user = await db.Users.FindAsync(userId);
    if (user == null)
    {
        return Results.NotFound(new { Message = "User not found." });
    }

    return Results.Ok(new
    {
        user.Id,
        user.FullName,
        user.Email,
        user.Role,
        user.CreatedAt
    });
})
.RequireAuthorization()
.WithName("GetCurrentUser")
.WithOpenApi();

app.Run();