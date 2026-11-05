using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request);
}

/// <summary>
/// 登录认证：本地 BCrypt 优先；开启 LDAP 时，本地校验失败再走 LDAP，
/// LDAP 验证通过即自动开通本地账号（挂 DefaultRole，若存在）并签发 JWT。
/// 自动开通的账号密码哈希是随机值——只能走 LDAP 登录，不能被猜本地密码。
/// </summary>
public class AuthService(
    SmartKbDbContext db,
    ITokenService tokenService,
    ILdapDirectory ldapDirectory,
    IOptions<LdapOptions> ldapOptions) : IAuthService
{
    public async Task<LoginResponse> LoginAsync(LoginRequest request)
    {
        var username = request.Username.Trim();
        var user = await db.Users.Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Username == username);

        // ---------- 本地认证（管理员与既有账号的主路径） ----------
        if (user is not null && !user.IsActive)
            throw new BusinessRuleException("账号已禁用，请联系管理员");

        if (user is not null && BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Issue(user);

        // ---------- LDAP 认证（可选；本地失败后兜底） ----------
        var ldapUser = await ldapDirectory.AuthenticateAsync(username, request.Password);
        if (ldapUser is null)
            throw new BusinessRuleException("用户名或密码错误");

        if (user is null)
            user = await ProvisionAsync(ldapUser);

        if (!user.IsActive)
            throw new BusinessRuleException("账号已禁用，请联系管理员");

        return Issue(user);
    }

    private LoginResponse Issue(User user)
    {
        var token = tokenService.GenerateToken(user);
        return new LoginResponse(token, 720,
            new CurrentUserDto(user.Id, user.Username, user.DisplayName,
                user.IsAdmin, user.Roles.Select(r => r.Name).ToList()));
    }

    /// <summary>LDAP 首登自动开通：随机密码哈希（本地不可登录）+ 可选默认角色</summary>
    private async Task<User> ProvisionAsync(LdapUserInfo ldapUser)
    {
        // 并发首登防护：唯一索引兜底，撞上就按已存在处理
        var existing = await db.Users.Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Username == ldapUser.Username);
        if (existing is not null) return existing;

        var user = new User
        {
            Username = ldapUser.Username,
            DisplayName = ldapUser.DisplayName,
            // 随机值哈希：本地密码路径不可登录，认证完全交给 LDAP
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
            IsAdmin = false,
            IsActive = true
        };

        var defaultRoleName = ldapOptions.Value.DefaultRole;
        if (!string.IsNullOrWhiteSpace(defaultRoleName))
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == defaultRoleName);
            if (role is not null) user.Roles.Add(role);
        }

        db.Users.Add(user);
        await db.SaveChangesAsync();
        await db.Entry(user).Collection(u => u.Roles).LoadAsync();
        return user;
    }
}
