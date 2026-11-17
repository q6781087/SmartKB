using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SmartKB.AI.Chat;
using SmartKB.AI.Configuration;
using SmartKB.AI.Embedding;
using SmartKB.Application;
using SmartKB.Application.Services;

namespace SmartKB.Tests;

/// <summary>
/// 模型运行时配置与切换测试：Merge 合并语义、覆盖应用与坏配置回退、
/// sys_settings 的 llm.* 键读写/校验/重置闭环。用 InMemory DbContext。
/// </summary>
public class LlmSwitchingTests
{
    private static LlmOptions Baseline() => new()
    {
        Mode = "Agnes",
        Agnes = new AgnesLlmOptions
        {
            BaseUrl = "https://apihub.agnes-ai.com/v1",
            ChatModel = "agnes-3.0-flash",
            ApiKey = "sk-baseline"
        },
        Cloud = new CloudLlmOptions
        {
            BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
            ChatModel = "glm-4.7",
            ApiKey = "cloud-baseline"
        },
        Local = new LocalLlmOptions { BaseUrl = "http://localhost:11434", ChatModel = "qwen3:14b" }
    };

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

    // ---------- Merge：运行时设置合并进静态配置 ----------

    [Fact]
    public void 空运行时设置_合并结果与静态配置一致()
    {
        var merged = LlmDefaults.Merge(Baseline(), LlmRuntimeSettings.Empty);

        Assert.Equal("Agnes", merged.Mode);
        Assert.Equal("agnes-3.0-flash", merged.Agnes.ChatModel);
        Assert.Equal("sk-baseline", merged.Agnes.ApiKey);
        Assert.Equal("glm-4.7", merged.Cloud.ChatModel);
        Assert.Equal("cloud-baseline", merged.Cloud.ApiKey);
        Assert.Equal("qwen3:14b", merged.Local.ChatModel);
    }

    [Fact]
    public void 运行时覆盖_模式模型端点生效_ApiKey仍来自静态配置()
    {
        var merged = LlmDefaults.Merge(Baseline(), new LlmRuntimeSettings
        {
            Mode = "Cloud",
            CloudChatModel = "glm-4.6",
            CloudBaseUrl = "https://proxy.example.com/v4"
        });

        Assert.Equal("Cloud", merged.Mode);
        Assert.Equal("glm-4.6", merged.Cloud.ChatModel);
        Assert.Equal("https://proxy.example.com/v4", merged.Cloud.BaseUrl);
        Assert.Equal("cloud-baseline", merged.Cloud.ApiKey); // 密钥绝不进配置中心
        Assert.Equal("agnes-3.0-flash", merged.Agnes.ChatModel); // 未覆盖的模式不受影响
    }

    [Fact]
    public void 非法模式_回退部署配置_不静默落到Cloud()
    {
        var merged = LlmDefaults.Merge(Baseline(), new LlmRuntimeSettings { Mode = "GPT-5" });

        Assert.Equal("Agnes", merged.Mode);
        Assert.Equal("agnes-3.0-flash", merged.Agnes.ChatModel);
    }

    // ---------- Apply：sys_settings 覆盖应用（纯函数） ----------

    [Fact]
    public void 覆盖应用_值生效_留空回退默认_首尾空白剔除()
    {
        var defaults = LlmDefaults.FromOptions(Baseline());
        var s = LlmDefaults.Apply(defaults, O(
            ("llm.mode", "\"Local\""),
            ("llm.local.chat_model", "\"  qwen3:32b  \""),
            ("llm.local.base_url", "\"\""))); // 空 = 未覆盖

        Assert.Equal("Local", s.Mode);
        Assert.Equal("qwen3:32b", s.LocalChatModel);
        Assert.Equal("http://localhost:11434", s.LocalBaseUrl);
    }

    [Fact]
    public void 覆盖应用_坏类型与非法模式_静默回退不拖垮问答()
    {
        var defaults = LlmDefaults.FromOptions(Baseline());
        var s = LlmDefaults.Apply(defaults, O(
            ("llm.mode", "123"),
            ("llm.cloud.chat_model", "null"),
            ("llm.mode2", "\"Cloud\""))); // 未知键被上层 KnownKeys 过滤，这里也不生效

        Assert.Equal("Agnes", s.Mode);
        Assert.Equal(defaults.CloudChatModel, s.CloudChatModel);
    }

