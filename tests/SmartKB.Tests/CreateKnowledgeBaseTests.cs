using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>建库授权闭环：非管理员建库后自动获得 Manage，避免新守卫造成"能建不能管"</summary>
public class CreateKnowledgeBaseTests
{
    [Fact]
    public async Task 非管理员建库_自动获得_Manage_且列表可见()
    {
        await using var db = TestDb.Create();
        db.Users.Add(new User
        {
            Id = 3, Username = "builder", DisplayName = "建库者", PasswordHash = "x"
        });
        await db.SaveChangesAsync();

        var current = new FakeCurrentUser(3);
        var guard = new KbAccessGuard(db, current);
        var svc = new KnowledgeBaseService(db, guard, current);

        var created = await svc.CreateAsync(
            new SaveKnowledgeBaseRequest("新库", null, "[]", false));

        Assert.True(created.Id > 0);
        await guard.AssertManageAsync(created.Id); // 不抛即通过
        var list = await svc.GetListAsync(3, isAdmin: false);
        Assert.Contains(list, k => k.Id == created.Id);
    }

    [Fact]
    public async Task 管理员建库_同样写入创建者授权()
    {
        await using var db = TestDb.Create();
        var current = new FakeCurrentUser(1, isAdmin: true);
        var svc = new KnowledgeBaseService(db, new KbAccessGuard(db, current), current);

        await svc.CreateAsync(new SaveKnowledgeBaseRequest("管理员库", null, "[]", false));

        Assert.Single(db.KnowledgeBases);
        var grant = Assert.Single(db.Permissions);
        Assert.Equal(1, grant.UserId);
        Assert.Equal(PermissionLevel.Manage, grant.Level);
    }

    /// <summary>
    /// 回归：账号建库时是管理员、之后被降级为普通用户，仍应能看到自己建的库。
    /// 旧实现（管理员建库不写授权）在这种情况下列表为空。
    /// </summary>
    [Fact]
    public async Task 管理员建的库_账号降级后仍可见()
    {
        await using var db = TestDb.Create();
        var current = new FakeCurrentUser(1, isAdmin: true);
        var svc = new KnowledgeBaseService(db, new KbAccessGuard(db, current), current);

        var created = await svc.CreateAsync(new SaveKnowledgeBaseRequest("降级验证库", null, "[]", false));

        // 列表按"普通用户"身份查询（等价于后续登录拿到的 isadmin=false）
        var list = await svc.GetListAsync(1, isAdmin: false);
        Assert.Contains(list, k => k.Id == created.Id);
    }

    [Fact]
    public async Task 重名建库_不产生库与授权()
    {
        await using var db = TestDb.Create();
        var current = new FakeCurrentUser(3);
        var svc = new KnowledgeBaseService(db, new KbAccessGuard(db, current), current);

        await svc.CreateAsync(new SaveKnowledgeBaseRequest("同名库", null, "[]", false));

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.CreateAsync(new SaveKnowledgeBaseRequest("同名库", null, "[]", false)));

        Assert.Single(db.KnowledgeBases);
        Assert.Single(db.Permissions);
    }
}
