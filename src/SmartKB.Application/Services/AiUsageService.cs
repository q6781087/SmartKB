using Microsoft.EntityFrameworkCore;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

/// <summary>AI 调用观测：落库 + 统计查询（token / 耗时 / 成功率）</summary>
public interface IAiUsageService
{
    Task LogAsync(AiInvocationLog log);

    /// <summary>汇总（近 days 天）</summary>
    Task<AiUsageSummaryDto> GetSummaryAsync(int days = 30);

    /// <summary>按天聚合（近 days 天，升序）</summary>
    Task<List<AiDailyUsageDto>> GetDailyAsync(int days = 30);

    /// <summary>最近调用明细（新→旧）</summary>
    Task<List<AiInvocationDto>> GetRecentAsync(int limit = 50);
}

public class AiUsageService(SmartKbDbContext db) : IAiUsageService
{
    public async Task LogAsync(AiInvocationLog log)
    {
        db.AiInvocationLogs.Add(log);
        await db.SaveChangesAsync();
    }

    public async Task<AiUsageSummaryDto> GetSummaryAsync(int days = 30)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var logs = db.AiInvocationLogs.AsNoTracking().Where(l => l.CreatedAt >= since);

        var total = await logs.CountAsync();
        var success = await logs.CountAsync(l => l.Success);
        var users = await logs.Select(l => l.UserId).Distinct().CountAsync();

        // 聚合交给 SQL，避免拉明细
        var agg = await logs.GroupBy(_ => 1).Select(g => new
        {
            TotalTokens = (long?)g.Sum(x => x.TotalTokens ?? 0),
            InputTokens = (long?)g.Sum(x => x.InputTokens ?? 0),
            OutputTokens = (long?)g.Sum(x => x.OutputTokens ?? 0),
            AvgTtft = g.Where(x => x.FirstTokenMs != null).Average(x => (double?)x.FirstTokenMs),
            AvgTotal = g.Average(x => (double?)x.TotalMs)
        }).FirstOrDefaultAsync();

        return new AiUsageSummaryDto(
            total, success, total == 0 ? 1 : Math.Round(success * 100.0 / total, 1),
            agg?.TotalTokens, agg?.InputTokens, agg?.OutputTokens,
            agg?.AvgTtft.HasValue == true ? Math.Round(agg.AvgTtft.Value, 0) : null,
            agg?.AvgTotal.HasValue == true ? Math.Round(agg.AvgTotal.Value, 0) : null,
            users);
    }

    public async Task<List<AiDailyUsageDto>> GetDailyAsync(int days = 30)
    {
        var since = DateTime.UtcNow.AddDays(-days);

        // EF Core + Npgsql 无法把 GroupBy(new DateTime(年,月,日)) + 条件投影翻译成 SQL，
        // 在 PostgreSQL 上会抛 InvalidOperationException，导致 /api/ai-usage/daily 直接 500。
        // 改为只投影聚合所需的 4 列，在内存中按 UTC 日期分组：
        // 每行仅 4 个标量、天数受 days(≤365) 约束，关系型 / InMemory 行为一致且可单测。
        var rows = await db.AiInvocationLogs.AsNoTracking()
            .Where(l => l.CreatedAt >= since)
            .Select(l => new { l.CreatedAt, l.Success, l.TotalTokens, l.TotalMs })
            .ToListAsync();

        return rows
            .GroupBy(l => l.CreatedAt.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var tokens = g.Sum(x => (long)(x.TotalTokens ?? 0));
                return new AiDailyUsageDto(
                    g.Key,
                    g.Count(),
                    g.Count(x => !x.Success),
                    tokens > 0 ? tokens : null,
                    g.Average(x => (double)x.TotalMs));
            })
            .ToList();
    }

    public async Task<List<AiInvocationDto>> GetRecentAsync(int limit = 50) =>
        await db.AiInvocationLogs.AsNoTracking()
            .OrderByDescending(l => l.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(l => new AiInvocationDto(
                l.Id, l.SessionId,
                l.User != null ? l.User.DisplayName : $"用户{ l.UserId }",
                l.Model, l.Question,
                l.InputTokens, l.OutputTokens, l.TotalTokens,
                l.FirstTokenMs, l.TotalMs, l.ToolCalls, l.RetrievedChunks,
                l.Success, l.ErrorMessage, l.CreatedAt))
            .ToListAsync();
}
