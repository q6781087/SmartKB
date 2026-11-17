using System.Text.Json;
using SmartKB.AI.Tools;

namespace SmartKB.Tests;

/// <summary>
/// 配置化工具的"保存时校验"与纯函数：URL 域名固定、占位符声明、参数 DSL、JSON Schema。
/// 这些是防 LLM 越权打内网/外网的最后一道静态防线。
/// </summary>
public class AgentToolValidatorTests
{
    private static readonly IReadOnlyList<ToolParameter> OneParam =
        [new ToolParameter("device_no", "string", "设备编号", true, "path")];

    private static AgentToolDefinition Def(
        string url = "https://erp.internal/api/device/{device_no}",
        IReadOnlyList<ToolParameter>? parameters = null,
        string method = "GET",
        string? body = null,
        IReadOnlyList<string>? hosts = null) => new(
            1, "query_device", "设备状态", "按编号查设备状态",
            method, url, body, parameters ?? OneParam,
            TimeoutSeconds: 8, MaxResponseChars: 4000, AllowedHosts: hosts);

    [Fact]
    public void 合法配置通过校验()
    {
        AgentToolValidator.Validate(Def());
    }

    [Theory]
    [InlineData("query-device")]      // 含连字符
    [InlineData("1_tool")]            // 数字开头
    [InlineData("a")]                 // 太短
    public void 工具名不合规范被拒(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AgentToolValidator.Validate(Def() with { Name = name }));
        Assert.Contains("工具名", ex.Message);
    }

    [Fact]
    public void 相对地址与非法scheme被拒()
    {
        Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(Def("/api/device/{device_no}")));
        Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(Def("file:///etc/passwd")));
    }

    [Fact]
    public void URL中userinfo被拒()
    {
        Assert.Throws<ArgumentException>(() =>
            AgentToolValidator.Validate(Def("https://user:pwd@erp.internal/api/{device_no}")));
    }

    [Fact]
    public void 未声明的占位符被拒()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AgentToolValidator.Validate(Def("https://erp.internal/api/{device_no}?token={secret}")));
        Assert.Contains("secret", ex.Message);
    }

    [Fact]
    public void 主机白名单外的地址被拒()
    {
        // 同主机（含显式端口）通过
        AgentToolValidator.Validate(Def(hosts: ["erp.internal"]));
        AgentToolValidator.Validate(Def("https://erp.internal:8443/api/{device_no}", hosts: ["erp.internal"]));

        var blocked = Assert.Throws<ArgumentException>(() =>
            AgentToolValidator.Validate(Def("https://evil.example.com/api/{device_no}", hosts: ["erp.internal"])));
        Assert.Contains("白名单", blocked.Message);
    }

    [Fact]
    public void 参数类型与位置白名单生效()
    {
        var badType = Def() with
        {
            Parameters = [new ToolParameter("x", "object", "", true, "query")]
        };
        Assert.Contains("type", Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(badType)).Message);

        var badLocation = Def() with
        {
            Parameters = [new ToolParameter("x", "string", "", true, "header")]
        };
        Assert.Contains("location", Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(badLocation)).Message);
    }

    [Fact]
    public void GET工具不允许body参数与请求体模板()
    {
        var withBodyParam = Def() with
        {
            Parameters = [new ToolParameter("x", "string", "", true, "body")]
        };
        Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(withBodyParam));
        Assert.Throws<ArgumentException>(() =>
            AgentToolValidator.Validate(Def(body: "{\"x\":{device_no}}")));
    }

    [Fact]
    public void 鉴权头必须配环境变量名_且禁止改写Host()
    {
        var noEnv = Def() with { AuthHeaderName = "Authorization", AuthEnvVar = null };
        Assert.Contains("环境变量", Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(noEnv)).Message);

        var hostHeader = Def() with
        {
            AuthHeaderName = "Host", AuthEnvVar = "SOME_TOKEN"
        };
        Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(hostHeader));

        // 只填变量名会静默不带鉴权，必须拦住
        var envOnly = Def() with { AuthEnvVar = "SOME_TOKEN" };
        Assert.Contains("请求头名",
            Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(envOnly)).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void 超时范围钳制(int seconds) =>
        Assert.Throws<ArgumentException>(() => AgentToolValidator.Validate(Def() with { TimeoutSeconds = seconds }));

    [Fact]
    public void Schema来自配置且不泄露鉴权信息()
    {
        var schema = AgentToolValidator.BuildJsonSchema(Def() with
        {
            AuthHeaderName = "Authorization", AuthEnvVar = "ERP_TOKEN",
            Parameters = OneParam.Concat([
                new ToolParameter("year", "integer", "年份", false, "query")
            ]).ToArray()
        });

        var root = schema.GetProperty("properties");
        Assert.Equal("string", root.GetProperty("device_no").GetProperty("type").GetString());
        Assert.Equal("integer", root.GetProperty("year").GetProperty("type").GetString());
        Assert.Equal("device_no", schema.GetProperty("required")[0].GetString());
        Assert.Single(schema.GetProperty("required").EnumerateArray());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.DoesNotContain("ERP_TOKEN", schema.GetRawText());
    }

    [Fact]
    public void 参数值不改变目标主机_且被URL编码()
    {
        var values = new Dictionary<string, string>
        {
            ["device_no"] = "evil.example.com/../../admin?x=1&y=2"
        };
        var uri = HttpToolExecutor.BuildRequestUri(
            "https://erp.internal:8443/api/device/{device_no}?year={year}", values);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("erp.internal", uri.Host);
        Assert.Equal(8443, uri.Port);
        Assert.DoesNotContain("evil.example.com", uri.Host);
        Assert.Contains("evil.example.com%2F..%2F..%2Fadmin", uri.AbsolutePath + uri.Query);
        Assert.EndsWith("year=", uri.Query); // 未提供的可选参数替换为空串
    }

    [Fact]
    public void 请求体占位符替换为JSON字面量_字符串自动转义()
    {
        var def = Def(
            "https://erp.internal/api/query",
            [new ToolParameter("device_no", "string", "", true, "body"),
             new ToolParameter("deep", "boolean", "", true, "body")],
            method: "POST",
            body: "{\"deviceNo\":{device_no},\"deep\":{deep}}");

        var body = HttpToolExecutor.BuildJsonBody(def, new Dictionary<string, string>
        {
            ["device_no"] = "A\"B\\C",
            ["deep"] = "true"
        });

        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("A\"B\\C", doc.RootElement.GetProperty("deviceNo").GetString());
        Assert.True(doc.RootElement.GetProperty("deep").GetBoolean());
    }

    [Fact]
    public void 参数DSL解析与白名单主机解析()
    {
        var ps = AgentToolValidator.ParseParameters(
            "[{\"name\":\"a\",\"type\":\"integer\",\"required\":true,\"location\":\"query\",\"description\":\"年份\"}]");
        Assert.Single(ps);
        Assert.True(ps[0].Required);
        Assert.Equal("integer", ps[0].Type);

        Assert.ThrowsAny<JsonException>(() => AgentToolValidator.ParseParameters("[{bad"));
        Assert.Throws<ArgumentException>(() => AgentToolValidator.ParseParameters("{\"name\":\"a\"}"));

        var hosts = AgentToolRegistry.ParseAllowedHosts("erp.internal; crm.internal, erp.internal\n");
        Assert.Equal(2, hosts.Count);
        Assert.Empty(AgentToolRegistry.ParseAllowedHosts(null));
    }
}
