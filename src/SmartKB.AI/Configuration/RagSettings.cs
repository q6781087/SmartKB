using System.Text.Json.Serialization;

namespace SmartKB.AI.Configuration;

/// <summary>
/// RAG / Agent 运行时参数。默认值来自 appsettings 的 RAG 节，
/// 管理后台写入 sys_settings 后覆盖（见 Application 层 SettingsService）。
/// 注意：这是"问答行为"配置，不含连接串/密钥——密钥只允许环境变量注入。
/// </summary>
public record RagSettings
{
    /// <summary>系统提示词</summary>
    public string SystemPrompt { get; init; } = RagDefaults.SystemPrompt;

    /// <summary>最终进入上下文的分块数</summary>
    public int TopK { get; init; } = 8;

    /// <summary>每路召回的候选数（融合/重排前的池子）</summary>
    public int CandidateTopK { get; init; } = 24;

    /// <summary>混合检索（向量 + PostgreSQL 全文/精确词）开关，默认关闭可随时回退纯向量</summary>
    public bool HybridEnabled { get; init; }

    /// <summary>元数据过滤开关（客户在 FieldSchema 里标 filterable 的字段）</summary>
    public bool MetadataFilterEnabled { get; init; } = true;

    /// <summary>重排开关，默认关闭</summary>
    public bool RerankEnabled { get; init; }

    /// <summary>Heuristic（零依赖）/ Api（外部 rerank 模型）</summary>
    public string RerankProvider { get; init; } = "Heuristic";

    /// <summary>重排候选池大小</summary>
    public int RerankTopN { get; init; } = 12;

    /// <summary>生成温度（null = 用模型默认）</summary>
    public float? Temperature { get; init; } = 0.2f;

    /// <summary>单次回答最大输出 token（null = 用模型默认）</summary>
    public int? MaxOutputTokens { get; init; } = 2048;

    /// <summary>RRF 融合常数（越小越看重头部排名，常用 60）</summary>
    public int RrfK { get; init; } = 60;

    /// <summary>配置版本号，用于缓存失效与后台展示</summary>
    [JsonIgnore]
    public int Version { get; init; }
}

/// <summary>默认值常量（appsettings 缺省、sys_settings 无记录时的兜底）</summary>
public static class RagDefaults
{
    public const string SystemPrompt = """
        你是企业知识库智能问答助手。严格遵守以下规则：

        1. 对话中已注入 [知识库预检索结果]，若其内容足以回答，直接作答，不要重复调用 search_knowledge。
        2. 预检索结果不足以回答时，再调用 search_knowledge 工具补充检索；仍检索不到就明确说"知识库中未找到相关内容"，禁止凭空编造企业内部信息。
        3. 回答只依据检索到的内容（预检索结果或工具返回）。
        4. 数量、金额、日期等精确数据类问题，优先调用 query_ledger 查台账结构化数据。
        5. 回答末尾用【来源】列出引用的文档名与片段位置，例如：来源：《采购合同》第3段。
        6. 用简体中文回答，简洁专业；列表数据用 Markdown 表格。
        """;

    public static readonly RagSettings Instance = new();

    /// <summary>后台可编辑的配置项元数据（前端据此渲染表单，新增项只改这里）</summary>
    public static readonly IReadOnlyList<RagSettingDescriptor> Descriptors =
    [
        new("rag.system_prompt", "系统提示词", "prompt", "string", "Agent 的行为约束；修改后立即对新问答生效"),
        new("rag.top_k", "进入上下文的分块数", "retrieval", "int", "最终送给模型的文档片段数量，建议 5–12"),
        new("rag.candidate_top_k", "每路召回候选数", "retrieval", "int", "融合/重排前的候选池，需 ≥ top_k"),
        new("rag.hybrid_enabled", "启用混合检索", "retrieval", "bool", "向量 + 全文/精确词双路 RRF 融合；关闭则纯向量"),
        new("rag.metadata_filter_enabled", "启用元数据过滤", "retrieval", "bool", "按知识库自定义字段（filterable）过滤文档"),
        new("rag.rerank_enabled", "启用重排", "rerank", "bool", "融合后二次排序，提升头部命中精度"),
        new("rag.rerank_provider", "重排实现", "rerank", "enum", "Heuristic（零依赖）/ Api（外部 rerank 模型）"),
        new("rag.rerank_top_n", "重排候选数", "rerank", "int", "参与重排的候选片段数"),
        new("rag.temperature", "生成温度", "generation", "number", "0–2，越低越稳定；清空后恢复默认值"),
        new("rag.max_output_tokens", "最大输出 token", "generation", "int", "单次回答上限；清空后恢复默认值"),
    ];
}

/// <summary>后台配置项描述</summary>
public record RagSettingDescriptor(string Key, string Label, string Group, string Type, string Description);

/// <summary>
/// 运行时参数提供者。定义在 AI 层（依赖倒置），由 Application 层实现，
/// 避免 AI 层反向引用业务层。
/// </summary>
public interface IRagSettingsProvider
{
    Task<RagSettings> GetAsync(CancellationToken ct = default);
}

/// <summary>未接配置中心时的静态实现（单测 / 独立运行场景）</summary>
public class StaticRagSettingsProvider(RagSettings settings) : IRagSettingsProvider
{
    public static StaticRagSettingsProvider Default { get; } = new(RagDefaults.Instance);

    public Task<RagSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(settings);
}
