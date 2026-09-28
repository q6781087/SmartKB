using ClosedXML.Excel;
using SmartKB.Domain.Enums;

namespace SmartKB.AI.Parsing;

/// <summary>
/// Excel (xlsx) 解析：双轨输出。
/// RAG 文本块 = 每行 "列: 值 | 列: 值"；同时输出表头与行数据（EnableLedger 时入库 kb_ledger_rows）。
/// </summary>
public class XlsxDocumentParser : IDocumentParser
{
    public DocumentType DocType => DocumentType.Xlsx;

    public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default)
    {
        using var workbook = new XLWorkbook(filePath);
        var blocks = new List<ParsedBlock>();
        var allRows = new List<Dictionary<string, string>>();
        var allColumns = new List<string>();

        foreach (var sheet in workbook.Worksheets)
        {
            ct.ThrowIfCancellationRequested();
            var used = sheet.RangeUsed();
            if (used is null) continue;

            var title = sheet.Name;
            var rows = used.Rows().ToList();
            if (rows.Count == 0) continue;

            // 首行作表头
            var headers = rows[0].Cells()
                .Select(c => (index: c.Address.ColumnNumber, text: c.GetString().Trim()))
                .Where(h => h.text.Length > 0)
                .ToList();

            if (headers.Count == 0) continue;

            foreach (var h in headers.Where(h => !allColumns.Contains(h.text)))
                allColumns.Add(h.text);

            // 表头行转一个块（辅助语义检索："这个台账有哪些列" 类问题）
            blocks.Add(new ParsedBlock(title,
                "表头：" + string.Join(" | ", headers.Select(h => h.text)), null));

            foreach (var row in rows.Skip(1))
            {
                var cells = row.Cells().ToDictionary(c => c.Address.ColumnNumber, c => c.GetString().Trim());
                var pairs = headers
                    .Select(h => (h.text, value: cells.TryGetValue(h.index, out var v) ? v : ""))
                    .Where(x => x.value.Length > 0)
                    .ToList();
                if (pairs.Count == 0) continue;

                var line = string.Join(" | ", pairs.Select(p => $"{p.text}: {p.value}"));
                blocks.Add(new ParsedBlock(title, line, null));

                allRows.Add(pairs.ToDictionary(p => p.text, p => p.value));
            }
        }

        if (allColumns.Count > 0)
        {
            return Task.FromResult(new ParsedDocument
            {
                Blocks = blocks,
                LedgerColumns = allColumns,
                LedgerRows = allRows
            });
        }

        return Task.FromResult(new ParsedDocument { Blocks = blocks });
    }
}
