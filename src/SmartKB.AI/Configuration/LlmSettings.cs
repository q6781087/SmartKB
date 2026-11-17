using System.Text.Json;
using System.Text.Json.Serialization;
using SmartKB.AI.Embedding;

namespace SmartKB.AI.Configuration;

/// <summary>
/// LLM 运行时配置（后台可改的部分）：模型模式与各模式的端点 / 对话模型名。
/// 空字符串 = 未覆盖，沿用部署时的静态配置（appsettings / 环境变量）。
/// 注意：只管对话模型——Embedding 由部署时的 Llm:Mode 决定且不参与运行时切换，
/// 因为换 Embedding 模型等于换向量空间，已有向量全部作废，必须是受控的重建操作。
/// ApiKey 永远只走环境变量，不进配置中心。
/// </summary>
public record LlmRuntimeSettings
{
    /// <summary>Agnes（默认）/ Cloud（智谱）/ Local（Ollama）；空 = 未覆盖</summary>
    public string Mode { get; init; } = "";

    public string AgnesBaseUrl { get; init; } = "";
    public string AgnesChatModel { get; init; } = "";
    public string CloudBaseUrl { get; init; } = "";
    public string CloudChatModel { get; init; } = "";
    public string LocalBaseUrl { get; init; } = "";
    public string LocalChatModel { get; init; } = "";

    /// <summary>配置版本号（缓存失效与后台展示）</summary>
    [JsonIgnore]
    public int Version { get; init; }

    public static readonly LlmRuntimeSettings Empty = new();

    public bool IsEmpty =>
        Mode.Length == 0 && AgnesBaseUrl.Length == 0 && AgnesChatModel.Length == 0 &&
        CloudBaseUrl.Length == 0 && CloudChatModel.Length == 0 &&
        LocalBaseUrl.Length == 0 && LocalChatModel.Length == 0;
}

/// <summary>模型配置项元数据与合并逻辑（后台可编辑项、默认值、sys_settings 覆盖应用）</summary>
public static class LlmDefaults
{
    public static readonly string[] AllowedModes = ["Agnes", "Cloud", "Local"];

    /// <summary>后台可编辑的配置项元数据（前端据此渲染表单）</summary>
    public static readonly IReadOnlyList<RagSettingDescriptor> Descriptors =
    [
        new("llm.mode", "模型模式", "llm", "enum", "对话模型走哪个服务；Embedding 不随此切换（由部署配置决定，换 Embedding 模型需重建全部向量）"),
        new("llm.agnes.base_url", "Agnes 端点", "llm", "string", "OpenAI 兼容 BaseUrl，如 https://apihub.agnes-ai.com/v1；留空沿用部署配置"),
        new("llm.agnes.chat_model", "Agnes 对话模型", "llm", "string", "如 agnes-3.0-flash；留空沿用部署配置"),
        new("llm.cloud.base_url", "智谱端点", "llm", "string", "如 https://open.bigmodel.cn/api/paas/v4；留空沿用部署配置"),
        new("llm.cloud.chat_model", "智谱对话模型", "llm", "string", "如 glm-4.7；留空沿用部署配置"),
        new("llm.local.base_url", "Ollama 端点", "llm", "string", "如 http://localhost:11434；留空沿用部署配置"),
        new("llm.local.chat_model", "Ollama 对话模型", "llm", "string", "如 qwen3:14b；留空沿用部署配置")
    ];

    /// <summary>静态配置作为默认基线（部署时的 appsettings / 环境变量）</summary>
    public static LlmRuntimeSettings FromOptions(LlmOptions o) => new()
    {
        Mode = o.Mode,
        AgnesBaseUrl = o.Agnes.BaseUrl,
        AgnesChatModel = o.Agnes.ChatModel,
        CloudBaseUrl = o.Cloud.BaseUrl,
        CloudChatModel = o.Cloud.ChatModel,
        LocalBaseUrl = o.Local.BaseUrl,
        LocalChatModel = o.Local.ChatModel
    };

