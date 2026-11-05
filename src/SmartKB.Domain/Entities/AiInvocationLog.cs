namespace SmartKB.Domain.Entities;

/// <summary>
/// AI 调用观测记录：每次问答一条（成功与失败都记）。
/// token 用量来自 LLM 响应 usage；TTFT=首字延迟；工具调用含检索与台账查询。
/// </summary>
public class AiInvocationLog
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    public ChatSession? Session { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>模型名（如 glm-4.7 / qwen3:14b）</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>问题摘要（前 200 字，便于排查定位）</summary>
    public string Question { get; set; } = string.Empty;

    /// <summary>提问 token 数（含系统提示 + 历史 + 工具定义）</summary>
    public int? InputTokens { get; set; }

    /// <summary>回答 token 数</summary>
    public int? OutputTokens { get; set; }

    /// <summary>总 token（部分模型不返回时为空，由前后端补算）</summary>
    public int? TotalTokens { get; set; }

    /// <summary>首字延迟 TTFT（毫秒）：用户体验核心指标</summary>
    public int? FirstTokenMs { get; set; }

    /// <summary>问答总耗时（毫秒，含工具调用与流式传输）</summary>
    public int TotalMs { get; set; }

    /// <summary>Agent 工具调用次数（检索 + 台账）</summary>
    public int ToolCalls { get; set; }

    /// <summary>向量检索命中的分块总数</summary>
    public int RetrievedChunks { get; set; }

    /// <summary>是否成功</summary>
    public bool Success { get; set; } = true;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
