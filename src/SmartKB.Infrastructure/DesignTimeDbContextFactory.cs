using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartKB.Infrastructure;

/// <summary>
/// EF 迁移设计时工厂：dotnet ef 命令用，不参与运行时。
/// 连接串取环境变量 SmartKB__ConnectionString，缺省用本地默认值。
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SmartKbDbContext>
{
    public SmartKbDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("SMARTKB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=smartkb;Username=smartkb;Password=smartkb";

        var options = new DbContextOptionsBuilder<SmartKbDbContext>()
            .UseNpgsql(conn, npgsql => npgsql.UseVector())
            .Options;

        return new SmartKbDbContext(options);
    }
}
