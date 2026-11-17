using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SmartKB.Api;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>
/// admin 角色与 IsAdmin 统一（单一真相源）：写入由角色派生、读取兼容旧列/旧 token，
/// 以及 KbPermission 用 KbId 做外键、不再生成 KnowledgeBaseId 影子列。
/// </summary>
public class AdminRoleUnificationTests
{
    private static IConfiguration JwtConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "SmartKB",
            ["Jwt:Audience"] = "SmartKB",
            ["Jwt:SecretKey"] = new string('k', 48),
            ["Jwt:ExpireMinutes"] = "720"
        }).Build();

    private static Role AdminRole() => new() { Id = 1, Name = SystemRoles.Admin, IsSystem = true };

    private sealed class NoLdap : ILdapDirectory
    {
        public Task<LdapUserInfo?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
            => Task.FromResult<LdapUserInfo?>(null);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    // ---------- 判定助手 ----------

    [Fact]
    public void 角色名判定_admin大小写不敏感_普通角色不算管理员()
    {
        Assert.True(SystemRoles.IsAdmin(["admin"]));
        Assert.True(SystemRoles.IsAdmin(["Admin"]));
        Assert.False(SystemRoles.IsAdmin(["kb_manager", "user"]));
        Assert.False(SystemRoles.IsAdmin([]));
    }

    [Fact]
    public void 用户挂admin角色_即使列为false_也算系统管理员()
    {
        var user = new User { Id = 1, IsAdmin = false };
        user.Roles.Add(AdminRole());

        Assert.True(user.IsSystemAdmin());
        Assert.True(user.Roles.HasAdminRole());
    }

    // ---------- ClaimsPrincipal / JWT ----------

    [Fact]
    public void Claims_只有admin角色_也判定为系统管理员()
    {
        var p = Principal(new Claim(ClaimTypes.Role, SystemRoles.Admin));
        Assert.True(p.IsAdmin());
    }

    [Fact]
    public void Claims_兼容旧token的isadmin声明()
    {
        var p = Principal(new Claim("isadmin", "true"));
        Assert.True(p.IsAdmin());
    }

    [Fact]
    public void Claims_普通用户_不是系统管理员()
    {
        var p = Principal(new Claim(ClaimTypes.Role, "kb_manager"), new Claim("isadmin", "false"));
        Assert.False(p.IsAdmin());
    }

    [Fact]
    public void JWT_按admin角色签发isadmin声明_不依赖列值()
    {
        var user = new User
        {
            Id = 7, Username = "lee", DisplayName = "Lee",
            PasswordHash = "x", IsAdmin = false
        };
        user.Roles.Add(AdminRole());

        var token = new TokenService(JwtConfig()).GenerateToken(user);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("true", jwt.Claims.Single(c => c.Type == "isadmin").Value);
        // TokenService 直接写 ClaimTypes.Role，payload 里保留该 URI（与 RoleClaimType 默认值一致）
        Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.Role && c.Value == SystemRoles.Admin);
    }

    // ---------- 用户管理：请求里的 IsAdmin 一律忽略 ----------

    [Fact]
    public async Task 创建用户_挂admin角色_忽略请求的false_落库为管理员()
    {
        await using var db = TestDb.Create();
        db.Roles.Add(AdminRole());
        await db.SaveChangesAsync();
        var svc = new UserService(db);

        var dto = await svc.CreateAsync(new CreateUserRequest(
            "lee", "password123", "Lee", null, IsAdmin: false, [1]));

        Assert.True(dto.IsAdmin);
        var entity = await db.Users.Include(u => u.Roles).SingleAsync();
        Assert.True(entity.IsAdmin);
        Assert.True(entity.IsSystemAdmin());
    }

    [Fact]
    public async Task 创建用户_请求IsAdmin为true但无admin角色_落库为非管理员()
    {
        await using var db = TestDb.Create();
        var svc = new UserService(db);

        var dto = await svc.CreateAsync(new CreateUserRequest(
            "lee", "password123", "Lee", null, IsAdmin: true, []));

        Assert.False(dto.IsAdmin);
        Assert.False((await db.Users.SingleAsync()).IsAdmin);
    }

    [Fact]
    public async Task 更新用户_摘掉admin角色_管理员标志同步取消()
    {
        await using var db = TestDb.Create();
        db.Roles.Add(AdminRole());
        await db.SaveChangesAsync();
        var svc = new UserService(db);
        var created = await svc.CreateAsync(new CreateUserRequest(
            "lee", "password123", "Lee", null, false, [1]));
        Assert.True(created.IsAdmin);

        var updated = await svc.UpdateAsync(created.Id,
            new UpdateUserRequest("Lee2", null, true, IsAdmin: true, []));

        Assert.False(updated.IsAdmin);
        Assert.False((await db.Users.SingleAsync()).IsAdmin);
    }

    // ---------- 下游消费方 ----------

    [Fact]
    public async Task 登录响应_按admin角色返回管理员_与JWT一致()
    {
        await using var db = TestDb.Create();
        var role = AdminRole();
        db.Roles.Add(role);
        db.Users.Add(new User
        {
            Id = 1, Username = "lee", DisplayName = "Lee",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
            IsAdmin = false, Roles = [role]
        });
        await db.SaveChangesAsync();

        var svc = new AuthService(db, new TokenService(JwtConfig()), new NoLdap(),
            Options.Create(new LdapOptions()));
        var response = await svc.LoginAsync(new LoginRequest("lee", "password123"));

        Assert.True(response.User.IsAdmin);
        Assert.Contains(SystemRoles.Admin, response.User.Roles);
    }

    [Fact]
    public async Task 检索权限_admin角色用户返回null_不做知识库过滤()
    {
        await using var db = TestDb.Create();
        var role = AdminRole();
        db.Roles.Add(role);
        db.Users.Add(new User
        {
            Id = 1, Username = "lee", DisplayName = "Lee", PasswordHash = "x",
            IsAdmin = false, Roles = [role]
        });
        await db.SaveChangesAsync();

        var svc = new PermissionService(db, new KbAccessGuard(db, new FakeCurrentUser(1)));
        Assert.Null(await svc.GetAccessibleKbIdsAsync(1));
    }

    // ---------- EF 模型：外键配置 ----------

    [Fact]
    public void EF模型_KbPermission使用KbId外键_无KnowledgeBaseId影子列_删库级联()
    {
        using var db = TestDb.Create();
        var entityType = db.Model.FindEntityType(typeof(KbPermission))!;

        Assert.Null(entityType.FindProperty("KnowledgeBaseId"));

        var fk = entityType.GetForeignKeys()
            .Single(f => f.PrincipalEntityType.ClrType == typeof(KnowledgeBase));
        Assert.Equal("KbId", Assert.Single(fk.Properties).Name);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }
}
