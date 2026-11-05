using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartKB.AI.Tools;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

/// <summary>
/// 配置化 Agent 工具的后台管理：CRUD + 启停 + 试运行。
/// 密钥只登记环境变量名，服务层也从不读取其值回显给前端（只告诉"已配置/未配置"）。
/// </summary>
public interface IAgentToolService
{
    Task<List<AgentToolDto>> ListAsync(CancellationToken ct = default);
    Task<AgentToolDto> GetAsync(long id, CancellationToken ct = default);
    Task<AgentToolDto> CreateAsync(AgentToolUpsertDto dto, CancellationToken ct = default);
    Task<AgentToolDto> UpdateAsync(long id, AgentToolUpsertDto dto, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<AgentToolDto> SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
    Task<AgentToolTestResultDto> TestAsync(AgentToolTestDto dto, CancellationToken ct = default);

    /// <summary>工具调用白名单（可授权的用户/角色候选一并给出）</summary>
    Task<AgentToolGrantsDto> GetGrantsAsync(long id, CancellationToken ct = default);

    /// <summary>全量覆盖白名单；传空列表即恢复"公开"</summary>
    Task<AgentToolGrantsDto> SetGrantsAsync(
        long id, SetToolGrantsDto dto, CancellationToken ct = default);
}

public class AgentToolService(
    SmartKbDbContext db,
    IAgentToolRegistry registry,
    IHttpToolExecutor executor,
    IConfiguration configuration) : IAgentToolService
{
    private static readonly HashSet<string> BuiltInToolNames = new(StringComparer.Ordinal)
        { "search_knowledge", "query_ledger" };

    public async Task<List<AgentToolDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.AgentTools.AsNoTracking()
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(ct);
        var restricted = (await db.AgentToolGrants.AsNoTracking()
            .Select(g => g.ToolId).Distinct().ToListAsync(ct)).ToHashSet();
        return rows.Select(t => ToDto(t, restricted.Contains(t.Id))).ToList();
    }

    public async Task<AgentToolDto> GetAsync(long id, CancellationToken ct = default) =>
        await ToDtoAsync(await FindAsync(id, ct), ct);

    public async Task<AgentToolDto> CreateAsync(AgentToolUpsertDto dto, CancellationToken ct = default)
    {
        var def = BuildDefinition(0, dto);
        await EnsureNameAvailableAsync(def.Name, null, ct);
        await EnsureEnabledQuotaAsync(dto.Enabled, null, ct);

        var entity = new AgentTool { CreatedAt = DateTime.UtcNow };
        Apply(dto, entity);
        db.AgentTools.Add(entity);
        await db.SaveChangesAsync(ct);
        registry.Invalidate();
        return await ToDtoAsync(entity, ct);
    }

