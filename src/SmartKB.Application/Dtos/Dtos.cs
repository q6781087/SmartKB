using System.Text.Json;
using SmartKB.AI.Configuration;
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

// ---------- AI 观测 ----------

/// <summary>观测汇总（近 N 天）</summary>
public record AiUsageSummaryDto(
    int TotalInvocations, int SuccessInvocations, double SuccessRate,
    long? TotalTokens, long? InputTokens, long? OutputTokens,
    double? AvgFirstTokenMs, double? AvgTotalMs, int DistinctUsers);

/// <summary>按天聚合</summary>
public record AiDailyUsageDto(
    DateTime Date, int Invocations, int Failures, long? Tokens, double? AvgTotalMs);

/// <summary>调用明细</summary>
public record AiInvocationDto(
    long Id, long SessionId, string UserName, string Model, string Question,
    int? InputTokens, int? OutputTokens, int? TotalTokens,
    int? FirstTokenMs, int TotalMs, int ToolCalls, int RetrievedChunks,
    bool Success, string? ErrorMessage, DateTime CreatedAt);

// ---------- 运行时配置中心 ----------

/// <summary>后台配置项（含当前值、来源与更新时间）</summary>
public record SettingItemDto(
    string Key, string Label, string Group, string Type, string Description,
    JsonElement Value, string Source, DateTime? UpdatedAt);

public record SettingsResponse(List<SettingItemDto> Items, RagSettings Effective);

// ---------- 审计日志 ----------

public record AuditLogDto(
    long Id, long? UserId, string? UserName, string Category, string Action,
    string Method, string Path, int StatusCode, int DurationMs,
    string? IpAddress, string? UserAgent, string? ErrorMessage,
    bool Success, DateTime CreatedAt);

public record AuditLogQuery(
    int Page, int PageSize, string? Category, long? UserId,
    bool? Success, string? Keyword, DateTime? From, DateTime? To);

public record AuditLogPageDto(
    List<AuditLogDto> Items, int Total, int Page, int PageSize,
    int TodayCount, int TodayFailures);

// ---------- 配置化 Agent 工具（P2） ----------

/// <summary>后台列表项。SecretConfigured 只表示环境变量已设置，绝不回显密钥值</summary>
public record AgentToolDto(
    long Id, string Name, string DisplayName, string Description,
    string HttpMethod, string UrlTemplate, string? BodyTemplate, string ParametersJson,
    string? AuthHeaderName, string? AuthEnvVar, string? AuthValuePrefix,
    int TimeoutSeconds, int MaxResponseChars, bool Enabled, int SortOrder,
    bool SecretConfigured, bool Restricted, List<long> AllowedKbIds,
    DateTime CreatedAt, DateTime UpdatedAt);

public record AgentToolUpsertDto(
    string Name, string DisplayName, string Description,
    string HttpMethod, string UrlTemplate, string? BodyTemplate, string ParametersJson,
    string? AuthHeaderName, string? AuthEnvVar, string? AuthValuePrefix,
    int TimeoutSeconds, int MaxResponseChars, bool Enabled, int SortOrder,
    List<long>? AllowedKbIds = null);

public record AgentToolTestDto(long Id, Dictionary<string, JsonElement> Arguments);

public record AgentToolTestResultDto(bool Success, int ElapsedMs, string Output);

/// <summary>工具调用白名单；空列表表示"公开"（所有登录用户可用）</summary>
public record SetToolGrantsDto(List<long> UserIds, List<long> RoleIds);

public record ToolGrantOptionDto(long Id, string Label);

public record AgentToolGrantsDto(
    long ToolId, List<long> UserIds, List<long> RoleIds,
    List<ToolGrantOptionDto> AvailableUsers, List<ToolGrantOptionDto> AvailableRoles);

// ---------- 文档级授权（P2） ----------

/// <summary>语义与工具白名单一致：无记录 = 跟随库权限；有记录 = 仅白名单用户/角色（+库管理员）可见</summary>
public record SetDocumentGrantsDto(List<long> UserIds, List<long> RoleIds);

public record DocumentGrantsDto(
    long DocumentId, List<long> UserIds, List<long> RoleIds,
    List<ToolGrantOptionDto> AvailableUsers, List<ToolGrantOptionDto> AvailableRoles);
