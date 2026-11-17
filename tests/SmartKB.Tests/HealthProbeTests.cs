using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartKB.Api.Health;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>
/// 健康检查分级与脱敏：DB/存储故障 → unhealthy，LLM 抖动 → degraded（不误杀容器），
/// 非关系型 provider → skipped；ApiKey 绝不出现在响应 Detail 里。
/// </summary>
public class HealthProbeTests
{
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("模拟网络不可达"));
    }

    private sealed class FakeFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "smartkb-health-" + Guid.NewGuid().ToString("N"));

    private static HealthProbe NewProbe(
        SmartKbDbContext db, IConfiguration config, HttpClient? client = null,
        HealthCheckCache? cache = null) =>
        new(db, config, new FakeFactory(client ?? new HttpClient(new NoNetworkHandler())),
            cache ?? new HealthCheckCache(), NullLogger<HealthProbe>.Instance);

    [Fact]
    public async Task 内存Provider与未配置ApiKey_整体degraded_四项检查齐全()
    {
        using var db = TestDb.Create();
        var dir = TempDir();
        try
        {
            var config = Config(
                ("Llm:Mode", "Agnes"),
                ("Llm:Agnes:BaseUrl", "https://example.invalid/v1"),
                ("Llm:Agnes:ApiKey", ""),
                ("SmartKB:FileStoragePath", dir));

            var report = await NewProbe(db, config).CheckAsync();

            Assert.Equal("degraded", report.Status);
            Assert.Equal("ok", report.Db); // 兼容旧版 { status, db } 字段
            Assert.Equal(4, report.Checks.Count);
            Assert.Contains(report.Checks, c => c.Name == "database" && c.Status == "healthy");
            Assert.Contains(report.Checks, c => c.Name == "storage" && c.Status == "healthy");
            Assert.Contains(report.Checks, c => c.Name == "pgvector" && c.Status == "skipped");
            Assert.Contains(report.Checks, c => c.Name == "llm" && c.Status == "degraded");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LLM端点不可达_只降级不判死_且密钥不回显()
    {
        using var db = TestDb.Create();
        var dir = TempDir();
        try
        {
            var config = Config(
                ("Llm:Mode", "Cloud"),
                ("Llm:Cloud:BaseUrl", "https://example.invalid/v1"),
                ("Llm:Cloud:ApiKey", "SuperSecret-Key"),
                ("SmartKB:FileStoragePath", dir));

            var report = await NewProbe(db, config, new HttpClient(new NoNetworkHandler())).CheckAsync();

            Assert.Equal("degraded", report.Status); // 不是 unhealthy，避免编排器重启容器
            Assert.Equal("degraded", report.Checks.Single(c => c.Name == "llm").Status);
            var details = string.Join("|", report.Checks.Select(c => c.Detail ?? ""));
            Assert.DoesNotContain("SuperSecret-Key", details);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 数据库不可用_判为unhealthy且db字段为error()
    {
        var db = TestDb.Create();
        var dir = TempDir();
        try
        {
            db.Dispose(); // CanConnectAsync 抛异常

            var config = Config(
                ("Llm:Mode", "Agnes"),
                ("Llm:Agnes:ApiKey", ""),
                ("SmartKB:FileStoragePath", dir));

            var report = await NewProbe(db, config).CheckAsync();

            Assert.Equal("unhealthy", report.Status);
            Assert.Equal("error", report.Db);
            Assert.Equal("unhealthy", report.Checks.Single(c => c.Name == "database").Status);
            Assert.Equal("skipped", report.Checks.Single(c => c.Name == "pgvector").Status);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 缓存命中时_不重复探测外部LLM端点()
    {
        using var db = TestDb.Create();
        var dir = TempDir();
        try
        {
            var handler = new CountingHandler();
            var config = Config(
                ("Llm:Mode", "Cloud"),
                ("Llm:Cloud:BaseUrl", "https://example.invalid/v1"),
                ("Llm:Cloud:ApiKey", "key"),
                ("SmartKB:FileStoragePath", dir));
            var probe = NewProbe(db, config, new HttpClient(handler), new HealthCheckCache());

            var first = await probe.CheckAsync();
            var second = await probe.CheckAsync();

            Assert.Same(first, second);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
