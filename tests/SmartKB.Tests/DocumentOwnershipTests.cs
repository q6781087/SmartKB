using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartKB.AI.Ingestion;
using SmartKB.Application;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>文档归属绑定：documentId 必须属于 kbId，且写操作必须有 Manage 权限</summary>
public class DocumentOwnershipTests
{
    private static async Task<(SmartKbDbContext Db, DocumentService Service)> SetupAsync(long userId = 1)
    {
        var db = TestDb.Create();
        db.AddRange(
            new User { Id = 1, Username = "u1", DisplayName = "张三", PasswordHash = "x" },
            new User { Id = 2, Username = "u2", DisplayName = "李四", PasswordHash = "x" },
            new KnowledgeBase { Id = 10, Name = "库A" },
            new KnowledgeBase { Id = 20, Name = "库B" });
        db.Permissions.AddRange(
            new KbPermission { KbId = 10, UserId = 1, Level = PermissionLevel.Manage },
            new KbPermission { KbId = 20, UserId = 1, Level = PermissionLevel.Manage });
        db.Documents.Add(new KbDocument
        {
            Id = 5,
            KbId = 10,
            FileName = "合同.pdf",
            FilePath = "/tmp/nonexistent-file",
            DocType = DocumentType.Pdf,
            Status = DocumentStatus.Failed
        });
        await db.SaveChangesAsync();

        var guard = new KbAccessGuard(db, new FakeCurrentUser(userId));
        var svc = new DocumentService(db, new DocumentQueue(),
            new ConfigurationBuilder().Build(), guard, new FakeCurrentUser(userId));
        return (db, svc);
    }

    [Fact]
    public async Task 跨知识库删除_被归属校验拒绝_文档仍在()
    {
        var (db, svc) = await SetupAsync();
        await using var scope = db;

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DeleteAsync(20, 5));
        Assert.Contains("不属于该知识库", ex.Message);
        Assert.Equal(1, await db.Documents.CountAsync());
    }

    [Fact]
    public async Task 跨知识库重试_被归属校验拒绝_状态不变()
    {
        var (db, svc) = await SetupAsync();
        await using var scope = db;

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RetryAsync(20, 5));
        Assert.Contains("不属于该知识库", ex.Message);
        Assert.Equal(DocumentStatus.Failed,
            (await db.Documents.SingleAsync()).Status);
    }

    [Fact]
    public async Task 无管理权限的用户_不能上传文档()
    {
        var (db, svc) = await SetupAsync(userId: 2);
        await using var scope = db;

        using var stream = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.UploadAsync(10, stream, "a.pdf", 2, null));
        Assert.Equal(1, await db.Documents.CountAsync());
    }

    [Fact]
    public async Task 无授权用户_不能枚举知识库文档列表()
    {
        var (db, svc) = await SetupAsync(userId: 2);
        await using var scope = db;

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetListAsync(10));
    }
}
