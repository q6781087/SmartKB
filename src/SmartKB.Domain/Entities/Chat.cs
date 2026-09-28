using SmartKB.Domain.Enums;

namespace SmartKB.Domain.Entities;

/// <summary>问答会话</summary>
public class ChatSession
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public User? User { get; set; }

    public string Title { get; set; } = "新会话";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}

/// <summary>问答消息</summary>
public class ChatMessage
{
    public long Id { get; set; }

    public long SessionId { get; set; }

    public ChatSession? Session { get; set; }

    public ChatMessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>引用溯源（JSON 数组：chunkId、文档名、片段内容、seq 定位）</summary>
    public string? CitationsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>运行时配置中心（管理后台可视化修改）</summary>
public class SysSetting
{
    /// <summary>配置键（主键）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>配置值（JSON）</summary>
    public string ValueJson { get; set; } = "{}";

    public string? Description { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
