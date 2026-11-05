using Microsoft.Extensions.DependencyInjection;
using SmartKB.AI.Configuration;
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
        services.AddScoped<IAiUsageService, AiUsageService>();
        services.AddScoped<IKbAccessGuard, KbAccessGuard>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAgentToolService, AgentToolService>();

        // 运行时配置中心：缓存为 Singleton，服务本身 Scoped（依赖 DbContext）。
        // 先注册 IRagSettingsProvider，AI 层的 TryAddSingleton 不会覆盖它。
        services.AddSingleton<SettingsCache>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IRagSettingsProvider>(sp => sp.GetRequiredService<ISettingsService>());
        return services;
    }
}
