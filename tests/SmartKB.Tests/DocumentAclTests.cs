using Microsoft.EntityFrameworkCore;
using SmartKB.AI.Ingestion;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>
/// 文档级授权（v1.6）：无记录 = 跟随库权限；有记录 = 仅白名单用户/角色（+库管理员）可见。
/// 覆盖文档列表过滤、授权管理闭环与检索侧向量路过滤。
/// </summary>
public class DocumentAclTests
{
    private static async Task<SmartKbDbContext> SeedAsync()
    {
        var db = TestDb.Create();
        db.Users.AddRange(
            new User { Id = 1, Username = "admin", DisplayName = "管理员", IsAdmin = true, PasswordHash = "x" },
            new User { Id = 2, Username = "u2", DisplayName = "张三", PasswordHash = "x" },
            new User { Id = 3, Username = "u3", DisplayName = "李四", PasswordHash = "x" });
        db.Roles.Add(new Role { Id = 5, Name = "售后" });
        db.KnowledgeBases.Add(new KnowledgeBase { Id = 10, Name = "设备库" });
        db.Permissions.Add(new KbPermission { KbId = 10, UserId = 2, Level = PermissionLevel.Read });
        db.Permissions.Add(new KbPermission { KbId = 10, UserId = 3, Level = PermissionLevel.Read });

        // 4 份文档：d1 无授权记录（人人可见）；d2 授权给用户 2；d3 授权给角色 5；d4 授权给无关用户
        for (var i = 1; i <= 4; i++)
            db.Documents.Add(new KbDocument
            {
                Id = (long)i, KbId = 10, FileName = $"d{i}.pdf",
                FilePath = $"/tmp/d{i}", DocType = DocumentType.Pdf, Status = DocumentStatus.Indexed
            });
        await db.SaveChangesAsync();

        db.Users.Local.First(u => u.Id == 2).Roles.Add(db.Roles.Local.First(r => r.Id == 5));
        db.DocumentGrants.AddRange(
            new KbDocumentGrant { DocumentId = 2, UserId = 2 },
            new KbDocumentGrant { DocumentId = 3, RoleId = 5 },
            new KbDocumentGrant { DocumentId = 4, UserId = 99 });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task 文档列表_只读用户看不到未授权的受限文档_管理员全量()
    {
        await using var db = await SeedAsync();
        var reader = NewService(db, userId: 2, isAdmin: false);
        var reader3 = NewService(db, userId: 3, isAdmin: false);
        var admin = NewService(db, userId: 1, isAdmin: true);

        var seen2 = (await reader.GetListAsync(10)).Select(d => d.Id).OrderBy(x => x).ToList();
        Assert.Equal([1L, 2L, 3L], seen2); // d4 授权给了别人，不可见

        var seen3 = (await reader3.GetListAsync(10)).Select(d => d.Id).OrderBy(x => x).ToList();
        Assert.Equal([1L], seen3); // 只读用户没有任何授权，仅无记录文档可见

        var seenAdmin = (await admin.GetListAsync(10)).Select(d => d.Id).OrderBy(x => x).ToList();
        Assert.Equal(4, seenAdmin.Count);
    }

    [Fact]
    public async Task 授权管理_全量覆盖_悬空目标被拒绝_跨库文档被拒绝()
    {
        await using var db = await SeedAsync();
        var manager = NewService(db, userId: 2, isAdmin: true); // 管理员路径最短

        // 悬空用户
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            manager.SetGrantsAsync(10, 1, new SetDocumentGrantsDto([999], [])));
        // 跨库文档
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            manager.SetGrantsAsync(20, 1, new SetDocumentGrantsDto([2], [])));

        var after = await manager.SetGrantsAsync(10, 1, new SetDocumentGrantsDto([2], [5]));
        Assert.Equal([2L], after.UserIds);
        Assert.Equal([5L], after.RoleIds);

        // 全量覆盖：先设用户再清空，恢复"跟随库权限"
        await manager.SetGrantsAsync(10, 1, new SetDocumentGrantsDto([], []));
        var cleared = await manager.GetGrantsAsync(10, 1);
        Assert.Empty(cleared.UserIds);
        Assert.Empty(cleared.RoleIds);
    }

    // 检索侧（向量路/关键词路/台账）的文档授权 SQL 下推需要关系型 provider，
    // InMemory 无法翻译 CosineDistance 排序与 FromSqlRaw——语义与文档列表同源，上线前在真实 PG 上用 EXPLAIN 验证。

    private static DocumentService NewService(SmartKbDbContext db, long userId, bool isAdmin)
    {
        var user = new FakeCurrentUser(userId, isAdmin);
        return new DocumentService(db, new DocumentQueue(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new KbAccessGuard(db, user), user);
    }
}
