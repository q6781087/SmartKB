using System.Security.Claims;

namespace SmartKB.Api;

/// <summary>当前登录用户访问辅助（从 JWT Claims 取）</summary>
public static class ClaimsExtensions
{
    public static long GetUserId(this ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub"), out var id)
            ? id
            : throw new UnauthorizedAccessException("Token 中缺少用户标识");

    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.FindFirstValue("isadmin") == "true";
}
