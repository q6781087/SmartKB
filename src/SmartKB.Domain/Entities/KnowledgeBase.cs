using SmartKB.Domain.Enums;

namespace SmartKB.Domain.Entities;

/// <summary>知识库</summary>
public class KnowledgeBase
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// 客户自定义元数据字段定义（JSON 数组）：
    /// [{ "name": "contract_no", "label": "合同编号", "type": "string", "filterable": true }]
    /// </summary>
    public string FieldSchemaJson { get; set; } = "[]";

    /// <summary>台账双轨入库开关：开启后 xlsx 同时生成 RAG 分块与结构化行</summary>
    public bool EnableLedger { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<KbDocument> Documents { get; set; } = new List<KbDocument>();

    public ICollection<KbPermission> Permissions { get; set; } = new List<KbPermission>();
}

/// <summary>知识库授权（用户或角色二选一）</summary>
public class KbPermission
{
    public long Id { get; set; }

    public long KbId { get; set; }

    public KnowledgeBase? KnowledgeBase { get; set; }

    /// <summary>授权给用户（与 RoleId 互斥，均空 = 拒绝所有）</summary>
    public long? UserId { get; set; }

    public User? User { get; set; }

    /// <summary>授权给角色</summary>
    public long? RoleId { get; set; }

    public Role? Role { get; set; }

    public PermissionLevel Level { get; set; } = PermissionLevel.Read;
}
