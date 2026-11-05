using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SmartKB.AI.Tools;

/// <summary>工具调用结果：Success 供后台"试运行"显示，Output 是回给 LLM / 后台的文本</summary>
public record HttpToolResult(bool Success, string Output);

/// <summary>
/// 配置化 HTTP 工具执行器：把 LLM 给出的参数安全地填进管理员登记的 URL 模板并调用。
///
/// 安全边界（四条硬约束）：
/// 1. host / scheme / port 来自配置模板，参数值只替换 authority 之后的 path/query，且 Uri 编码；
///    拼完再断言最终 Uri 的 scheme/host/port 与模板一致，杜绝 LLM 把请求打到别的主机。
/// 2. 密钥只存环境变量名，值运行时读取，永不写入日志、异常或返回文本。
/// 3. 响应按字符数截断，只允许文本类 content-type；不跟随重定向（防白名单绕过）。
/// 4. 所有失败降级为可读文本返回给模型，问答主流程不中断。
/// </summary>
public interface IHttpToolExecutor
{
    /// <summary>问答链路用：失败也返回可读文本，让模型能解释并继续回答</summary>
    Task<string> InvokeAsync(
        AgentToolDefinition definition,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default);

    /// <summary>后台试运行用：带明确的成功标记</summary>
    Task<HttpToolResult> InvokeDetailedAsync(
        AgentToolDefinition definition,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default);
}

