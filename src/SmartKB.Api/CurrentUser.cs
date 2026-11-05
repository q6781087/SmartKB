using System.Security.Claims;
using SmartKB.Application;

namespace SmartKB.Api;

/// <summary>从 JWT Claims 解析当前用户（HTTP 与 SignalR 连接均可取到已认证的 Principal）</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public long UserId => Principal is { } p
        ? p.GetUserId()
        : throw new UnauthorizedAccessException("未认证或 Token 中缺少用户标识");

    public bool IsAdmin => Principal?.IsAdmin() ?? false;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
}
