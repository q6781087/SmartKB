namespace SmartKB.Domain.Entities;

/// <summary>
/// 操作审计日志：记录所有状态变更类请求（登录、用户/角色/知识库/文档/授权/配置变更）。
///
/// 安全边界：**只记元数据，不记请求体与响应体**——避免密码、ApiKey、文档正文入库。
/// 由 Api 层全局 ActionFilter 自动落库，业务服务零侵入。
/// </summary>
public class SysAuditLog
{
    public long Id { get; set; }

    /// <summary>操作人 ID；登录等匿名请求为 null</summary>
    public long? UserId { get; set; }

    public string? UserName { get; set; }

    /// <summary>业务分类：auth / users / roles / kbs / documents / permissions / settings / chats</summary>
    public string Category { get; set; } = "other";

    /// <summary>语义化动作（如 create_kb、reset_password），未命中映射时为归一化后的 "METHOD path"</summary>
    public string Action { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    /// <summary>请求路径（含 query string，已限长）</summary>
    public string Path { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public int DurationMs { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>失败原因（仅异常消息，截断），成功为 null</summary>
    public string? ErrorMessage { get; set; }

    public bool Success { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
