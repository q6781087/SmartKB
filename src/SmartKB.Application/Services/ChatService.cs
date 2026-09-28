using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartKB.AI.Agent;
using SmartKB.Application.Dtos;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Application.Services;

/// <summary>问答会话与消息持久化（流式回答内容由 Hub 写入）</summary>
public interface IChatService
{
    Task<ChatSessionDto> CreateSessionAsync(long userId, CreateSessionRequest request);
    Task<List<ChatSessionDto>> GetSessionsAsync(long userId);
    Task<List<ChatMessageDto>> GetMessagesAsync(long userId, long sessionId);
    Task DeleteSessionAsync(long userId, long sessionId);

    /// <summary>写用户消息并返回（流式开始前调用）</summary>
    Task<ChatMessage> AddUserMessageAsync(long userId, long sessionId, string content);

    /// <summary>取最近 historyTake 条历史（供编排器组装多轮上下文）</summary>
    Task<List<(ChatMessageRole Role, string Content)>> GetHistoryAsync(long sessionId, int historyTake = 8);

    /// <summary>流式结束后落库回答 + 引用</summary>
    Task SaveAssistantMessageAsync(long sessionId, string answer, IReadOnlyList<Citation>? citations);

    /// <summary>会话归属校验（非本人会话直接拒绝）</summary>
    Task<ChatSession> GetOwnedSessionAsync(long userId, long sessionId);

    /// <summary>引用序列化为 JSON（与前端约定结构）</summary>
    string SerializeCitations(IReadOnlyList<Citation> citations);
}

public class ChatService(SmartKbDbContext db) : IChatService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ChatSessionDto> CreateSessionAsync(long userId, CreateSessionRequest request)
    {
        var session = new ChatSession
        {
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "新会话" : request.Title.Trim()
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();
        return ToDto(session);
    }

    public async Task<List<ChatSessionDto>> GetSessionsAsync(long userId) =>
        (await db.ChatSessions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.LastActiveAt)
            .ToListAsync())
        .Select(ToDto)
        .ToList();

    public async Task<List<ChatMessageDto>> GetMessagesAsync(long userId, long sessionId)
    {
        _ = await GetOwnedSessionAsync(userId, sessionId);
        return await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .Select(m => new ChatMessageDto(
                m.Id, m.Role, m.Content, m.CitationsJson, m.CreatedAt))
            .ToListAsync();
    }

    public async Task DeleteSessionAsync(long userId, long sessionId)
    {
        var session = await GetOwnedSessionAsync(userId, sessionId);
        db.ChatSessions.Remove(session); // 级联删除消息
        await db.SaveChangesAsync();
    }

    public async Task<ChatMessage> AddUserMessageAsync(long userId, long sessionId, string content)
    {
        var session = await GetOwnedSessionAsync(userId, sessionId);
        var message = new ChatMessage
        {
            SessionId = sessionId,
            Role = ChatMessageRole.User,
            Content = content
        };
        db.ChatMessages.Add(message);
        session.LastActiveAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return message;
    }

    public async Task<List<(ChatMessageRole Role, string Content)>> GetHistoryAsync(
        long sessionId, int historyTake = 8) =>
        (await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Id)
            .Take(historyTake)
            .ToListAsync())
        .OrderBy(m => m.Id)
        .Select(m => (m.Role, m.Content))
        .ToList();

    public async Task SaveAssistantMessageAsync(
        long sessionId, string answer, IReadOnlyList<Citation>? citations)
    {
        db.ChatMessages.Add(new ChatMessage
        {
            SessionId = sessionId,
            Role = ChatMessageRole.Assistant,
            Content = answer,
            CitationsJson = citations is { Count: > 0 } ? SerializeCitations(citations) : null
        });
        await db.SaveChangesAsync();
    }

    public async Task<ChatSession> GetOwnedSessionAsync(long userId, long sessionId) =>
        await db.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId)
        ?? throw new BusinessRuleException("会话不存在或无权访问");

    public string SerializeCitations(IReadOnlyList<Citation> citations) =>
        JsonSerializer.Serialize(citations.Select(c => new CitationDto(
            c.ChunkId, c.DocumentId, c.FileName, c.Seq, c.Title, c.Excerpt, c.Score)), JsonOpts);

    private static ChatSessionDto ToDto(ChatSession s) =>
        new(s.Id, s.Title, s.CreatedAt, s.LastActiveAt);
}
