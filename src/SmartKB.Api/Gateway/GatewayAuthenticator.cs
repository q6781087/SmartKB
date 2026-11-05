using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartKB.Infrastructure;

namespace SmartKB.Api.Gateway;

public sealed record GatewayIdentity(long UserId, string Username, bool IsAdmin);

public sealed class GatewayConfigurationException(string message) : Exception(message);

/// <summary>
/// 网关鉴权。两条路径：
/// 1) 静态服务间 Key（仅环境变量）→ 绑定到配置指定的真实账号，权限完全按该账号算；
/// 2) 现有 JWT → 按登录用户本人算（客户系统已接登录时可直接复用）。
/// Key 用 SHA256 + 定长比较，不回显、不进日志。
/// </summary>
public interface IGatewayAuthenticator
{
    bool IsReady { get; }

    Task<GatewayIdentity?> AuthenticateAsync(HttpContext http, CancellationToken ct);
}

public class GatewayAuthenticator(
    IOptions<GatewayOptions> options,
    SmartKbDbContext db,
    ILogger<GatewayAuthenticator> logger) : IGatewayAuthenticator
{
    private readonly GatewayOptions _options = options.Value;

    public bool IsReady => _options.Configured;

    public async Task<GatewayIdentity?> AuthenticateAsync(HttpContext http, CancellationToken ct)
    {
        var supplied = ExtractBearer(http);
        if (string.IsNullOrEmpty(supplied)) return null;

        if (_options.Configured && KeyEquals(supplied, _options.ApiKey))
        {
            var account = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username == _options.ServiceUsername && u.IsActive, ct);
            if (account is null)
            {
                logger.LogError("网关绑定的服务账号 {User} 不存在或已禁用", _options.ServiceUsername);
                throw new GatewayConfigurationException("网关服务账号不存在或已禁用，请联系管理员");
            }
            return new GatewayIdentity(account.Id, account.Username, account.IsAdmin);
        }

        // 兜底：复用现有 JwtBearer 校验参数，不重复实现签名验证
        var result = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null) return null;

        var rawId = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? result.Principal.FindFirstValue("sub");
        if (!long.TryParse(rawId, out var userId)) return null;

        return new GatewayIdentity(
            userId,
            result.Principal.Identity?.Name ?? "jwt-user",
            result.Principal.FindFirstValue("isadmin") == "true");
    }

    private static string? ExtractBearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.Length == 0) return null;
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;
    }

    /// <summary>先哈希到定长再比较，避免长度差异泄露；不使用字符串相等</summary>
    public static bool KeyEquals(string supplied, string configured)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
