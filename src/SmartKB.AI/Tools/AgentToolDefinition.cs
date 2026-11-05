using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartKB.AI.Tools;

/// <summary>
/// 工具定义（AI 层纯数据，不依赖 EF 实体）。Application 层负责从 sys_agent_tools 映射，
/// ChatOrchestrator 只认这个类型——AI 层不感知后台管理接口。
/// </summary>
public sealed record AgentToolDefinition(
    long Id,
    string Name,
    string DisplayName,
    string Description,
    string HttpMethod,
    string UrlTemplate,
    string? BodyTemplate,
    IReadOnlyList<ToolParameter> Parameters,
    string? AuthHeaderName = null,
    string? AuthEnvVar = null,
    string? AuthValuePrefix = null,
    int TimeoutSeconds = 8,
    int MaxResponseChars = 4000,
    IReadOnlyList<string>? AllowedHosts = null,
    IReadOnlyList<long>? AllowedKbIds = null);

/// <summary>参数定义；location 决定占位符被替换到 path/query 还是 POST body</summary>
public sealed record ToolParameter(
    string Name,
    string Type,
    string Description,
    bool Required,
    string Location);

/// <summary>
/// 配置校验与 JSON Schema 生成。所有规则都是"保存时拦住 + 调用时再校验一次"，
/// 因为 sys_agent_tools 可能被直接改库绕过后台表单。
/// </summary>
public static class AgentToolValidator
{
    private static readonly Regex NameRegex = new(@"^[a-zA-Z][a-zA-Z0-9_]{1,63}$", RegexOptions.Compiled);
    private static readonly Regex ParamNameRegex = new(@"^[a-zA-Z][a-zA-Z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex EnvVarRegex = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex HeaderRegex = new(@"^[A-Za-z0-9-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"\{([a-zA-Z][a-zA-Z0-9_]{0,63})\}", RegexOptions.Compiled);

    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        { "string", "number", "integer", "boolean" };
    private static readonly HashSet<string> Locations = new(StringComparer.OrdinalIgnoreCase)
        { "path", "query", "body" };
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Cookie" };

    public const int MaxParameters = 12;

    /// <summary>知识库范围白名单上限（控制 prompt 体积）</summary>
    public const int MaxAllowedKbIds = 50;

    /// <summary>
    /// 解析工具的知识库范围 DSL（JSON 数组）；空/空白 = 不限制。
    /// 元素必须是非负整数，直接改库的坏配置也在这里拦住。
    /// </summary>
    public static IReadOnlyList<long> ParseAllowedKbIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("知识库范围必须是 JSON 数字数组");
        if (doc.RootElement.GetArrayLength() > MaxAllowedKbIds)
            throw new ArgumentException($"知识库范围最多 {MaxAllowedKbIds} 个");

        var ids = new List<long>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind is not (JsonValueKind.Number))
                throw new ArgumentException("知识库范围必须是 JSON 数字数组");
            var id = item.TryGetInt64(out var v) ? v
                : throw new ArgumentException("知识库 ID 必须是整数");
            if (id <= 0)
                throw new ArgumentException("知识库 ID 必须为正整数");
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>把后台提交的参数 DSL 解析为强类型列表（坏 JSON 抛可读异常）</summary>
    public static IReadOnlyList<ToolParameter> ParseParameters(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("参数定义必须是 JSON 数组");
        if (doc.RootElement.GetArrayLength() > MaxParameters)
            throw new ArgumentException($"参数最多 {MaxParameters} 个");

        var list = new List<ToolParameter>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            static string? S(JsonElement e, string k) =>
                e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static bool B(JsonElement e, string k) =>
                e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

            var name = S(item, "name") ?? throw new ArgumentException("参数缺少 name");
            var type = S(item, "type") ?? "string";
            var location = S(item, "location") ?? "query";
            list.Add(new ToolParameter(name, type, S(item, "description") ?? "", B(item, "required"), location));
        }
        return list;
    }

    /// <summary>完整校验；配置类错误一律以中文 ArgumentException 抛出，不暴露堆栈给 LLM</summary>
    public static void Validate(AgentToolDefinition d)
    {
        if (!NameRegex.IsMatch(d.Name))
            throw new ArgumentException("工具名须以字母开头，仅含字母/数字/下划线，长度 2–64");
        if (string.IsNullOrWhiteSpace(d.Description) || d.Description.Length > 1000)
            throw new ArgumentException("工具描述须在 1–1000 字之间（模型靠它判断何时调用）");
        if (d.HttpMethod is not ("GET" or "POST"))
            throw new ArgumentException("HTTP 方法仅支持 GET / POST");
        if (d.Parameters.Count > MaxParameters)
            throw new ArgumentException($"参数最多 {MaxParameters} 个");

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in d.Parameters)
        {
            if (!ParamNameRegex.IsMatch(p.Name))
                throw new ArgumentException($"参数名 {p.Name} 不合法（字母开头，仅含字母/数字/下划线）");
            if (!declared.Add(p.Name))
                throw new ArgumentException($"参数名 {p.Name} 重复");
            if (!Types.Contains(p.Type))
                throw new ArgumentException($"参数 {p.Name} 的 type 仅支持 string/number/integer/boolean");
            if (!Locations.Contains(p.Location))
                throw new ArgumentException($"参数 {p.Name} 的 location 仅支持 path/query/body");
            if (p.Location.Equals("body", StringComparison.OrdinalIgnoreCase) && d.HttpMethod != "POST")
                throw new ArgumentException($"参数 {p.Name} 位于 body，HTTP 方法必须是 POST");
            if (p.Description.Length > 300)
                throw new ArgumentException($"参数 {p.Name} 的描述过长");
        }

        // ---- URL：必须绝对地址，scheme/host 固定，占位符只能来自已声明参数 ----
        if (!Uri.TryCreate(d.UrlTemplate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("URL 模板必须是 http/https 绝对地址");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("URL 模板不允许包含用户名密码或 fragment");
        if (string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("URL 模板缺少主机名");
        if (d.AllowedHosts is { Count: > 0 } hosts &&
            !hosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(h, uri.Authority, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"目标主机 {uri.Host} 不在 Tools:AllowedHosts 白名单内");

        foreach (Match m in PlaceholderRegex.Matches(d.UrlTemplate))
            if (!declared.Contains(m.Groups[1].Value))
                throw new ArgumentException($"URL 中的占位符 {{{m.Groups[1].Value}}} 未在参数中声明");

        if (d.BodyTemplate is { Length: > 0 } body)
        {
            if (d.HttpMethod != "POST")
                throw new ArgumentException("只有 POST 工具才能配置请求体模板");
            if (body.Length > 8000)
                throw new ArgumentException("请求体模板过长（上限 8000 字符）");
            foreach (Match m in PlaceholderRegex.Matches(body))
                if (!declared.Contains(m.Groups[1].Value))
                    throw new ArgumentException($"请求体中的占位符 {{{m.Groups[1].Value}}} 未在参数中声明");
        }

        if (!string.IsNullOrEmpty(d.AuthHeaderName))
        {
            if (!HeaderRegex.IsMatch(d.AuthHeaderName) || ForbiddenHeaders.Contains(d.AuthHeaderName))
                throw new ArgumentException($"鉴权头 {d.AuthHeaderName} 不合法或属于禁止改写的请求头");
            if (string.IsNullOrEmpty(d.AuthEnvVar) || !EnvVarRegex.IsMatch(d.AuthEnvVar))
                throw new ArgumentException("配置鉴权头时必须给出合法的密钥环境变量名（只存变量名，密钥不入库）");
        }
        else if (!string.IsNullOrEmpty(d.AuthEnvVar))
        {
            // 只填变量名不填头名会静默不带鉴权，管理员容易误判为"已配置"
            throw new ArgumentException("已填写密钥环境变量名，必须同时指定鉴权请求头名");
        }
        if (d.AuthValuePrefix is { Length: > 64 })
            throw new ArgumentException("鉴权值前缀过长");
        if (d.AllowedKbIds is { Count: > MaxAllowedKbIds })
            throw new ArgumentException($"知识库范围最多 {MaxAllowedKbIds} 个");
        if (d.TimeoutSeconds is < 1 or > 30)
            throw new ArgumentException("超时须在 1–30 秒之间");
        if (d.MaxResponseChars is < 200 or > 20000)
            throw new ArgumentException("响应上限须在 200–20000 字符之间");
    }

    /// <summary>生成给 LLM 的 function JSON Schema（不含任何密钥信息）</summary>
    public static JsonElement BuildJsonSchema(AgentToolDefinition d)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in d.Parameters)
        {
            properties[p.Name] = new Dictionary<string, object>
            {
                ["type"] = p.Type.ToLowerInvariant(),
                ["description"] = p.Description
            };
            if (p.Required) required.Add(p.Name);
        }
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>把参数值按声明类型转成字符串（LLM 传回来的 JsonElement / 原生值都兼容）</summary>
    public static string? Coerce(object? raw, string type)
    {
        if (raw is null) return null;
        if (raw is JsonElement je)
        {
            if (je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            raw = je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => je.GetRawText()
            };
        }
        var text = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return type.ToLowerInvariant() switch
        {
            "number" or "integer" when !decimal.TryParse(text, out _) => null,
            "boolean" when !bool.TryParse(text, out _) => null,
            _ => text
        };
    }
}
