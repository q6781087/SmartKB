namespace SmartKB.Application;

/// <summary>
/// LDAP/AD 配置（P2 可选，默认关闭）。绑定密码走环境变量（如 LDAP_BIND_PASSWORD），
/// 不写入配置文件与代码库。
/// </summary>
public sealed class LdapOptions
{
    public bool Enabled { get; set; }

    /// <summary>域控地址，如 ad.corp.local</summary>
    public string Server { get; set; } = string.Empty;

    public int Port { get; set; } = 389;

    /// <summary>使用 LDAPS（端口通常 636）</summary>
    public bool UseSsl { get; set; }

    /// <summary>搜索起点，如 DC=corp,DC=local</summary>
    public string BaseDn { get; set; } = string.Empty;

    /// <summary>用户搜索过滤器，{0} 为登录名；默认适配 AD</summary>
    public string SearchFilter { get; set; } = "(&(objectClass=user)(sAMAccountName={0}))";

    /// <summary>服务账号 DN（先绑服务账号搜索用户 DN，再以用户 DN + 密码验证）</summary>
    public string? BindDn { get; set; }

    /// <summary>服务账号密码（生产走环境变量 LDAP_BIND_PASSWORD 注入）</summary>
    public string? BindPassword { get; set; }

    /// <summary>LDAP 自动开通的账号挂到该角色名（不存在则不挂，由管理员手动授权）</summary>
    public string? DefaultRole { get; set; }

    /// <summary>展示名属性</summary>
    public string DisplayNameAttribute { get; set; } = "displayName";
}

/// <summary>LDAP 认证成功后的用户信息</summary>
public record LdapUserInfo(string Username, string DisplayName);

/// <summary>
/// LDAP 目录抽象：实现在 Api 层（依赖 System.DirectoryServices.Protocols），
/// 业务层只认接口——单测用假实现覆盖开通逻辑，不依赖真实域控。
/// </summary>
public interface ILdapDirectory
{
    /// <summary>验证用户名密码；失败/未启用返回 null</summary>
    Task<LdapUserInfo?> AuthenticateAsync(string username, string password, CancellationToken ct = default);
}
