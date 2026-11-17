using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartKB.AI.Agent;
using SmartKB.AI.Tools;
using SmartKB.Api.Controllers;
using SmartKB.Api.Gateway;
using SmartKB.Application.Dtos;
using SmartKB.Application.Services;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>
/// 网关完整流程（fake 掉外部依赖）：非流式响应格式、SSE 协议、权限闭包、观测落库。
/// 不启动 Web 服务器，直接调用 Controller 并用 DefaultHttpContext 承接请求/响应流。
/// </summary>
public class GatewayControllerFlowTests
{
    private sealed class FakeAuth(GatewayIdentity identity) : IGatewayAuthenticator
    {
        public bool IsReady => true;

        public Task<GatewayIdentity?> AuthenticateAsync(HttpContext http, CancellationToken ct) =>
            Task.FromResult<GatewayIdentity?>(identity);
    }

    private sealed class FakePermissions(List<long>? accessible) : IPermissionService
    {
        public Task<List<long>?> GetAccessibleKbIdsAsync(long userId) => Task.FromResult(accessible);
        public Task GrantAsync(GrantPermissionRequest request) => throw new NotImplementedException();
        public Task RevokeAsync(long permissionId) => throw new NotImplementedException();
        public Task<List<PermissionDto>> GetKbPermissionsAsync(long kbId) => throw new NotImplementedException();
    }

    private sealed class FakeChat : IChatService
    {
        public string? SavedAnswer { get; private set; }

        public Task<ChatSessionDto> CreateSessionAsync(long userId, CreateSessionRequest request) =>
            Task.FromResult(new ChatSessionDto(
                11, request.Title ?? "新会话", DateTime.UtcNow, DateTime.UtcNow));

        public Task<ChatMessage> AddUserMessageAsync(long userId, long sessionId, string content) =>
            Task.FromResult(new ChatMessage { Id = 1, SessionId = sessionId, Content = content });

        public Task SaveAssistantMessageAsync(
            long sessionId, string answer, IReadOnlyList<Citation>? citations)
        {
            SavedAnswer = answer;
            return Task.CompletedTask;
        }

        public Task<ChatSession> GetOwnedSessionAsync(long userId, long sessionId) =>
            Task.FromResult(new ChatSession { Id = sessionId, UserId = userId });

        public Task<List<ChatSessionDto>> GetSessionsAsync(long userId) => throw new NotImplementedException();
        public Task<List<ChatMessageDto>> GetMessagesAsync(long userId, long sessionId) => throw new NotImplementedException();
        public Task DeleteSessionAsync(long userId, long sessionId) => throw new NotImplementedException();
        public Task<List<(ChatMessageRole Role, string Content)>> GetHistoryAsync(
            long sessionId, int historyTake = 8) => throw new NotImplementedException();
        public string SerializeCitations(IReadOnlyList<Citation> citations) => "[]";
    }

    private sealed class FakeUsage : IAiUsageService
    {
        public AiInvocationLog? Last { get; private set; }

        public Task LogAsync(AiInvocationLog log)
        {
            Last = log;
            return Task.CompletedTask;
        }

        public Task<AiUsageSummaryDto> GetSummaryAsync(int days = 30) => throw new NotImplementedException();
        public Task<List<AiDailyUsageDto>> GetDailyAsync(int days = 30) => throw new NotImplementedException();
        public Task<List<AiInvocationDto>> GetRecentAsync(int limit = 50) => throw new NotImplementedException();
    }

    private sealed record AskCall(
        List<long>? Accessible, ToolCaller Caller, string Question,
        List<long> RequestedKbIds, ChatRequestOptions? Options,
        IReadOnlyList<(ChatMessageRole Role, string Content)> History);

    private sealed class FakeOrchestrator(IReadOnlyList<StreamDelta> deltas) : IChatOrchestrator
    {
        public AskCall? Last { get; private set; }

        public OrchestrationStream AskStreamAsync(
            List<long>? accessibleKbIds, ToolCaller caller, string question,
            List<long> requestedKbIds,
            IReadOnlyList<(ChatMessageRole Role, string Content)> history,
            ChatRequestOptions? options = null, CancellationToken ct = default)
        {
            Last = new AskCall(accessibleKbIds, caller, question, requestedKbIds, options, history);
            return new OrchestrationStream(Enumerate(), new InvocationTracker("test-model"));
        }

        private async IAsyncEnumerable<StreamDelta> Enumerate()
        {
            foreach (var delta in deltas)
            {
                await Task.Yield();
                yield return delta;
            }
        }
    }

    private static readonly IReadOnlyList<Citation> Citations =
        [new Citation(5, 6, "合同.pdf", 2, null, "保修期 12 个月", 0.9f)];

