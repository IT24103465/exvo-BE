using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ExvoAuthService.Models;
using ExvoAuthService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Exvo.AuthService.Tests;

public class TokenServiceTests
{
    private const string Key = "Mutation_Test_Only_Signing_Key_At_Least_32_Bytes!";

    private static TokenService Create(bool configured = true) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(configured ? new Dictionary<string, string?>
        {
            ["Jwt:Key"] = Key,
            ["Jwt:Issuer"] = "test-issuer",
            ["Jwt:Audience"] = "test-audience"
        } : new Dictionary<string, string?>()).Build());

    [Theory]
    [InlineData("Organizer", "Alice", "Company", "REG-1", "0771234567")]
    [InlineData("Attendee", "", "", "", "")]
    [InlineData(null, null, null, null, null)]
    public void Token_preserves_identity_role_and_optional_claims(string? role, string? name,
        string? company, string? registration, string? contact)
    {
        var user = new User
        {
            Id = 42,
            Email = "alice@example.test",
            Role = role!,
            FullName = name!,
            CompanyName = company,
            CompanyRegNumber = registration,
            ContactNumber = contact
        };
        var before = DateTime.UtcNow;
        var encoded = Create().GenerateToken(user);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(encoded, new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)),
            ValidIssuer = "test-issuer",
            ValidAudience = "test-audience",
            ClockSkew = TimeSpan.Zero
        }, out var validated);
        Assert.Equal("42", principal.FindFirst("sub")?.Value);
        Assert.Equal(user.Email, principal.FindFirst("email")?.Value);
        Assert.Equal(name ?? "", principal.FindFirst("fullName")?.Value);
        Assert.Equal(role ?? "Attendee", principal.FindFirst("role")?.Value);
        Assert.Equal(2, principal.FindAll("role").Count());
        Assert.Equal(string.IsNullOrEmpty(company) ? null : company, principal.FindFirst("companyName")?.Value);
        Assert.Equal(string.IsNullOrEmpty(registration) ? null : registration, principal.FindFirst("companyRegNumber")?.Value);
        Assert.Equal(string.IsNullOrEmpty(contact) ? null : contact, principal.FindFirst("contactNumber")?.Value);
        Assert.True(Guid.TryParse(principal.FindFirst("jti")?.Value, out _));
        Assert.InRange(validated.ValidTo, before.AddHours(8).AddSeconds(-1), DateTime.UtcNow.AddHours(8));
        Assert.Equal(SecurityAlgorithms.HmacSha256, ((JwtSecurityToken)validated).Header.Alg);
    }

    [Fact]
    public void Missing_configuration_uses_existing_defaults_and_unique_token_ids()
    {
        var service = Create(false);
        var user = new User { Id = 7, Email = "default@example.test" };
        var handler = new JwtSecurityTokenHandler();
        var first = service.GenerateToken(user);
        handler.ValidateToken(first, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!")),
            ValidIssuer = "ExvoAuthService",
            ValidAudience = "ExvoPlatform",
            ValidateIssuerSigningKey = true
        }, out _);
        Assert.NotEqual(handler.ReadJwtToken(first).Id, handler.ReadJwtToken(service.GenerateToken(user)).Id);
    }
}
