using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace PgMonitorApi.Services;

/// <summary>
/// Sinh JWT token cho tài khoản admin cấu hình sẵn. Token hết hạn sau 8 tiếng.
/// </summary>
public class JwtTokenService
{
    private readonly IConfiguration _config;

    public JwtTokenService(IConfiguration config) => _config = config;

    public string CreateToken(string username)
    {
        var key = _config["Jwt:Key"]
                  ?? throw new InvalidOperationException("Thiếu cấu hình Jwt:Key");
        var issuer = _config["Jwt:Issuer"] ?? "PgMonitorApi";
        var audience = _config["Jwt:Audience"] ?? "PgMonitorApi";

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(ClaimTypes.Name, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8), // hết hạn sau 8 tiếng
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
