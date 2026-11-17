using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using SmartKB.Api.Filters;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>审计日志：写入容错、分页/过滤/今日统计、分页参数边界</summary>
public class AuditServiceTests
{
    private static AuditService NewService(SmartKbDbContext db) =>
        new(db, NullLogger<AuditService>.Instance);

    private static SysAuditLog Log(string category, string action, string user, bool success = true) => new()
    {
        Category = category,
        Action = action,
        UserName = user,
        UserId = success ? 1 : null,
        Method = "POST",
        Path = $"/api/{category}",
        StatusCode = success ? 200 : 401,
        DurationMs = 12,
        Success = success,
        CreatedAt = DateTime.UtcNow
    };

    private static async Task SeedAsync(AuditService svc)
    {
        for (var i = 0; i < 5; i++)
            await svc.RecordAsync(Log("kbs", "create_kb", "张工"));
        var failedLogin = Log("auth", "login", "陌生人", success: false);
        failedLogin.ErrorMessage = "密码错误";
        await svc.RecordAsync(failedLogin);
    }

    [Fact]
    public async Task 分页查询_分类结果与关键字过滤均生效()
    {
        using var db = TestDb.Create();
        var svc = NewService(db);
        await SeedAsync(svc);

        var all = await svc.QueryAsync(new AuditLogQuery(1, 20, null, null, null, null, null, null));
        Assert.Equal(6, all.Total);
        Assert.Equal(6, all.TodayCount);
        Assert.Equal(1, all.TodayFailures);
        Assert.Equal("login", all.Items[0].Action); // 按 Id 降序

        var kbs = await svc.QueryAsync(new AuditLogQuery(1, 20, "kbs", null, null, null, null, null));
        Assert.Equal(5, kbs.Total);
        Assert.All(kbs.Items, i => Assert.Equal("kbs", i.Category));

        var failed = await svc.QueryAsync(new AuditLogQuery(1, 20, null, null, false, null, null, null));
        Assert.Single(failed.Items);
        Assert.Equal("密码错误", failed.Items[0].ErrorMessage);

        var byUser = await svc.QueryAsync(new AuditLogQuery(1, 20, null, null, null, "陌生人", null, null));
        Assert.Single(byUser.Items);

        var onlyFirstUser = await svc.QueryAsync(new AuditLogQuery(1, 20, null, 1, null, null, null, null));
        Assert.Equal(5, onlyFirstUser.Total);

        var future = await svc.QueryAsync(new AuditLogQuery(
            1, 20, null, null, null, null, DateTime.UtcNow.AddDays(1), null));
        Assert.Equal(0, future.Total);
        Assert.Equal(6, future.TodayCount); // 今日统计不受列表过滤影响
    }

    [Fact]
    public async Task 分页参数_越界自动钳制()
    {
        using var db = TestDb.Create();
        var svc = NewService(db);
        await SeedAsync(svc);

        var clamped = await svc.QueryAsync(new AuditLogQuery(0, 999, null, null, null, null, null, null));
        Assert.Equal(1, clamped.Page);
        Assert.Equal(100, clamped.PageSize);
        Assert.Equal(6, clamped.Items.Count);

        var page2 = await svc.QueryAsync(new AuditLogQuery(2, 2, null, null, null, null, null, null));
        Assert.Equal(2, page2.Items.Count);
        Assert.Equal(6, page2.Total);
    }

    [Fact]
    public async Task 审计写入失败_不抛异常_不影响业务请求()
    {
        var db = TestDb.Create();
        db.Dispose(); // 已释放的 DbContext 必然写入失败

        var svc = NewService(db);
        await svc.RecordAsync(Log("kbs", "create_kb", "张工"));
    }

    [Fact]
    public void 状态码_从ActionResult读取_避免Forbid被误记为200()
    {
        // ActionFilter 阶段 Response.StatusCode 仍是 200，必须看 Result
        Assert.Equal(403, AuditLogFilter.ResolveStatus(null, new ForbidResult(), 200));
        Assert.Equal(401, AuditLogFilter.ResolveStatus(null, new UnauthorizedResult(), 200));
        Assert.Equal(400, AuditLogFilter.ResolveStatus(
            null, new ObjectResult(new { message = "x" }) { StatusCode = 400 }, 200));
        Assert.Equal(204, AuditLogFilter.ResolveStatus(null, new StatusCodeResult(204), 200));
        Assert.Equal(200, AuditLogFilter.ResolveStatus(null, new OkResult(), 200));
    }

    [Fact]
    public void 状态码_业务异常按类型映射为403或400()
    {
        Assert.Equal(403, AuditLogFilter.ResolveStatus(new ForbiddenException(), null, 200));
        Assert.Equal(400, AuditLogFilter.ResolveStatus(new BusinessRuleException("x"), null, 200));
        Assert.Equal(500, AuditLogFilter.ResolveStatus(new InvalidOperationException(), null, 200));
    }
}
