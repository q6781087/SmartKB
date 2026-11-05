using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartKB.AI.Configuration;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

/// <summary>
/// 运行时配置中心：默认值来自 appsettings 的 RAG 节，管理后台写入 sys_settings 覆盖。
/// 只放"问答行为"参数——连接串、ApiKey 一律不走这里（仅环境变量注入）。
/// </summary>
public interface ISettingsService : IRagSettingsProvider
{
    /// <summary>后台列表：配置项元数据 + 当前生效值 + 来源</summary>
    Task<SettingsResponse> GetAllAsync(CancellationToken ct = default);

    /// <summary>批量更新（key 必须在白名单内，值按类型与范围校验）</summary>
    Task<RagSettings> UpdateAsync(Dictionary<string, JsonElement> updates, CancellationToken ct = default);

    /// <summary>恢复默认：删除所有 rag.* 覆盖记录</summary>
    Task<RagSettings> ResetAsync(CancellationToken ct = default);
}

public class SettingsService(
    SmartKbDbContext db,
    IConfiguration configuration,
    SettingsCache cache) : ISettingsService
{
    private const string KeyPrefix = "rag.";

    private static readonly HashSet<string> KnownKeys =
        RagDefaults.Descriptors.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>appsettings 的 RAG 节作为客户环境的默认基线</summary>
    private RagSettings BuildDefaults()
    {
        string? S(string k, string? d) => configuration[$"RAG:{k}"] is { Length: > 0 } s ? s : d;
        int I(string k, int d) => int.TryParse(configuration[$"RAG:{k}"], out var v) ? v : d;
        bool B(string k, bool d) => bool.TryParse(configuration[$"RAG:{k}"], out var v) ? v : d;
        float? F(string k, float? d) => float.TryParse(configuration[$"RAG:{k}"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
        int? NI(string k, int? d) => int.TryParse(configuration[$"RAG:{k}"], out var v) ? v : d;

        return RagDefaults.Instance with
        {
            SystemPrompt = S("SystemPrompt", RagDefaults.SystemPrompt)!,
            TopK = I("TopK", RagDefaults.Instance.TopK),
            CandidateTopK = I("CandidateTopK", RagDefaults.Instance.CandidateTopK),
            HybridEnabled = B("HybridEnabled", RagDefaults.Instance.HybridEnabled),
            MetadataFilterEnabled = B("MetadataFilterEnabled", RagDefaults.Instance.MetadataFilterEnabled),
            RerankEnabled = B("RerankEnabled", RagDefaults.Instance.RerankEnabled),
            RerankProvider = S("RerankProvider", RagDefaults.Instance.RerankProvider)!,
            RerankTopN = I("RerankTopN", RagDefaults.Instance.RerankTopN),
            RrfK = I("RrfK", RagDefaults.Instance.RrfK),
            Temperature = F("Temperature", RagDefaults.Instance.Temperature),
            MaxOutputTokens = NI("MaxOutputTokens", RagDefaults.Instance.MaxOutputTokens)
        };
    }

    public Task<RagSettings> GetAsync(CancellationToken ct = default) =>
        cache.Get() is { } cached ? Task.FromResult(cached) : LoadAsync(ct);

    private async Task<RagSettings> LoadAsync(CancellationToken ct)
    {
        var overrides = await ReadOverridesAsync(ct);
        var settings = Apply(BuildDefaults(), overrides);
        return cache.Set(settings);
    }

    private async Task<Dictionary<string, JsonElement>> ReadOverridesAsync(CancellationToken ct)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.Key.StartsWith(KeyPrefix))
            .ToListAsync(ct);

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!KnownKeys.Contains(row.Key)) continue;
            try
            {
                using var doc = JsonDocument.Parse(row.ValueJson);
                result[row.Key] = doc.RootElement.Clone();
            }
            catch (JsonException) { /* 坏配置忽略，回退默认 */ }
        }
        return result;
    }

    /// <summary>把 sys_settings 覆盖项应用到默认配置（纯函数，可单测）</summary>
    public static RagSettings Apply(RagSettings defaults, IReadOnlyDictionary<string, JsonElement> overrides)
    {
        var s = defaults;
        JsonElement? O(string key) => overrides.TryGetValue(key, out var v) ? v : null;

        string? Str(string key) => O(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        int? Int(string key) => O(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;
        bool? Bool(string key) => O(key) is { ValueKind: JsonValueKind.True or JsonValueKind.False } v ? v.GetBoolean() : null;
        double? Num(string key) => O(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) ? n : null;

        return s with
        {
            SystemPrompt = Str("rag.system_prompt") is { Length: > 0 } prompt ? prompt : s.SystemPrompt,
            TopK = Math.Clamp(Int("rag.top_k") ?? s.TopK, 1, 30),
            CandidateTopK = Math.Clamp(Int("rag.candidate_top_k") ?? s.CandidateTopK, 1, 100),
            HybridEnabled = Bool("rag.hybrid_enabled") ?? s.HybridEnabled,
            MetadataFilterEnabled = Bool("rag.metadata_filter_enabled") ?? s.MetadataFilterEnabled,
            RerankEnabled = Bool("rag.rerank_enabled") ?? s.RerankEnabled,
            RerankProvider = Str("rag.rerank_provider") is { Length: > 0 } p ? p : s.RerankProvider,
            RerankTopN = Math.Clamp(Int("rag.rerank_top_n") ?? s.RerankTopN, 1, 50),
            Temperature = Num("rag.temperature") is { } t ? (float)Math.Clamp(t, 0, 2) : s.Temperature,
            MaxOutputTokens = Int("rag.max_output_tokens") is { } m ? Math.Clamp(m, 128, 32768) : s.MaxOutputTokens
        };
    }

    public async Task<SettingsResponse> GetAllAsync(CancellationToken ct = default)
    {
        var overrides = await ReadOverridesAsync(ct);
        var defaults = BuildDefaults();
        var effective = Apply(defaults, overrides);

        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.Key.StartsWith(KeyPrefix))
            .ToDictionaryAsync(s => s.Key, ct);

        var items = RagDefaults.Descriptors.Select(d =>
        {
            var hasOverride = overrides.TryGetValue(d.Key, out var value);
            if (!hasOverride)
                value = DefaultValue(d.Key, defaults);
            return new SettingItemDto(
                d.Key, d.Label, d.Group, d.Type, d.Description,
                value, hasOverride ? "database" : "default",
                hasOverride && rows.TryGetValue(d.Key, out var row) ? row.UpdatedAt : null);
        }).ToList();

        return new SettingsResponse(items, effective);
    }

    /// <summary>默认值的 JSON 表示（前端表单用）</summary>
    public static JsonElement DefaultValue(string key, RagSettings s)
    {
        object? value = key switch
        {
            "rag.system_prompt" => s.SystemPrompt,
            "rag.top_k" => s.TopK,
            "rag.candidate_top_k" => s.CandidateTopK,
            "rag.hybrid_enabled" => s.HybridEnabled,
            "rag.metadata_filter_enabled" => s.MetadataFilterEnabled,
            "rag.rerank_enabled" => s.RerankEnabled,
            "rag.rerank_provider" => s.RerankProvider,
            "rag.rerank_top_n" => s.RerankTopN,
            "rag.temperature" => s.Temperature,
            "rag.max_output_tokens" => s.MaxOutputTokens,
            _ => null
        };
        return JsonSerializer.SerializeToElement(value);
    }

    public async Task<RagSettings> UpdateAsync(
        Dictionary<string, JsonElement> updates, CancellationToken ct = default)
    {
        if (updates.Count == 0)
            throw new BusinessRuleException("没有需要更新的配置项");

        foreach (var (key, value) in updates)
        {
            var descriptor = RagDefaults.Descriptors.FirstOrDefault(d =>
                string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null)
                throw new BusinessRuleException($"未知配置项：{key}");
            ValidateValue(descriptor, value);
        }

        foreach (var (key, value) in updates)
        {
            var canonicalKey = RagDefaults.Descriptors
                .First(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)).Key;
            var descriptor = RagDefaults.Descriptors.First(d => d.Key == canonicalKey);
            var json = JsonSerializer.Serialize(value);

            var existing = await db.Settings.FindAsync(new object?[] { canonicalKey }, ct);
            if (existing is null)
            {
                db.Settings.Add(new SysSetting
                {
                    Key = canonicalKey,
                    ValueJson = json,
                    Description = descriptor.Label,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                existing.ValueJson = json;
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
        Invalidate();
        return await LoadAsync(ct);
    }

    public async Task<RagSettings> ResetAsync(CancellationToken ct = default)
    {
        var rows = await db.Settings.Where(s => s.Key.StartsWith(KeyPrefix)).ToListAsync(ct);
        if (rows.Count > 0)
        {
            db.Settings.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
        }
        Invalidate();
        return await GetAsync(ct);
    }

    private void Invalidate() => cache.Invalidate();

    private static void ValidateValue(RagSettingDescriptor d, JsonElement v)
    {
        // 数值项允许显式传 null = 清除该覆盖、恢复默认（前端"留空"）
        var isNull = v.ValueKind == JsonValueKind.Null;
        var ok = isNull
            ? d.Type is "int" or "number"
            : d.Type switch
            {
                "string" => v.ValueKind == JsonValueKind.String,
                "enum" => v.ValueKind == JsonValueKind.String,
                "bool" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "int" => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _),
                "number" => v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out _),
                _ => false
            };
        if (!ok)
            throw new BusinessRuleException($"配置项 {d.Label} 的值类型不正确（要求 {d.Type}）");
        if (isNull) return;

        switch (d.Key)
        {
            case "rag.system_prompt" when v.GetString() is { Length: > 8000 } or { Length: 0 }:
                throw new BusinessRuleException("系统提示词长度须在 1–8000 字符之间");
            case "rag.rerank_provider" when v.GetString() is not ("Heuristic" or "Api"):
                throw new BusinessRuleException("重排实现仅支持 Heuristic 或 Api");
            case "rag.top_k" when v.GetInt32() is < 1 or > 30:
                throw new BusinessRuleException("top_k 须在 1–30 之间");
            case "rag.candidate_top_k" when v.GetInt32() is < 1 or > 100:
                throw new BusinessRuleException("candidate_top_k 须在 1–100 之间");
            case "rag.rerank_top_n" when v.GetInt32() is < 1 or > 50:
                throw new BusinessRuleException("rerank_top_n 须在 1–50 之间");
            case "rag.temperature" when v.GetDouble() is < 0 or > 2:
                throw new BusinessRuleException("temperature 须在 0–2 之间");
            case "rag.max_output_tokens" when v.GetInt32() is < 128 or > 32768:
                throw new BusinessRuleException("max_output_tokens 须在 128–32768 之间");
        }
    }
}
