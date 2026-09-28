using SmartKB.Domain.Enums;

namespace SmartKB.AI.Parsing;

/// <summary>解析出的内容块（标题路径用于 chunk 元数据与引用定位）</summary>
public record ParsedBlock(string TitlePath, string Text, int? PageIndex);

/// <summary>解析结果</summary>
public record ParsedDocument
{
    /// <summary>RAG 文本块</summary>
    public List<ParsedBlock> Blocks { get; init; } = [];

    /// <summary>台账表头（xlsx 且识别到表头时填充）</summary>
    public List<string>? LedgerColumns { get; init; }

    /// <summary>台账行数据（键 = 表头）</summary>
    public List<Dictionary<string, string>>? LedgerRows { get; init; }
}

/// <summary>文档解析器：每种格式一个实现，全托管 .NET 无外部依赖</summary>
public interface IDocumentParser
{
    DocumentType DocType { get; }

    /// <summary>解析文件为内容块；扫描件/加密等无法解析时抛 NotSupportedException（上层标记 Failed）</summary>
    Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default);
}
