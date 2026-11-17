using Microsoft.EntityFrameworkCore;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>
/// AI 调用观测：落库 → 汇总 / 按天聚合 / 明细。
/// 重点覆盖按天聚合改为「最小列投影 + 内存分组」后的语义，以及 PostgreSQL 方言下的翻译回归。
/// </summary>
public class AiUsageServiceTests
{
    private static AiUsageService NewService(SmartKbDbContext db) => new(db);

    private static AiInvocationLog Log(DateTime at, bool success = true,
        int? totalTokens = 100, int totalMs = 1000) => new()
    {
        SessionId = 1,
        UserId = 1,
        Model = "test-model",
        Question = "问题",
        InputTokens = 60,
        OutputTokens = 40,
        TotalTokens = totalTokens,
        TotalMs = totalMs,
        Success = success,
        CreatedAt = at
    };

    [Fact]
    public async Task 按天聚合_同一天多条合并且各项统计正确()
    {
        await using var db = TestDb.Create();
        var day = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        db.AiInvocationLogs.AddRange(
            Log(day.AddHours(1), success: true, totalTokens: 100, totalMs: 1000),
            Log(day.AddHours(2), success: false, totalTokens: 200, totalMs: 3000),
            Log(day.AddHours(3), success: true, totalTokens: null, totalMs: 2000));
        await db.SaveChangesAsync();

        var result = await NewService(db).GetDailyAsync(30);

        var d = Assert.Single(result);
        Assert.Equal(day.Year, d.Date.Year);
        Assert.Equal(day.Month, d.Date.Month);
        Assert.Equal(day.Day, d.Date.Day);
        Assert.Equal(3, d.Invocations);
        Assert.Equal(1, d.Failures);
        Assert.Equal(300L, d.Tokens);          // null 按 0 计
        Assert.Equal(2000d, d.AvgTotalMs);     // (1000+3000+2000)/3
    }

    [Fact]
    public async Task 按天聚合_跨天分组且按日期升序()
    {
        await using var db = TestDb.Create();
        var today = DateTime.UtcNow.Date;
        db.AiInvocationLogs.AddRange(
            Log(today.AddHours(9), totalTokens: 10, totalMs: 500),
            Log(today.AddDays(-1).AddHours(9), totalTokens: 20, totalMs: 700),
            Log(today.AddDays(-1).AddHours(18), totalTokens: 30, totalMs: 900));
        await db.SaveChangesAsync();

        var result = await NewService(db).GetDailyAsync(30);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].Date < result[1].Date);
        Assert.Equal(2, result[0].Invocations);   // 昨天
        Assert.Equal(50L, result[0].Tokens);
        Assert.Equal(800d, result[0].AvgTotalMs);
        Assert.Equal(1, result[1].Invocations);   // 今天
    }

    [Fact]
    public async Task 按天聚合_超出days范围的记录不计入()
    {
        await using var db = TestDb.Create();
        db.AiInvocationLogs.AddRange(
            Log(DateTime.UtcNow.AddDays(-1)),
            Log(DateTime.UtcNow.AddDays(-40)));
        await db.SaveChangesAsync();

        var result = await NewService(db).GetDailyAsync(30);

        Assert.Single(result);
        Assert.Equal(1, result[0].Invocations);
    }

    [Fact]
    public async Task 按天聚合_全零token返回null_空表返回空列表()
    {
        await using var empty = TestDb.Create();
        Assert.Empty(await NewService(empty).GetDailyAsync(30));

        await using var db = TestDb.Create();
        db.AiInvocationLogs.Add(Log(DateTime.UtcNow, totalTokens: null));
        await db.SaveChangesAsync();

        var d = Assert.Single(await NewService(db).GetDailyAsync(30));
        Assert.Null(d.Tokens);
        Assert.Equal(0, d.Failures);
    }

    [Fact]
    public async Task 落库后_汇总与最近明细都能读到()
    {
        await using var db = TestDb.Create();
        var svc = NewService(db);
        // GetRecentAsync 的投影要读 User 导航取显示名，夹具需有对应账号
        db.Users.Add(new User
        {
            Id = 1, Username = "tester", PasswordHash = "x", DisplayName = "测试用户"
        });
        await db.SaveChangesAsync();
        await svc.LogAsync(Log(DateTime.UtcNow, success: true, totalTokens: 100, totalMs: 1000));
        await svc.LogAsync(Log(DateTime.UtcNow, success: false, totalTokens: 50, totalMs: 2000));

        var summary = await svc.GetSummaryAsync(30);
        Assert.Equal(2, summary.TotalInvocations);
        Assert.Equal(1, summary.SuccessInvocations);
        Assert.Equal(50d, summary.SuccessRate);
        Assert.Equal(150L, summary.TotalTokens);

        var recent = await svc.GetRecentAsync(50);
        Assert.Equal(2, recent.Count);
        Assert.Equal("问题", recent[0].Question);
        Assert.Equal(1, recent.Count(x => x.Success));
        Assert.Equal(1, recent.Count(x => !x.Success));
    }

    [Fact]
    public async Task 按天聚合_PostgreSQL方言下不再是不可翻译的表达式()
    {
        // 不连真实库：用 Npgsql 方言跑「翻译阶段」，端口 1 必然连接失败。
        // 旧实现 GroupBy(new DateTime(年,月,日)) + 条件投影会在连接之前先抛
        // InvalidOperationException("The LINQ expression ... could not be translated")，
        // 这正是线上 /api/ai-usage/daily 反复 500 的根因。
        var options = new DbContextOptionsBuilder<SmartKbDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1")
            .Options;
        await using var db = new SmartKbDbContext(options);

        var ex = await Record.ExceptionAsync(() => NewService(db).GetDailyAsync(30));

        Assert.NotNull(ex); // 无真实库，执行阶段必然失败
        Assert.DoesNotContain("could not be translated", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The LINQ expression", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
