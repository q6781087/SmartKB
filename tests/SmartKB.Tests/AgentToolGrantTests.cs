using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartKB.AI.Tools;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>工具调用白名单：运行时过滤、角色继承、管理员短路、缓存失效与管理闭环</summary>
public class AgentToolGrantTests
{
    private sealed class FakeExecutor : IHttpToolExecutor
    {
        public Task<string> InvokeAsync(AgentToolDefinition d,
            IReadOnlyDictionary<string, object?> a, CancellationToken ct = default) =>
            Task.FromResult("ok");

        public Task<HttpToolResult> InvokeDetailedAsync(AgentToolDefinition d,
            IReadOnlyDictionary<string, object?> a, CancellationToken ct = default) =>
            Task.FromResult(new HttpToolResult(true, "ok"));
    }

    private static (SmartKbDbContext Db, AgentToolService Service, IAgentToolRegistry Registry) New()
    {
        var db = TestDb.Create();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tools:AllowedHosts"] = null })
            .Build();
        IAgentToolRegistry registry = new AgentToolRegistry(
            db, new AgentToolCache(), config, NullLogger<AgentToolRegistry>.Instance);
        var service = new AgentToolService(db, registry, new FakeExecutor(), config);
        return (db, service, registry);
    }

    private static AgentToolUpsertDto Dto(string name) => new(
        name, "显示名", "按条件查询业务系统", "GET",
        "https://erp.internal/api/ping", null, "[]",
        null, null, null, 8, 4000, true, 0);

    private static (long A, long B, long C, long RoleId) SeedPrincipals(SmartKbDbContext db)
    {
        var role = new Role { Name = "ops" };
        var a = new User { Username = "a", DisplayName = "用户A", PasswordHash = "x" };
        var b = new User { Username = "b", DisplayName = "用户B", PasswordHash = "x" };
        var c = new User
        {
            Username = "c", DisplayName = "用户C", PasswordHash = "x",
            Roles = new List<Role> { role }
        };
        db.AddRange(a, b, c, role);
        db.SaveChanges();
        return (a.Id, b.Id, c.Id, role.Id);
    }

    [Fact]
    public async Task 未设限工具公开_设限工具仅用户白名单可见_管理员不受限()
    {
        var scope = New();
        using var db = scope.Db;
        var p = SeedPrincipals(db);
        await scope.Service.CreateAsync(Dto("open_tool"));
        var limited = await scope.Service.CreateAsync(Dto("limited_tool"));

        await scope.Service.SetGrantsAsync(limited.Id, new SetToolGrantsDto([p.A], []));

        var forB = await scope.Registry.GetEnabledForAsync(new ToolCaller(p.B, false));
        Assert.Single(forB);
        Assert.Equal("open_tool", forB[0].Name);

        var forA = await scope.Registry.GetEnabledForAsync(new ToolCaller(p.A, false));
        Assert.Equal(2, forA.Count);

        var forAdmin = await scope.Registry.GetEnabledForAsync(new ToolCaller(999999, true));
        Assert.Equal(2, forAdmin.Count);
    }

    [Fact]
    public async Task 角色白名单对用户所属角色生效()
    {
        var scope = New();
        using var db = scope.Db;
        var p = SeedPrincipals(db);
        await scope.Service.CreateAsync(Dto("open_tool"));
        var limited = await scope.Service.CreateAsync(Dto("limited_tool"));
        await scope.Service.SetGrantsAsync(limited.Id, new SetToolGrantsDto([], [p.RoleId]));

        var forC = await scope.Registry.GetEnabledForAsync(new ToolCaller(p.C, false));
        Assert.Equal(2, forC.Count);

        var forB = await scope.Registry.GetEnabledForAsync(new ToolCaller(p.B, false));
        Assert.Single(forB);
    }

    [Fact]
    public async Task 授权变更立即失效注册表缓存()
    {
        var scope = New();
        using var db = scope.Db;
        var p = SeedPrincipals(db);
        await scope.Service.CreateAsync(Dto("open_tool"));
        var limited = await scope.Service.CreateAsync(Dto("limited_tool"));

        // 先查一次让它进缓存
        Assert.Equal(2, (await scope.Registry.GetEnabledForAsync(new ToolCaller(p.B, false))).Count);

        await scope.Service.SetGrantsAsync(limited.Id, new SetToolGrantsDto([p.A], []));
        Assert.Single(await scope.Registry.GetEnabledForAsync(new ToolCaller(p.B, false)));
    }

    [Fact]
    public async Task 授权管理闭环_覆盖_恢复公开_受限标记()
    {
        var scope = New();
        using var db = scope.Db;
        var p = SeedPrincipals(db);
        var tool = await scope.Service.CreateAsync(Dto("open_tool"));

        var initial = await scope.Service.GetGrantsAsync(tool.Id);
        Assert.Empty(initial.UserIds);
        Assert.Empty(initial.RoleIds);
        Assert.NotEmpty(initial.AvailableUsers);
        Assert.NotEmpty(initial.AvailableRoles);

        var saved = await scope.Service.SetGrantsAsync(
            tool.Id, new SetToolGrantsDto([p.A], [p.RoleId]));
        Assert.Contains(p.A, saved.UserIds);
        Assert.Contains(p.RoleId, saved.RoleIds);

        var list = await scope.Service.ListAsync();
        Assert.True(list.Single(x => x.Id == tool.Id).Restricted);

        var cleared = await scope.Service.SetGrantsAsync(tool.Id, new SetToolGrantsDto([], []));
        Assert.Empty(cleared.UserIds);
        Assert.Empty(cleared.RoleIds);
        Assert.False((await scope.Service.ListAsync()).Single(x => x.Id == tool.Id).Restricted);
    }

    [Fact]
    public async Task 授权到不存在的用户或角色直接拒绝()
    {
        var scope = New();
        using var db = scope.Db;
        var tool = await scope.Service.CreateAsync(Dto("open_tool"));

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.SetGrantsAsync(tool.Id, new SetToolGrantsDto([987654], [])));
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.SetGrantsAsync(tool.Id, new SetToolGrantsDto([], [987654])));
        Assert.Empty((await scope.Service.GetGrantsAsync(tool.Id)).UserIds);
    }
}
