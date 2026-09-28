using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SmartKB.AI.Embedding;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Retrieval;

/// <summary>检索结果片段（引用溯源最小单元）</summary>
public record RetrievedChunk(
    long ChunkId,
    long DocumentId,
    string FileName,
    int Seq,
    string Content,
    string? Title,
    float Score);

/// <summary>
/// 知识库向量检索：pgvector 余弦相似度 + <b>SQL 层强制权限过滤</b>。
/// accessibleKbIds 由调用方从 IPermissionService 取得；null 表示管理员不过滤。
/// </summary>
public interface IKnowledgeRetrievalService
{
    Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK = 8, CancellationToken ct = default);
}

public class KnowledgeRetrievalService(
    SmartKbDbContext db,
    IEmbeddingProvider embeddingProvider) : IKnowledgeRetrievalService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<List<RetrievedChunk>> SearchAsync(
        string question, List<long>? accessibleKbIds, int topK = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return [];

        // 1. 问题向量化
        var vectors = await embeddingProvider.EmbedBatchAsync([question.Trim()], ct);
        var queryVector = new Vector(vectors[0]);

        // 2. 向量余弦检索 + 权限过滤（同一查询内完成，杜绝越权召回）
        var query = db.Chunks.AsNoTracking()
            .Join(db.Documents, c => c.DocumentId, d => d.Id, (c, d) => new { Chunk = c, Doc = d });

        if (accessibleKbIds is not null)
        {
            if (accessibleKbIds.Count == 0) return [];
            query = query.Where(x => accessibleKbIds.Contains(x.Chunk.KbId));
        }

        var rows = await query
            .OrderBy(x => x.Chunk.Embedding!.CosineDistance(queryVector))
            .Take(topK)
            .Select(x => new
            {
                ChunkId = x.Chunk.Id,
                x.Chunk.DocumentId,
                x.Doc.FileName,
                x.Chunk.Seq,
                x.Chunk.Content,
                x.Chunk.MetadataJson,
                Distance = x.Chunk.Embedding!.CosineDistance(queryVector)
            })
            .ToListAsync(ct);

        // 3. 组装结果（分块元数据里提取标题路径，供引用展示）
        return rows.Select(r =>
        {
            string? title = null;
            try
            {
                using var meta = JsonDocument.Parse(r.MetadataJson);
                if (meta.RootElement.TryGetProperty("title", out var t))
                    title = t.GetString();
            }
            catch { /* 元数据非 JSON 时忽略 */ }

            return new RetrievedChunk(
                r.ChunkId, r.DocumentId, r.FileName, r.Seq, r.Content, title,
                1f - (float)r.Distance); // 余弦距离转相似度
        }).ToList();
    }
}
