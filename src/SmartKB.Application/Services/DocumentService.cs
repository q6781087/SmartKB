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
    /// <summary>上传文档：权限断言 → 校验 → 落盘 → 建档 → 入处理队列</summary>
    Task<DocumentDto> UploadAsync(long kbId, Stream fileStream, string fileName, long userId, string? metadataJson);

    Task<List<DocumentDto>> GetListAsync(long kbId);

    /// <summary>失败重试（仅 Failed 状态；documentId 必须属于 kbId）</summary>
    Task RetryAsync(long kbId, long documentId);

    /// <summary>删除文档（含向量与台账清理；documentId 必须属于 kbId）</summary>
    Task DeleteAsync(long kbId, long documentId);

    /// <summary>文档级授权查询（仅 Manage 权限）</summary>
    Task<DocumentGrantsDto> GetGrantsAsync(long kbId, long documentId, CancellationToken ct = default);

    /// <summary>全量覆盖文档级授权；传空列表即恢复"跟随库权限"</summary>
    Task<DocumentGrantsDto> SetGrantsAsync(
        long kbId, long documentId, SetDocumentGrantsDto dto, CancellationToken ct = default);
}

/// <summary>
/// 文档管理：上传走异步流水线，状态实时流转。
/// 权限在服务层强制（IKbAccessGuard），控制器只负责取参数——防止绕过控制器或跨库传 id。
/// 文档级授权（v1.6）：有授权记录的文档仅白名单用户/角色可见（Manage 权限与管理员不受限）。
/// </summary>
public class DocumentService(
    SmartKbDbContext db,
    IDocumentQueue queue,
    IConfiguration configuration,
    IKbAccessGuard guard,
    ICurrentUser currentUser) : IDocumentService
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
        await guard.AssertManageAsync(kbId);

        var kb = await db.KnowledgeBases.FindAsync(kbId)
            ?? throw new BusinessRuleException("知识库不存在");

        var ext = Path.GetExtension(fileName);
        if (!ExtensionMap.TryGetValue(ext, out var docType))
            throw new BusinessRuleException("仅支持 docx / pdf / xlsx / txt / md 格式");

        // 保存目录：{FileStoragePath}/{kbId}/{yyyyMM}/{guid}{ext}
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
        await guard.AssertReadAsync(kbId);

        var query = db.Documents.AsNoTracking().Where(d => d.KbId == kbId);

        // 文档级授权：Manage 权限/管理员看全量；只读用户看不到"被设了白名单且不含自己"的文档
        if (!await guard.CanManageAsync(kbId))
        {
            var roleIds = await db.Users.AsNoTracking()
                .Where(u => u.Id == currentUser.UserId)
                .SelectMany(u => u.Roles.Select(r => r.Id))
                .ToListAsync();
            query = query.Where(d =>
                !db.DocumentGrants.Any(g => g.DocumentId == d.Id) ||
                db.DocumentGrants.Any(g => g.DocumentId == d.Id &&
                    (g.UserId == currentUser.UserId ||
                     (g.RoleId != null && roleIds.Contains(g.RoleId.Value)))));
        }

        return await query
            .Select(d => new DocumentDto(
                d.Id, d.KbId, d.FileName, d.DocType, d.Status,
                d.ErrorMessage, d.CreatedAt, d.IndexedAt,
                db.Chunks.Count(c => c.DocumentId == d.Id)))
            .ToListAsync();
    }

    public async Task RetryAsync(long kbId, long documentId)
    {
        await guard.AssertManageAsync(kbId);

        // 归属绑定：documentId 必须属于 kbId，杜绝跨库操作
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.KbId == kbId)
            ?? throw new BusinessRuleException("文档不存在或不属于该知识库");

        if (doc.Status != DocumentStatus.Failed)
            throw new BusinessRuleException("仅失败状态的文档可重试");

        doc.Status = DocumentStatus.Uploaded;
        doc.ErrorMessage = null;
        await db.SaveChangesAsync();

        await queue.EnqueueAsync(doc.Id);
    }

    public async Task DeleteAsync(long kbId, long documentId)
    {
        await guard.AssertManageAsync(kbId);

        // 归属绑定：documentId 必须属于 kbId
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.KbId == kbId)
            ?? throw new BusinessRuleException("文档不存在或不属于该知识库");

        // 清理向量、台账与文档级授权数据
        await db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync();
        await db.LedgerRows.Where(r => r.SourceDocumentId == documentId).ExecuteDeleteAsync();
        await db.DocumentGrants.Where(g => g.DocumentId == documentId).ExecuteDeleteAsync();
        db.Documents.Remove(doc);
        await db.SaveChangesAsync();

        // 清理物理文件（失败不阻塞，磁盘清理兜底）
        try
        {
            if (File.Exists(doc.FilePath)) File.Delete(doc.FilePath);
        }
        catch (IOException) { /* 忽略：文件被占用等场景 */ }
    }

    public async Task<DocumentGrantsDto> GetGrantsAsync(
        long kbId, long documentId, CancellationToken ct = default)
    {
        await guard.AssertManageAsync(kbId);
        await FindAsync(kbId, documentId);

        var grants = await db.DocumentGrants.AsNoTracking()
            .Where(g => g.DocumentId == documentId)
            .ToListAsync(ct);
        return await BuildGrantsDtoAsync(documentId, grants, ct);
    }

    public async Task<DocumentGrantsDto> SetGrantsAsync(
        long kbId, long documentId, SetDocumentGrantsDto dto, CancellationToken ct = default)
    {
        await guard.AssertManageAsync(kbId);
        await FindAsync(kbId, documentId);

        var userIds = (dto.UserIds ?? []).Distinct().ToList();
        var roleIds = (dto.RoleIds ?? []).Distinct().ToList();
        if (userIds.Count > 200 || roleIds.Count > 100)
            throw new BusinessRuleException("单个文档最多授权 200 个用户 / 100 个角色");

        // 防 dangling：目标必须真实存在（不做静默忽略，避免管理员以为授权成功）
        var validUsers = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        if (validUsers.Count != userIds.Count)
            throw new BusinessRuleException("授权用户不存在或已删除");

        var validRoles = await db.Roles.AsNoTracking()
            .Where(r => roleIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        if (validRoles.Count != roleIds.Count)
            throw new BusinessRuleException("授权角色不存在或已删除");

        var existing = await db.DocumentGrants.Where(g => g.DocumentId == documentId).ToListAsync(ct);
        db.DocumentGrants.RemoveRange(existing);
        db.DocumentGrants.AddRange(userIds.Select(uid => new KbDocumentGrant
        {
            DocumentId = documentId, UserId = uid, CreatedAt = DateTime.UtcNow
        }));
        db.DocumentGrants.AddRange(roleIds.Select(rid => new KbDocumentGrant
        {
            DocumentId = documentId, RoleId = rid, CreatedAt = DateTime.UtcNow
        }));
        await db.SaveChangesAsync(ct);

        return await GetGrantsAsync(kbId, documentId, ct);
    }

    private async Task<DocumentGrantsDto> BuildGrantsDtoAsync(
        long documentId, List<KbDocumentGrant> grants, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Take(500)
            .Select(u => new ToolGrantOptionDto(u.Id, u.DisplayName + "（" + u.Username + "）"))
            .ToListAsync(ct);
        var roles = await db.Roles.AsNoTracking()
            .OrderBy(r => r.Name).ThenBy(r => r.Id)
            .Select(r => new ToolGrantOptionDto(r.Id, r.Name))
            .ToListAsync(ct);

        return new DocumentGrantsDto(
            documentId,
            grants.Where(g => g.UserId.HasValue).Select(g => g.UserId!.Value).Distinct().ToList(),
            grants.Where(g => g.RoleId.HasValue).Select(g => g.RoleId!.Value).Distinct().ToList(),
            users, roles);
    }

    /// <summary>归属绑定：documentId 必须属于 kbId，杜绝跨库操作</summary>
    private async Task<KbDocument> FindAsync(long kbId, long documentId) =>
        await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.KbId == kbId)
        ?? throw new BusinessRuleException("文档不存在或不属于该知识库");

    private static DocumentDto ToDto(KbDocument d, int chunkCount) =>
        new(d.Id, d.KbId, d.FileName, d.DocType, d.Status,
            d.ErrorMessage, d.CreatedAt, d.IndexedAt, chunkCount);
}
