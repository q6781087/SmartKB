using System.Text.RegularExpressions;
using SmartKB.Domain.Enums;

namespace SmartKB.AI.Parsing;

/// <summary>Txt / Markdown 解析：Markdown 保留标题层级</summary>
public class PlainTextDocumentParser : IDocumentParser
{
    private static readonly Regex MdHeading = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);

    public DocumentType DocType { get; }

    public PlainTextDocumentParser(DocumentType docType) => DocType = docType;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        var text = File.ReadAllText(filePath);
        var result = new ParsedDocument();

        if (DocType == DocumentType.Txt)
        {
            if (text.Trim().Length > 0)
                result.Blocks.Add(new ParsedBlock(string.Empty, text.Trim(), null));
            return Task.FromResult(result);
        }

        // Markdown：# 标题切分
        var titlePath = new List<string>();
        var buffer = new List<string>();

        void Flush()
        {
            var body = string.Join("\n", buffer).Trim();
            buffer.Clear();
            if (body.Length > 0)
                result.Blocks.Add(new ParsedBlock(string.Join(" / ", titlePath), body, null));
        }

        foreach (var line in text.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            var m = MdHeading.Match(line.TrimEnd('\r'));
            if (m.Success)
            {
                Flush();
                var level = m.Groups[1].Value.Length;
                var title = m.Groups[2].Value.Trim();

                if (level == 1) titlePath.Clear();
                else if (titlePath.Count >= level) titlePath.RemoveRange(level - 1, titlePath.Count - level + 1);
                while (titlePath.Count < level - 1) titlePath.Add("");

                if (titlePath.Count >= level) titlePath[level - 1] = title;
                else titlePath.Add(title);
            }
            else
            {
                buffer.Add(line.TrimEnd('\r'));
            }
        }

        Flush();
        return Task.FromResult(result);
    }
}