    public async Task<AgentToolDto> UpdateAsync(
        long id, AgentToolUpsertDto dto, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        var def = BuildDefinition(id, dto);
        await EnsureNameAvailableAsync(def.Name, id, ct);
        await EnsureEnabledQuotaAsync(dto.Enabled, id, ct);

        Apply(dto, entity);
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        registry.Invalidate();
        return await ToDtoAsync(entity, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        db.AgentTools.Remove(entity);
        await db.SaveChangesAsync(ct);
        registry.Invalidate();
    }

    public async Task<AgentToolDto> SetEnabledAsync(
        long id, bool enabled, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        await EnsureEnabledQuotaAsync(enabled, id, ct);
        entity.Enabled = enabled;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        registry.Invalidate();
        return await ToDtoAsync(entity, ct);
    }

    public async Task<AgentToolTestResultDto> TestAsync(
        AgentToolTestDto dto, CancellationToken ct = default)
    {
        var entity = await FindAsync(dto.Id, ct);
        var def = BuildDefinition(entity.Id, ToUpsert(entity));

        var args = dto.Arguments.ToDictionary(
            kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

        var sw = Stopwatch.StartNew();
        var result = await executor.InvokeDetailedAsync(def, args, ct);
        sw.Stop();
        return new AgentToolTestResultDto(result.Success, (int)sw.ElapsedMilliseconds, result.Output);
    }

    public async Task<AgentToolGrantsDto> GetGrantsAsync(long id, CancellationToken ct = default)
    {
        _ = await FindAsync(id, ct);

        var grants = await db.AgentToolGrants.AsNoTracking()
            .Where(g => g.ToolId == id)
            .ToListAsync(ct);

        var users = await db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Take(500)
            .Select(u => new ToolGrantOptionDto(u.Id, u.DisplayName + "（" + u.Username + "）"))
            .ToListAsync(ct);

        var roles = await db.Roles.AsNoTracking()
            .OrderBy(r => r.Name).ThenBy(r => r.Id)
            .Select(r => new ToolGrantOptionDto(r.Id, r.Name))
            .ToListAsync(ct);

        return new AgentToolGrantsDto(
            id,
            grants.Where(g => g.UserId.HasValue).Select(g => g.UserId!.Value).Distinct().ToList(),
            grants.Where(g => g.RoleId.HasValue).Select(g => g.RoleId!.Value).Distinct().ToList(),
            users, roles);
    }

    public async Task<AgentToolGrantsDto> SetGrantsAsync(
        long id, SetToolGrantsDto dto, CancellationToken ct = default)
    {
        _ = await FindAsync(id, ct);

        var userIds = (dto.UserIds ?? []).Distinct().ToList();
        var roleIds = (dto.RoleIds ?? []).Distinct().ToList();
        if (userIds.Count > 200 || roleIds.Count > 100)
            throw new BusinessRuleException("单个工具最多授权 200 个用户 / 100 个角色");

        // 防 dangling：目标必须真实存在（不做静默忽略，避免管理员以为授权成功）
        var validUsers = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        if (validUsers.Count != userIds.Count)
            throw new BusinessRuleException("授权用户不存在或已删除");

        var validRoles = await db.Roles.AsNoTracking()
            .Where(r => roleIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        if (validRoles.Count != roleIds.Count)
            throw new BusinessRuleException("授权角色不存在或已删除");

        var existing = await db.AgentToolGrants.Where(g => g.ToolId == id).ToListAsync(ct);
        db.AgentToolGrants.RemoveRange(existing);
        db.AgentToolGrants.AddRange(userIds.Select(uid => new AgentToolGrant
        {
            ToolId = id, UserId = uid, CreatedAt = DateTime.UtcNow
        }));
        db.AgentToolGrants.AddRange(roleIds.Select(rid => new AgentToolGrant
        {
            ToolId = id, RoleId = rid, CreatedAt = DateTime.UtcNow
        }));
        await db.SaveChangesAsync(ct);
        registry.Invalidate();

        return await GetGrantsAsync(id, ct);
    }

    // ---------- 内部 ----------

    private async Task<AgentTool> FindAsync(long id, CancellationToken ct) =>
        await db.AgentTools.FirstOrDefaultAsync(t => t.Id == id, ct)
        ?? throw new BusinessRuleException($"工具 {id} 不存在");

    /// <summary>构造定义并做完整校验；ArgumentException / JsonException 统一转成可读的业务错误</summary>
    private AgentToolDefinition BuildDefinition(long id, AgentToolUpsertDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.DisplayName) || dto.DisplayName.Length > 128)
            throw new BusinessRuleException("显示名称须在 1–128 字之间");
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.UrlTemplate) ||
            string.IsNullOrWhiteSpace(dto.ParametersJson) || string.IsNullOrWhiteSpace(dto.HttpMethod))
            throw new BusinessRuleException("工具名、URL 模板、HTTP 方法与参数定义均不能为空");
        if (dto.UrlTemplate.Length > 1000)
            throw new BusinessRuleException("URL 模板过长（上限 1000 字符）");
        if (dto.ParametersJson.Length > 8000)
            throw new BusinessRuleException("参数定义过长");
        if (BuiltInToolNames.Contains(dto.Name))
            throw new BusinessRuleException($"{dto.Name} 是内置工具名，请换一个");

        var allowedKbIds = dto.AllowedKbIds ?? [];
        if (allowedKbIds.Count > AgentToolValidator.MaxAllowedKbIds)
            throw new BusinessRuleException($"知识库范围最多 {AgentToolValidator.MaxAllowedKbIds} 个");

