namespace SmartKB.AI.Chunking;

public class ChunkOptions
{
    /// <summary>目标块大小（字符近似 token，中文 1 字 ≈ 1 token）</summary>
    public int TargetSize { get; set; } = 600;

    /// <summary>相邻块重叠字符数（保上下文连贯）</summary>
    public int OverlapSize { get; set; } = 100;

    /// <summary>单块硬上限（超长强制硬切）</summary>
    public int MaxSize { get; set; } = 1200;
}

/// <summary>切分结果</summary>
public record ChunkDraft(string TitlePath, string Text, int? PageIndex);

/// <summary>
/// 规则切分器：块级合并 + 句界软切 + 尾部重叠。
/// 不引外部依赖；中文按句号/问号/换行切句。
/// </summary>
public class TextChunker
{
    public List<ChunkDraft> Split(Parsing.ParsedDocument doc, ChunkOptions opt)
    {
        var drafts = new List<ChunkDraft>();

        foreach (var block in doc.Blocks)
        {
            if (block.Text.Length <= opt.TargetSize)
            {
                // 尝试并入上一块（同标题 + 合并后不超限）
                var last = drafts.Count > 0 ? drafts[^1] : null;
                if (last is not null && last.TitlePath == block.TitlePath &&
                    last.Text.Length + block.Text.Length <= opt.MaxSize)
                {
                    drafts[^1] = last with { Text = last.Text + "\n" + block.Text };
                }
                else
                {
                    drafts.Add(new ChunkDraft(block.TitlePath, block.Text, block.PageIndex));
                }
                continue;
            }

            // 超长块：按句切再组装
            foreach (var piece in SplitBySentences(block.Text, opt))
                drafts.Add(new ChunkDraft(block.TitlePath, piece, block.PageIndex));
        }

        return drafts;
    }

    private static IEnumerable<string> SplitBySentences(string text, ChunkOptions opt)
    {
        var sentences = RegexSplit(text);
        var current = new List<string>();
        var length = 0;

        foreach (var s in sentences)
        {
            // 单句超硬上限：按字符硬切
            if (s.Length > opt.MaxSize)
            {
                if (length > 0)
                {
                    yield return JoinWithOverlap(current, opt.OverlapSize);
                    current.Clear();
                    length = 0;
                }
                for (var i = 0; i < s.Length; i += opt.TargetSize)
                {
                    var end = Math.Min(i + opt.TargetSize + opt.OverlapSize, s.Length);
                    yield return s[i..end];
                }
                continue;
            }

            if (length + s.Length > opt.TargetSize && length > 0)
            {
                yield return JoinWithOverlap(current, opt.OverlapSize);
                // 保留尾部句子作重叠上下文
                var tail = new List<string>();
                var tailLen = 0;
                for (var i = current.Count - 1; i >= 0; i--)
                {
                    tailLen += current[i].Length;
                    if (tailLen > opt.OverlapSize) break;
                    tail.Insert(0, current[i]);
                }
                current.Clear();
                current.AddRange(tail);
                length = tailLen;
            }

            current.Add(s);
            length += s.Length;
        }

        if (length > 0)
            yield return JoinWithOverlap(current, 0);
    }

    private static string JoinWithOverlap(List<string> parts, int overlapSize)
    {
        var text = string.Join("", parts);
        if (overlapSize > 0 && text.Length > overlapSize)
        {
            // 组装时尾部句子已做重叠，这里仅在强制合并场景兜底
        }
        return text;
    }

    private static List<string> RegexSplit(string text)
    {
        // 中文句界 + 换行，保留分隔符
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '。' or '！' or '？' or '；' or '\n' or '.' or '!' or '?')
            {
                // 连续分隔符合并为一句
                if (i + 1 < text.Length && IsSeparator(text[i + 1]))
                    continue;
                var piece = text[start..(i + 1)].Trim();
                if (piece.Length > 0) parts.Add(piece);
                start = i + 1;
            }
        }
        var rest = text[start..].Trim();
        if (rest.Length > 0) parts.Add(rest);
        return parts;

        static bool IsSeparator(char c) => c is '。' or '！' or '？' or '；' or '\n' or '.' or '!' or '?' or '"' or '」' or '》';
    }
}
