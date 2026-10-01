var builder = WebApplication.CreateBuilder(args);

// Register YARP Reverse Proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Configure CORS for Frontend Client
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins")
    .Get<string[]>()?
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .ToArray() ?? Array.Empty<string>();

if (builder.Environment.IsProduction() && allowedOrigins.Length == 0)
{
    throw new InvalidOperationException("Configure at least one Cors:AllowedOrigins entry (for example Cors__AllowedOrigins__0) in Production.");
}

if (allowedOrigins.Contains("*"))
{
    throw new InvalidOperationException("Cors:AllowedOrigins must contain explicit origins; wildcard origins cannot be used with credentials.");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseRouting();
app.UseCors("AllowFrontend");

app.MapGet("/health", () => Results.Ok(new { service = "ApiGateway", status = "healthy" }))
    .AllowAnonymous();

// Map Reverse Proxy Routes
app.MapReverseProxy();

app.Run();
