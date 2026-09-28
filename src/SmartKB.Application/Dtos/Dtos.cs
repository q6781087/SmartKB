using SmartKB.Domain.Enums;

namespace SmartKB.Application.Dtos;

// ---------- 认证 ----------

public record LoginRequest(string Username, string Password);

public record CurrentUserDto(long Id, string Username, string DisplayName, bool IsAdmin, List<string> Roles);

public record LoginResponse(string Token, int ExpiresInMinutes, CurrentUserDto User);

// ---------- 用户 ----------

public record UserDto(long Id, string Username, string DisplayName, string? Department,
    bool IsAdmin, bool IsActive, DateTime CreatedAt, List<RoleDto> Roles);

public record CreateUserRequest(string Username, string Password, string DisplayName,
    string? Department, bool IsAdmin, List<long> RoleIds);

public record UpdateUserRequest(string DisplayName, string? Department,
    bool IsActive, bool IsAdmin, List<long> RoleIds);

public record ResetPasswordRequest(string NewPassword);

public record ChangePasswordRequest(string OldPassword, string NewPassword);

// ---------- 角色 ----------

public record RoleDto(long Id, string Name, string? Description, bool IsSystem);

public record CreateRoleRequest(string Name, string? Description);

// ---------- 知识库 ----------

public record KnowledgeBaseDto(long Id, string Name, string? Description,
    string FieldSchemaJson, bool EnableLedger, DateTime CreatedAt, int DocumentCount);

public record SaveKnowledgeBaseRequest(string Name, string? Description,
    string FieldSchemaJson, bool EnableLedger);

// ---------- 文档 ----------

public record DocumentDto(long Id, long KbId, string FileName, DocumentType DocType,
    DocumentStatus Status, string? ErrorMessage, DateTime CreatedAt, DateTime? IndexedAt,
    int ChunkCount);

// ---------- 授权 ----------

public record GrantPermissionRequest(long KbId, long? UserId, long? RoleId, PermissionLevel Level);

public record PermissionDto(long Id, long KbId, long? UserId, string? UserDisplayName,
    long? RoleId, string? RoleName, PermissionLevel Level);

/// <summary>知识库自定义字段定义（FieldSchemaJson 的元素结构）</summary>
public record FieldDefinition(string Name, string Label, string Type, bool Filterable);

// ---------- 会话 ----------

public record ChatSessionDto(long Id, string Title, DateTime CreatedAt, DateTime LastActiveAt);

public record CreateSessionRequest(string? Title);

public record ChatMessageDto(long Id, ChatMessageRole Role, string Content,
    string? CitationsJson, DateTime CreatedAt);

public record CitationDto(long ChunkId, long DocumentId, string FileName, int Seq,
    string? Title, string Excerpt, float Score);
