using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartKB.Api.Controllers;
using SmartKB.Api.Gateway;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>OpenAI 请求体解析与知识库路由（纯函数，不需要起服务）</summary>
public class GatewayProtocolTests
{
    private static GatewayChatRequest Parse(string json, int maxMessages = 32, int maxQuestion = 8000)
    {
        using var doc = JsonDocument.Parse(json);
        return GatewayRequestParser.Parse(doc.RootElement, maxMessages, maxQuestion);
    }

    [Fact]
    public void 解析标准请求_model路由与多轮历史()
    {
        const string json = """
        {
          "model": "smartkb:1,2",
          "messages": [
            {"role": "system", "content": "你是助手"},
            {"role": "user", "content": "上一轮问题"},
            {"role": "assistant", "content": "上一轮回答"},
            {"role": "user", "content": "本轮问题"}
          ],
          "stream": true,
          "stream_options": {"include_usage": true},
          "temperature": 0.5,
          "max_tokens": 1024,
          "session_id": 7
        }
        """;
        var req = Parse(json);

        Assert.Equal("本轮问题", req.Question);
        Assert.Equal("你是助手", req.SystemPrompt);
        Assert.True(req.Stream);
        Assert.True(req.IncludeUsage);
        Assert.Equal(new long[] { 1, 2 }, req.KbIds);
        Assert.Equal(7L, req.SessionId);
        Assert.Equal(0.5f, req.Temperature);
        Assert.Equal(1024, req.MaxTokens);

        var history = GatewayRequestParser.ToHistory(req);
        Assert.Equal(2, history.Count);
        Assert.Equal(SmartKB.Domain.Enums.ChatMessageRole.User, history[0].Role);
        Assert.Equal("上一轮问题", history[0].Content);
        Assert.Equal(SmartKB.Domain.Enums.ChatMessageRole.Assistant, history[1].Role);
    }

    [Fact]
    public void kb_ids优先于model_未知model不报错()
    {
        var a = Parse("""{"model":"smartkb:9","kb_ids":[3,4],"messages":[{"role":"user","content":"Q"}]}""");
        Assert.Equal(new long[] { 3, 4 }, a.KbIds);

        var b = Parse("""{"model":"gpt-4o","messages":[{"role":"user","content":"Q"}]}""");
        Assert.Empty(b.KbIds);
        Assert.False(b.Stream);
        Assert.Null(b.SessionId);

        var c = Parse("""{"model":"smartkb:all","messages":[{"role":"user","content":"Q"}]}""");
        Assert.Empty(c.KbIds);
    }

    [Fact]
    public void 多模态content只取文本片段()
    {
        var req = Parse("""
        {"messages":[{"role":"user","content":[
          {"type":"text","text":"第一段"},
          {"type":"image_url","image_url":{"url":"https://x/y.png"}},
          {"type":"text","text":"第二段"}
        ]}]}
        """);
        Assert.Equal("第一段\n第二段", req.Question);
    }

    [Fact]
    public void 非法请求一律返回可读错误()
    {
        Assert.Throws<GatewayRequestException>(() => Parse("""{"messages":[]}"""));
        Assert.Throws<GatewayRequestException>(() =>
            Parse("""{"messages":[{"role":"assistant","content":"A"}]}"""));
        Assert.Throws<GatewayRequestException>(() =>
            Parse("""{"model":"smartkb:a","messages":[{"role":"user","content":"Q"}]}"""));
        Assert.Throws<GatewayRequestException>(() =>
            Parse("""{"kb_ids":["x"],"messages":[{"role":"user","content":"Q"}]}"""));
        Assert.Throws<GatewayRequestException>(() =>
            Parse("""{"temperature":9,"messages":[{"role":"user","content":"Q"}]}"""));
        Assert.Throws<GatewayRequestException>(() =>
            Parse("""{"max_tokens":1,"messages":[{"role":"user","content":"Q"}]}"""));
        Assert.Throws<GatewayRequestException>(() =>
            Parse("""{"messages":[{"role":"user","content":"一二三四五六七八九十"}]}""",
                maxQuestion: 5));
        Assert.Throws<GatewayRequestException>(() => Parse("""
        {"messages":[{"role":"user","content":"1"},{"role":"user","content":"2"},
                     {"role":"user","content":"3"},{"role":"user","content":"4"}]}
        """, maxMessages: 3));
    }

