using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartKB.Api;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;

namespace SmartKB.Api.Filters;

/// <summary>
/// 全局审计过滤器：自动记录所有 /api 下的写操作（POST/PUT/PATCH/DELETE）与登录。
///
/// 设计取舍：
/// - 只记元数据（谁、何时、什么动作、状态码、耗时、IP/UA），**不读请求体**——
///   密码、ApiKey、文档正文绝不进审计表；GET 不记，避免文档状态轮询刷出海量噪音。
/// - 用独立 DI scope 落库，与业务请求的 DbContext 状态隔离；审计失败只记日志。
/// - 业务异常在本层尚未经中间件转换，按异常类型直接推断 403/400/500。
/// </summary>
public class AuditLogFilter(IServiceScopeFactory scopeFactory, ILogger<AuditLogFilter> logger)
    : IAsyncActionFilter
{
    private static readonly HashSet<string> WriteMethods =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    private static readonly Dictionary<string, string> ActionMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["POST /api/auth/login"] = "login",
            ["POST /api/auth/change-password"] = "change_password",
            ["POST /api/users"] = "create_user",
            ["PUT /api/users/{id}"] = "update_user",
            ["PUT /api/users/{id}/password"] = "reset_password",
            ["POST /api/roles"] = "create_role",
            ["DELETE /api/roles/{id}"] = "delete_role",
            ["POST /api/kbs"] = "create_kb",
            ["PUT /api/kbs/{id}"] = "update_kb",
            ["DELETE /api/kbs/{id}"] = "delete_kb",
            ["POST /api/kbs/{id}/documents"] = "upload_document",
            ["POST /api/kbs/{id}/documents/{id}/retry"] = "retry_document",
            ["PUT /api/kbs/{id}/documents/{id}/grants"] = "set_document_grants",
            ["DELETE /api/kbs/{id}/documents/{id}"] = "delete_document",
            ["POST /api/kbs/permissions"] = "grant_permission",
            ["DELETE /api/kbs/permissions/{id}"] = "revoke_permission",
            ["POST /api/chats"] = "create_session",
            ["DELETE /api/chats/{id}"] = "delete_session",
            ["PUT /api/settings"] = "update_settings",
            ["POST /api/settings/reset"] = "reset_settings",
            ["POST /api/agent-tools"] = "create_tool",
            ["PUT /api/agent-tools/{id}"] = "update_tool",
            ["DELETE /api/agent-tools/{id}"] = "delete_tool",
            ["PATCH /api/agent-tools/{id}/enabled"] = "toggle_tool",
            ["POST /api/agent-tools/test"] = "test_tool",
            ["PUT /api/agent-tools/{id}/grants"] = "set_tool_grants"
        };

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var request = http.Request;
        if (!WriteMethods.Contains(request.Method) ||
            !request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var executed = await next();
        stopwatch.Stop();

        var exception = executed.Exception;
        var status = ResolveStatus(exception, executed.Result, http.Response.StatusCode);

        var path = request.Path.Value ?? string.Empty;
        var forwarded = request.Headers["X-Forwarded-For"].FirstOrDefault();
        var ip = forwarded?.Split(',')[0].Trim()
                 ?? http.Connection.RemoteIpAddress?.ToString()
                 ?? string.Empty;

        long? userId = null;
        if (http.User.Identity?.IsAuthenticated == true)
        {
            try { userId = http.User.GetUserId(); }
            catch (UnauthorizedAccessException) { /* token 无有效用户标识时不阻断 */ }
        }

        var log = new SysAuditLog
        {
            UserId = userId,
            UserName = Truncate(ResolveUserName(context, http), 128),
            Category = ResolveCategory(path),
            Action = Truncate(ResolveAction(request.Method, path), 64) ?? string.Empty,
            Method = request.Method,
            Path = Truncate(path + request.QueryString, 256) ?? string.Empty,
            StatusCode = status,
            DurationMs = (int)stopwatch.ElapsedMilliseconds,
            IpAddress = Truncate(ip, 64),
            UserAgent = Truncate(request.Headers.UserAgent.ToString(), 256),
            ErrorMessage = Truncate(exception?.Message, 500),
            Success = status is >= 200 and < 400
        };

        try
        {
            using var scope = scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            // 不用 RequestAborted：请求结束后 token 可能已取消，审计仍要落库
            await audit.RecordAsync(log, CancellationToken.None);
        }
        catch (Exception writeException)
        {
            logger.LogError(writeException, "审计日志落库失败（action={Action}）", log.Action);
        }
    }

    /// <summary>
    /// 推断本次请求最终的状态码。
    /// 关键点：ActionFilter 阶段 Result 还没执行，Response.StatusCode 通常仍是 200，
    /// 所以 Forbid() / Unauthorized() / ObjectResult(StatusCode) 必须从 Result 上读，
    /// 否则所有 403 都会被审计误记成"成功"。业务异常尚未经中间件转换，按类型推断。
    /// </summary>
    public static int ResolveStatus(Exception? exception, IActionResult? result, int responseStatus)
    {
        if (exception is not null)
            return exception switch
            {
                ForbiddenException => StatusCodes.Status403Forbidden,
                BusinessRuleException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            };

        return result switch
        {
            ForbidResult => StatusCodes.Status403Forbidden,
            UnauthorizedResult => StatusCodes.Status401Unauthorized,
            ObjectResult { StatusCode: { } code } => code,
            StatusCodeResult s => s.StatusCode,
            _ => responseStatus
        };
    }

    private static string? ResolveUserName(ActionExecutingContext context, HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated == true)
            return http.User.Identity?.Name
                   ?? http.User.FindFirstValue("displayName")
                   ?? "已认证用户";

        // 登录请求此时尚未认证，只取用户名，不碰密码
        return context.ActionArguments.Values.OfType<LoginRequest>().FirstOrDefault()?.Username;
    }

    private static string ResolveCategory(string path)
    {
        if (path.Contains("/documents", StringComparison.OrdinalIgnoreCase)) return "documents";
        if (path.Contains("/permissions", StringComparison.OrdinalIgnoreCase)) return "permissions";
        if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)) return "auth";
        if (path.StartsWith("/api/users", StringComparison.OrdinalIgnoreCase)) return "users";
        if (path.StartsWith("/api/roles", StringComparison.OrdinalIgnoreCase)) return "roles";
        if (path.StartsWith("/api/kbs", StringComparison.OrdinalIgnoreCase)) return "kbs";
        if (path.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase)) return "settings";
        if (path.StartsWith("/api/agent-tools", StringComparison.OrdinalIgnoreCase)) return "tools";
        if (path.StartsWith("/api/chats", StringComparison.OrdinalIgnoreCase)) return "chats";
        return "other";
    }

    /// <summary>路径中的数字 ID 归一化后再查语义映射，未命中则回退为可读的 "METHOD path"</summary>
    private static string ResolveAction(string method, string path)
    {
        var normalized = string.Join("/",
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(seg => long.TryParse(seg, out _) ? "{id}" : seg));
        normalized = "/" + normalized;
        var key = $"{method} {normalized}";
        return ActionMap.TryGetValue(key, out var action) ? action : key;
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
