using System.Threading.Channels;

namespace SmartKB.AI.Ingestion;

/// <summary>文档处理队列（单实例内存队列；重启后由 Host 扫描 Uploaded/Parsing 状态恢复）</summary>
public class DocumentQueue
{
    private readonly Channel<long> _channel = Channel.CreateBounded<long>(
        new BoundedChannelOptions(200) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(long documentId) => _channel.Writer.WriteAsync(documentId);

    public IAsyncEnumerable<long> DequeueAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
