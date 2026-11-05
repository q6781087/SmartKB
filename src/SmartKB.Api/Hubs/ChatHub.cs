using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SmartKB.AI.Agent;
using SmartKB.AI.Tools;
using SmartKB.Api;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;

namespace SmartKB.Api.Hubs;

/// <summary>
/// 问答流式 Hub。
/// 客户端 → 服务端：Ask(sessionId, kbIds, question)
/// 服务端 → 客户端：token（增量文本）/ citations（引用）/ done（完整回答 + sessionId）/ error
/// 认证：JWT 通过 query string ?access_token=... 传递（WebSocket 无自定义 header）
/// 观测：每次问答结束（成功/失败/断连）落一条 ai_invocation_logs
/// </summary>
[Authorize]
public class ChatHub(
    IChatOrchestrator orchestrator,
    IChatService chat,
    IPermissionService permissions,
    IAiUsageService aiUsage) : Hub
{
    public async Task Ask(long? sessionId, List<long> kbIds, string question)
    {
        InvocationTracker? tracker = null;
        try
        {
            var userId = Context.User!.GetUserId();

            if (string.IsNullOrWhiteSpace(question))
            {
                await Clients.Caller.SendAsync("error", "问题不能为空");
                return;
            }

            // 1. 会话：未指定则自动创建（标题取问题前 30 字）
            if (sessionId is null or <= 0)
            {
                var created = await chat.CreateSessionAsync(userId,
                    new CreateSessionRequest(question.Length > 30 ? question[..30] : question));
                sessionId = created.Id;
            }
            else
            {
                _ = await chat.GetOwnedSessionAsync(userId, sessionId.Value); // 归属校验
            }

            // 2. 历史上下文（在写入本轮消息之前取，避免重复）
            var history = await chat.GetHistoryAsync(sessionId.Value);

            // 3. 权限解析（编排器与工具闭包继承该范围，LLM 无法越权）
            var accessible = await permissions.GetAccessibleKbIdsAsync(userId);

            // 4. 用户消息落库
            await chat.AddUserMessageAsync(userId, sessionId.Value, question.Trim());

            await Clients.Caller.SendAsync("session", sessionId.Value);

            // 5. 流式编排 → 打字机推送（tracker 与流生命周期解耦，异常也不丢观测数据）
            var orchestration = orchestrator.AskStreamAsync(
                accessible,
                new ToolCaller(userId, Context.User!.IsAdmin()),
                question.Trim(), kbIds ?? [], history,
                options: null, ct: Context.ConnectionAborted);
            tracker = orchestration.Tracker;

            await foreach (var delta in orchestration.Deltas)
            {
                switch (delta.Kind)
                {
                    case DeltaKind.Token:
                        await Clients.Caller.SendAsync("token", delta.Text);
                        break;

                    case DeltaKind.Citations:
                        await Clients.Caller.SendAsync("citations",
                            delta.Citations?.Select(c => new CitationDto(
                                c.ChunkId, c.DocumentId, c.FileName, c.Seq,
                                c.Title, c.Excerpt, c.Score)));
                        break;

                    case DeltaKind.Done:
                        await chat.SaveAssistantMessageAsync(
                            sessionId.Value, delta.Text, delta.Citations);
                        await Clients.Caller.SendAsync("done", new
                        {
                            sessionId = sessionId.Value,
                            answer = delta.Text
                        });
                        break;

                    case DeltaKind.Error:
                        tracker.Success = false;
                        tracker.ErrorMessage = delta.Text;
                        await Clients.Caller.SendAsync("error", delta.Text);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：不推错误，但照常落观测（Success 保持 true，回答被截断由 token 数体现）
            if (tracker is not null) tracker.ErrorMessage = "客户端断开连接";
        }
        catch (BusinessRuleException ex)
        {
            if (tracker is not null) { tracker.Success = false; tracker.ErrorMessage = ex.Message; }
            await Clients.Caller.SendAsync("error", ex.Message);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "问答流式处理异常 SessionId={SessionId}", sessionId);
            if (tracker is not null) { tracker.Success = false; tracker.ErrorMessage = ex.Message; }
            await Clients.Caller.SendAsync("error", "服务处理异常，请稍后重试");
        }
        finally
        {
            // 观测落库：任何路径（成功/业务错/异常/断连）都记一条
            if (tracker is not null && sessionId is > 0)
            {
                try
                {
                    await aiUsage.LogAsync(new Domain.Entities.AiInvocationLog
                    {
                        SessionId = sessionId.Value,
                        UserId = Context.User!.GetUserId(),
                        Model = tracker.Model,
                        Question = question.Length > 200 ? question[..200] : question,
                        InputTokens = tracker.InputTokens,
                        OutputTokens = tracker.OutputTokens,
                        TotalTokens = tracker.TotalTokens ?? tracker.InputTokens + tracker.OutputTokens,
                        FirstTokenMs = tracker.FirstTokenMs,
                        TotalMs = (int)(DateTimeOffset.UtcNow - tracker.StartedAt).TotalMilliseconds,
                        ToolCalls = tracker.ToolCalls,
                        RetrievedChunks = tracker.RetrievedChunks,
                        Success = tracker.Success,
                        ErrorMessage = tracker.ErrorMessage,
                    });
                }
                catch (Exception logEx)
                {
                    Serilog.Log.Warning(logEx, "AI 调用观测落库失败 SessionId={SessionId}", sessionId);
                }
            }
        }
    }
}
