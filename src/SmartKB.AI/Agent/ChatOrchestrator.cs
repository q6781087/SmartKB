using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SmartKB.AI.Configuration;
using SmartKB.AI.Embedding;
using SmartKB.AI.Retrieval;
using SmartKB.AI.Tools;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.AI.Agent;

/// <summary>引用出处（落库到 ChatMessage.CitationsJson）</summary>
public record Citation(
    long ChunkId,
    long DocumentId,
    string FileName,
    int Seq,
    string? Title,
    string Excerpt,
    float Score);

/// <summary>流式增量类型</summary>
public enum DeltaKind
{
    /// <summary>生成文本增量</summary>
    Token,

    /// <summary>检索命中的引用（流式过程中累积，最终完整版随 Done 落库）</summary>
    Citations,

    /// <summary>回答结束</summary>
    Done,

    /// <summary>错误（流式流程终止）</summary>
    Error
}

/// <summary>流式增量</summary>
public record StreamDelta(
    DeltaKind Kind,
    string Text,
    IReadOnlyList<Citation>? Citations = null);

/// <summary>
/// 单次问答的可选覆盖项。SignalR 走配置中心默认值（options = null）；
/// OpenAI 兼容网关可在配置允许时携带请求里的 system / temperature / max_tokens。
/// 权限范围与工具白名单不在此处——它们由调用方以独立参数强制传入，不接受请求覆盖。
/// </summary>
public sealed record ChatRequestOptions(
    string? SystemPrompt = null,
    float? Temperature = null,
    int? MaxOutputTokens = null);

/// <summary>
/// 单次问答观测追踪器：编排过程中实时写入，无论成功/失败/断连，
/// 调用方（Hub）最终从 Snapshot() 取数落库 ai_invocation_logs。
/// </summary>
public class InvocationTracker(string model)
{
    public string Model { get; } = model;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int? FirstTokenMs { get; set; }
    public int ToolCalls { get; set; }
    public int RetrievedChunks { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }

    public InvocationStats Snapshot(int totalMs) => new(
        Model, InputTokens, OutputTokens,
        TotalTokens ?? (InputTokens + OutputTokens),
        FirstTokenMs, totalMs, ToolCalls, RetrievedChunks);
}

/// <summary>单次问答观测统计快照</summary>
public record InvocationStats(
    string Model,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int? FirstTokenMs,
    int TotalMs,
    int ToolCalls,
    int RetrievedChunks);

/// <summary>编排结果：流 + 追踪器（观测数据与流生命周期解耦，异常也不丢）</summary>
public record OrchestrationStream(
    IAsyncEnumerable<StreamDelta> Deltas,
    InvocationTracker Tracker);

/// <summary>
/// 问答编排器：MAF ChatClientAgent + 函数工具（知识库检索 / 台账查询）。
/// 权限由调用方解析后传入（accessibleKbIds，null = 管理员不过滤），
/// 工具闭包继承该范围，LLM 无法越权——AI 层不感知用户体系，保持纯粹。
/// 行为参数（提示词 / topK / 混合检索 / 重排 / 温度）来自 IRagSettingsProvider，后台可热更新。
/// </summary>
public interface IChatOrchestrator
{
    OrchestrationStream AskStreamAsync(
        List<long>? accessibleKbIds,
        ToolCaller caller,
        string question,
        List<long> requestedKbIds,
        IReadOnlyList<(ChatMessageRole Role, string Content)> history,
        ChatRequestOptions? options = null,
        CancellationToken ct = default);
}

