using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Ingestion;

/// <summary>
/// 后台消费：逐个处理队列中的文档。
/// 启动时扫描 Uploaded/Parsing 状态的历史文档重新入队（进程重启自恢复）。
/// </summary>
public class DocumentProcessingHost(
    DocumentQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<DocumentProcessingHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动恢复
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartKbDbContext>();
            var pending = await db.Documents
                .Where(d => d.Status == DocumentStatus.Uploaded || d.Status == DocumentStatus.Parsing)
                .Select(d => d.Id)
                .ToListAsync(stoppingToken);

            foreach (var id in pending)
                await queue.EnqueueAsync(id);

            if (pending.Count > 0)
                logger.LogInformation("恢复 {Count} 个待处理文档", pending.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动恢复待处理文档失败（数据库暂不可用？）");
        }

        await foreach (var documentId in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var ingest = scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>();
                await ingest.IngestAsync(documentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 状态与错误信息已在 IngestAsync 内落库，这里只记日志继续消费
                logger.LogWarning(ex, "文档 {DocumentId} 入库流水线异常，已标记 Failed", documentId);
            }
        }
    }
}
