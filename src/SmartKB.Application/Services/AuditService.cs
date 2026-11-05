using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

/// <summary>操作审计日志：写入与管理员查询</summary>
public interface IAuditService
{
    /// <summary>写一条审计。审计失败绝不抛给业务调用方（不能因日志拖垮请求）</summary>
    Task RecordAsync(SysAuditLog log, CancellationToken ct = default);

    Task<AuditLogPageDto> QueryAsync(AuditLogQuery query, CancellationToken ct = default);
}

public class AuditService(SmartKbDbContext db, ILogger<AuditService> logger) : IAuditService
{
    public async Task RecordAsync(SysAuditLog log, CancellationToken ct = default)
    {
        try
        {
            db.AuditLogs.Add(log);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // 审计写入失败只记日志，不影响业务请求结果
            logger.LogError(ex, "审计日志写入失败（action={Action}, path={Path}）", log.Action, log.Path);
        }
    }

    public async Task<AuditLogPageDto> QueryAsync(AuditLogQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);

        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Category))
            query = query.Where(l => l.Category == q.Category.Trim());
        if (q.UserId is { } userId)
            query = query.Where(l => l.UserId == userId);
        if (q.Success is { } success)
            query = query.Where(l => l.Success == success);
        if (q.From is { } from)
            query = query.Where(l => l.CreatedAt >= from);
        if (q.To is { } to)
            query = query.Where(l => l.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var kw = q.Keyword.Trim();
            query = query.Where(l =>
                (l.UserName != null && l.UserName.Contains(kw))
                || l.Path.Contains(kw)
                || l.Action.Contains(kw));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new AuditLogDto(
                l.Id, l.UserId, l.UserName, l.Category, l.Action,
                l.Method, l.Path, l.StatusCode, l.DurationMs,
                l.IpAddress, l.UserAgent, l.ErrorMessage,
                l.Success, l.CreatedAt))
            .ToListAsync(ct);

        var today = DateTime.UtcNow.Date;
        var todayCount = await db.AuditLogs.AsNoTracking()
            .CountAsync(l => l.CreatedAt >= today, ct);
        var todayFailures = await db.AuditLogs.AsNoTracking()
            .CountAsync(l => l.CreatedAt >= today && !l.Success, ct);

        return new AuditLogPageDto(items, total, page, pageSize, todayCount, todayFailures);
    }
}