    private static (GatewayController Controller, DefaultHttpContext Http, FakeOrchestrator Orchestrator,
        FakeChat Chat, FakeUsage Usage) New(string body, IReadOnlyList<StreamDelta> deltas,
        bool allowSystemPrompt = false)
    {
        var options = new GatewayOptions
        {
            Enabled = true, ApiKey = "k", ServiceUsername = "svc",
            AllowSystemPrompt = allowSystemPrompt
        };
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/v1/chat/completions";
        http.Request.Headers.Authorization = "Bearer k";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Response.Body = new MemoryStream();

        var orchestrator = new FakeOrchestrator(deltas);
        var chat = new FakeChat();
        var usage = new FakeUsage();
        // InMemory 上下文无需显式释放；Controller 只是持有引用，这些用例不会查库
        var db = TestDb.Create();

        var controller = new GatewayController(
            Options.Create(options),
            new FakeAuth(new GatewayIdentity(42, "svc", false)),
            orchestrator, chat, new FakePermissions([1L, 2L]), usage,
            db, NullLogger<GatewayController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
        return (controller, http, orchestrator, chat, usage);
    }

    private const string CompletionBody = """
    {
      "model": "smartkb:2,3",
      "messages": [
        {"role": "system", "content": "外部系统提示词"},
        {"role": "user", "content": "保修期多久？"}
      ],
      "kb_ids": [2, 3],
      "temperature": 0.3
    }
    """;

    [Fact]
    public async Task 非流式返回标准OpenAI结构与引用扩展_权限闭包正确()
    {
        var (controller, _, orchestrator, chat, usage) = New(
            CompletionBody,
            [
                new StreamDelta(DeltaKind.Token, "保修期"),
                new StreamDelta(DeltaKind.Token, "12 个月"),
                new StreamDelta(DeltaKind.Citations, string.Empty, Citations),
                new StreamDelta(DeltaKind.Done, "保修期12 个月")
            ]);

        var result = await controller.ChatCompletions(CancellationToken.None);
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(200, content.StatusCode);

        using var doc = JsonDocument.Parse(content.Content!);
        var root = doc.RootElement;
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("smartkb:2,3", root.GetProperty("model").GetString());
        var choice = root.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        var message = choice.GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal("保修期12 个月", message.GetProperty("content").GetString());
        Assert.Equal("合同.pdf",
            message.GetProperty("smartkb_citations")[0].GetProperty("fileName").GetString());
        Assert.True(root.TryGetProperty("usage", out _));

        // 权限范围由服务端给出，请求只能缩小不能扩大；system 默认被忽略
        var call = Assert.IsType<AskCall>(orchestrator.Last);
        Assert.Equal(new long[] { 1, 2 }, call.Accessible);
        Assert.Equal(new long[] { 2, 3 }, call.RequestedKbIds);
        Assert.Equal(42, call.Caller.UserId);
        Assert.False(call.Caller.IsAdmin);
        Assert.Null(call.Options!.SystemPrompt);
        Assert.Equal(0.3f, call.Options.Temperature);

        Assert.Equal("保修期12 个月", chat.SavedAnswer);
        Assert.NotNull(usage.Last);
        Assert.Equal(42, usage.Last!.UserId);
        Assert.Equal("保修期多久？", usage.Last.Question);
    }

    [Fact]
    public async Task 流式输出标准SSE分块_usage与引用扩展()
    {
        const string body = """
        {
          "model": "smartkb",
          "messages": [{"role": "user", "content": "Q"}],
          "stream": true,
          "stream_options": {"include_usage": true}
        }
        """;
        var (controller, http, _, chat, _) = New(body,
        [
            new StreamDelta(DeltaKind.Token, "你"),
            new StreamDelta(DeltaKind.Token, "好"),
            new StreamDelta(DeltaKind.Citations, string.Empty, Citations),
            new StreamDelta(DeltaKind.Done, "你好")
        ]);

        var result = await controller.ChatCompletions(CancellationToken.None);
        Assert.IsType<EmptyResult>(result);

        var sse = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        Assert.StartsWith("data: {", sse);
        Assert.Contains("\"object\":\"chat.completion.chunk\"", sse);
        Assert.Contains("\"content\":\"你\"", sse);
        Assert.Contains("\"smartkb_citations\"", sse);
        Assert.Contains("\"finish_reason\":\"stop\"", sse);
        Assert.Contains("\"usage\":{\"prompt_tokens\"", sse);
        Assert.EndsWith("data: [DONE]\n\n", sse);
        Assert.Contains("text/event-stream", http.Response.Headers.ContentType.ToString());
        Assert.Equal("no", http.Response.Headers["X-Accel-Buffering"].ToString());
        Assert.Equal("你好", chat.SavedAnswer);
    }

    [Fact]
    public async Task 编排器错误以OpenAI错误格式返回()
    {
        var (controller, _, _, _, usage) = New(CompletionBody,
        [
            new StreamDelta(DeltaKind.Error, "上游模型服务不可用")
        ]);

        var result = await controller.ChatCompletions(CancellationToken.None);
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(502, content.StatusCode);
        using var doc = JsonDocument.Parse(content.Content!);
        Assert.Equal("上游模型服务不可用",
            doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.False(usage.Last!.Success);
    }

    [Fact]
    public async Task 允许开关开启时system消息才覆盖系统提示词()
    {
        var (controller, _, orchestrator, _, _) = New(
            CompletionBody,
            [new StreamDelta(DeltaKind.Done, "ok")],
            allowSystemPrompt: true);

        await controller.ChatCompletions(CancellationToken.None);
        Assert.Equal("外部系统提示词", orchestrator.Last!.Options!.SystemPrompt);
    }
}
