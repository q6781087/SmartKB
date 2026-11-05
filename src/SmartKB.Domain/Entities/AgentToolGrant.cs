namespace SmartKB.Domain.Entities;

/// <summary>
/// 配置化工具的调用授权（白名单）。语义：
/// - 某工具没有任何授权记录 → 公开，所有可登录用户都能在问答中调用（兼容 v1.4 行为）；
/// - 一旦存在任意授权记录 → 仅被列出的用户 / 角色可用（管理员始终可用）；
/// - UserId 与 RoleId 二选一，由 DbContext 的 check 约束与服务端校验共同保证。
/// </summary>
public class AgentToolGrant
{
    public long Id { get; set; }

    public long ToolId { get; set; }

    public long? UserId { get; set; }

    public long? RoleId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
