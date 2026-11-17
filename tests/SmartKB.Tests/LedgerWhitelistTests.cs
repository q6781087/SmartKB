using SmartKB.AI.Retrieval;
using SmartKB.Domain.Entities;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>台账查询安全边界：字段名必须来自真实数据白名单，操作符只允许固定集合</summary>
public class LedgerWhitelistTests
{
    private static async Task<(SmartKbDbContext Db, LedgerQueryService Service)> SetupAsync()
    {
        var db = TestDb.Create();
        db.LedgerRows.Add(new KbLedgerRow
        {
            Id = 1,
            KbId = 7,
            SourceDocumentId = 1,
            RowDataJson = """{"合同编号":"HT-001","金额":"100"}"""
        });
        await db.SaveChangesAsync();
        return (db, new LedgerQueryService(db));
    }

    [Fact]
    public async Task 伪造字段_被白名单拒绝()
    {
        var (db, svc) = await SetupAsync();
        await using var scope = db;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.QueryAsync(7, [new LedgerFilter("恶意字段", "eq", "x")]));
        Assert.Contains("非法台账字段", ex.Message);
    }

    [Fact]
    public async Task 危险操作符_被固定集合拒绝()
    {
        var (db, svc) = await SetupAsync();
        await using var scope = db;

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.QueryAsync(7, [new LedgerFilter("合同编号", "drop table", "x")]));
        Assert.Contains("非法操作符", ex.Message);
    }

    [Fact]
    public async Task 字段白名单_只来自真实数据键()
    {
        var (db, svc) = await SetupAsync();
        await using var scope = db;

        var fields = await svc.GetLedgerFieldsAsync(7);
        Assert.Contains("合同编号", fields);
        Assert.Contains("金额", fields);
        Assert.DoesNotContain("恶意字段", fields);
    }

    [Fact]
    public async Task 无过滤条件_直接拒绝()
    {
        var (db, svc) = await SetupAsync();
        await using var scope = db;

        await Assert.ThrowsAsync<ArgumentException>(() => svc.QueryAsync(7, []));
    }
}
