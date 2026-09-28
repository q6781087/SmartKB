using Microsoft.EntityFrameworkCore;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

public interface IPermissionService
{
    Task GrantAsync(GrantPermissionRequest request);
    Task RevokeAsync(long permissionId);
    Task<List<PermissionDto>> GetKbPermissionsAsync(long kbId);

    /// <summary>
    /// 用户可访问的知识库 ID 集合。
    /// 返回 null 表示管理员（不过滤）——这是 RAG 检索强制权限过滤的核心入口。
    /// </summary>
    Task<List<long>?> GetAccessibleKbIdsAsync(long userId);
}

/// <summary>知识库授权：用户或角色二选一授予 Read/Manage</summary>
public class PermissionService(SmartKbDbContext db) : IPermissionService
{
    public async Task GrantAsync(GrantPermissionRequest request)
    {
        if (request.UserId is null && request.RoleId is null)
            throw new BusinessRuleException("必须指定授权用户或角色");

        _ = await db.KnowledgeBases.FindAsync(request.KbId)
            ?? throw new BusinessRuleException("知识库不存在");

        if (request.UserId is not null)
            _ = await db.Users.FindAsync(request.UserId.Value)
                ?? throw new BusinessRuleException("被授权用户不存在");

        if (request.RoleId is not null)
            _ = await db.Roles.FindAsync(request.RoleId.Value)
                ?? throw new BusinessRuleException("被授权角色不存在");

        var exists = await db.Permissions.AnyAsync(p =>
            p.KbId == request.KbId && p.UserId == request.UserId && p.RoleId == request.RoleId);

        if (exists)
            throw new BusinessRuleException("该授权已存在，请勿重复授予");

        db.Permissions.Add(new KbPermission
        {
            KbId = request.KbId,
            UserId = request.UserId,
            RoleId = request.RoleId,
            Level = request.Level
        });
        await db.SaveChangesAsync();
    }

    public async Task RevokeAsync(long permissionId)
    {
        var p = await db.Permissions.FindAsync(permissionId)
            ?? throw new BusinessRuleException("授权记录不存在");
        db.Permissions.Remove(p);
        await db.SaveChangesAsync();
    }

    public async Task<List<PermissionDto>> GetKbPermissionsAsync(long kbId) =>
        await db.Permissions.AsNoTracking()
            .Where(p => p.KbId == kbId)
            .Select(p => new PermissionDto(
                p.Id, p.KbId, p.UserId,
                p.User != null ? p.User.DisplayName : null,
                p.RoleId, p.Role != null ? p.Role.Name : null,
                p.Level))
            .ToListAsync();

    public async Task<List<long>?> GetAccessibleKbIdsAsync(long userId)
    {
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new BusinessRuleException("用户不存在");

        if (user.IsAdmin) return null; // 管理员不过滤

        var roleIds = user.Roles.Select(r => r.Id).ToList();

        return await db.Permissions.AsNoTracking()
            .Where(p => p.UserId == userId ||
                        (p.RoleId != null && roleIds.Contains(p.RoleId.Value)))
            .Select(p => p.KbId)
            .Distinct()
            .ToListAsync();
    }
}
