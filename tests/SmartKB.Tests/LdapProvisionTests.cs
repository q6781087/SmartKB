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
/// LDAP 可选登录（v1.6）：本地优先；LDAP 验证通过自动开通账号并挂默认角色；
/// 禁用账号即使 LDAP 通过也拒绝；LDAP 未命中按普通登录失败处理。
/// </summary>
public class LdapProvisionTests
{
    private sealed class FakeLdap(LdapUserInfo? result) : ILdapDirectory
    {
        public int Calls { get; private set; }

        public Task<LdapUserInfo?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static async Task<(SmartKbDbContext Db, AuthService Svc, FakeLdap Ldap)> SetupAsync(
        LdapUserInfo? ldapResult, string? defaultRole = "售后")
    {
        var db = TestDb.Create();
        db.Users.Add(new User
        {
            Id = 1, Username = "local", DisplayName = "本地用户",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("good-pw")
        });
        db.Roles.Add(new Role { Id = 5, Name = "售后" });
        await db.SaveChangesAsync();

        var ldap = new FakeLdap(ldapResult);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "SmartKB",
                ["Jwt:Audience"] = "SmartKB",
                ["Jwt:SecretKey"] = new string('k', 48),
                ["Jwt:ExpireMinutes"] = "720"
            }).Build();
        var svc = new AuthService(db, new TokenService(config), ldap, Options.Create(new LdapOptions
        {
            Enabled = true, Server = "ad.corp.local", BaseDn = "DC=corp,DC=local",
            DefaultRole = defaultRole
        }));
        return (db, svc, ldap);
    }

    [Fact]
    public async Task 本地密码正确_不触发LDAP()
    {
        var (db, svc, ldap) = await SetupAsync(new LdapUserInfo("local", "x"));
        var resp = await svc.LoginAsync(new LoginRequest("local", "good-pw"));
        Assert.Equal("本地用户", resp.User.DisplayName);
        Assert.Equal(0, ldap.Calls);
    }

    [Fact]
    public async Task 本地密码错误_LDAP通过_用既有账号登录()
    {
        var (db, svc, _) = await SetupAsync(new LdapUserInfo("local", "本地用户"));
        var resp = await svc.LoginAsync(new LoginRequest("local", "ldap-pw"));
        Assert.Equal(1, resp.User.Id);
        Assert.Equal(1, await db.Users.CountAsync()); // 不重复建档
    }

    [Fact]
    public async Task 陌生用户_LDAP通过_自动开通并挂默认角色_本地密码不可登录()
    {
        var (db, svc, _) = await SetupAsync(new LdapUserInfo("zhang.san", "张三"));
        var resp = await svc.LoginAsync(new LoginRequest("zhang.san", "ad-pw"));

        Assert.Equal("张三", resp.User.DisplayName);
        var user = await db.Users.Include(u => u.Roles).SingleAsync(u => u.Username == "zhang.san");
        Assert.False(user.IsAdmin);
        Assert.Equal("售后", user.Roles.Single().Name);
        // 随机密码哈希：本地密码路径不可登录
        Assert.False(BCrypt.Net.BCrypt.Verify("ad-pw", user.PasswordHash));
    }

    [Fact]
    public async Task 陌生用户_LDAP未命中_按登录失败处理()
    {
        var (db, svc, ldap) = await SetupAsync(null);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.LoginAsync(new LoginRequest("ghost", "pw")));
        Assert.Equal(0, await db.Users.CountAsync(u => u.Username == "ghost"));
    }

    [Fact]
    public async Task 已禁用账号_LDAP通过也拒绝()
    {
        var (db, svc, _) = await SetupAsync(new LdapUserInfo("local", "本地用户"));
        var user = await db.Users.SingleAsync(u => u.Id == 1);
        user.IsActive = false;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.LoginAsync(new LoginRequest("local", "ldap-pw")));
    }

    [Fact]
    public void 过滤器值_转义RFC4515特殊字符_防LDAP注入()
    {
        Assert.Equal(@"a\5cb\28c\29d\2ae", LdapDirectoryService.EscapeFilterValue(@"a\b(c)d*e"));
        Assert.Equal(@"x\00y", LdapDirectoryService.EscapeFilterValue("x\0y"));
    }
}
