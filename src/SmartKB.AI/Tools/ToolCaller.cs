namespace SmartKB.AI.Tools;

/// <summary>
/// 本次问答的调用者身份。AI 层不引用用户体系，只拿最小必要信息：
/// 用户 ID、是否管理员；角色 ID 由注册表按 UserId 现查（不信任 JWT 里可能过期的角色声明）。
/// SignalR Hub 与 OpenAI 兼容网关都必须显式传入，避免出现"没人认领"的工具调用。
/// </summary>
public record ToolCaller(long UserId, bool IsAdmin);
