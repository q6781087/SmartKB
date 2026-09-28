using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using SmartKB.AI.Chunking;
using SmartKB.AI.Embedding;
using SmartKB.AI.Parsing;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Ingestion;

/// <summary>文档入库流水线：解析 → 切分 → 向量化 → 入库（状态流转）</summary>
public interface IDocumentIngestionService
{
    Task IngestAsync(long documentId, CancellationToken ct = default);
}

public class DocumentIngestionService(
    SmartKbDbContext db,
    IEnumerable<IDocumentParser> parsers,
    TextChunker chunker,
    IEmbeddingProvider embedding,
    IOptions<LlmOptions> llmOptions,
    ILogger<DocumentIngestionService> logger) : IDocumentIngestionService
{
    private const int EmbedBatchSize = 32;

    public async Task IngestAsync(long documentId, CancellationToken ct = default)
    {
        var doc = await db.Documents.Include(d => d.KnowledgeBase)
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new InvalidOperationException($"文档不存在：{documentId}");

        doc.Status = DocumentStatus.Parsing;
        doc.ErrorMessage = null;
        await db.SaveChangesAsync(ct);

        try
        {
            var parser = parsers.FirstOrDefault(p => p.DocType == doc.DocType)
                ?? throw new NotSupportedException($"不支持的文档类型：{doc.DocType}");

            // 1. 解析
            var parsed = await parser.ParseAsync(doc.FilePath, ct);

            // 2. 清理旧分块（重试场景）
            await db.Chunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
            await db.LedgerRows.Where(r => r.SourceDocumentId == doc.Id).ExecuteDeleteAsync(ct);

            // 3. 切分
            var chunks = chunker.Split(parsed, new ChunkOptions());
            if (chunks.Count == 0)
                throw new NotSupportedException("未提取到有效文本内容");

            // 4. 向量化（分批） + 5. 入库
            var seq = 0;
            foreach (var batch in chunks.Chunk(EmbedBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var vectors = await embedding.EmbedBatchAsync(
                    batch.Select(b => b.Text).ToArray(), ct);

                for (var i = 0; i < batch.Length; i++)
                {
                    db.Chunks.Add(new KbChunk
                    {
                        DocumentId = doc.Id,
                        KbId = doc.KbId,
                        Seq = seq++,
                        Content = batch[i].Text,
                        // 中文按字符近似 token
                        TokenCount = batch[i].Text.Length,
                        MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            title = batch[i].TitlePath,
                            page = batch[i].PageIndex
                        }),
                        Embedding = new Vector(vectors[i])
                    });
                }
                await db.SaveChangesAsync(ct);
            }

            // 6. Excel 台账双轨入库
            if (doc.DocType == DocumentType.Xlsx && doc.KnowledgeBase?.EnableLedger == true
                && parsed.LedgerRows is { Count: > 0 })
            {
                foreach (var row in parsed.LedgerRows)
                {
                    db.LedgerRows.Add(new KbLedgerRow
                    {
                        KbId = doc.KbId,
                        SourceDocumentId = doc.Id,
                        RowDataJson = System.Text.Json.JsonSerializer.Serialize(row)
                    });
                }
                await db.SaveChangesAsync(ct);
                logger.LogInformation("台账入库完成 {DocId}：{Rows} 行", doc.Id, parsed.LedgerRows.Count);
            }

            doc.Status = DocumentStatus.Indexed;
            doc.IndexedAt = DateTime.UtcNow;
            doc.ErrorMessage = null;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("文档入库完成 {DocId}：{Chunks} 块（模式 {Mode}）",
                doc.Id, seq, llmOptions.Value.Mode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            doc.Status = DocumentStatus.Failed;
            doc.ErrorMessage = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await db.SaveChangesAsync(ct);
            logger.LogError(ex, "文档入库失败 {DocId}", doc.Id);
            throw;
        }
    }
}
