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

    /// <summary>
    /// 系统管理员。该列是 admin 角色的派生冗余值（保留列以兼容存量部署与外部报表），
    /// 不再由客户端直接设置：所有写入统一按 <see cref="SystemRoles.Admin"/> 角色计算。
    /// </summary>
    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Role> Roles { get; set; } = new List<Role>();
}

/// <summary>
/// 内置角色名。RBAC 的单一真相源：拥有 admin 角色 ≡ 系统管理员。
/// 历史上 sys_users.IsAdmin 与角色可独立设置，导致控制器角色 claim 与 isadmin claim 错配
/// （挂了 admin 角色却被当普通用户过滤知识库）。现在写入由角色派生、读取兼容两者。
/// </summary>
public static class SystemRoles
{
    public const string Admin = "admin";
    public const string KbManager = "kb_manager";
    public const string NormalUser = "user";

    public static bool IsAdmin(IEnumerable<string> roleNames) =>
        roleNames.Any(n => string.Equals(n, Admin, StringComparison.OrdinalIgnoreCase));
}

public static class UserRoleExtensions
{
    /// <summary>实体集合是否已挂 admin 角色</summary>
    public static bool HasAdminRole(this IEnumerable<Role> roles) =>
        roles.Any(r => string.Equals(r.Name, SystemRoles.Admin, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 是否系统管理员。以角色为准，并兼容 IsAdmin 列（旧数据、未加载 Roles 的场景）。
    /// 调用方应尽量 Include(u =&gt; u.Roles)，否则只能依赖列值。
    /// </summary>
    public static bool IsSystemAdmin(this User user) =>
        user.IsAdmin || user.Roles.HasAdminRole();
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
