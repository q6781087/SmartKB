using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Tools;

/// <summary>单个工具的授权白名单（空集表示尚未对该工具设限）</summary>
public sealed record ToolGrantRule(HashSet<long> UserIds, HashSet<long> RoleIds);

/// <summary>一次加载的注册表快照：工具定义 + 授权规则，两者同源缓存、同步失效</summary>
public sealed record AgentToolCatalog(
    IReadOnlyList<AgentToolDefinition> Tools,
    IReadOnlyDictionary<long, ToolGrantRule> Rules);

/// <summary>
/// 工具注册表：问答时取当前启用的工具定义。缓存为 Singleton（10 秒 TTL + 保存时主动失效），
/// 读库服务为 Scoped（跟随 DbContext 生命周期）。
/// </summary>
public interface IAgentToolRegistry
{
    Task<IReadOnlyList<AgentToolDefinition>> GetEnabledAsync(CancellationToken ct = default);

    /// <summary>按调用者身份过滤：无授权记录的工具公开，有记录的工具仅白名单用户/角色可用</summary>
    Task<IReadOnlyList<AgentToolDefinition>> GetEnabledForAsync(
        ToolCaller caller, CancellationToken ct = default);

    /// <summary>后台增删改（含授权变更）后主动失效，避免等 TTL</summary>
    void Invalidate();
}

public sealed class AgentToolCache
{
    private readonly object _gate = new();
    private AgentToolCatalog? _catalog;
    private DateTimeOffset _expiresAt;

    public AgentToolCatalog? Get()
    {
        lock (_gate)
            return _catalog is not null && DateTimeOffset.UtcNow < _expiresAt ? _catalog : null;
    }

    public AgentToolCatalog Set(AgentToolCatalog catalog)
    {
        lock (_gate)
        {
            _catalog = catalog;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
            return catalog;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _catalog = null;
            _expiresAt = default;
        }
    }
}

public class AgentToolRegistry(
    SmartKbDbContext db,
    AgentToolCache cache,
    IConfiguration configuration,
    ILogger<AgentToolRegistry> logger) : IAgentToolRegistry
{
    /// <summary>注入 prompt 的工具数量上限，防止工具过多挤占上下文</summary>
    public const int MaxEnabledTools = 20;

    public async Task<IReadOnlyList<AgentToolDefinition>> GetEnabledAsync(CancellationToken ct = default) =>
        (await GetCatalogAsync(ct)).Tools;

    public async Task<IReadOnlyList<AgentToolDefinition>> GetEnabledForAsync(
        ToolCaller caller, CancellationToken ct = default)
    {
        var catalog = await GetCatalogAsync(ct);

        // 快速路径：没有设限的工具，或管理员
        if (catalog.Rules.Count == 0 || caller.IsAdmin) return catalog.Tools;

        // 角色 ID 现查库，不信任可能过期的 JWT 角色声明。
        // 用 Include 显式加载多对多导航（InMemory provider 下导航投影不可靠）
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Id == caller.UserId, ct);
        var roleIds = user?.Roles.Select(r => r.Id).ToHashSet() ?? [];

        return catalog.Tools
            .Where(t => !catalog.Rules.TryGetValue(t.Id, out var rule)
                        || rule.UserIds.Contains(caller.UserId)
                        || roleIds.Overlaps(rule.RoleIds))
            .ToArray();
    }

    private async Task<AgentToolCatalog> GetCatalogAsync(CancellationToken ct) =>
        cache.Get() is { } cached ? cached : await LoadAsync(ct);

    private async Task<AgentToolCatalog> LoadAsync(CancellationToken ct)
    {
        var rows = await db.AgentTools.AsNoTracking()
            .Where(t => t.Enabled)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .Take(MaxEnabledTools)
            .ToListAsync(ct);

        var grants = await db.AgentToolGrants.AsNoTracking().ToListAsync(ct);

        var hosts = ParseAllowedHosts(configuration["Tools:AllowedHosts"]);
        var list = new List<AgentToolDefinition>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                // 直接改库也要过校验，坏配置只跳过该工具、不影响问答主流程
                var definition = FromEntity(row, hosts);
                AgentToolValidator.Validate(definition);
                list.Add(definition);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                logger.LogWarning(ex, "工具 {Tool}(Id={Id}) 配置不合法，已跳过加载", row.Name, row.Id);
            }
        }

        var rules = grants.GroupBy(g => g.ToolId)
            .ToDictionary(
                g => g.Key,
                g => new ToolGrantRule(
                    g.Where(x => x.UserId.HasValue).Select(x => x.UserId!.Value).ToHashSet(),
                    g.Where(x => x.RoleId.HasValue).Select(x => x.RoleId!.Value).ToHashSet()));

        return cache.Set(new AgentToolCatalog(list, rules));
    }

    public void Invalidate() => cache.Invalidate();

    /// <summary>实体 → AI 层定义（纯函数，便于单测）</summary>
    public static AgentToolDefinition FromEntity(AgentTool t, IReadOnlyList<string>? allowedHosts = null) => new(
        t.Id, t.Name, t.DisplayName, t.Description, t.HttpMethod, t.UrlTemplate, t.BodyTemplate,
        AgentToolValidator.ParseParameters(t.ParametersJson),
        t.AuthHeaderName, t.AuthEnvVar, t.AuthValuePrefix,
        t.TimeoutSeconds, t.MaxResponseChars, allowedHosts,
        AgentToolValidator.ParseAllowedKbIds(t.AllowedKbIdsJson));

    /// <summary>Tools:AllowedHosts 支持逗号/分号分隔，元素可为 host 或 host:port；留空表示不限制</summary>
    public static IReadOnlyList<string> ParseAllowedHosts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length <= 253)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
