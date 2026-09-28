using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Retrieval;

/// <summary>台账过滤条件（LLM 只允许产出此结构，字段必须通过白名单校验）</summary>
public record LedgerFilter(string Field, string Op, string Value);

/// <summary>
/// 台账结构化查询（kb_ledger_rows，JSONB）。
/// 安全边界：字段名必须在该库实际存在的 JSON 键白名单内（不白名单不进 SQL），
/// 操作符只允许固定集合，值一律走参数化——LLM 产出仅作"参数"，不拼 SQL。
/// </summary>
public interface ILedgerQueryService
{
    /// <summary>该库台账实际存在的字段名（取自行数据 JSON 键，供工具描述与白名单）</summary>
    Task<List<string>> GetLedgerFieldsAsync(long kbId, CancellationToken ct = default);

    /// <summary>按过滤条件查询台账行（最多 maxRows 行）</summary>
    Task<List<Dictionary<string, string>>> QueryAsync(
        long kbId, IReadOnlyList<LedgerFilter> filters, int maxRows = 50, CancellationToken ct = default);
}

public class LedgerQueryService(SmartKbDbContext db) : ILedgerQueryService
{
    private static readonly string[] AllowedOps = ["eq", "contains", "gt", "gte", "lt", "lte"];

    public async Task<List<string>> GetLedgerFieldsAsync(long kbId, CancellationToken ct = default)
    {
        // 采样前 200 行，解析 JSON 键并集（台账表头即键）
        var samples = await db.LedgerRows.AsNoTracking()
            .Where(r => r.KbId == kbId)
            .Select(r => r.RowDataJson)
            .Take(200)
            .ToListAsync(ct);

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var json in samples)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    keys.Add(prop.Name);
            }
            catch { /* 单行坏数据跳过 */ }
        }
        return [.. keys];
    }

    public async Task<List<Dictionary<string, string>>> QueryAsync(
        long kbId, IReadOnlyList<LedgerFilter> filters, int maxRows = 50, CancellationToken ct = default)
    {
        if (filters.Count == 0)
            throw new ArgumentException("至少需要一个过滤条件");

        var whitelist = await GetLedgerFieldsAsync(kbId, ct);
        var wheres = new List<string> { "\"KbId\" = {0}" };
        var parameters = new List<object> { kbId };
        var paramIndex = 1;

        foreach (var filter in filters)
        {
            var field = filter.Field.Trim();
            var op = filter.Op.Trim().ToLowerInvariant();

            // 白名单校验：字段必须真实存在于台账数据；操作符固定集合
            if (!whitelist.Contains(field, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"非法台账字段：{field}");
            if (!AllowedOps.Contains(op))
                throw new ArgumentException($"非法操作符：{op}（仅支持 eq/contains/gt/gte/lt/lte）");

            // 字段名已过白名单，可安全进入 SQL（仍做单引号转义兜底）
            var jsonPath = $"\"RowDataJson\"->>'{field.Replace("'", "''")}'";

            switch (op)
            {
                case "eq":
                    wheres.Add($"{jsonPath} = {{{paramIndex}}}");
                    parameters.Add(filter.Value);
                    paramIndex++;
                    break;

                case "contains":
                    wheres.Add($"{jsonPath} ILIKE {{{paramIndex}}}");
                    parameters.Add($"%{EscapeLike(filter.Value)}%");
                    paramIndex++;
                    break;

                default:
                    // gt/gte/lt/lte：优先按数值比较，非数值退化为文本比较
                    if (decimal.TryParse(filter.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                    {
                        wheres.Add($"({jsonPath})::numeric {PgOp(op)} {{{paramIndex}}}");
                    }
                    else
                    {
                        wheres.Add($"{jsonPath} {PgOp(op)} {{{paramIndex}}}");
                    }
                    parameters.Add(filter.Value);
                    paramIndex++;
                    break;
            }
        }

        var sql = $"SELECT * FROM kb_ledger_rows WHERE {string.Join(" AND ", wheres)} LIMIT {{{paramIndex}}}";
        parameters.Add(maxRows);

        var rows = await db.LedgerRows
            .FromSqlRaw(sql, [.. parameters])
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.Select(ToPlainDict).ToList();
    }

    private static string PgOp(string op) => op switch
    {
        "gt" => ">",
        "gte" => ">=",
        "lt" => "<",
        "lte" => "<=",
        _ => "="
    };

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static Dictionary<string, string> ToPlainDict(KbLedgerRow row)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(row.RowDataJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
        }
        catch
        {
            result["_raw"] = row.RowDataJson;
        }
        return result;
    }
}
