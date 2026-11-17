using SmartKB.AI.Tools;
using SmartKB.Domain.Entities;

namespace SmartKB.Tests;

/// <summary>工具知识库范围（v1.6）：空 = 不限制；解析失败直接拒绝，坏配置不静默放开</summary>
public class ToolKbScopeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("[]")]
    public void 空配置_解析为不限范围(string? json)
    {
        Assert.Empty(AgentToolValidator.ParseAllowedKbIds(json));
    }

    [Fact]
    public void 合法数组_解析去重()
    {
        var ids = AgentToolValidator.ParseAllowedKbIds("[1, 3, 3, 7]");
        Assert.Equal([1L, 3L, 7L], ids);
    }

    [Theory]
    [InlineData("[0]")]
    [InlineData("[-1]")]
    [InlineData("[1.5]")]
    [InlineData("[\"1\"]")]
    [InlineData("{}")]
    [InlineData("[1,")]
    public void 非法配置_抛可读异常(string json)
    {
        Assert.ThrowsAny<Exception>(() => AgentToolValidator.ParseAllowedKbIds(json));
    }

    [Fact]
    public void 超过上限_校验拒绝()
    {
        var tooMany = Enumerable.Range(1, AgentToolValidator.MaxAllowedKbIds + 1)
            .Select(i => (long)i).ToList();
        var def = Def(tooMany);
        var ex = Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(def));
        Assert.Contains("知识库范围", ex.Message);
    }

    [Fact]
    public void 实体映射_带范围字段()
    {
        var entity = new AgentTool
        {
            Id = 9, Name = "query_erp", DisplayName = "ERP", Description = "查 ERP",
            HttpMethod = "GET", UrlTemplate = "https://erp.internal/api/x",
            ParametersJson = "[]", AllowedKbIdsJson = "[2,4]"
        };
        var def = AgentToolRegistry.FromEntity(entity);
        Assert.Equal([2L, 4L], def.AllowedKbIds);
    }

    private static AgentToolDefinition Def(IReadOnlyList<long> kbIds) => new(
        1, "query_erp", "ERP", "查 ERP", "GET", "https://erp.internal/api/x", null,
        [], AllowedKbIds: kbIds);
}
