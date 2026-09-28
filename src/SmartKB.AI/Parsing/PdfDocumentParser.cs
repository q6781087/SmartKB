using SmartKB.Domain.Enums;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace SmartKB.AI.Parsing;

/// <summary>
/// PDF 解析：PdfPig 按页提取文本（ContentOrderTextExtractor，阅读顺序）。
/// 扫描件（无文本层）/ 加密件 → NotSupportedException → 上层标记 Failed 提示。
/// </summary>
public class PdfDocumentParser : IDocumentParser
{
    public DocumentType DocType => DocumentType.Pdf;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        var result = new ParsedDocument();

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(filePath);
        }
        catch (Exception ex) when (ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("PDF 已加密，请上传解密版本");
        }

        using (document)
        {
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                var text = ContentOrderTextExtractor.GetText(page).Trim();
                if (text.Length == 0)
                    continue;

                result.Blocks.Add(new ParsedBlock(string.Empty, text, page.Number));
            }
        }

        if (result.Blocks.Count == 0)
            throw new NotSupportedException("PDF 未提取到任何文本（可能为扫描件/纯图片），当前版本不支持 OCR，请使用含文本层的 PDF");

        return Task.FromResult(result);
    }
}
