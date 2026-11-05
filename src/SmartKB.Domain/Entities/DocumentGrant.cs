namespace SmartKB.Domain.Entities;

/// <summary>
/// 文档级授权（P2）。语义与工具白名单一致：
/// 文档没有任何授权记录 = 跟随知识库权限（库内 Read 用户都可见）；
/// 一旦有记录 = 仅白名单用户/角色（+ 库管理员）可见。
/// </summary>
public class KbDocumentGrant
{
    public long Id { get; set; }

    public long DocumentId { get; set; }

    /// <summary>授权给用户（与 RoleId 互斥）</summary>
    public long? UserId { get; set; }

    /// <summary>授权给角色</summary>
    public long? RoleId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
