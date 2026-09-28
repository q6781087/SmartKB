namespace SmartKB.Domain.Enums;

/// <summary>文档类型</summary>
public enum DocumentType
{
    Docx = 1,
    Pdf = 2,
    Xlsx = 3,
    Txt = 4,
    Md = 5
}

/// <summary>文档解析状态</summary>
public enum DocumentStatus
{
    /// <summary>已上传，待解析</summary>
    Uploaded = 0,

    /// <summary>解析中</summary>
    Parsing = 1,

    /// <summary>已向量化入库</summary>
    Indexed = 2,

    /// <summary>解析失败</summary>
    Failed = 3
}

/// <summary>知识库授权级别</summary>
public enum PermissionLevel
{
    /// <summary>只读（检索 + 问答）</summary>
    Read = 1,

    /// <summary>可管理（上传文档 + 维护字段）</summary>
    Manage = 2
}

/// <summary>聊天消息角色</summary>
public enum ChatMessageRole
{
    User = 1,
    Assistant = 2
}
