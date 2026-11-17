using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartKB.Domain.Entities;

namespace SmartKB.Infrastructure;

public static class DependencyInjection
{
    /// <summary>注册数据层：DbContext(Npgsql) + 默认种子</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("缺少连接串 ConnectionStrings:Default");

        services.AddDbContext<SmartKbDbContext>(opt =>
            opt.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseVector();
            }));

        return services;
    }

    /// <summary>
    /// 应用迁移 + 种子数据（默认角色与超级管理员）。
    /// 放在启动阶段执行，私有化交付时首次起库自动初始化。
    /// </summary>
    public static async Task SeedAsync(this IServiceProvider provider, string adminPassword)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartKbDbContext>();

        await db.Database.MigrateAsync();

        if (!await db.Roles.AnyAsync())
        {
            db.Roles.AddRange(
                new Role { Name = "admin", Description = "系统管理员", IsSystem = true },
                new Role { Name = "kb_manager", Description = "知识库管理员", IsSystem = true },
                new Role { Name = "user", Description = "普通用户", IsSystem = true });
            await db.SaveChangesAsync();
        }

        if (!await db.Users.AnyAsync())
        {
            var adminRole = await db.Roles.SingleAsync(r => r.Name == "admin");
            db.Users.Add(new User
            {
                Username = "admin",
                // 首次部署必须改密（部署文档注明）
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(adminPassword),
                DisplayName = "系统管理员",
                IsAdmin = true,
                Roles = [adminRole]
            });
            await db.SaveChangesAsync();
        }

        // 存量数据自愈：把 sys_users.IsAdmin 刷成与 admin 角色一致。
        // 统一之前这两个值可独立设置，历史库里可能存在"挂了 admin 角色却 IsAdmin=false"
        // 或"IsAdmin=true 却没有 admin 角色"的错配账号，登录后会表现成权限莫名其妙。
        var allUsers = await db.Users.Include(u => u.Roles).ToListAsync();
        var changed = false;
        foreach (var u in allUsers)
        {
            var expected = u.Roles.HasAdminRole();
            if (u.IsAdmin == expected) continue;
            u.IsAdmin = expected;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync();
    }
}
