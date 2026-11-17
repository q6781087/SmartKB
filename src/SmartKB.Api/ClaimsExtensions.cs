using System.Security.Claims;
using SmartKB.Domain.Entities;

namespace SmartKB.Api;

/// <summary>当前登录用户访问辅助（从 JWT Claims 取）</summary>
public static class ClaimsExtensions
{
    public static long GetUserId(this ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub"), out var id)
            ? id
            : throw new UnauthorizedAccessException("Token 中缺少用户标识");

    /// <summary>
    /// 系统管理员判定：admin 角色是唯一真相源，同时兼容旧 token 里的 isadmin claim。
    /// 控制器 [Authorize(Roles="admin")] 与业务层守卫必须走同一判定，不再出现错配。
    /// </summary>
    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.FindFirstValue("isadmin") == "true" || user.IsInRole(SystemRoles.Admin);
}
