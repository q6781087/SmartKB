using SmartKB.AI.Ingestion;

namespace SmartKB.Tests;

/// <summary>文档队列模式（v1.6 多实例支持）：配置解析与两种实现的入队语义</summary>
public class QueueModeTests
{
    [Theory]
    [InlineData(null, QueueMode.Memory)]
    [InlineData("", QueueMode.Memory)]
    [InlineData("Memory", QueueMode.Memory)]
    [InlineData("memory", QueueMode.Memory)]
    [InlineData("Database", QueueMode.Database)]
    [InlineData("db", QueueMode.Database)]
    [InlineData("DISTRIBUTED", QueueMode.Database)]
    [InlineData("其他值", QueueMode.Memory)] // 未知值回退 Memory，保持单实例既有行为
    public void 配置解析_未知值回退Memory(string? raw, QueueMode expected)
    {
        Assert.Equal(expected, QueueModeOptions.Resolve(raw));
    }

    [Fact]
    public async Task 数据库队列实现_入队是无操作_由轮询器按状态发现()
    {
        // Database 模式：状态已在库里（Uploaded），Enqueue 必须不抛不阻（Channel 满时阻塞的坑）
        IDocumentQueue queue = new DatabaseDocumentQueue();
        for (var i = 0; i < 1000; i++)
            await queue.EnqueueAsync(i);
    }

    [Fact]
    public async Task 内存队列实现_先进先出()
    {
        var queue = new DocumentQueue();
        await queue.EnqueueAsync(42);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        long? first = null;
        await foreach (var id in queue.DequeueAllAsync(cts.Token))
        {
            first = id;
            break;
        }
        Assert.Equal(42L, first);
    }
}
