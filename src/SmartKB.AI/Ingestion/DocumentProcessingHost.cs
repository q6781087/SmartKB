using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Ingestion;

/// <summary>
/// 后台消费：逐个处理队列中的文档。
/// Memory 模式：启动时扫描 Uploaded/Parsing 状态的历史文档重新入队（进程重启自恢复）。
/// Database 模式（v1.6 多实例支持）：轮询认领 Uploaded 文档，
/// 以 PostgreSQL FOR UPDATE SKIP LOCKED 逐条抢占——多实例不会重复消费。
/// </summary>
public class DocumentProcessingHost(
    DocumentQueue memoryQueue,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DocumentProcessingHost> logger) : BackgroundService
{
    private static readonly TimeSpan DbPollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mode = QueueModeOptions.Resolve(configuration["SmartKB:QueueMode"]);
        if (mode == QueueMode.Database)
        {
            await ConsumeDatabaseAsync(stoppingToken);
            return;
        }

        // ---------- Memory 模式：启动恢复 + Channel 消费 ----------
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartKbDbContext>();
            var pending = await db.Documents
                .Where(d => d.Status == DocumentStatus.Uploaded || d.Status == DocumentStatus.Parsing)
                .Select(d => d.Id)
                .ToListAsync(stoppingToken);

            foreach (var id in pending)
                await memoryQueue.EnqueueAsync(id);

            if (pending.Count > 0)
                logger.LogInformation("恢复 {Count} 个待处理文档", pending.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动恢复待处理文档失败（数据库暂不可用？）");
        }

        await foreach (var documentId in memoryQueue.DequeueAllAsync(stoppingToken))
        {
            await ProcessOneAsync(documentId, stoppingToken);
        }
    }

    /// <summary>
    /// Database 模式消费循环：单条 SQL 认领（Uploaded → Parsing），抢不到就休眠。
    /// 认领与状态推进在一条 UPDATE 内完成，天然防多实例重复消费。
    /// </summary>
    private async Task ConsumeDatabaseAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("文档队列运行于 Database 模式（多实例安全）");

        // 崩溃恢复：进程重启时把遗留的 Parsing 文档放回队列。
        // 注意：与其他实例同时重启时可能重复处理个别文档——入库流水线幂等（先清旧分块），可接受。
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SmartKbDbContext>();
            if (db.Database.IsRelational())
            {
                var recovered = await RecoverStaleParsingAsync(db, stoppingToken);
                if (recovered > 0)
                    logger.LogInformation("崩溃恢复：{Count} 个 Parsing 文档已放回队列", recovered);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "崩溃恢复扫描失败，跳过（新上传文档不受影响）");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            long? claimed = null;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmartKbDbContext>();
                if (db.Database.IsRelational())
                    claimed = await ClaimNextAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 数据库暂不可用：记日志退避重试，不让宿主进程退出
                logger.LogWarning(ex, "文档队列认领失败，{Interval}s 后重试", DbPollInterval.TotalSeconds);
            }

            if (claimed is not { } documentId)
            {
                await Task.Delay(DbPollInterval, stoppingToken);
                continue;
            }

            await ProcessOneAsync(documentId, stoppingToken);
        }
    }

    /// <summary>
    /// 认领下一条待处理文档。FOR UPDATE SKIP LOCKED 保证多实例各抢各的。
    /// </summary>
    internal static async Task<long?> ClaimNextAsync(SmartKbDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var closeAfter = conn.State != System.Data.ConnectionState.Open;
        if (closeAfter) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE kb_documents SET "Status" = 1
                WHERE "Id" = (
                    SELECT "Id" FROM kb_documents
                    WHERE "Status" = 0
                    ORDER BY "Id"
                    FOR UPDATE SKIP LOCKED LIMIT 1
                )
                RETURNING "Id";
                """;
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long id ? id : result is int i ? i : null;
        }
        finally
        {
            if (closeAfter) await conn.CloseAsync();
        }
    }

    /// <summary>崩溃恢复：遗留的 Parsing 文档（进程崩溃时状态停在中间态）放回 Uploaded</summary>
    internal static async Task<int> RecoverStaleParsingAsync(SmartKbDbContext db, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var closeAfter = conn.State != System.Data.ConnectionState.Open;
        if (closeAfter) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE kb_documents SET "Status" = 0, "ErrorMessage" = NULL
                WHERE "Status" = 1;
                """;
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (closeAfter) await conn.CloseAsync();
        }
    }

    private async Task ProcessOneAsync(long documentId, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var ingest = scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>();
            await ingest.IngestAsync(documentId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 状态与错误信息已在 IngestAsync 内落库，这里只记日志继续消费
            logger.LogWarning(ex, "文档 {DocumentId} 入库流水线异常，已标记 Failed", documentId);
        }
    }
}
