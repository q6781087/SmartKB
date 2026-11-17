using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartKB.AI.Tools;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>后台管理服务：保存校验、唯一名、白名单、启停配额、缓存失效与试运行闭环</summary>
public class AgentToolServiceTests
{
    private sealed class FakeExecutor : IHttpToolExecutor
    {
        public Task<string> InvokeAsync(AgentToolDefinition d,
            IReadOnlyDictionary<string, object?> a, CancellationToken ct = default) =>
            Task.FromResult("模拟上游返回");

        public Task<HttpToolResult> InvokeDetailedAsync(AgentToolDefinition d,
            IReadOnlyDictionary<string, object?> a, CancellationToken ct = default) =>
            Task.FromResult(new HttpToolResult(true, "模拟上游返回"));
    }

    private static IConfiguration Config(string? allowedHosts = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tools:AllowedHosts"] = allowedHosts
            })
            .Build();

    private static (SmartKbDbContext Db, AgentToolService Service,
        IAgentToolRegistry Registry, AgentToolCache Cache) New(string? allowedHosts = null)
    {
        var db = TestDb.Create();
        var config = Config(allowedHosts);
        var cache = new AgentToolCache();
        IAgentToolRegistry registry = new AgentToolRegistry(
            db, cache, config, NullLogger<AgentToolRegistry>.Instance);
        var service = new AgentToolService(db, registry, new FakeExecutor(), config);
        return (db, service, registry, cache);
    }

    private static AgentToolUpsertDto Dto(
        string name = "query_device",
        string url = "https://erp.internal/api/device/{device_no}",
        bool enabled = true) => new(
        name, "设备状态", "按设备编号查询当前状态", "GET", url, null,
        "[{\"name\":\"device_no\",\"type\":\"string\",\"description\":\"设备编号\",\"required\":true,\"location\":\"path\"}]",
        null, null, null, 8, 4000, enabled, 0);

    [Fact]
    public async Task 非法配置在保存时被拦住()
    {
        var scope = New();
        using var db = scope.Db;
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.CreateAsync(Dto(url: "/relative/api/{device_no}")));

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.CreateAsync(Dto(name: "search_knowledge")));

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.CreateAsync(Dto(url: "https://erp.internal/api/{undeclared}")));
    }

    [Fact]
    public async Task 主机白名单由部署配置强制()
    {
        var scope = New("erp.internal");
        using var db = scope.Db;
        var ok = await scope.Service.CreateAsync(Dto());
        Assert.Equal("query_device", ok.Name);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.CreateAsync(Dto(name: "other_tool", url: "https://evil.example.com/api/{device_no}")));
    }

    [Fact]
    public async Task 同名工具不可重复()
    {
        var scope = New();
        using var db = scope.Db;
        await scope.Service.CreateAsync(Dto());
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.CreateAsync(Dto(name: "query_device")));
    }

    [Fact]
    public async Task 保存后注册表立即生效_停用删除均失效缓存()
    {
        var scope = New();
        using var db = scope.Db;
        var created = await scope.Service.CreateAsync(Dto());

        var loaded = await scope.Registry.GetEnabledAsync();
        Assert.Single(loaded);
        Assert.Equal("query_device", loaded[0].Name);

        await scope.Service.SetEnabledAsync(created.Id, false);
        Assert.Empty(await scope.Registry.GetEnabledAsync());

        await scope.Service.SetEnabledAsync(created.Id, true);
        Assert.Single(await scope.Registry.GetEnabledAsync());

        await scope.Service.DeleteAsync(created.Id);
        Assert.Empty(await scope.Registry.GetEnabledAsync());
    }

    [Fact]
    public async Task 坏配置直接改库也只跳过该工具_不影响其他工具()
    {
        var scope = New();
        using var db = scope.Db;
        scope.Db.AgentTools.Add(new AgentTool
        {
            Name = "bad_tool", DisplayName = "坏的", Description = "坏配置",
            HttpMethod = "GET", UrlTemplate = "https://erp.internal/api/{missing}",
            ParametersJson = "[]", Enabled = true
        });
        scope.Db.AgentTools.Add(new AgentTool
        {
            Name = "good_tool", DisplayName = "好的", Description = "正常配置",
            HttpMethod = "GET", UrlTemplate = "https://erp.internal/api/ping",
            ParametersJson = "[]", Enabled = true
        });
        await scope.Db.SaveChangesAsync();

        var loaded = await scope.Registry.GetEnabledAsync();
        Assert.Single(loaded);
        Assert.Equal("good_tool", loaded[0].Name);
    }

    [Fact]
    public async Task 启用数量有上限()
    {
        var scope = New();
        using var db = scope.Db;
        for (var i = 0; i < AgentToolRegistry.MaxEnabledTools; i++)
            await scope.Service.CreateAsync(Dto(name: $"tool_{i}"));

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            scope.Service.CreateAsync(Dto(name: "one_too_many")));
    }

    [Fact]
    public async Task 试运行返回上游结果且能回读编辑()
    {
        var scope = New();
        using var db = scope.Db;
        var created = await scope.Service.CreateAsync(Dto());

        var args = new Dictionary<string, JsonElement>
        {
            ["device_no"] = JsonSerializer.SerializeToElement("D-1")
        };
        var result = await scope.Service.TestAsync(new AgentToolTestDto(created.Id, args));
        Assert.True(result.Success);
        Assert.Equal("模拟上游返回", result.Output);

        var updated = await scope.Service.UpdateAsync(created.Id, Dto() with
        {
            DisplayName = "新名称", MaxResponseChars = 2000
        });
        Assert.Equal("新名称", updated.DisplayName);
        Assert.Equal(2000, updated.MaxResponseChars);
    }

    [Fact]
    public async Task 密钥状态只显示是否已配置_不回显值()
    {
        const string env = "SMARTKB_TEST_SERVICE_TOKEN";
        Environment.SetEnvironmentVariable(env, "plain-secret-should-not-leak");
        try
        {
            var scope = New();
        using var db = scope.Db;
            var dto = Dto() with { AuthHeaderName = "X-Api-Key", AuthEnvVar = env };
            var created = await scope.Service.CreateAsync(dto);

            Assert.True(created.SecretConfigured);
            Assert.DoesNotContain("plain-secret-should-not-leak", JsonSerializer.Serialize(created));
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }
}