    [Fact]
    public void 解析当前模型名_跟随运行时模式()
    {
        var baseline = Baseline();
        Assert.Equal("agnes-3.0-flash", LlmDefaults.ResolveChatModel(baseline, LlmRuntimeSettings.Empty));
        Assert.Equal("glm-4.7", LlmDefaults.ResolveChatModel(baseline, new LlmRuntimeSettings { Mode = "Cloud" }));
        Assert.Equal("qwen3:14b", LlmDefaults.ResolveChatModel(baseline, new LlmRuntimeSettings { Mode = "Local" }));
        Assert.Equal("glm-4.6",
            LlmDefaults.ResolveChatModel(baseline, new LlmRuntimeSettings { Mode = "Cloud", CloudChatModel = "glm-4.6" }));
    }

    // ---------- 配置项元数据 ----------

    [Fact]
    public void 描述符_键唯一_全在llm前缀内_枚举模式在允许集内()
    {
        var keys = LlmDefaults.Descriptors.Select(d => d.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, k => Assert.StartsWith("llm.", k));
        Assert.Contains("llm.mode", keys);
        Assert.Equal(7, keys.Count);
    }

    // ---------- sys_settings 读写闭环 ----------

    [Fact]
    public async Task 更新模型配置_保存即生效_标记为数据库覆盖()
    {
        var svc = NewService();

        await svc.UpdateAsync(O(
            ("llm.mode", "\"Cloud\""),
            ("llm.cloud.chat_model", "\"glm-4.6\"")));

        var llm = await svc.GetLlmAsync();
        Assert.Equal("Cloud", llm.Mode);
        Assert.Equal("glm-4.6", llm.CloudChatModel);

        var all = await svc.GetAllAsync();
        Assert.Equal("Cloud", all.Llm.Mode);
        Assert.Equal("database", all.Items.Single(i => i.Key == "llm.mode").Source);
        Assert.Equal("default", all.Items.Single(i => i.Key == "llm.agnes.base_url").Source);
        Assert.NotNull(all.Items.Single(i => i.Key == "llm.cloud.chat_model").UpdatedAt);
    }

    [Fact]
    public async Task GetLlmAsync_未覆盖时返回部署默认值()
    {
        var svc = NewService();
        var llm = await svc.GetLlmAsync();

        Assert.Equal("Agnes", llm.Mode);
        Assert.Equal("agnes-3.0-flash", llm.AgnesChatModel);
    }

    [Fact]
    public async Task 更新_非法模型模式与非法URL_抛业务异常且不落库()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("llm.mode", "\"GPT-5\""))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("llm.cloud.base_url", "\"ftp://x.example.com\""))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("llm.cloud.base_url", "\"   \""))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("llm.local.chat_model", "\"\""))));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(O(("llm.cloud.chat_model", "123"))));

        var all = await svc.GetAllAsync();
        Assert.All(all.Items, i => Assert.Equal("default", i.Source));
    }

    [Fact]
    public async Task 重置_删除llm覆盖_回到部署默认()
    {
        var svc = NewService();
        await svc.UpdateAsync(O(("llm.mode", "\"Local\""), ("llm.local.chat_model", "\"qwen3:32b\"")));
        Assert.Equal("Local", (await svc.GetLlmAsync()).Mode);

        await svc.ResetAsync();

        var llm = await svc.GetLlmAsync();
        Assert.Equal("Agnes", llm.Mode);
        Assert.Equal("qwen3:14b", llm.LocalChatModel);

        var all = await svc.GetAllAsync();
        Assert.All(all.Items, i => Assert.Equal("default", i.Source));
    }

    [Fact]
    public async Task 模型与RAG配置_保存互不影响_重置一起清空()
    {
        var svc = NewService();
        await svc.UpdateAsync(O(("llm.mode", "\"Cloud\"")));
        await svc.UpdateAsync(O(("rag.top_k", "12")));

        var all = await svc.GetAllAsync();
        Assert.Equal("Cloud", all.Llm.Mode);
        Assert.Equal(12, all.Effective.TopK);

        await svc.ResetAsync();
        all = await svc.GetAllAsync();
        Assert.Equal("Agnes", all.Llm.Mode);
        Assert.Equal(RagDefaults.Instance.TopK, all.Effective.TopK);
    }

    // ---------- 缓存 ----------

    [Fact]
    public void LLM缓存_Set后命中_Invalidate后失效()
    {
        var cache = new SettingsCache();
        Assert.Null(cache.GetLlm());

        var s = cache.SetLlm(new LlmRuntimeSettings { Mode = "Cloud" });
        Assert.Same(s, cache.GetLlm());

        cache.Invalidate();
        Assert.Null(cache.GetLlm());
        Assert.Null(cache.Get()); // RAG 与 LLM 缓存一起失效
    }
}
