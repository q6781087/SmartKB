using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SmartKB.AI.Tools;

namespace SmartKB.Tests;

/// <summary>
/// 执行器安全边界回归：host 固定、密钥只从环境变量取且不回显、响应截断、失败降级。
/// 全部走内存 HttpMessageHandler，不需要真实网络或数据库。
/// </summary>
public class HttpToolExecutorTests
{
    private sealed record Captured(string Method, string Url, string Headers, string? Body);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        TimeSpan? delay = null) : HttpMessageHandler
    {
        public Captured? Last { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = new Captured(
                request.Method.Method,
                request.RequestUri!.ToString(),
                string.Join(";", request.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}")),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            if (delay is { } d) await Task.Delay(d, cancellationToken);
            return respond(request);
        }
    }

    private sealed class FakeFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static AgentToolDefinition Def(
        string? authHeader = null, string? authEnv = null, string? prefix = null,
        int maxChars = 4000, int timeout = 8, string method = "GET",
        IReadOnlyList<ToolParameter>? ps = null, string? body = null) => new(
            1, "query_device", "设备状态", "按编号查设备状态",
            method,
            method == "GET"
                ? "https://erp.internal/api/device/{device_no}?year={year}"
                : "https://erp.internal/api/query",
            body,
            ps ?? [new ToolParameter("device_no", "string", "设备编号", true, "path"),
                   new ToolParameter("year", "string", "年份", false, "query")],
            authHeader, authEnv, prefix, timeout, maxChars);

    private static (HttpToolExecutor Executor, RecordingHandler Handler) New(
        Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan? delay = null)
    {
        var handler = new RecordingHandler(respond, delay);
        var executor = new HttpToolExecutor(
            new FakeFactory(new HttpClient(handler)), NullLogger<HttpToolExecutor>.Instance);
        return (executor, handler);
    }

    private static HttpResponseMessage Json(string text, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public async Task 参数被URL编码且主机不被改写()
    {
        var (executor, handler) = New(_ => Json("{\"status\":\"运行\"}"));
        var result = await executor.InvokeDetailedAsync(Def(), Args(
            ("device_no", "evil.example.com/../../admin?x=1"), ("year", "2025")));

        Assert.True(result.Success);
        Assert.Contains("运行", result.Output);
        Assert.StartsWith("https://erp.internal/", handler.Last!.Url);
        Assert.DoesNotContain("evil.example.com", new Uri(handler.Last.Url).Host);
        Assert.Contains("year=2025", handler.Last.Url);
    }

    [Fact]
    public async Task 缺少必填参数直接返回可读错误且不发请求()
    {
        var (executor, handler) = New(_ => Json("{}"));
        var result = await executor.InvokeDetailedAsync(Def(), Args());

        Assert.False(result.Success);
        Assert.Contains("device_no", result.Output);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task 密钥取自环境变量_请求头正确_但绝不回显到结果()
    {
        const string env = "SMARTKB_TEST_TOOL_TOKEN";
        const string secret = "super-secret-42";
        Environment.SetEnvironmentVariable(env, secret);
        try
        {
            var (executor, handler) = New(_ => Json("{\"ok\":true}"));
            var result = await executor.InvokeDetailedAsync(
                Def(authHeader: "Authorization", authEnv: env, prefix: "Bearer "),
                Args(("device_no", "D-1")));

            Assert.True(result.Success);
            Assert.Contains($"Bearer {secret}", handler.Last!.Headers);
            Assert.DoesNotContain(secret, result.Output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, null);
        }
    }

    [Fact]
    public async Task 环境变量未配置时失败且不抛堆栈()
    {
        const string env = "SMARTKB_TEST_MISSING_TOKEN";
        Environment.SetEnvironmentVariable(env, null);
        var (executor, handler) = New(_ => Json("{}"));
        var result = await executor.InvokeDetailedAsync(
            Def(authHeader: "X-Api-Key", authEnv: env), Args(("device_no", "D-1")));

        Assert.False(result.Success);
        Assert.Contains(env, result.Output);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task 响应按字符截断()
    {
        var (executor, _) = New(_ => Json(new string('x', 20000)));
        var result = await executor.InvokeDetailedAsync(
            Def(maxChars: 500), Args(("device_no", "D-1")));

        Assert.True(result.Success);
        Assert.Contains("响应已截断", result.Output);
        Assert.True(result.Output.Length < 600);
    }

    [Fact]
    public async Task 非文本内容与非2xx都降级为可读文本()
    {
        var binary = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") }
            }
        };
        var (e1, _) = New(_ => binary);
        var r1 = await e1.InvokeDetailedAsync(Def(), Args(("device_no", "D-1")));
        Assert.Contains("非文本内容", r1.Output);

        var (e2, _) = New(_ => Json("{\"err\":\"boom\"}", HttpStatusCode.InternalServerError));
        var r2 = await e2.InvokeDetailedAsync(Def(), Args(("device_no", "D-1")));
        Assert.False(r2.Success);
        Assert.Contains("500", r2.Output);
    }

    [Fact]
    public async Task 重定向不跟随()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.Found);
        resp.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
        var (executor, _) = New(_ => resp);
        var result = await executor.InvokeDetailedAsync(Def(), Args(("device_no", "D-1")));

        Assert.False(result.Success);
        Assert.Contains("不跟随", result.Output);
    }

    [Fact]
    public async Task 超时降级为文本_不影响主流程()
    {
        var (executor, _) = New(_ => Json("{}"), TimeSpan.FromSeconds(5));
        var result = await executor.InvokeDetailedAsync(
            Def(timeout: 1), Args(("device_no", "D-1")));

        Assert.False(result.Success);
        Assert.Contains("超时", result.Output);
    }

    [Fact]
    public async Task POST请求体按JSON字面量替换()
    {
        var def = Def(
            method: "POST",
            ps: [new ToolParameter("device_no", "string", "", true, "body"),
                 new ToolParameter("year", "integer", "", true, "body")],
            body: "{\"deviceNo\":{device_no},\"year\":{year}}");
        var (executor, handler) = New(_ => Json("{\"ok\":true}"));
        var result = await executor.InvokeDetailedAsync(def, Args(
            ("device_no", "A\"B"), ("year", 2025)));

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(handler.Last!.Body!);
        Assert.Equal("A\"B", doc.RootElement.GetProperty("deviceNo").GetString());
        Assert.Equal(2025, doc.RootElement.GetProperty("year").GetInt32());
        Assert.Equal("POST", handler.Last.Method);
    }

    // ---------- 动态 AIFunction ----------

    private sealed class FakeExecutor(string output) : IHttpToolExecutor
    {
        public int Calls { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastArgs { get; private set; }

        public Task<string> InvokeAsync(AgentToolDefinition d,
            IReadOnlyDictionary<string, object?> a, CancellationToken ct = default)
        {
            Calls++;
            LastArgs = a;
            return Task.FromResult(output);
        }

        public Task<HttpToolResult> InvokeDetailedAsync(AgentToolDefinition d,
            IReadOnlyDictionary<string, object?> a, CancellationToken ct = default)
        {
            Calls++;
            LastArgs = a;
            return Task.FromResult(new HttpToolResult(true, output));
        }
    }

    [Fact]
    public async Task 动态函数的Schema来自配置_调用计数生效()
    {
        var executor = new FakeExecutor("设备正常");
        var calls = 0;
        var fn = new ConfiguredHttpToolFunction(Def(), executor, () => calls++);

        Assert.Equal("query_device", fn.Name);
        Assert.Equal("按编号查设备状态", fn.Description);
        Assert.Equal("object", fn.JsonSchema.GetProperty("type").GetString());
        Assert.True(fn.JsonSchema.GetProperty("properties").TryGetProperty("device_no", out _));

        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["device_no"] = "D-1" });
        var result = await fn.InvokeAsync(args);

        Assert.Equal("设备正常", result?.ToString());
        Assert.Equal(1, calls);
        Assert.Equal(1, executor.Calls);
        Assert.Equal("D-1", executor.LastArgs!["device_no"]?.ToString());
    }
}
