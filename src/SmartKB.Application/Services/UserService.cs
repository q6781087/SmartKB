using Microsoft.EntityFrameworkCore;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

public interface IUserService
{
    Task<(List<UserDto> Items, int Total)> GetListAsync(string? keyword, int page, int pageSize);
    Task<UserDto> CreateAsync(CreateUserRequest request);
    Task<UserDto> UpdateAsync(long id, UpdateUserRequest request);
    Task ResetPasswordAsync(long id, string newPassword);
    Task ChangePasswordAsync(long userId, string oldPassword, string newPassword);
}

/// <summary>用户管理（管理员功能）</summary>
public class UserService(SmartKbDbContext db) : IUserService
{
    public async Task<(List<UserDto>, int)> GetListAsync(string? keyword, int page, int pageSize)
    {
        var query = db.Users.Include(u => u.Roles).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
            query = query.Where(u => u.Username.Contains(keyword) || u.DisplayName.Contains(keyword));

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        return (items.Select(ToDto).ToList(), total);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request)
    {
        if (await db.Users.AnyAsync(u => u.Username == request.Username))
            throw new BusinessRuleException($"用户名 {request.Username} 已存在");
        ValidatePassword(request.Password);

        var roles = await db.Roles.Where(r => request.RoleIds.Contains(r.Id)).ToListAsync();
        var user = new User
        {
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            DisplayName = request.DisplayName,
            Department = request.Department,
            IsAdmin = request.IsAdmin,
            Roles = roles
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return ToDto(user);
    }

    public async Task<UserDto> UpdateAsync(long id, UpdateUserRequest request)
    {
        var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == id)
            ?? throw new BusinessRuleException("用户不存在");

        if (!user.IsActive && !request.IsActive && user.IsAdmin)
            throw new BusinessRuleException("不能禁用管理员账号");

        user.DisplayName = request.DisplayName;
        user.Department = request.Department;
        user.IsActive = request.IsActive;
        user.IsAdmin = request.IsAdmin;

        var roles = await db.Roles.Where(r => request.RoleIds.Contains(r.Id)).ToListAsync();
        user.Roles.Clear();
        foreach (var role in roles) user.Roles.Add(role);

        await db.SaveChangesAsync();
        return ToDto(user);
    }

    public async Task ResetPasswordAsync(long id, string newPassword)
    {
        ValidatePassword(newPassword);
        var user = await db.Users.FindAsync(id)
            ?? throw new BusinessRuleException("用户不存在");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await db.SaveChangesAsync();
    }

    public async Task ChangePasswordAsync(long userId, string oldPassword, string newPassword)
    {
        ValidatePassword(newPassword);
        var user = await db.Users.FindAsync(userId)
            ?? throw new BusinessRuleException("用户不存在");
        if (!BCrypt.Net.BCrypt.Verify(oldPassword, user.PasswordHash))
            throw new BusinessRuleException("原密码错误");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await db.SaveChangesAsync();
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new BusinessRuleException("密码长度至少 8 位");
    }

    private static UserDto ToDto(User u) => new(
        u.Id, u.Username, u.DisplayName, u.Department, u.IsAdmin, u.IsActive, u.CreatedAt,
        u.Roles.Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystem)).ToList());
}
