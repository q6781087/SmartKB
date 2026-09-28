using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartKB.AI.Ingestion;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

public interface IDocumentService
{
    /// <summary>上传文档：校验 → 落盘 → 建档 → 入处理队列</summary>
    Task<DocumentDto> UploadAsync(long kbId, Stream fileStream, string fileName, long userId, string? metadataJson);

    Task<List<DocumentDto>> GetListAsync(long kbId);

    /// <summary>失败重试（仅 Failed 状态）</summary>
    Task RetryAsync(long documentId);

    Task DeleteAsync(long documentId);
}

/// <summary>文档管理：上传走异步流水线，状态实时流转（前端轮询或 M3 换 SignalR 推送）</summary>
public class DocumentService(
    SmartKbDbContext db,
    DocumentQueue queue,
    IConfiguration configuration) : IDocumentService
{
    private static readonly Dictionary<string, DocumentType> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = DocumentType.Docx,
        [".pdf"] = DocumentType.Pdf,
        [".xlsx"] = DocumentType.Xlsx,
        [".txt"] = DocumentType.Txt,
        [".md"] = DocumentType.Md
    };

    public async Task<DocumentDto> UploadAsync(
        long kbId, Stream fileStream, string fileName, long userId, string? metadataJson)
    {
        var kb = await db.KnowledgeBases.FindAsync(kbId)
            ?? throw new BusinessRuleException("知识库不存在");

        var ext = Path.GetExtension(fileName);
        if (!ExtensionMap.TryGetValue(ext, out var docType))
            throw new BusinessRuleException("仅支持 docx / pdf / xlsx / txt / md 格式");

        // 保存目录：{root}/{FileStoragePath}/{kbId}/{yyyyMM}/{guid}{ext}
        var root = configuration["SmartKB:FileStoragePath"] ?? "AppData/files";
        var dir = Path.Combine(root, kbId.ToString(), DateTime.UtcNow.ToString("yyyyMM"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");

        await using (fileStream)
        await using (var output = File.Create(filePath))
        {
            await fileStream.CopyToAsync(output);
        }

        var doc = new KbDocument
        {
            KbId = kbId,
            FileName = fileName,
            FilePath = filePath,
            DocType = docType,
            Status = DocumentStatus.Uploaded,
            MetadataJson = string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson,
            UploadedBy = userId
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        await queue.EnqueueAsync(doc.Id);

        return ToDto(doc, 0);
    }

    public async Task<List<DocumentDto>> GetListAsync(long kbId)
    {
        return await db.Documents.AsNoTracking()
            .Where(d => d.KbId == kbId)
            .Select(d => new DocumentDto(
                d.Id, d.KbId, d.FileName, d.DocType, d.Status,
                d.ErrorMessage, d.CreatedAt, d.IndexedAt,
                db.Chunks.Count(c => c.DocumentId == d.Id)))
            .ToListAsync();
    }

    public async Task RetryAsync(long documentId)
    {
        var doc = await db.Documents.FindAsync(documentId)
            ?? throw new BusinessRuleException("文档不存在");

        if (doc.Status != DocumentStatus.Failed)
            throw new BusinessRuleException("仅失败状态的文档可重试");

        doc.Status = DocumentStatus.Uploaded;
        doc.ErrorMessage = null;
        await db.SaveChangesAsync();

        await queue.EnqueueAsync(doc.Id);
    }

    public async Task DeleteAsync(long documentId)
    {
        var doc = await db.Documents.FindAsync(documentId)
            ?? throw new BusinessRuleException("文档不存在");

        // 清理向量与台账数据
        await db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync();
        await db.LedgerRows.Where(r => r.SourceDocumentId == documentId).ExecuteDeleteAsync();
        db.Documents.Remove(doc);
        await db.SaveChangesAsync();

        // 清理物理文件（失败不阻塞，磁盘清理兜底）
        try
        {
            if (File.Exists(doc.FilePath)) File.Delete(doc.FilePath);
        }
        catch (IOException) { /* 忽略：文件被占用等场景 */ }
    }

    private static DocumentDto ToDto(KbDocument d, int chunkCount) =>
        new(d.Id, d.KbId, d.FileName, d.DocType, d.Status,
            d.ErrorMessage, d.CreatedAt, d.IndexedAt, chunkCount);
}
