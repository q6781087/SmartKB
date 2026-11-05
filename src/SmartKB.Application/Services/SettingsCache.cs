using SmartKB.AI.Configuration;

namespace SmartKB.Application.Services;

/// <summary>
/// 运行时配置缓存（Singleton）。
/// SettingsService 依赖 Scoped 的 DbContext，不能自己做成单例，
/// 因此把"10 秒 TTL 缓存 + 版本号"独立出来，两者生命周期解耦。
/// 多实例部署下各实例最多有 10 秒配置延迟，后台保存后本实例立即失效。
/// </summary>
public class SettingsCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private RagSettings? _value;
    private DateTime _cachedAt;
    private int _version;

    public int Version { get { lock (_sync) return _version; } }

    public RagSettings? Get()
    {
        lock (_sync)
            return _value is not null && DateTime.UtcNow - _cachedAt < Ttl ? _value : null;
    }

    public RagSettings Set(RagSettings settings)
    {
        lock (_sync)
        {
            _value = settings with { Version = _version };
            _cachedAt = DateTime.UtcNow;
            return _value;
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _value = null;
            _version++;
        }
    }
}
