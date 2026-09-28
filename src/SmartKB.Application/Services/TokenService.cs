using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SmartKB.Domain.Entities;

namespace SmartKB.Application.Services;

public class JwtSettings
{
    public string Issuer { get; set; } = "SmartKB";
    public string Audience { get; set; } = "SmartKB";
    public string SecretKey { get; set; } = string.Empty;
    public int ExpireMinutes { get; set; } = 720;
}

public interface ITokenService
{
    string GenerateToken(User user);
}

/// <summary>JWT 签发（密钥仅从配置注入，严禁硬编码）</summary>
public class TokenService(IConfiguration configuration) : ITokenService
{
    private readonly JwtSettings _settings =
        configuration.GetSection("Jwt").Get<JwtSettings>()
        ?? throw new InvalidOperationException("缺少 Jwt 配置节");

    public string GenerateToken(User user)
    {
        if (string.IsNullOrEmpty(_settings.SecretKey) || _settings.SecretKey.Length < 32)
            throw new InvalidOperationException("Jwt:SecretKey 未配置或长度不足 32 字符");

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("displayName", user.DisplayName),
            new("isadmin", user.IsAdmin ? "true" : "false")
        };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r.Name)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_settings.ExpireMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
