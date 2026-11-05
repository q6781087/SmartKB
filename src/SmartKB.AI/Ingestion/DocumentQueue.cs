using System.Threading.Channels;

namespace SmartKB.AI.Ingestion;

/// <summary>
/// 文档处理队列抽象（P2 多实例支持）。
/// Memory：进程内 Channel，单实例默认；Database：复用 kb_documents 状态机，
/// 由 Host 轮询以 PostgreSQL FOR UPDATE SKIP LOCKED 认领，多实例安全。
/// </summary>
public interface IDocumentQueue
{
    /// <summary>文档已建档（Status=Uploaded）后触发消费；Database 实现是 no-op（轮询器按状态发现）</summary>
    ValueTask EnqueueAsync(long documentId);
}

/// <summary>文档处理队列（单实例内存队列；重启后由 Host 扫描 Uploaded/Parsing 状态恢复）</summary>
public class DocumentQueue : IDocumentQueue
{
    private readonly Channel<long> _channel = Channel.CreateBounded<long>(
        new BoundedChannelOptions(200) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(long documentId) => _channel.Writer.WriteAsync(documentId);

    public IAsyncEnumerable<long> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>数据库队列模式下的入队句柄：状态已在库里（Uploaded），轮询器自动发现，无需额外动作</summary>
public sealed class DatabaseDocumentQueue : IDocumentQueue
{
    public ValueTask EnqueueAsync(long documentId) => ValueTask.CompletedTask;
}

/// <summary>队列模式</summary>
public enum QueueMode
{
    Memory,
    Database
}

/// <summary>解析 SmartKB:QueueMode 配置（默认 Memory，保持既有单实例行为）</summary>
public static class QueueModeOptions
{
    public static QueueMode Resolve(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "database" or "db" or "distributed" => QueueMode.Database,
        _ => QueueMode.Memory
    };
}
