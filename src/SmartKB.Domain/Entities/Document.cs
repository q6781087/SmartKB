using SmartKB.Domain.Enums;

namespace SmartKB.Domain.Entities;

/// <summary>知识库文档</summary>
public class KbDocument
{
    public long Id { get; set; }

    public long KbId { get; set; }

    public KnowledgeBase? KnowledgeBase { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>原始文件落盘相对路径</summary>
    public string FilePath { get; set; } = string.Empty;

    public DocumentType DocType { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;

    /// <summary>失败原因（Status=Failed 时）</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>客户自定义元数据（JSON，结构由知识库 FieldSchema 定义）</summary>
    public string MetadataJson { get; set; } = "{}";

    public long UploadedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? IndexedAt { get; set; }
}

/// <summary>文档分块（RAG 检索单元）</summary>
public class KbChunk
{
    public long Id { get; set; }

    public long DocumentId { get; set; }

    public KbDocument? Document { get; set; }

    public long KbId { get; set; }

    /// <summary>分块序号（文档内递增，用于引用定位）</summary>
    public int Seq { get; set; }

    public string Content { get; set; } = string.Empty;

    public int TokenCount { get; set; }

    /// <summary>块级元数据（标题路径、页码等，JSON）</summary>
    public string MetadataJson { get; set; } = "{}";

    /// <summary>1024 维向量，与智谱 embedding-3 (dims=1024) 和 bge-m3 对齐</summary>
    public Pgvector.Vector? Embedding { get; set; }
}

/// <summary>台账结构化行（Excel 逐行导入）</summary>
public class KbLedgerRow
{
    public long Id { get; set; }

    public long KbId { get; set; }

    public KnowledgeBase? KnowledgeBase { get; set; }

    public long SourceDocumentId { get; set; }

    public KbDocument? SourceDocument { get; set; }

    /// <summary>行数据（JSON，键 = 台账表头）</summary>
    public string RowDataJson { get; set; } = "{}";
}