    [Fact]
    public void 配置项必须三者齐备才算启用()
    {
        Assert.False(new GatewayOptions { Enabled = true, ApiKey = "k" }.Configured);
        Assert.False(new GatewayOptions { Enabled = true, ServiceUsername = "svc" }.Configured);
        Assert.False(new GatewayOptions { ApiKey = "k", ServiceUsername = "svc" }.Configured);
        Assert.True(new GatewayOptions
        {
            Enabled = true, ApiKey = "k", ServiceUsername = "svc"
        }.Configured);
    }
}

/// <summary>网关鉴权：静态 Key 绑定服务账号、定长比较、配置不完整时不可用</summary>
public class GatewayAuthTests
{
    private static (SmartKbDbContext Db, GatewayAuthenticator Auth) NewAuth(
        GatewayOptions options, bool createUser = true)
    {
        var db = TestDb.Create();
        if (createUser)
        {
            db.Users.Add(new User
            {
                Username = "svc", DisplayName = "接口服务账号",
                PasswordHash = "x", IsActive = true, IsAdmin = false
            });
            db.SaveChanges();
        }
        var auth = new GatewayAuthenticator(
            Options.Create(options), db, NullLogger<GatewayAuthenticator>.Instance);
        return (db, auth);
    }

    private static GatewayOptions Ready() => new()
    {
        Enabled = true, ApiKey = "s3cr3t", ServiceUsername = "svc"
    };

    [Fact]
    public async Task 静态Key绑定服务账号且身份不含密钥()
    {
        var (db, auth) = NewAuth(Ready());
        using (db)
        {
            Assert.True(auth.IsReady);
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = "Bearer s3cr3t";

            var identity = await auth.AuthenticateAsync(ctx, CancellationToken.None);
            Assert.NotNull(identity);
            Assert.Equal("svc", identity.Username);
            Assert.False(identity.IsAdmin);
            Assert.DoesNotContain("s3cr3t", identity.ToString());
        }
    }

    [Fact]
    public async Task 服务账号缺失视为配置错误而非认证失败()
    {
        var (db, auth) = NewAuth(Ready(), createUser: false);
        using (db)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = "Bearer s3cr3t";
            await Assert.ThrowsAsync<GatewayConfigurationException>(() =>
                auth.AuthenticateAsync(ctx, CancellationToken.None));
        }
    }

    [Fact]
    public void 密钥定长比较_不因长度或前缀误判()
    {
        Assert.True(GatewayAuthenticator.KeyEquals("token-123", "token-123"));
        Assert.False(GatewayAuthenticator.KeyEquals("token-123", "token-124"));
        Assert.False(GatewayAuthenticator.KeyEquals("token", "token-123"));
        Assert.False(GatewayAuthenticator.KeyEquals("", "token-123"));
    }
}

public class GatewayControllerTests
{
    private sealed class FakeAuth(bool ready) : IGatewayAuthenticator
    {
        public bool IsReady => ready;

        public Task<GatewayIdentity?> AuthenticateAsync(HttpContext http, CancellationToken ct) =>
            Task.FromResult<GatewayIdentity?>(new GatewayIdentity(1, "x", false));
    }

    [Fact]
    public async Task 网关默认关闭时返回OpenAI格式错误()
    {
        var controller = new GatewayController(
            Options.Create(new GatewayOptions()), new FakeAuth(false),
            null!, null!, null!, null!, null!, NullLogger<GatewayController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Models(CancellationToken.None);
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(503, content.StatusCode);
        Assert.Contains("\"error\"", content.Content!);
        Assert.Contains("server_error", content.Content!);
    }
}
