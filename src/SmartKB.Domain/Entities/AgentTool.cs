namespace SmartKB.Domain.Entities;

/// <summary>
/// 配置化 Agent 工具（P2）：管理后台登记一个 HTTP 接口，问答时由 LLM 按需调用。
/// 安全边界：密钥不入库，只记录环境变量名；参数值只能进 path/query/body，host 固定不可被参数改写。
/// </summary>
public class AgentTool
{
    public long Id { get; set; }

    /// <summary>函数名，须匹配 ^[a-zA-Z][a-zA-Z0-9_]{1,63}$（OpenAI function name 规范）</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>给 LLM 看的工具说明，决定模型何时调用</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>GET / POST</summary>
    public string HttpMethod { get; set; } = "GET";

    /// <summary>绝对 URL 模板，可含 {param} 占位符，如 https://erp/api/device/{device_no}?year={year}</summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>POST JSON 体模板，占位符写 {param}（不带引号），由服务端做 JSON 字面量替换</summary>
    public string? BodyTemplate { get; set; }

    /// <summary>参数 DSL（JSON 数组）：name/type/description/required/location</summary>
    public string ParametersJson { get; set; } = "[]";

    /// <summary>
    /// 允许调用该工具的知识库 ID（JSON 数组，如 "[1,3]"）；空数组 = 不限制。
    /// 问答时与请求的知识库范围求交，无交集则本次不注入该工具。
    /// </summary>
    public string AllowedKbIdsJson { get; set; } = "[]";

    /// <summary>鉴权头名，如 Authorization / X-Api-Key</summary>
    public string? AuthHeaderName { get; set; }

    /// <summary>密钥所在的环境变量名（只存名字，值运行时读取）</summary>
    public string? AuthEnvVar { get; set; }

    /// <summary>鉴权值前缀，如 "Bearer "</summary>
    public string? AuthValuePrefix { get; set; }

    public int TimeoutSeconds { get; set; } = 8;

    public int MaxResponseChars { get; set; } = 4000;

    public bool Enabled { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
