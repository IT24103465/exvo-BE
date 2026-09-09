using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ExvoAuthService.Models;
using Microsoft.IdentityModel.Tokens;

namespace ExvoAuthService.Services
{
    public class TokenService
    {
        private readonly IConfiguration _config;

        public TokenService(IConfiguration config)
        {
            _config = config;
        }

        public string GenerateToken(User user)
        {
            var jwtKey = _config["Jwt:Key"] ?? "Exvo_Super_Secret_JWT_Key_2026_Must_Be_Long_Enough!";
            var issuer = _config["Jwt:Issuer"] ?? "ExvoAuthService";
            var audience = _config["Jwt:Audience"] ?? "ExvoPlatform";

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("fullName", user.FullName ?? string.Empty),
                new Claim(ClaimTypes.Role, user.Role ?? "Attendee"),
                new Claim("role", user.Role ?? "Attendee"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            if (!string.IsNullOrEmpty(user.CompanyName))
            {
                claims.Add(new Claim("companyName", user.CompanyName));
            }
            if (!string.IsNullOrEmpty(user.CompanyRegNumber))
            {
                claims.Add(new Claim("companyRegNumber", user.CompanyRegNumber));
            }
            if (!string.IsNullOrEmpty(user.ContactNumber))
            {
                claims.Add(new Claim("contactNumber", user.ContactNumber));
            }

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddHours(8),
                Issuer = issuer,
                Audience = audience,
                SigningCredentials = credentials
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);

            return tokenHandler.WriteToken(token);
        }
    }
}
