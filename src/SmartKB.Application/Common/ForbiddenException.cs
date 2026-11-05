namespace SmartKB.Application;

/// <summary>越权访问 → HTTP 403（与业务规则异常的 400 区分，便于前端与审计识别）</summary>
public class ForbiddenException(string message = "无权执行该操作") : Exception(message);
