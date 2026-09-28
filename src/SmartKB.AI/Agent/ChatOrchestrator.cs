using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using SmartKB.AI.Chat;
using SmartKB.AI.Embedding;
using SmartKB.AI.Retrieval;
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
public record StreamDelta(DeltaKind Kind, string Text, IReadOnlyList<Citation>? Citations = null);

/// <summary>
/// 问答编排器：MAF ChatClientAgent + 函数工具（知识库检索 / 台账查询）。
/// 权限由调用方解析后传入（accessibleKbIds，null = 管理员不过滤），
/// 工具闭包继承该范围，LLM 无法越权——AI 层不感知用户体系，保持纯粹。
/// </summary>
public interface IChatOrchestrator
{
    IAsyncEnumerable<StreamDelta> AskStreamAsync(
        List<long>? accessibleKbIds,
        string question,
        List<long> requestedKbIds,
        IReadOnlyList<(ChatMessageRole Role, string Content)> history,
        CancellationToken ct = default);
}

public class ChatOrchestrator(
    IChatClient chatClient,
    IKnowledgeRetrievalService retrieval,
    ILedgerQueryService ledger,
    SmartKbDbContext db) : IChatOrchestrator
{
    private const string SystemPrompt = """
        你是企业知识库智能问答助手。严格遵守以下规则：

        1. 对话中已注入 [知识库预检索结果]，若其内容足以回答，直接作答，不要重复调用 search_knowledge。
        2. 预检索结果不足以回答时，再调用 search_knowledge 工具补充检索；仍检索不到就明确说"知识库中未找到相关内容"，禁止凭空编造企业内部信息。
        3. 回答只依据检索到的内容（预检索结果或工具返回）。
        4. 数量、金额、日期等精确数据类问题，优先调用 query_ledger 查台账结构化数据。
        5. 回答末尾用【来源】列出引用的文档名与片段位置，例如：来源：《采购合同》第3段。
        6. 用简体中文回答，简洁专业；列表数据用 Markdown 表格。
        """;

    public async IAsyncEnumerable<StreamDelta> AskStreamAsync(
        List<long>? accessibleKbIds,
        string question,
        List<long> requestedKbIds,
        IReadOnlyList<(ChatMessageRole Role, string Content)> history,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
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

        var searchTool = AIFunctionFactory.Create(
            async (string question) =>
            {
                var chunks = await retrieval.SearchAsync(question, kbScope, topK: 8, ct);
                foreach (var c in chunks.Where(c => !citations.Any(x => x.ChunkId == c.ChunkId)))
                {
                    citations.Add(new Citation(
                        c.ChunkId, c.DocumentId, c.FileName, c.Seq, c.Title,
                        c.Content.Length > 200 ? c.Content[..200] + "…" : c.Content,
                        c.Score));
                }
                return chunks.Count == 0
                    ? "（知识库中未检索到相关内容）"
                    : string.Join("\n\n---\n\n", chunks.Select(FormatChunk));
            },
            name: "search_knowledge",
            description: $"在用户有权访问的企业知识库中做语义检索，返回最相关的文档片段。{kbDesc}");

        // ---------- 3. 台账查询工具 ----------
        var ledgerTool = AIFunctionFactory.Create(
            async (long kbId, string field, string op, string value) =>
            {
                // kbId 也纳入权限校验（防 LLM 越权指定）
                if (kbScope is not null && !kbScope.Contains(kbId))
                    return "错误：无权访问该知识库的台账";

                var fields = await ledger.GetLedgerFieldsAsync(kbId, ct);
                if (fields.Count == 0)
                    return $"错误：知识库 {kbId} 没有台账数据（仅启用台账双轨的 Excel 库有）";

                var rows = await ledger.QueryAsync(kbId, [new LedgerFilter(field, op, value)], maxRows: 50, ct);
                if (rows.Count == 0) return "（台账中无匹配数据）";

                var header = string.Join(" | ", fields);
                var body = string.Join("\n", rows.Select(r =>
                    string.Join(" | ", fields.Select(f => r.TryGetValue(f, out var v) ? v : ""))));
                return $"共 {rows.Count} 行（字段：{string.Join("、", fields)}）\n{header}\n{body}";
            },
            name: "query_ledger",
            description: "按条件精确查询 Excel 台账数据。field 必须是台账真实列名；op 仅支持 eq/contains/gt/gte/lt/lte；value 为比较值。适合查合同金额、设备编号等精确数据。");

        // ---------- 4. MAF Agent（UseFunctionInvocation 自动执行工具调用） ----------
        var agent = new ChatClientAgent(
            chatClient.AsBuilder().UseFunctionInvocation().Build(),
            instructions: SystemPrompt,
            name: "SmartKB");

        var messages = new List<ChatMessage>();
        // 多轮历史（最近 8 条）+ 预检索上下文 + 本轮问题
        foreach (var (role, content) in history.TakeLast(8))
            messages.Add(new ChatMessage(
                role == ChatMessageRole.User ? ChatRole.User : ChatRole.Assistant,
                content));

        // ---------- 4.5 预检索：先向量检索并注入上下文，省掉"LLM 决定调工具→工具执行→回到 LLM"的第一轮往返，首字延迟可减 2s 左右。
        // 检索命中的引用直接累积进 citations；工具保留，供预检索不足时补充调用。
        var preChunks = await retrieval.SearchAsync(question, kbScope, topK: 8, ct);
        foreach (var c in preChunks.Where(c => !citations.Any(x => x.ChunkId == c.ChunkId)))
        {
            citations.Add(new Citation(
                c.ChunkId, c.DocumentId, c.FileName, c.Seq, c.Title,
                c.Content.Length > 200 ? c.Content[..200] + "…" : c.Content,
                c.Score));
        }
        var preContext = preChunks.Count == 0
            ? "（知识库中未检索到相关内容）"
            : string.Join("\n\n---\n\n", preChunks.Select(FormatChunk));
        messages.Add(new ChatMessage(ChatRole.User, $"[知识库预检索结果]\n{preContext}"));
        messages.Add(new ChatMessage(ChatRole.User, question));

        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions { Tools = [searchTool, ledgerTool] }
        };

        // ---------- 5. 流式输出（异常上抛，由 Hub 统一转 Error 事件） ----------
        var answer = new System.Text.StringBuilder();

        await foreach (var update in agent.RunStreamingAsync(messages, options: runOptions, cancellationToken: ct))
        {
            var text = update.Text;
            if (string.IsNullOrEmpty(text)) continue;
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
