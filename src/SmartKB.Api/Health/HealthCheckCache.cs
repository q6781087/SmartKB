namespace SmartKB.Api.Health;

/// <summary>
/// 健康检查结果短期缓存（Singleton，10 秒 TTL）。
/// /health 常被 Docker/网关每 5 秒探一次，若每次都去请求 LLM /models，
/// 会被厂商限流甚至产生额外费用；缓存后同一窗口内复用结果，故障最多延迟 10 秒反映。
/// 实例由 DI 注入（非静态），单测之间互不串味。
/// </summary>
public class HealthCheckCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private HealthReport? _cached;
    private DateTime _cachedAt;

    public HealthReport? Get()
    {
        lock (_sync)
            return _cached is not null && DateTime.UtcNow - _cachedAt < Ttl ? _cached : null;
    }

    public void Set(HealthReport report)
    {
        lock (_sync)
        {
            _cached = report;
            _cachedAt = DateTime.UtcNow;
        }
    }
}
