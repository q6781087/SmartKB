using Microsoft.EntityFrameworkCore;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

/// <summary>
/// 知识库对象级授权守卫（服务端强制，不依赖前端隐藏，也不依赖控制器 Attribute）。
/// 所有针对具体知识库的读写操作，必须先过这里；LLM 检索侧另有 GetAccessibleKbIdsAsync 兜底。
/// </summary>
public interface IKbAccessGuard
{
    /// <summary>断言对该库有 Manage 权限（管理员短路）</summary>
    Task AssertManageAsync(long kbId, CancellationToken ct = default);

    /// <summary>断言对该库有任一级别授权（Read 或 Manage；Manage 隐含 Read）</summary>
    Task AssertReadAsync(long kbId, CancellationToken ct = default);

    /// <summary>是否具有 Manage 权限（不抛异常，供文档列表等"分级可见"场景）</summary>
    Task<bool> CanManageAsync(long kbId, CancellationToken ct = default);

    /// <summary>断言系统管理员（删库等系统级操作）</summary>
    void AssertAdmin();
}

public class KbAccessGuard(SmartKbDbContext db, ICurrentUser currentUser) : IKbAccessGuard
{
    public void AssertAdmin()
    {
        if (!currentUser.IsAdmin)
            throw new ForbiddenException("仅系统管理员可执行该操作");
    }

    public async Task AssertManageAsync(long kbId, CancellationToken ct = default)
    {
        if (currentUser.IsAdmin) return;
        if (!await HasGrantAsync(kbId, requireManage: true, ct))
            throw new ForbiddenException($"无权管理知识库 {kbId}");
    }

    public async Task AssertReadAsync(long kbId, CancellationToken ct = default)
    {
        if (currentUser.IsAdmin) return;
        if (!await HasGrantAsync(kbId, requireManage: false, ct))
            throw new ForbiddenException($"无权访问知识库 {kbId}");
    }

    public Task<bool> CanManageAsync(long kbId, CancellationToken ct = default) =>
        currentUser.IsAdmin ? Task.FromResult(true) : HasGrantAsync(kbId, requireManage: true, ct);

    /// <summary>用户直接授权 ∪ 所属角色授权；requireManage 时仅接受 Manage 级别</summary>
    private async Task<bool> HasGrantAsync(long kbId, bool requireManage, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var roleIds = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .SelectMany(u => u.Roles.Select(r => r.Id))
            .ToListAsync(ct);

        var query = db.Permissions.AsNoTracking().Where(p =>
            p.KbId == kbId &&
            (p.UserId == userId || (p.RoleId != null && roleIds.Contains(p.RoleId.Value))));

        if (requireManage)
            query = query.Where(p => p.Level == PermissionLevel.Manage);

        return await query.AnyAsync(ct);
    }
}
