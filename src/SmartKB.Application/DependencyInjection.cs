using Microsoft.Extensions.DependencyInjection;
using SmartKB.Application.Services;

namespace SmartKB.Application;

public static class DependencyInjection
{
    /// <summary>注册业务服务（原生 DI，Scoped 跟随请求生命周期）</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IKnowledgeBaseService, KnowledgeBaseService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IChatService, ChatService>();
        return services;
    }
}