public class ChatOrchestrator(
    IChatClient chatClient,
    IKnowledgeRetrievalService retrieval,
    ILedgerQueryService ledger,
    SmartKbDbContext db,
    LlmOptions llmOptions,
    IRagSettingsProvider settingsProvider,
    IAgentToolRegistry toolRegistry,
    IHttpToolExecutor toolExecutor,
    ILogger<ChatOrchestrator> logger) : IChatOrchestrator
{
    public OrchestrationStream AskStreamAsync(
        List<long>? accessibleKbIds,
        ToolCaller caller,
        string question,
        List<long> requestedKbIds,
        IReadOnlyList<(ChatMessageRole Role, string Content)> history,
        ChatRequestOptions? options = null,
        CancellationToken ct = default)
    {
        var modelName = llmOptions.Mode switch
        {
            var m when m.Equals("Local", StringComparison.OrdinalIgnoreCase) => llmOptions.Local.ChatModel,
            var m when m.Equals("Agnes", StringComparison.OrdinalIgnoreCase) => llmOptions.Agnes.ChatModel,
            _ => llmOptions.Cloud.ChatModel
        };
        var tracker = new InvocationTracker(modelName);
        return new OrchestrationStream(
            StreamCoreAsync(accessibleKbIds, caller, question, requestedKbIds, history,
                options, tracker, ct),
            tracker);
    }

    private async IAsyncEnumerable<StreamDelta> StreamCoreAsync(
        List<long>? accessibleKbIds,
        ToolCaller caller,
        string question,
        List<long> requestedKbIds,
        IReadOnlyList<(ChatMessageRole Role, string Content)> history,
        ChatRequestOptions? options,
        InvocationTracker tracker,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // ---------- 0. 运行时配置 + 观测计时 ----------
        var settings = await settingsProvider.GetAsync(ct);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // ---------- 1. 权限范围（调用方已解析；这里只做指定库与可访问集求交） ----------
        List<long>? kbScope;
        if (requestedKbIds.Count > 0)
        {
            var scope = accessibleKbIds is null
                ? requestedKbIds
                : requestedKbIds.Where(accessibleKbIds.Contains).ToList();
            if (scope.Count == 0)
            {
                yield return new StreamDelta(DeltaKind.Error, "没有可访问的知识库，请联系管理员授权");
                yield break;
            }
            kbScope = scope;
        }
        else
        {
            // 未指定：限可访问全集（null = 管理员不过滤）
            kbScope = accessibleKbIds;
        }

        // ---------- 2. 检索工具（调用即累积引用） ----------
        var citations = new List<Citation>();
        var kbDesc = await DescribeKbScopeAsync(kbScope, ct);
        var fields = await retrieval.GetFilterableFieldsAsync(kbScope, ct);
        var fieldDesc = MetadataFilterEngine.Describe(fields);

        void AddCitations(IEnumerable<RetrievedChunk> chunks)
        {
            foreach (var c in chunks.Where(c => !citations.Any(x => x.ChunkId == c.ChunkId)))
            {
                citations.Add(new Citation(
                    c.ChunkId, c.DocumentId, c.FileName, c.Seq, c.Title,
                    c.Content.Length > 200 ? c.Content[..200] + "…" : c.Content,
                    c.Score));
            }
        }

        string FormatResults(List<RetrievedChunk> chunks) => chunks.Count == 0
            ? "（知识库中未检索到相关内容）"
            : string.Join("\n\n---\n\n", chunks.Select(FormatChunk));

        var searchTool = AIFunctionFactory.Create(
            async (string question, string filters) =>
            {
                tracker.ToolCalls++;
                List<MetadataFilter> parsed;
                try
                {
                    parsed = MetadataFilterEngine.ParseFilters(filters);
                }
                catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
                {
                    return $"错误：filters 格式不正确（{ex.Message}），应传 JSON 数组，不需要过滤时传空字符串。";
                }

                try
                {
                    var chunks = await retrieval.SearchAsync(
                        question, kbScope, settings.TopK, parsed, caller, ct);
                    tracker.RetrievedChunks += chunks.Count;
                    AddCitations(chunks);
                    return FormatResults(chunks);
                }
                catch (ArgumentException ex)
                {
                    // 字段名/操作符未过白名单：把可读原因回给模型，不暴露堆栈
                    return $"错误：{ex.Message}";
                }
            },
            name: "search_knowledge",
            description: $"在用户有权访问的企业知识库中做语义检索，返回最相关的文档片段。{kbDesc} " +
                "filters 为 JSON 数组字符串，按客户自定义字段筛选文档，例如 " +
                "[{\"field\":\"contract_year\",\"op\":\"gte\",\"value\":\"2025\"}]；" +
                "op 仅支持 eq/contains/gt/gte/lt/lte；不需要过滤时必须传空字符串 \"\"。" +
                (settings.MetadataFilterEnabled ? fieldDesc : "（元数据过滤当前已关闭，请传空字符串）"));

        // ---------- 3. 台账查询工具 ----------
        var ledgerTool = AIFunctionFactory.Create(
            async (long kbId, string field, string op, string value) =>
            {
                tracker.ToolCalls++;
                // kbId 也纳入权限校验（防 LLM 越权指定）
                if (kbScope is not null && !kbScope.Contains(kbId))
                    return "错误：无权访问该知识库的台账";

                var ledgerFields = await ledger.GetLedgerFieldsAsync(kbId, ct);
                if (ledgerFields.Count == 0)
                    return $"错误：知识库 {kbId} 没有台账数据（仅启用台账双轨的 Excel 库有）";

                try
                {
                    var rows = await ledger.QueryAsync(kbId, [new LedgerFilter(field, op, value)],
                        maxRows: 50, caller, ct);
                    if (rows.Count == 0) return "（台账中无匹配数据）";

                    var header = string.Join(" | ", ledgerFields);
                    var body = string.Join("\n", rows.Select(r =>
                        string.Join(" | ", ledgerFields.Select(f => r.TryGetValue(f, out var v) ? v : ""))));
                    return $"共 {rows.Count} 行（字段：{string.Join("、", ledgerFields)}）\n{header}\n{body}";
                }
                catch (ArgumentException ex)
                {
                    return $"错误：{ex.Message}";
                }
            },
            name: "query_ledger",
            description: "按条件精确查询 Excel 台账数据。field 必须是台账真实列名；op 仅支持 eq/contains/gt/gte/lt/lte；value 为比较值。适合查合同金额、设备编号等精确数据。");

        // ---------- 4. MAF Agent（UseFunctionInvocation 自动执行工具调用） ----------
        var agent = new ChatClientAgent(
            chatClient.AsBuilder().UseFunctionInvocation().Build(),
            // 网关可在配置允许时用请求里的 system 覆盖；默认仍由配置中心统一管控
            instructions: string.IsNullOrWhiteSpace(options?.SystemPrompt)
                ? settings.SystemPrompt
                : options.SystemPrompt,
            name: "SmartKB");

        var messages = new List<ChatMessage>();
        // 多轮历史（最近 8 条）+ 预检索上下文 + 本轮问题
        foreach (var (role, content) in history.TakeLast(8))
            messages.Add(new ChatMessage(
                role == ChatMessageRole.User ? ChatRole.User : ChatRole.Assistant,
                content));

        // ---------- 4.5 预检索：先检索并注入上下文，省掉"LLM 决定调工具→工具执行→回到 LLM"的第一轮往返。
        // 预检索不加元数据过滤（此时还没解析用户意图），带筛选条件的精确问答由工具补充。
        var preChunks = await retrieval.SearchAsync(question, kbScope, settings.TopK, null, caller, ct);
        tracker.RetrievedChunks += preChunks.Count;
        AddCitations(preChunks);
        messages.Add(new ChatMessage(ChatRole.User, $"[知识库预检索结果]\n{FormatResults(preChunks)}"));
        messages.Add(new ChatMessage(ChatRole.User, question));

        // ---------- 4.8 配置化 HTTP 工具（后台登记，运行时注入） ----------
        // 单个工具加载/执行失败不能拖垮问答：执行器已把异常降级为文本，这里再兜一层加载异常。
        var tools = new List<AITool> { searchTool, ledgerTool };
        try
        {
            foreach (var def in await toolRegistry.GetEnabledForAsync(caller, ct))
            {
                // 与内置工具同名会让模型看到重复函数定义，直接跳过
                if (def.Name is "search_knowledge" or "query_ledger")
                {
                    logger.LogWarning("配置化工具 {Tool} 与内置工具重名，已跳过", def.Name);
                    continue;
                }

                // 知识库范围隔离：工具限定了可用库时，与本次请求范围求交，无交集不注入。
                // kbScope 为 null 表示管理员且未指定范围，视为全量可用。
                if (def.AllowedKbIds is { Count: > 0 } allowed &&
                    !(kbScope is null || kbScope.Any(allowed.Contains)))
                    continue;

                tools.Add(new ConfiguredHttpToolFunction(def, toolExecutor, () => tracker.ToolCalls++));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "配置化工具加载失败，本次问答改用内置工具");
        }

        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                Tools = tools,
                Temperature = options?.Temperature ?? settings.Temperature,
                MaxOutputTokens = options?.MaxOutputTokens ?? settings.MaxOutputTokens
            }
        };

        // ---------- 5. 流式输出（异常上抛由 Hub 处理；观测数据已实时写入 tracker，不丢） ----------
        var answer = new System.Text.StringBuilder();

        await foreach (var update in agent.RunStreamingAsync(messages, options: runOptions, cancellationToken: ct))
        {
            // token usage：OpenAI 兼容接口在流式末尾返回 UsageContent
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                tracker.InputTokens = (int?)usage.Details.InputTokenCount ?? tracker.InputTokens;
                tracker.OutputTokens = (int?)usage.Details.OutputTokenCount ?? tracker.OutputTokens;
                tracker.TotalTokens = (int?)usage.Details.TotalTokenCount ?? tracker.TotalTokens;
            }

            var text = update.Text;
            if (string.IsNullOrEmpty(text)) continue;
            tracker.FirstTokenMs ??= (int)stopwatch.ElapsedMilliseconds; // TTFT
            answer.Append(text);
            yield return new StreamDelta(DeltaKind.Token, text);
        }

        if (citations.Count > 0)
            yield return new StreamDelta(DeltaKind.Citations, string.Empty, citations);

        yield return new StreamDelta(DeltaKind.Done, answer.ToString());
    }

    private static string FormatChunk(RetrievedChunk c) =>
        $"【{c.FileName} · 第{c.Seq + 1}段{(string.IsNullOrEmpty(c.Title) ? "" : $" · {c.Title}")}】\n{c.Content}";

    /// <summary>把可访问的知识库列表写进工具描述，让模型知道检索范围</summary>
    private async Task<string> DescribeKbScopeAsync(List<long>? kbScope, CancellationToken ct)
    {
        if (kbScope is null)
            return "当前为管理员，可检索全部知识库。";
        if (kbScope.Count == 0)
            return "用户尚未被授权任何知识库。";

        var names = await db.KnowledgeBases.AsNoTracking()
            .Where(k => kbScope.Contains(k.Id))
            .Select(k => $"{k.Id}={k.Name}")
            .ToListAsync(ct);
        return $"可检索的知识库：{string.Join("、", names)}。";
    }
}
