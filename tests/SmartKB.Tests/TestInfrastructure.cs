using Microsoft.EntityFrameworkCore;
using SmartKB.Application;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

internal static class TestDb
{
    /// <summary>每个用例独立内存库，避免测试间状态串味</summary>
    public static SmartKbDbContext Create()
    {
        var options = new DbContextOptionsBuilder<SmartKbDbContext>()
            .UseInMemoryDatabase($"smartkb-test-{Guid.NewGuid():N}")
            .Options;
        return new SmartKbDbContext(options);
    }
}

internal sealed class FakeCurrentUser(long userId, bool isAdmin = false) : ICurrentUser
{
    public long UserId { get; } = userId;
    public bool IsAdmin { get; } = isAdmin;
    public bool IsAuthenticated => true;
}
