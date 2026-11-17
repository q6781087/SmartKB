using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartKB.AI.Configuration;
using SmartKB.Application;
using SmartKB.Application.Services;

namespace SmartKB.Tests;

/// <summary>
/// P1 配置中心测试：覆盖值解析/回退、白名单与范围校验、缓存失效、sys_settings 读写闭环。
/// 用 InMemory DbContext，不触碰真实 PostgreSQL。
/// </summary>
public class RagSettingsTests
{
    private static Dictionary<string, JsonElement> O(params (string Key, string Json)[] pairs)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, json) in pairs)
        {
            using var doc = JsonDocument.Parse(json);
            result[key] = doc.RootElement.Clone();
        }
        return result;
    }

    private static SettingsService NewService() =>
        new(TestDb.Create(), new ConfigurationBuilder().Build(), new SettingsCache());

    [Fact]
    public void 覆盖值_按类型生效_未覆盖项保持默认()
    {
        var s = SettingsService.Apply(RagDefaults.Instance, O(
            ("rag.top_k", "12"),
            ("rag.hybrid_enabled", "true"),
            ("rag.rerank_provider", "\"Api\""),
            ("rag.system_prompt", "\"你是合同助手\""),
            ("rag.temperature", "0.7")));

        Assert.Equal(12, s.TopK);
        Assert.True(s.HybridEnabled);
        Assert.Equal("Api", s.RerankProvider);
        Assert.Equal("你是合同助手", s.SystemPrompt);
        Assert.Equal(0.7f, s.Temperature);
        Assert.Equal(RagDefaults.Instance.MaxOutputTokens, s.MaxOutputTokens); // 未覆盖
        Assert.False(s.RerankEnabled); // 默认关闭
    }

    [Fact]
    public void 类型不符与未知键_静默回退默认_不让坏配置拖垮问答()
    {
        var s = SettingsService.Apply(RagDefaults.Instance, O(
            ("rag.top_k", "\"abc\""),
            ("rag.hybrid_enabled", "1"),        // bool 只认 true/false
            ("rag.temperature", "\"hot\""),
            ("unknown.key", "999")));

        Assert.Equal(RagDefaults.Instance.TopK, s.TopK);
        Assert.Equal(RagDefaults.Instance.HybridEnabled, s.HybridEnabled);
        Assert.Equal(RagDefaults.Instance.Temperature, s.Temperature);
    }

    [Fact]
    public void 越界数值_被夹到合法区间()
    {
        var s = SettingsService.Apply(RagDefaults.Instance, O(
            ("rag.top_k", "999"),
            ("rag.candidate_top_k", "-5"),
            ("rag.rerank_top_n", "999"),
            ("rag.temperature", "9"),
            ("rag.max_output_tokens", "1")));

        Assert.Equal(30, s.TopK);
        Assert.Equal(1, s.CandidateTopK);
        Assert.Equal(50, s.RerankTopN);
        Assert.Equal(2f, s.Temperature);
        Assert.Equal(128, s.MaxOutputTokens);
    }

    [Fact]
    public void null数值_清除覆盖恢复默认()
    {
        var s = SettingsService.Apply(RagDefaults.Instance, O(
            ("rag.temperature", "null"),
            ("rag.max_output_tokens", "null")));

        Assert.Equal(RagDefaults.Instance.Temperature, s.Temperature);
        Assert.Equal(RagDefaults.Instance.MaxOutputTokens, s.MaxOutputTokens);
    }

    [Fact]
    public void 描述符_键唯一且全在白名单前缀内()
    {
        var keys = RagDefaults.Descriptors.Select(d => d.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("rag.", k));
        Assert.Contains(keys, k => k == "rag.system_prompt");
        Assert.Contains(keys, k => k == "rag.hybrid_enabled");
        Assert.All(RagDefaults.Descriptors, d =>
            Assert.Contains(d.Type, new[] { "string", "bool", "int", "number", "enum" }));
    }

    [Fact]
    public void 默认值JSON_与生效值一致()
    {
        Assert.Equal(8, SettingsService.DefaultValue("rag.top_k", RagDefaults.Instance).GetInt32());
        Assert.False(SettingsService.DefaultValue("rag.hybrid_enabled", RagDefaults.Instance).GetBoolean());
    }

    [Fact]
    public void 缓存_Set后命中_Invalidate后失效且版本递增()
    {
        var cache = new SettingsCache();
        var v0 = cache.Version;

        var s = cache.Set(RagDefaults.Instance);
        Assert.Same(s, cache.Get());
        Assert.Equal(v0, s.Version);

        cache.Invalidate();
        Assert.Null(cache.Get());
        Assert.Equal(v0 + 1, cache.Version);
        Assert.Equal(v0 + 1, cache.Set(RagDefaults.Instance).Version);
    }

    [Fact]
    public async Task 更新_未知键与非法值_抛业务异常且不落库()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("evil.key", "1"))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("rag.top_k", "\"abc\""))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("rag.top_k", "999"))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("rag.rerank_provider", "\"Unknown\""))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("rag.temperature", "3.5"))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(new Dictionary<string, JsonElement>()));

        var all = await svc.GetAllAsync();
        Assert.Equal(RagDefaults.Instance.TopK, all.Effective.TopK);
        Assert.All(all.Items, i => Assert.Equal("default", i.Source));
    }

    [Fact]
    public async Task 保存后_生效值立即变化且标记为数据库覆盖()
    {
        var svc = NewService();

        var effective = await svc.UpdateAsync(O(
            ("rag.top_k", "12"),
            ("rag.system_prompt", "\"只依据合同回答\""),
            ("rag.hybrid_enabled", "true")));

        Assert.Equal(12, effective.TopK);
        Assert.Equal("只依据合同回答", effective.SystemPrompt);
        Assert.True(effective.HybridEnabled);

        var all = await svc.GetAllAsync();
        Assert.Equal(12, all.Effective.TopK);
        Assert.Equal("database", all.Items.Single(i => i.Key == "rag.top_k").Source);
        Assert.Equal("default", all.Items.Single(i => i.Key == "rag.rerank_enabled").Source);
        Assert.NotNull(all.Items.Single(i => i.Key == "rag.top_k").UpdatedAt);
    }

    [Fact]
    public async Task null数值可保存_生效值回退默认()
    {
        var svc = NewService();
        await svc.UpdateAsync(O(("rag.temperature", "1.5")));
        Assert.Equal(1.5f, (await svc.GetAsync()).Temperature);

        var effective = await svc.UpdateAsync(O(("rag.temperature", "null")));
        Assert.Equal(RagDefaults.Instance.Temperature, effective.Temperature);
    }

    [Fact]
    public async Task 重置_删除全部覆盖并回到默认()
    {
        var svc = NewService();
        await svc.UpdateAsync(O(("rag.top_k", "20"), ("rag.rerank_enabled", "true")));
        Assert.Equal(20, (await svc.GetAsync()).TopK);

        var effective = await svc.ResetAsync();
        Assert.Equal(RagDefaults.Instance.TopK, effective.TopK);
        Assert.False(effective.RerankEnabled);

        var all = await svc.GetAllAsync();
        Assert.All(all.Items, i => Assert.Equal("default", i.Source));
    }
}
