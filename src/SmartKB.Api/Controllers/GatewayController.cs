using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartKB.AI.Agent;
using SmartKB.AI.Tools;
using SmartKB.Api.Gateway;
using SmartKB.Application;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Api.Controllers;

/// <summary>
/// OpenAI 兼容网关（v1.5，P2）。默认关闭，开启后现有 ChatGPT 客户端 / 业务系统可零改造接入：
///   GET  /v1/models             列出可访问知识库（model = smartkb:kbId 可限定范围）
///   POST /v1/chat/completions   非流式 + SSE 流式
///
/// 安全边界：鉴权独立于 [Authorize]（静态服务间 Key 或现有 JWT）；知识库范围与工具白名单
/// 一律由服务端按身份计算，请求里的 kb_ids / system 都无权扩大权限；引用与用量以扩展字段返回。
/// </summary>
[AllowAnonymous]
[Route("v1")]
public class GatewayController(
    IOptions<GatewayOptions> options,
    IGatewayAuthenticator authenticator,
    IChatOrchestrator orchestrator,
    IChatService chat,
    IPermissionService permissions,
    IAiUsageService aiUsage,
    SmartKbDbContext db,
    ILogger<GatewayController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private GatewayOptions Options => options.Value;

    [HttpGet("models")]
    public async Task<IActionResult> Models(CancellationToken ct)
    {
        var (failure, identity) = await PreflightAsync(ct);
        if (failure is not null) return failure;

        try
        {
            var accessible = await permissions.GetAccessibleKbIdsAsync(identity!.UserId);
            var query = db.KnowledgeBases.AsNoTracking();
            if (accessible is not null)
                query = query.Where(k => accessible.Contains(k.Id));

            var kbs = await query.OrderBy(k => k.Id)
                .Select(k => new { k.Id, k.Name })
                .ToListAsync(ct);

            var data = new List<object>
            {
                new { id = GatewayRequestParser.DefaultModel, @object = "model", owned_by = "smartkb" }
            };
            foreach (var kb in kbs)
                data.Add(new { id = $"smartkb:{kb.Id}", @object = "model", owned_by = "smartkb", name = kb.Name });

            return JsonResult(StatusCodes.Status200OK, new { @object = "list", data });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "网关模型列表查询失败 UserId={UserId}", identity!.UserId);
            return Error(StatusCodes.Status502BadGateway, "知识库列表查询失败", "server_error");
        }
    }

    [HttpPost("chat/completions")]
    public async Task<IActionResult> ChatCompletions(CancellationToken ct)
    {
        var (failure, identity) = await PreflightAsync(ct);
        if (failure is not null) return failure;

        GatewayChatRequest request;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            // 配置误填 0 / 负数时钳制到合理区间，避免所有请求被无差别拒绝
            var maxMessages = Math.Clamp(Options.MaxMessages, 1, 200);
            var maxQuestionChars = Math.Clamp(Options.MaxQuestionChars, 100, 100_000);
            request = GatewayRequestParser.Parse(doc.RootElement, maxMessages, maxQuestionChars);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "请求体不是合法 JSON");
        }
        catch (GatewayRequestException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }

        // 权限与会话：与 SignalR 走完全相同的服务层，不另开一条宽松路径
        long sessionId;
        try
        {
            if (request.SessionId is { } sid)
            {
                _ = await chat.GetOwnedSessionAsync(identity!.UserId, sid);
                sessionId = sid;
            }
            else
            {
                var title = request.Question.Length > 30
                    ? "[API] " + request.Question[..30]
                    : "[API] " + request.Question;
                sessionId = (await chat.CreateSessionAsync(
                    identity!.UserId, new CreateSessionRequest(title))).Id;
            }

            var accessible = await permissions.GetAccessibleKbIdsAsync(identity.UserId);
            var history = GatewayRequestParser.ToHistory(request);
            await chat.AddUserMessageAsync(identity.UserId, sessionId, request.Question);

            var chatOptions = new ChatRequestOptions(
                Options.AllowSystemPrompt ? request.SystemPrompt : null,
                request.Temperature,
                request.MaxTokens);

            var orchestration = orchestrator.AskStreamAsync(
                accessible,
                new ToolCaller(identity.UserId, identity.IsAdmin),
                request.Question,
                request.KbIds.ToList(),
                history,
                chatOptions,
                ct);

            var completionId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
            try
            {
                return request.Stream
                    ? await WriteStreamAsync(request, completionId, orchestration, sessionId, identity, ct)
                    : await WriteCompletionAsync(request, completionId, orchestration, sessionId, identity, ct);
            }
            finally
            {
                await LogInvocationAsync(orchestration.Tracker, sessionId, identity, request.Question);
            }
        }
        catch (BusinessRuleException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error(499, "客户端断开连接", "server_error");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "网关问答处理异常 UserId={UserId}", identity!.UserId);
            return Error(StatusCodes.Status502BadGateway, "上游模型服务异常，请稍后重试", "server_error");
        }
    }

    // ---------- 非流式 ----------

    private async Task<IActionResult> WriteCompletionAsync(
        GatewayChatRequest request, string completionId, OrchestrationStream orchestration,
        long sessionId, GatewayIdentity identity, CancellationToken ct)
    {
        var answer = new StringBuilder();
        IReadOnlyList<Citation>? citations = null;
        string? failure = null;

        await foreach (var delta in orchestration.Deltas)
        {
            switch (delta.Kind)
            {
                case DeltaKind.Token:
                    answer.Append(delta.Text);
                    break;
                case DeltaKind.Citations:
                    citations = delta.Citations;
                    break;
                case DeltaKind.Error:
                    failure = delta.Text;
                    orchestration.Tracker.Success = false;
                    orchestration.Tracker.ErrorMessage = delta.Text;
                    break;
            }
        }

        if (failure is not null)
            return Error(StatusCodes.Status502BadGateway, failure, "server_error");

        var text = answer.ToString();
        await chat.SaveAssistantMessageAsync(sessionId, text, citations);

        var tracker = orchestration.Tracker;
        var payload = new
        {
            id = completionId,
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = request.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = text,
                        smartkb_citations = ToCitationDtos(citations)
                    },
                    finish_reason = "stop"
                }
            },
            usage = BuildUsage(tracker)
        };
        return JsonResult(StatusCodes.Status200OK, payload);
    }

    // ---------- SSE 流式 ----------

    private async Task<IActionResult> WriteStreamAsync(
        GatewayChatRequest request, string completionId, OrchestrationStream orchestration,
        long sessionId, GatewayIdentity identity, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var answer = new StringBuilder();
        IReadOnlyList<Citation>? citations = null;
        string? failure = null;

        async Task WriteChunkAsync(Dictionary<string, object?>? delta, string? finishReason, object? usage = null)
        {
            var choices = delta is null
                ? Array.Empty<object>()
                : new object[] { new { index = 0, delta, finish_reason = finishReason } };
            var chunk = new
            {
                id = completionId,
                @object = "chat.completion.chunk",
                created,
                model = request.Model,
                choices,
                usage
            };
            await Response.WriteAsync("data: " + JsonSerializer.Serialize(chunk, JsonOpts) + "\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        async Task WriteDoneAsync()
        {
            await Response.WriteAsync("data: [DONE]\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            await foreach (var delta in orchestration.Deltas)
            {
                switch (delta.Kind)
                {
                    case DeltaKind.Token:
                        answer.Append(delta.Text);
                        await WriteChunkAsync(new Dictionary<string, object?> { ["content"] = delta.Text }, null);
                        break;
                    case DeltaKind.Citations:
                        citations = delta.Citations;
                        break;
                    case DeltaKind.Error:
                        failure = delta.Text;
                        orchestration.Tracker.Success = false;
                        orchestration.Tracker.ErrorMessage = delta.Text;
                        break;
                }
            }

            if (failure is not null)
            {
                await WriteErrorEventAsync(failure);
                await WriteDoneAsync();
                return new EmptyResult();
            }

            var text = answer.ToString();
            await chat.SaveAssistantMessageAsync(sessionId, text, citations);

            // 引用走扩展字段单独一个 chunk，避免污染正文 delta
            if (citations is { Count: > 0 })
                await WriteChunkAsync(new Dictionary<string, object?>
                {
                    ["smartkb_citations"] = ToCitationDtos(citations)
                }, null);

            await WriteChunkAsync(new Dictionary<string, object?>(), "stop");

            if (request.IncludeUsage)
                await WriteChunkAsync(null, null, BuildUsage(orchestration.Tracker));

            await WriteDoneAsync();
        }
        catch (OperationCanceledException)
        {
            // 客户端断开：SSE 已写出部分内容，不再尝试补发
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "网关流式输出异常 UserId={UserId}", identity.UserId);
            orchestration.Tracker.Success = false;
            orchestration.Tracker.ErrorMessage = ex.Message;
            try { await WriteErrorEventAsync("上游模型服务异常，请稍后重试"); await WriteDoneAsync(); }
            catch { /* 连接已断，忽略 */ }
        }
        return new EmptyResult();

        async Task WriteErrorEventAsync(string message)
        {
            var payload = new { error = new { message, type = "server_error" } };
            await Response.WriteAsync("data: " + JsonSerializer.Serialize(payload, JsonOpts) + "\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    // ---------- 通用 ----------

    private async Task<(IActionResult? Failure, GatewayIdentity? Identity)> PreflightAsync(CancellationToken ct)
    {
        if (!authenticator.IsReady)
            return (Error(StatusCodes.Status503ServiceUnavailable,
                "OpenAI 兼容网关未启用（需配置 Gateway__Enabled / Gateway__ApiKey / Gateway__ServiceUsername）",
                "server_error"), null);

        GatewayIdentity? identity;
        try
        {
            identity = await authenticator.AuthenticateAsync(HttpContext, ct);
        }
        catch (GatewayConfigurationException ex)
        {
            return (Error(StatusCodes.Status503ServiceUnavailable, ex.Message, "server_error"), null);
        }

        return identity is null
            ? (Error(StatusCodes.Status401Unauthorized,
                "缺少或无效的 Authorization: Bearer <API Key 或 JWT>",
                "authentication_error"), null)
            : (null, identity);
    }

    /// <summary>观测落库：与 SignalR 一致，任何路径都记一条；失败只记日志，不影响响应</summary>
    private async Task LogInvocationAsync(
        InvocationTracker tracker, long sessionId, GatewayIdentity identity, string question)
    {
        try
        {
            await aiUsage.LogAsync(new AiInvocationLog
            {
                SessionId = sessionId,
                UserId = identity.UserId,
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
                ErrorMessage = tracker.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "网关 AI 调用观测落库失败 SessionId={SessionId}", sessionId);
        }
    }

    private static IReadOnlyList<CitationDto>? ToCitationDtos(IReadOnlyList<Citation>? citations) =>
        citations is { Count: > 0 }
            ? citations.Select(c => new CitationDto(
                c.ChunkId, c.DocumentId, c.FileName, c.Seq, c.Title, c.Excerpt, c.Score)).ToList()
            : null;

    private static object BuildUsage(InvocationTracker tracker) => new
    {
        prompt_tokens = tracker.InputTokens ?? 0,
        completion_tokens = tracker.OutputTokens ?? 0,
        total_tokens = tracker.TotalTokens
            ?? (tracker.InputTokens ?? 0) + (tracker.OutputTokens ?? 0)
    };

    private static ContentResult JsonResult(int status, object payload) => new()
    {
        StatusCode = status,
        ContentType = "application/json; charset=utf-8",
        Content = JsonSerializer.Serialize(payload, JsonOpts)
    };

    private static ContentResult Error(int status, string message,
        string type = "invalid_request_error", string? code = null) =>
        JsonResult(status, new
        {
            error = new { message, type, code }
        });
}
