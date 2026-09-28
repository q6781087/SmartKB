using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SmartKB.Domain.Enums;

// OpenXml.Wordprocessing 也有 DocumentType，显式消歧
using DocumentType = SmartKB.Domain.Enums.DocumentType;

namespace SmartKB.AI.Parsing;

/// <summary>Word (docx) 解析：按标题层级组织块，表格转行式文本</summary>
public class DocxDocumentParser : IDocumentParser
{
    public DocumentType DocType => DocumentType.Docx;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new NotSupportedException("Word 文档结构异常（无正文）");

        var result = new ParsedDocument();
        var titlePath = new List<string>();
        var buffer = new List<string>();

        void Flush()
        {
            var text = string.Join("\n", buffer).Trim();
            buffer.Clear();
            if (text.Length > 0)
                result.Blocks.Add(new ParsedBlock(string.Join(" / ", titlePath), text, null));
        }

        foreach (var element in body.Elements())
        {
            ct.ThrowIfCancellationRequested();

            if (element is Paragraph p)
            {
                var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                var text = p.InnerText.Trim();

                if (text.Length == 0)
                    continue;

                // Heading1..9 / Title 视为标题，更新标题路径
                if (style is not null && (style.StartsWith("Heading") || style == "Title"))
                {
                    Flush();

                    var levelText = style == "Title" ? "1" : style["Heading".Length..];
                    if (!int.TryParse(levelText, out var level)) level = 1;
                    level = Math.Clamp(level, 1, 9);

                    // 低级标题清空同级及以下
                    if (level == 1) titlePath.Clear();
                    else if (titlePath.Count >= level) titlePath.RemoveRange(level - 1, titlePath.Count - level + 1);
                    while (titlePath.Count < level - 1) titlePath.Add("");

                    if (titlePath.Count >= level) titlePath[level - 1] = text;
                    else titlePath.Add(text);
                }
                else
                {
                    buffer.Add(text);
                }
            }
            else if (element is Table table)
            {
                Flush();
                var lines = new List<string>();
                foreach (var row in table.Elements<TableRow>())
                {
                    var cells = row.Elements<TableCell>().Select(c => c.InnerText.Trim());
                    lines.Add(string.Join(" | ", cells));
                }
                if (lines.Count > 0)
                    result.Blocks.Add(new ParsedBlock(string.Join(" / ", titlePath), string.Join("\n", lines), null));
            }
        }

        Flush();
        return Task.FromResult(result);
    }
}
