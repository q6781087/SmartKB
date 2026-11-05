namespace SmartKB.Application;

/// <summary>
/// 当前登录用户抽象。接口定义在 Application 层，由 Api 层用 IHttpContextAccessor 实现，
/// 目的是让业务服务层能自己设防，而不是只在控制器上加 Authorize Attribute。
/// </summary>
public interface ICurrentUser
{
    long UserId { get; }

    bool IsAdmin { get; }

    bool IsAuthenticated { get; }
}
