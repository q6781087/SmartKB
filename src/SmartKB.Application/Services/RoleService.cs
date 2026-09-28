using Microsoft.EntityFrameworkCore;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

public interface IRoleService
{
    Task<List<RoleDto>> GetListAsync();
    Task<RoleDto> CreateAsync(CreateRoleRequest request);
    Task DeleteAsync(long id);
}

/// <summary>角色管理：系统内置角色（admin/kb_manager/user）不可删</summary>
public class RoleService(SmartKbDbContext db) : IRoleService
{
    public async Task<List<RoleDto>> GetListAsync() =>
        await db.Roles.AsNoTracking()
            .Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystem))
            .ToListAsync();

    public async Task<RoleDto> CreateAsync(CreateRoleRequest request)
    {
        if (await db.Roles.AnyAsync(r => r.Name == request.Name))
            throw new BusinessRuleException($"角色 {request.Name} 已存在");

        var role = new Role { Name = request.Name, Description = request.Description };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return new RoleDto(role.Id, role.Name, role.Description, false);
    }

    public async Task DeleteAsync(long id)
    {
        var role = await db.Roles.Include(r => r.Users).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new BusinessRuleException("角色不存在");

        if (role.IsSystem)
            throw new BusinessRuleException("系统内置角色不可删除");
        if (role.Users.Count > 0)
            throw new BusinessRuleException($"仍有 {role.Users.Count} 个用户绑定该角色，先解除绑定");

        db.Roles.Remove(role);
        await db.SaveChangesAsync();
    }
}