public class HttpToolExecutor(
    IHttpClientFactory httpFactory,
    ILogger<HttpToolExecutor> logger) : IHttpToolExecutor
{
    private static readonly Regex PlaceholderRegex =
        new(@"\{([a-zA-Z][a-zA-Z0-9_]{0,63})\}", RegexOptions.Compiled);

    private static HttpToolResult Fail(string text) => new(false, text);
    private static HttpToolResult Ok(string text) => new(true, text);

    public async Task<string> InvokeAsync(
        AgentToolDefinition definition,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default) =>
        (await InvokeDetailedAsync(definition, arguments, ct)).Output;

    public async Task<HttpToolResult> InvokeDetailedAsync(
        AgentToolDefinition definition,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        try
        {
            AgentToolValidator.Validate(definition);
        }
        catch (ArgumentException ex)
        {
            return Fail($"工具配置错误：{ex.Message}");
        }

        // ---------- 1. 参数：只取声明过的，必填缺失/类型不符直接回给模型 ----------
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in definition.Parameters)
        {
            arguments.TryGetValue(p.Name, out var raw);
            var value = AgentToolValidator.Coerce(raw, p.Type);
            if (value is null || value.Length == 0)
            {
                if (p.Required) return Fail($"缺少必填参数 {p.Name}（{p.Description}）");
                continue;
            }
            if (value.Length > 500) return Fail($"参数 {p.Name} 过长（上限 500 字符）");
            values[p.Name] = value;
        }

        // ---------- 2. URL：authority 固定，只替换其后内容 ----------
        Uri url;
        try
        {
            url = BuildRequestUri(definition.UrlTemplate, values);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "工具 {Tool} 的 URL 构建失败", definition.Name);
            return Fail("工具 URL 构建失败，请联系管理员检查配置");
        }

        // ---------- 3. POST body：占位符替换为 JSON 字面量，杜绝拼接注入 ----------
        string? body;
        try
        {
            body = BuildJsonBody(definition, values);
        }
        catch (JsonException)
        {
            return Fail("请求体模板替换后不是合法 JSON，请联系管理员检查配置");
        }

        // ---------- 4. 鉴权：只从环境变量取值，缺失即失败 ----------
        string? authValue = null;
        if (definition.AuthHeaderName is { Length: > 0 } headerName)
        {
            var envName = definition.AuthEnvVar!;
            var secret = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(secret))
                return Fail($"工具鉴权未配置：请在服务端设置环境变量 {envName}");
            authValue = (definition.AuthValuePrefix ?? string.Empty) + secret;
        }

        // ---------- 5. 调用（短超时 + 不跟随重定向） ----------
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(definition.TimeoutSeconds));
            var client = httpFactory.CreateClient("agent-tools");
            using var request = new HttpRequestMessage(new HttpMethod(definition.HttpMethod), url);

            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (authValue is not null)
                request.Headers.TryAddWithoutValidation(definition.AuthHeaderName!, authValue);

            using var response = await client.SendAsync(request, cts.Token);
            var text = await ReadLimitedAsync(response.Content, definition.MaxResponseChars, cts.Token);

            if ((int)response.StatusCode is >= 300 and < 400)
                return Fail($"工具返回重定向（HTTP {(int)response.StatusCode}），出于安全考虑不跟随，请联系管理员改用最终地址");

            var prefix = response.IsSuccessStatusCode
                ? string.Empty
                : $"工具返回 HTTP {(int)response.StatusCode} {response.StatusCode}：\n";
            return response.IsSuccessStatusCode
                ? Ok(prefix + text)
                : Fail(prefix + text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail($"工具调用超时（{definition.TimeoutSeconds} 秒），请改用知识库检索或稍后重试");
        }
        catch (OperationCanceledException)
        {
            throw; // 用户断开连接，交给上层终止
        }
        catch (Exception ex)
        {
            // 异常详情只进服务端日志，返回给模型的文本不含 URL / 请求头
            logger.LogWarning(ex, "工具 {Tool} 调用失败", definition.Name);
            return Fail("工具调用失败（网络或上游异常），请改用知识库检索或告知用户稍后重试");
        }
    }

    /// <summary>
    /// 纯函数：authority（scheme://host:port）保持原样，仅替换其后的 path/query 占位符。
    /// 参数值统一 Uri.EscapeDataString，因此含 / ? &amp; @ : 的值也不会改变 URL 结构。
    /// </summary>
    public static Uri BuildRequestUri(string template, IReadOnlyDictionary<string, string> values)
    {
        var baseUri = new Uri(template, UriKind.Absolute);
        var schemeEnd = template.IndexOf("://", StringComparison.Ordinal);
        var authorityStart = schemeEnd + 3;
        var authorityEnd = template.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? template : template[..authorityEnd];
        var rest = authorityEnd < 0 ? string.Empty : template[authorityEnd..];

        var replaced = PlaceholderRegex.Replace(rest, m =>
            values.TryGetValue(m.Groups[1].Value, out var v)
                ? Uri.EscapeDataString(v)
                : string.Empty);

        var final = new Uri(authority + replaced, UriKind.Absolute);
        if (final.Scheme != baseUri.Scheme ||
            !string.Equals(final.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) ||
            final.Port != baseUri.Port)
            throw new InvalidOperationException("参数值改写了目标主机，已拦截");
        return final;
    }

    /// <summary>纯函数：{param} 替换为 JSON 字面量；string 会带引号并转义，body 模板里不要再手写引号</summary>
    public static string? BuildJsonBody(AgentToolDefinition d, IReadOnlyDictionary<string, string> values)
    {
        if (d.BodyTemplate is not { Length: > 0 } template) return null;

        var body = PlaceholderRegex.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (!values.TryGetValue(name, out var v)) return "null";
            var type = d.Parameters.FirstOrDefault(p => p.Name == name)?.Type ?? "string";
            return type.ToLowerInvariant() switch
            {
                "boolean" => v.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
                "number" or "integer" => v,
                _ => JsonSerializer.Serialize(v)
            };
        });

        using var doc = JsonDocument.Parse(body); // 替换后必须仍是合法 JSON
        return body;
    }

    /// <summary>按字符数限长读取，避免上游返回超大响应把内存打爆</summary>
    internal static async Task<string> ReadLimitedAsync(
        HttpContent content, int maxChars, CancellationToken ct)
    {
        var type = content.Headers.ContentType?.MediaType ?? string.Empty;
        if (type.Length > 0 &&
            !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !type.Contains("json", StringComparison.OrdinalIgnoreCase) &&
            !type.Contains("xml", StringComparison.OrdinalIgnoreCase))
            return $"（上游返回非文本内容 {type}，已忽略）";

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars + 1];
        var total = 0;
        while (total < maxChars + 1)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, Math.Min(1024, maxChars + 1 - total)), ct);
            if (read == 0) break;
            total += read;
        }
        var truncated = total > maxChars;
        var text = new string(buffer, 0, Math.Min(total, maxChars));
        return truncated ? text + "…（响应已截断）" : text;
    }
}
