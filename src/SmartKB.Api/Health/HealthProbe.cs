using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SmartKB.Infrastructure;

namespace SmartKB.Api.Health;

/// <summary>单分项检查结果。Status: healthy / degraded / unhealthy / skipped</summary>
public record HealthComponent(string Name, string Status, int LatencyMs, string? Detail);

/// <summary>
/// 健康检查报告。保留 Db 字段兼容旧版 { status, db } 响应；
/// 新增 Checks 分项与整体健康等级，供 Docker/网关探活与运维排障。
/// </summary>
public record HealthReport(
    string Status, string Db, DateTime CheckedAt, IReadOnlyList<HealthComponent> Checks);

public interface IHealthProbe
{
    Task<HealthReport> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// 分项探活：数据库 / pgvector 扩展与索引 / 文件存储可写 / LLM 端点连通性。
///
/// 分级原则（直接影响容器会不会被误杀）：
/// - database、storage 不可用 → unhealthy（HTTP 503），服务确实不能工作；
/// - pgvector 索引缺失、LLM 端点不可达或凭据错 → degraded（HTTP 200），
///   LLM 临时抖动不应触发编排器重启容器；
/// - 非关系型 provider（单测）→ skipped，不参与评级。
///
/// 同一个 Scoped DbContext 不能被多个并行 Task 共用（EF 非线程安全），
/// 因此 database 与 pgvector 合并在一个串行任务里，其余两项并行。
/// 密钥只用于发请求，绝不回显到 Detail。
/// </summary>
public class HealthProbe(
    SmartKbDbContext db,
    IConfiguration configuration,
    IHttpClientFactory httpFactory,
    HealthCheckCache cache,
    ILogger<HealthProbe> logger) : IHealthProbe
{
    private const string Healthy = "healthy";
    private const string Degraded = "degraded";
    private const string Unhealthy = "unhealthy";
    private const string Skipped = "skipped";

    public async Task<HealthReport> CheckAsync(CancellationToken ct = default)
    {
        // 高频探活复用 10 秒内的结果，避免每 5 秒打一次 LLM /models
        if (cache.Get() is { } cached) return cached;

        var parts = await Task.WhenAll(
            CheckDataLayerAsync(ct),
            CheckStorageAsync(ct),
            CheckLlmAsync(ct));

        var checks = parts.SelectMany(x => x).ToList();
        var dbStatus = checks.First(c => c.Name == "database").Status;
        var status = checks.Any(c => c.Status == Unhealthy) ? Unhealthy
            : checks.Any(c => c.Status == Degraded) ? Degraded
            : Healthy;

        var report = new HealthReport(
            status, dbStatus == Healthy ? "ok" : "error", DateTime.UtcNow, checks);
        cache.Set(report);
        return report;
    }

    /// <summary>数据库 + 向量能力，串行共用一个 DbContext</summary>
    private async Task<List<HealthComponent>> CheckDataLayerAsync(CancellationToken ct)
    {
        var result = new List<HealthComponent>();
        var sw = Stopwatch.StartNew();

        bool connected;
        try
        {
            connected = await db.Database.CanConnectAsync(ct);
            result.Add(new HealthComponent("database", connected ? Healthy : Unhealthy,
                (int)sw.ElapsedMilliseconds, connected ? null : "无法连接数据库"));
        }
        catch (Exception ex)
        {
            result.Add(Fail("database", sw, ex));
            connected = false;
        }

        if (!connected || !db.Database.IsRelational())
        {
            result.Add(new HealthComponent("pgvector", Skipped, 0,
                connected ? "非关系型 provider（测试环境）" : "数据库不可用，跳过检查"));
            return result;
        }

        sw.Restart();
        try
        {
            var conn = db.Database.GetDbConnection();
            var closeAfter = conn.State != System.Data.ConnectionState.Open;
            if (closeAfter) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT
                      EXISTS(SELECT 1 FROM pg_extension WHERE extname = 'vector') AS has_ext,
                      EXISTS(SELECT 1 FROM pg_indexes WHERE tablename = 'kb_chunks'
                             AND indexdef ILIKE '%hnsw%') AS has_hnsw,
                      EXISTS(SELECT 1 FROM pg_indexes
                             WHERE indexname = 'IX_kb_chunks_Content_Fts') AS has_fts
                    """;

                bool hasExt = false, hasHnsw = false, hasFts = false;
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        hasExt = reader.GetBoolean(0);
                        hasHnsw = reader.GetBoolean(1);
                        hasFts = reader.GetBoolean(2);
                    }
                }

                var missing = new List<string>();
                if (!hasExt) missing.Add("vector 扩展");
                if (!hasHnsw) missing.Add("HNSW 向量索引");
                var detail = $"vector扩展={(hasExt ? "有" : "无")}、HNSW={(hasHnsw ? "有" : "无")}、" +
                             $"全文GIN索引={(hasFts ? "有" : "无")}";
                result.Add(new HealthComponent("pgvector",
                    missing.Count > 0 ? Degraded : Healthy,
                    (int)sw.ElapsedMilliseconds,
                    missing.Count > 0 ? "缺失：" + string.Join("、", missing) : detail));
            }
            finally
            {
                if (closeAfter) await conn.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            result.Add(Fail("pgvector", sw, ex, Degraded));
        }

        return result;
    }

    private async Task<List<HealthComponent>> CheckStorageAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // 与 DocumentService 保持同一路径解析规则（相对工作目录）
            var root = configuration["SmartKB:FileStoragePath"] ?? "AppData/files";
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".health-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);

            return [new HealthComponent("storage", Healthy, (int)sw.ElapsedMilliseconds,
                Path.GetFullPath(root))];
        }
        catch (Exception ex)
        {
            return [Fail("storage", sw, ex)];
        }
    }

    private async Task<List<HealthComponent>> CheckLlmAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var mode = configuration["Llm:Mode"] ?? "Agnes";
        var local = mode.Equals("Local", StringComparison.OrdinalIgnoreCase);
        var agnes = mode.Equals("Agnes", StringComparison.OrdinalIgnoreCase);

        var baseUrl = local
            ? configuration["Llm:Local:BaseUrl"] ?? "http://localhost:11434"
            : agnes
                ? configuration["Llm:Agnes:BaseUrl"] ?? "https://apihub.agnes-ai.com/v1"
                : configuration["Llm:Cloud:BaseUrl"] ?? "https://open.bigmodel.cn/api/paas/v4";
        var apiKey = agnes
            ? configuration["Llm:Agnes:ApiKey"] ?? ""
            : configuration["Llm:Cloud:ApiKey"] ?? "";
        var path = local ? "api/tags" : "models"; // Ollama 用原生 /api/tags， OpenAI 兼容端点用 /models

        if (!local && string.IsNullOrWhiteSpace(apiKey))
            return [new HealthComponent("llm", Degraded, 0, $"模式 {mode} 未配置 ApiKey，无法发起探活")];

        try
        {
            var client = httpFactory.CreateClient("health-llm");
            var baseUri = new Uri(baseUrl.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.SendAsync(request, timeout.Token);
            var code = (int)response.StatusCode;

            // 只要不是网络错误 / 5xx，就说明端点可达；404 多为厂商不提供 /models，不算故障
            if (code >= 500)
                return [new HealthComponent("llm", Degraded, (int)sw.ElapsedMilliseconds,
                    $"端点返回 HTTP {code}（{mode}）")];
            if (code is 401 or 403)
                return [new HealthComponent("llm", Degraded, (int)sw.ElapsedMilliseconds,
                    $"端点可达但凭据被拒绝（HTTP {code}，{mode}）")];

            return [new HealthComponent("llm", Healthy, (int)sw.ElapsedMilliseconds,
                $"{mode} 端点可达（HTTP {code}）")];
        }
        catch (Exception ex)
        {
            // 超时/网络不可达属于 degraded，不让编排器因此重启容器
            return [Fail("llm", sw, ex, Degraded)];
        }
    }

    private HealthComponent Fail(string name, Stopwatch sw, Exception ex, string status = Unhealthy)
    {
        logger.LogWarning(ex, "健康检查分项 {Name} 失败", name);
        var message = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
        return new HealthComponent(name, status, (int)sw.ElapsedMilliseconds, message);
    }
}