    /// <summary>把 sys_settings 覆盖项应用到默认配置（纯函数，可单测）。
    /// 非法模式静默回退默认，坏配置不拖垮问答（与 RAG 配置同一原则）。</summary>
    public static LlmRuntimeSettings Apply(LlmRuntimeSettings defaults,
        IReadOnlyDictionary<string, JsonElement> overrides)
    {
        JsonElement? O(string key) => overrides.TryGetValue(key, out var v) ? v : null;
        string? Str(string key) => O(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

        string Pick(string key, string fallback)
        {
            var s = Str(key);
            return string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
        }

        var mode = Pick("llm.mode", defaults.Mode);
        if (!AllowedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            mode = defaults.Mode;

        return defaults with
        {
            Mode = mode,
            AgnesBaseUrl = Pick("llm.agnes.base_url", defaults.AgnesBaseUrl),
            AgnesChatModel = Pick("llm.agnes.chat_model", defaults.AgnesChatModel),
            CloudBaseUrl = Pick("llm.cloud.base_url", defaults.CloudBaseUrl),
            CloudChatModel = Pick("llm.cloud.chat_model", defaults.CloudChatModel),
            LocalBaseUrl = Pick("llm.local.base_url", defaults.LocalBaseUrl),
            LocalChatModel = Pick("llm.local.chat_model", defaults.LocalChatModel)
        };
    }

    /// <summary>把运行时设置合并进静态 LlmOptions：BaseUrl / 模型名 / 模式可被运行时覆盖，
    /// ApiKey 只来自静态配置（环境变量）。结果交给 ChatClientFactory 构建客户端。</summary>
    public static LlmOptions Merge(LlmOptions baseline, LlmRuntimeSettings s)
    {
        var mode = string.IsNullOrWhiteSpace(s.Mode) ? baseline.Mode : s.Mode;
        if (!AllowedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            mode = baseline.Mode; // 坏配置回退部署值，不静默落到 Cloud 分支

        var merged = new LlmOptions
        {
            Mode = mode,
            Agnes = new AgnesLlmOptions
            {
                ApiKey = baseline.Agnes.ApiKey,
                BaseUrl = Or(s.AgnesBaseUrl, baseline.Agnes.BaseUrl),
                ChatModel = Or(s.AgnesChatModel, baseline.Agnes.ChatModel)
            },
            Cloud = new CloudLlmOptions
            {
                ApiKey = baseline.Cloud.ApiKey,
                BaseUrl = Or(s.CloudBaseUrl, baseline.Cloud.BaseUrl),
                ChatModel = Or(s.CloudChatModel, baseline.Cloud.ChatModel),
                EmbeddingModel = baseline.Cloud.EmbeddingModel,
                EmbeddingDims = baseline.Cloud.EmbeddingDims
            },
            Local = new LocalLlmOptions
            {
                BaseUrl = Or(s.LocalBaseUrl, baseline.Local.BaseUrl),
                ChatModel = Or(s.LocalChatModel, baseline.Local.ChatModel),
                EmbeddingModel = baseline.Local.EmbeddingModel
            }
        };
        return merged;

        static string Or(string runtime, string fallback) =>
            string.IsNullOrWhiteSpace(runtime) ? fallback : runtime.Trim();
    }

    /// <summary>当前生效的对话模型名（观测记录用）</summary>
    public static string ResolveChatModel(LlmOptions baseline, LlmRuntimeSettings s)
    {
        var merged = Merge(baseline, s);
        return merged.Mode switch
        {
            var m when m.Equals("Local", StringComparison.OrdinalIgnoreCase) => merged.Local.ChatModel,
            var m when m.Equals("Agnes", StringComparison.OrdinalIgnoreCase) => merged.Agnes.ChatModel,
            _ => merged.Cloud.ChatModel
        };
    }
}

/// <summary>
/// 运行时模型配置提供者。定义在 AI 层（依赖倒置），由 Application 层实现（读 sys_settings）。
/// 与 IRagSettingsProvider 同款：实现是 Scoped（依赖 DbContext），单例消费者经 IServiceScopeFactory 解析。
/// </summary>
public interface ILlmSettingsProvider
{
    Task<LlmRuntimeSettings> GetAsync(CancellationToken ct = default);
}

/// <summary>未接配置中心时的静态实现（单测 / 独立运行场景）：全部沿用部署配置</summary>
public class StaticLlmSettingsProvider : ILlmSettingsProvider
{
    public static readonly StaticLlmSettingsProvider Empty = new();

    public Task<LlmRuntimeSettings> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(LlmRuntimeSettings.Empty);
}
