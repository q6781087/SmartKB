using SmartKB.Application;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>对象级授权守卫：Read 不能升级为 Manage，无授权一律拒绝，管理员短路</summary>
public class KbAccessGuardTests
{
    private static async Task<(SmartKbDbContext Db, KbAccessGuard Guard)> SetupAsync(bool isAdmin = false)
    {
        var db = TestDb.Create();
        db.AddRange(
            new User { Id = 1, Username = "u1", DisplayName = "张三", PasswordHash = "x" },
            new KnowledgeBase { Id = 10, Name = "只读库" },
            new KnowledgeBase { Id = 20, Name = "可管库" },
            new KnowledgeBase { Id = 30, Name = "无权库" });
        db.Permissions.AddRange(
            new KbPermission { KbId = 10, UserId = 1, Level = PermissionLevel.Read },
            new KbPermission { KbId = 20, UserId = 1, Level = PermissionLevel.Manage });
        await db.SaveChangesAsync();
        return (db, new KbAccessGuard(db, new FakeCurrentUser(1, isAdmin)));
    }

    [Fact]
    public async Task Read_级别_不能执行_管理操作()
    {
        var (db, guard) = await SetupAsync();
        await using var scope = db;

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => guard.AssertManageAsync(10));
        Assert.Contains("无权管理", ex.Message);
    }

    [Fact]
    public async Task Manage_级别_可读可管()
    {
        var (db, guard) = await SetupAsync();
        await using var scope = db;

        await guard.AssertManageAsync(20);
        await guard.AssertReadAsync(20); // Manage 隐含 Read
        await guard.AssertReadAsync(10);
    }

    [Fact]
    public async Task 无授权的知识库_读写都被拒()
    {
        var (db, guard) = await SetupAsync();
        await using var scope = db;

        await Assert.ThrowsAsync<ForbiddenException>(() => guard.AssertManageAsync(30));
        await Assert.ThrowsAsync<ForbiddenException>(() => guard.AssertReadAsync(30));
        await Assert.ThrowsAsync<ForbiddenException>(() => guard.AssertReadAsync(999));
    }

    [Fact]
    public async Task 管理员_短路放行()
    {
        var (db, guard) = await SetupAsync(isAdmin: true);
        await using var scope = db;

        await guard.AssertManageAsync(30);
        await guard.AssertReadAsync(30);
        guard.AssertAdmin();
    }

    [Fact]
    public void 非管理员_AssertAdmin_被拒()
    {
        using var db = TestDb.Create();
        var guard = new KbAccessGuard(db, new FakeCurrentUser(1));

        Assert.Throws<ForbiddenException>(() => guard.AssertAdmin());
    }

    [Fact]
    public async Task 角色授权_同样生效()
    {
        await using var db = TestDb.Create();
        var role = new Role { Id = 7, Name = "销售部" };
        var user = new User { Id = 2, Username = "u2", DisplayName = "李四", PasswordHash = "x" };
        user.Roles.Add(role);
        db.AddRange(user, role, new KnowledgeBase { Id = 40, Name = "销售库" });
        db.Permissions.Add(new KbPermission
        {
            KbId = 40, RoleId = 7, Level = PermissionLevel.Manage
        });
        await db.SaveChangesAsync();

        var guard = new KbAccessGuard(db, new FakeCurrentUser(2));
        await guard.AssertManageAsync(40);
        await Assert.ThrowsAsync<ForbiddenException>(() => guard.AssertManageAsync(10));
    }
}