        try
        {
            var def = new AgentToolDefinition(
                id,
                dto.Name.Trim(),
                dto.DisplayName.Trim(),
                (dto.Description ?? string.Empty).Trim(),
                dto.HttpMethod.Trim().ToUpperInvariant(),
                dto.UrlTemplate.Trim(),
                string.IsNullOrWhiteSpace(dto.BodyTemplate) ? null : dto.BodyTemplate.Trim(),
                AgentToolValidator.ParseParameters(dto.ParametersJson),
                NullIfBlank(dto.AuthHeaderName),
                NullIfBlank(dto.AuthEnvVar),
                dto.AuthValuePrefix,
                dto.TimeoutSeconds,
                dto.MaxResponseChars,
                AgentToolRegistry.ParseAllowedHosts(configuration["Tools:AllowedHosts"]),
                allowedKbIds);
            AgentToolValidator.Validate(def);
            return def;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            throw new BusinessRuleException(ex.Message);
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void Apply(AgentToolUpsertDto dto, AgentTool entity)
    {
        entity.Name = dto.Name.Trim();
        entity.DisplayName = dto.DisplayName.Trim();
        entity.Description = (dto.Description ?? string.Empty).Trim();
        entity.HttpMethod = dto.HttpMethod.Trim().ToUpperInvariant();
        entity.UrlTemplate = dto.UrlTemplate.Trim();
        entity.BodyTemplate = NullIfBlank(dto.BodyTemplate);
        entity.ParametersJson = dto.ParametersJson;
        entity.AllowedKbIdsJson = System.Text.Json.JsonSerializer.Serialize(dto.AllowedKbIds ?? []);
        entity.AuthHeaderName = NullIfBlank(dto.AuthHeaderName);
        entity.AuthEnvVar = NullIfBlank(dto.AuthEnvVar);
        entity.AuthValuePrefix = NullIfBlank(dto.AuthValuePrefix);
        entity.TimeoutSeconds = dto.TimeoutSeconds;
        entity.MaxResponseChars = dto.MaxResponseChars;
        entity.Enabled = dto.Enabled;
        entity.SortOrder = dto.SortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
    }

    private static AgentToolUpsertDto ToUpsert(AgentTool t) => new(
        t.Name, t.DisplayName, t.Description, t.HttpMethod, t.UrlTemplate, t.BodyTemplate,
        t.ParametersJson, t.AuthHeaderName, t.AuthEnvVar, t.AuthValuePrefix,
        t.TimeoutSeconds, t.MaxResponseChars, t.Enabled, t.SortOrder,
        AgentToolValidator.ParseAllowedKbIds(t.AllowedKbIdsJson).ToList());

    private async Task<AgentToolDto> ToDtoAsync(AgentTool t, CancellationToken ct) =>
        ToDto(t, await db.AgentToolGrants.AsNoTracking().AnyAsync(g => g.ToolId == t.Id, ct));

    private static AgentToolDto ToDto(AgentTool t, bool restricted) => new(
        t.Id, t.Name, t.DisplayName, t.Description, t.HttpMethod, t.UrlTemplate,
        t.BodyTemplate, t.ParametersJson, t.AuthHeaderName, t.AuthEnvVar, t.AuthValuePrefix,
        t.TimeoutSeconds, t.MaxResponseChars, t.Enabled, t.SortOrder,
        !string.IsNullOrEmpty(t.AuthEnvVar) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(t.AuthEnvVar)),
        restricted,
        AgentToolValidator.ParseAllowedKbIds(t.AllowedKbIdsJson).ToList(),
        t.CreatedAt, t.UpdatedAt);

    private async Task EnsureNameAvailableAsync(string name, long? excludeId, CancellationToken ct)
    {
        var exists = await db.AgentTools.AsNoTracking()
            .AnyAsync(t => t.Name == name && (excludeId == null || t.Id != excludeId), ct);
        if (exists) throw new BusinessRuleException($"工具名 {name} 已存在");
    }

    private async Task EnsureEnabledQuotaAsync(bool enabled, long? excludeId, CancellationToken ct)
    {
        if (!enabled) return;
        var count = await db.AgentTools.AsNoTracking()
            .CountAsync(t => t.Enabled && (excludeId == null || t.Id != excludeId), ct);
        if (count >= AgentToolRegistry.MaxEnabledTools)
            throw new BusinessRuleException(
                $"同时启用的工具最多 {AgentToolRegistry.MaxEnabledTools} 个，请先停用其他工具");
    }
}
