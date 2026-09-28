namespace SmartKB.Application;

/// <summary>业务规则异常 → HTTP 400 + 错误消息</summary>
public class BusinessRuleException(string message) : Exception(message);
