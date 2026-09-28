using SmartKB.Domain.Enums;

namespace SmartKB.Domain.Entities;

/// <summary>系统用户</summary>
public class User
{
    public long Id { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>BCrypt 哈希，永不存明文</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Department { get; set; }

    /// <summary>超级管理员，绕过所有权限检查</summary>
    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Role> Roles { get; set; } = new List<Role>();
}

/// <summary>角色（RBAC）</summary>
public class Role
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsSystem { get; set; }

    public ICollection<User> Users { get; set; } = new List<User>();
}
