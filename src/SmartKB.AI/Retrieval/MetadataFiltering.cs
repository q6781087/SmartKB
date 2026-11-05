using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartKB.AI.Retrieval;

/// <summary>文档元数据过滤条件（LLM 只产出参数，字段名必须过白名单）</summary>
public record MetadataFilter(string Field, string Op, string Value);

/// <summary>知识库自定义字段（来自 KnowledgeBase.FieldSchemaJson）</summary>
public record FilterableField(string Name, string Label, string Type);

/// <summary>
/// 元数据过滤引擎（纯函数，无 DB 依赖，可单测）。
///
/// 安全边界：字段名只允许来自知识库 FieldSchema 中 filterable=true 的项，
/// 且必须匹配 ^[A-Za-z0-9_]{1,64}$；操作符固定集合；值一律作为字符串比较。
/// 过滤发生在"文档级"：先算出允许的 documentId 集合，再让向量/关键词两路 SQL
/// 都以 DocumentId IN (...) 过滤——权限与元数据都下推到 SQL，不是前端藏起来。
/// </summary>
public static partial class MetadataFilterEngine
{
    public static readonly string[] AllowedOps = ["eq", "contains", "gt", "gte", "lt", "lte"];

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex SafeFieldName();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>解析多个知识库的 FieldSchemaJson，取可过滤字段并集（按字段名去重）</summary>
    public static List<FilterableField> ParseFilterableFields(IEnumerable<string> schemaJsons)
    {
        var result = new Dictionary<string, FilterableField>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in schemaJsons)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("name", out var nameEl)) continue;
                    var name = nameEl.GetString();
                    if (string.IsNullOrWhiteSpace(name) || !SafeFieldName().IsMatch(name)) continue;
                    if (item.TryGetProperty("filterable", out var fEl) && fEl.ValueKind == JsonValueKind.False)
                        continue;

                    var label = item.TryGetProperty("label", out var labelEl) ? labelEl.GetString() ?? name : name;
                    var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "string" : "string";
                    result.TryAdd(name, new FilterableField(name, label, type));
                }
            }
            catch (JsonException) { /* 单个知识库 schema 坏了不影响其他库 */ }
        }
        return [.. result.Values.OrderBy(f => f.Name, StringComparer.Ordinal)];
    }

    /// <summary>工具描述里告诉模型有哪些可过滤字段</summary>
    public static string Describe(IReadOnlyList<FilterableField> fields) =>
        fields.Count == 0
            ? "当前知识库未配置可过滤字段。"
            : "可过滤字段：" + string.Join("、", fields.Select(f =>
                $"{f.Name}（{f.Label}，{f.Type}）")) + "。";

    /// <summary>解析 LLM 传入的 filters JSON；非法 JSON 直接抛，由工具转成可读错误</summary>
    public static List<MetadataFilter> ParseFilters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "[]" or "{}" or "null")
            return [];

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            return [ReadOne(doc.RootElement)];
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("filters 必须是 JSON 数组或对象");

        return [.. doc.RootElement.EnumerateArray().Select(ReadOne)];

        static MetadataFilter ReadOne(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("过滤条件必须是对象");
            var field = GetString(el, "field") ?? GetString(el, "Field") ?? "";
            var op = GetString(el, "op") ?? GetString(el, "Op") ?? "eq";
            var value = GetString(el, "value") ?? GetString(el, "Value") ?? "";
            return new MetadataFilter(field, op, value);
        }

        static string? GetString(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()
                : null;
    }

    /// <summary>白名单 + 操作符校验；不过直接抛 ArgumentException（工具捕获后回给模型）</summary>
    public static void Validate(
        IReadOnlyList<MetadataFilter> filters, IReadOnlyList<FilterableField> allowedFields)
    {
        if (filters.Count == 0) return;
        if (filters.Count > 8)
            throw new ArgumentException("过滤条件最多 8 个");

        var allowed = allowedFields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in filters)
        {
            if (string.IsNullOrWhiteSpace(f.Field) || !SafeFieldName().IsMatch(f.Field.Trim()))
                throw new ArgumentException($"非法元数据字段名：{f.Field}");
            if (!allowed.Contains(f.Field.Trim()))
                throw new ArgumentException($"字段 {f.Field} 未配置为可过滤");
            if (!AllowedOps.Contains(f.Op.Trim().ToLowerInvariant()))
                throw new ArgumentException($"非法操作符：{f.Op}（仅支持 eq/contains/gt/gte/lt/lte）");
            if (f.Value.Length > 256)
                throw new ArgumentException("过滤值过长（上限 256 字符）");
        }
    }

    /// <summary>内存精确匹配：把文档 MetadataJson 与各条件逐个比对（全部满足才返回 true）</summary>
    public static bool Matches(string? metadataJson, IReadOnlyList<MetadataFilter> filters)
    {
        if (filters.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var f in filters)
            {
                if (!doc.RootElement.TryGetProperty(f.Field.Trim(), out var prop))
                {
                    // 大小写不敏感兜底
                    prop = default;
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (string.Equals(p.Name, f.Field.Trim(), StringComparison.OrdinalIgnoreCase))
                        { prop = p.Value; break; }
                    if (prop.ValueKind == JsonValueKind.Undefined) return false;
                }

                var actual = prop.ValueKind == JsonValueKind.String
                    ? prop.GetString() ?? string.Empty
                    : prop.GetRawText();
                if (!Compare(actual, f.Op.Trim().ToLowerInvariant(), f.Value))
                    return false;
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Compare(string actual, string op, string expected) => op switch
    {
        "eq" => string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase),
        "contains" => actual.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase),
        "gt" or "gte" or "lt" or "lte" => CompareOrdered(actual, op, expected),
        _ => false
    };

    private static bool CompareOrdered(string actual, string op, string expected)
    {
        var hasA = decimal.TryParse(actual.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var a);
        var hasB = decimal.TryParse(expected.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var b);
        var numeric = hasA && hasB;
        var cmp = numeric
            ? decimal.Compare(a, b)
            : string.Compare(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

        return op switch
        {
            "gt" => cmp > 0,
            "gte" => cmp >= 0,
            "lt" => cmp < 0,
            "lte" => cmp <= 0,
            _ => false
        };
    }
}
