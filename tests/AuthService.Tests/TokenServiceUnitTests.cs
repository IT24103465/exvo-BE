using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using ExvoAuthService.Models;
using ExvoAuthService.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Exvo.AuthService.Tests;

public class TokenServiceUnitTests
{
    [Theory]
    [InlineData("Admin", "Admin")]
    [InlineData("Organizer", "Organizer")]
    [InlineData("Attendee", "Attendee")]
    [InlineData(null, "Attendee")]
    public void GenerateToken_ContainsExpectedRole(
        string? inputRole,
        string expectedRole)
    {
        // Arrange: Supply controlled test configuration.
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:Key"] =
                "Unit_Test_Only_Key_0123456789_ABCDEFGHIJKLMNOPQRSTUVWXYZ",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var service = new TokenService(configuration);

        var user = new User
        {
            Id = 10,
            Email = "test@example.com",
            FullName = "Test User",
            Role = inputRole!
        };

        // Act: Generate a real token using production code.
        var tokenString = service.GenerateToken(user);

        var token = new JwtSecurityTokenHandler()
            .ReadJwtToken(tokenString);

        // Assert: Verify the role and user identity.
        Assert.Contains(token.Claims,
            claim => claim.Type == "role"
                && claim.Value == expectedRole);

        Assert.Equal("10", token.Subject);
        Assert.Equal("TestIssuer", token.Issuer);
        Assert.Contains("TestAudience", token.Audiences);
    }
    [Fact]
    public void GenerateToken_HasValidSignatureAndEightHourExpiry()
    {
        // Arrange
        const string testKey =
            "Unit_Test_Only_Key_0123456789_ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = testKey,
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience"
            })
            .Build();

        var service = new TokenService(configuration);
        var user = new User
        {
            Id = 10,
            Email = "test@example.com",
            FullName = "Test User",
            Role = "Attendee"
        };

        var before = DateTime.UtcNow;

        // Act
        var tokenString = service.GenerateToken(user);

        var after = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler();

        var principal = handler.ValidateToken(
            tokenString,
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(testKey)),
                ValidateIssuer = true,
                ValidIssuer = "TestIssuer",
                ValidateAudience = true,
                ValidAudience = "TestAudience",
                ValidateLifetime = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidAlgorithms = new[]
                {
                    SecurityAlgorithms.HmacSha256
                },
                ClockSkew = TimeSpan.Zero
            },
            out var validatedToken);

        // Assert
        Assert.NotNull(principal.Identity);
        Assert.True(principal.Identity!.IsAuthenticated);

        var jwt = Assert.IsType<JwtSecurityToken>(validatedToken);
        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Header.Alg);

        // JWT timestamps have second precision.
        Assert.InRange(
            jwt.ValidTo,
            before.AddHours(8).AddSeconds(-1),
            after.AddHours(8));
    }
}
