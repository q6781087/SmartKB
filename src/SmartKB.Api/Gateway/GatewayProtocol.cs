using System.Globalization;
using System.Text.Json;

namespace SmartKB.Api.Gateway;

/// <summary>
/// OpenAI 兼容网关的部署级配置。默认关闭；ApiKey 只从环境变量注入（Gateway__ApiKey），
/// 绝不出现在 appsettings.json、日志或响应里。
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>总开关：关掉时 /v1/* 一律返回 503，避免意外暴露</summary>
    public bool Enabled { get; set; }

    /// <summary>服务间调用的静态 Bearer Key（仅环境变量）</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>静态 Key 绑定到的真实账号（按该账号计算知识库与工具权限）</summary>
    public string ServiceUsername { get; set; } = string.Empty;

    /// <summary>是否允许请求里的 system 消息覆盖系统提示词（默认关，统一由配置中心管控）</summary>
    public bool AllowSystemPrompt { get; set; }

    /// <summary>单次请求最多携带的消息条数</summary>
    public int MaxMessages { get; set; } = 32;

    /// <summary>单条问题最大字符数</summary>
    public int MaxQuestionChars { get; set; } = 8000;

    public bool Configured => Enabled
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ServiceUsername);
}

public sealed record GatewayMessage(string Role, string Content);

/// <summary>解析后的 /v1/chat/completions 请求（不含身份，身份由服务端强制解析）</summary>
public sealed record GatewayChatRequest(
    string Model,
    IReadOnlyList<GatewayMessage> Messages,
    string Question,
    string? SystemPrompt,
    bool Stream,
    bool IncludeUsage,
    IReadOnlyList<long> KbIds,
    long? SessionId,
    float? Temperature,
    int? MaxTokens);

public sealed class GatewayRequestException(string message) : Exception(message);

/// <summary>
/// OpenAI 请求体的宽容解析：只取需要的字段，未知字段一律忽略（各客户端会塞很多扩展字段）。
/// 权限相关的 kbIds 只作为"请求范围"，最终仍由编排器与 accessibleKbIds 求交。
/// </summary>
public static class GatewayRequestParser
{
    public const string DefaultModel = "smartkb";

    public static GatewayChatRequest Parse(
        JsonElement root, int maxMessages = 32, int maxQuestionChars = 8000)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new GatewayRequestException("请求体必须是 JSON 对象");

        var model = ReadString(root, "model") ?? DefaultModel;
        var stream = ReadBool(root, "stream") ?? false;
        var includeUsage = false;
        if (root.TryGetProperty("stream_options", out var so) && so.ValueKind == JsonValueKind.Object)
            includeUsage = ReadBool(so, "include_usage") ?? false;

        if (!root.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array ||
            messages.GetArrayLength() == 0)
            throw new GatewayRequestException("messages 不能为空");

        if (messages.GetArrayLength() > maxMessages)
            throw new GatewayRequestException($"messages 最多 {maxMessages} 条");

        var parsed = new List<GatewayMessage>(messages.GetArrayLength());
        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
                throw new GatewayRequestException("messages 元素必须是对象");
            var role = (ReadString(m, "role") ?? string.Empty).Trim().ToLowerInvariant();
            var content = ReadContent(m);
            if (role.Length == 0 || string.IsNullOrWhiteSpace(content))
                continue; // 空消息直接忽略，不把空白塞进上下文
            parsed.Add(new GatewayMessage(role, content));
        }

        var lastUser = parsed.FindLastIndex(m => m.Role == "user");
        if (lastUser < 0)
            throw new GatewayRequestException("messages 中至少需要一条 user 消息");

        var question = parsed[lastUser].Content;
        if (question.Length > maxQuestionChars)
            throw new GatewayRequestException($"问题过长（上限 {maxQuestionChars} 字符）");

        var system = parsed.LastOrDefault(m => m.Role == "system")?.Content;

        var kbIds = ReadKbIds(root);
        if (kbIds.Count == 0)
            kbIds = ParseModelKbIds(model);

        return new GatewayChatRequest(
            model,
            parsed,
            question,
            system,
            stream,
            includeUsage,
            kbIds,
            ReadNullableLong(root, "session_id"),
            ReadOptionalFloat(root, "temperature", 0, 2),
            ReadOptionalInt(root, "max_tokens", 128, 32768));
    }

    /// <summary>把 messages 转成编排器要的历史（最后一条 user 是本轮问题，不重复进历史）</summary>
    public static List<(Domain.Enums.ChatMessageRole Role, string Content)> ToHistory(
        GatewayChatRequest request)
    {
        var list = request.Messages;
        var lastUser = -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Role == "user") lastUser = i;
        return list
            .Where((m, i) => i != lastUser && m.Role is "user" or "assistant")
            .Select(m => (m.Role == "user"
                    ? Domain.Enums.ChatMessageRole.User
                    : Domain.Enums.ChatMessageRole.Assistant,
                m.Content))
            .ToList();
    }

    /// <summary>
    /// model 形如 smartkb:1,2 可限定知识库；smartkb / smartkb:all 或任意其他名字 = 全部可访问库。
    /// 客户端常硬编码 gpt-4o 等名字，这里不报错，避免兼容性问题。
    /// </summary>
    public static List<long> ParseModelKbIds(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) ||
            !model.StartsWith("smartkb:", StringComparison.OrdinalIgnoreCase))
            return [];

        var raw = model["smartkb:".Length..].Trim();
        if (raw.Length == 0 || raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            return [];

        var result = new List<long>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new GatewayRequestException(
                    $"model 中的知识库 ID 不合法：{part}；应形如 smartkb:1,2");
            if (!result.Contains(id)) result.Add(id);
        }
        return result;
    }

    private static List<long> ReadKbIds(JsonElement root)
    {
        if (!root.TryGetProperty("kb_ids", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<long>();
        foreach (var item in arr.EnumerateArray())
        {
            var ok = item.ValueKind switch
            {
                JsonValueKind.Number => item.TryGetInt64(out var n) && n > 0 ? n : 0,
                JsonValueKind.String => long.TryParse(item.GetString(), out var s) && s > 0 ? s : 0,
                _ => 0
            };
            if (ok <= 0)
                throw new GatewayRequestException("kb_ids 元素必须是正整数（知识库 ID）");
            if (!result.Contains(ok)) result.Add(ok);
        }
        return result;
    }

    /// <summary>content 既可能是字符串，也可能是多模态 part 数组（只取 text 部分）</summary>
    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var type = ReadString(part, "type");
            if (type is null or "text")
            {
                var text = ReadString(part, "text");
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
        }
        return string.Join("\n", parts);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? ReadBool(JsonElement obj, string name) => obj.TryGetProperty(name, out var v)
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        }
        : null;

    private static long? ReadNullableLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var n) && n > 0 ? n : null;

    private static float? ReadOptionalFloat(JsonElement obj, string name, double min, double max)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number ||
            !v.TryGetDouble(out var d)) return null;
        if (d < min || d > max)
            throw new GatewayRequestException($"{name} 须在 {min}–{max} 之间");
        return (float)d;
    }

    private static int? ReadOptionalInt(JsonElement obj, string name, int min, int max)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number ||
            !v.TryGetInt32(out var n)) return null;
        if (n < min || n > max)
            throw new GatewayRequestException($"{name} 须在 {min}–{max} 之间");
        return n;
    }
}
