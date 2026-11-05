using System.Text.RegularExpressions;

namespace SmartKB.AI.Retrieval;

/// <summary>
/// 混合召回融合与关键词提取（纯函数，零依赖，可单测）。
/// RRF（Reciprocal Rank Fusion）：每一路按名次给分 1/(k+rank)，多路相加，
/// 不需要把向量距离与全文相关度做量纲归一化——这是双路检索最省心的融合方式。
/// </summary>
public static partial class HybridFusion
{
    public record FusedItem(long ChunkId, double Score);

    /// <summary>两路（可推广到多路）RRF 融合</summary>
    public static List<FusedItem> RrfFuse(
        IReadOnlyList<long> vectorRankedIds,
        IReadOnlyList<long> keywordRankedIds,
        int k = 60)
    {
        var scores = new Dictionary<long, double>();
        Accumulate(scores, vectorRankedIds, k, weight: 1.0);
        Accumulate(scores, keywordRankedIds, k, weight: 1.0);
        return [.. scores
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key)
            .Select(x => new FusedItem(x.Key, x.Value))];
    }

    /// <summary>单路结果直接转融合结构（混合检索关闭时复用同一后续管线）</summary>
    public static List<FusedItem> Single(IReadOnlyList<long> rankedIds, int k = 60)
    {
        var scores = new Dictionary<long, double>();
        Accumulate(scores, rankedIds, k, weight: 1.0);
        return [.. scores.OrderByDescending(x => x.Value).ThenBy(x => x.Key)
            .Select(x => new FusedItem(x.Key, x.Value))];
    }

    private static void Accumulate(
        Dictionary<long, double> scores, IReadOnlyList<long> ids, int k, double weight)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            scores[id] = scores.GetValueOrDefault(id) + weight / (k + i + 1);
        }
    }

    /// <summary>
    /// 从问题里抽取用于 PostgreSQL 精确词兜底的关键词。
    /// 中文长句按 2-gram 切（simple 分词器对中文不友好），编号/型号/英文术语整串保留
    /// ——这正是"合同编号、条款号、设备型号"这类纯向量检索最弱的场景。
    /// </summary>
    public static List<string> ExtractKeywords(string question, int max = 6)
    {
        if (string.IsNullOrWhiteSpace(question)) return [];
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length < 2 || s.Length > 32) return;
            if (StopBigram().IsMatch(s)) return;
            if (seen.Add(s)) result.Add(s);
        }

        foreach (Match m in TokenPattern().Matches(question))
        {
            var token = m.Value;
            if (token[0] is >= '0' and <= '9' || token[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                Add(token); // 编号 / 型号 / 英文 / 日期
            }
            else if (token.Length <= 8)
            {
                Add(token); // 短中文词组整体
            }
            else
            {
                // 长中文串按 2-gram 切，过滤含虚词的组合
                for (var i = 0; i + 1 < token.Length && result.Count < max * 2; i++)
                    Add(token.Substring(i, 2));
            }
            if (result.Count >= max) break;
        }

        return [.. result.Take(max)];
    }

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9_\-/\.]{1,31}|[\u4e00-\u9fff]{2,}", RegexOptions.Compiled)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[的了和与是在有请什么如何哪请问关于]|(什么|怎么|哪些)", RegexOptions.Compiled)]
    private static partial Regex StopBigram();
}
