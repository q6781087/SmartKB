using System.Text.Json;
using SmartKB.AI.Retrieval;

namespace SmartKB.Tests;

/// <summary>
/// P1 检索增强纯函数测试：元数据白名单、操作符匹配、RRF 融合、关键词提取、启发式重排。
/// 不依赖数据库，直接覆盖"LLM 产参数 → 服务端校验 → SQL 下推"这条链路上最易出错的部分。
/// </summary>
public class RagRetrievalTests
{
    private const string Schema = """
        [{"name":"contract_year","label":"合同年度","type":"number","filterable":true},
         {"name":"dept","label":"部门","type":"string","filterable":false},
         {"name":"bad.name","label":"注入字段名","type":"string","filterable":true}]
        """;

    [Fact]
    public void 可过滤字段_排除显式关闭与非法字段名_并跨库去重()
    {
        var other = """[{"name":"contract_year","label":"年度"},{"name":"region","label":"区域"}]""";

        var fields = MetadataFilterEngine.ParseFilterableFields([Schema, other, "{坏JSON"]);

        Assert.Equal(["contract_year", "region"], fields.Select(f => f.Name));
        Assert.Equal("合同年度", fields[0].Label); // 先出现的库优先
    }

    [Fact]
    public void 空Schema与坏JSON_返回空列表_不抛异常()
    {
        Assert.Empty(MetadataFilterEngine.ParseFilterableFields(["", "[]", "{}", "{bad"]));
        Assert.Contains("未配置可过滤字段", MetadataFilterEngine.Describe([]));
    }

    [Fact]
    public void 解析filters_兼容空值与单对象_数字值转字符串()
    {
        Assert.Empty(MetadataFilterEngine.ParseFilters(null));
        Assert.Empty(MetadataFilterEngine.ParseFilters("  "));
        Assert.Empty(MetadataFilterEngine.ParseFilters("[]"));
        Assert.Empty(MetadataFilterEngine.ParseFilters("{}"));
        Assert.Empty(MetadataFilterEngine.ParseFilters("null"));

        var one = Assert.Single(MetadataFilterEngine.ParseFilters(
            """{"field":"contract_year","op":"gte","value":2025}"""));
        Assert.Equal("contract_year", one.Field);
        Assert.Equal("gte", one.Op);
        Assert.Equal("2025", one.Value);

        var many = MetadataFilterEngine.ParseFilters(
            """[{"field":"contract_year","op":"gte","value":"2025"},{"field":"region","op":"eq","value":"华东"}]""");
        Assert.Equal(2, many.Count);
        Assert.Equal("华东", many[1].Value);

        Assert.ThrowsAny<JsonException>(() => MetadataFilterEngine.ParseFilters("[{bad"));
        Assert.Throws<ArgumentException>(() => MetadataFilterEngine.ParseFilters("\"字符串\""));
    }

    [Fact]
    public void 白名单校验_拒绝未知字段注入字段非法操作符与超量条件()
    {
        var allowed = MetadataFilterEngine.ParseFilterableFields([Schema]);
        MetadataFilterEngine.Validate([new MetadataFilter("contract_year", "GTE", "2025")], allowed); // 大小写不敏感

        Assert.Throws<ArgumentException>(() =>
            MetadataFilterEngine.Validate([new MetadataFilter("secret", "eq", "x")], allowed));
        Assert.Throws<ArgumentException>(() =>
            MetadataFilterEngine.Validate([new MetadataFilter("a;DROP TABLE", "eq", "x")], allowed));
        Assert.Throws<ArgumentException>(() =>
            MetadataFilterEngine.Validate([new MetadataFilter("contract_year", "drop", "x")], allowed));
        Assert.Throws<ArgumentException>(() =>
            MetadataFilterEngine.Validate([new MetadataFilter("contract_year", "eq", new string('x', 257))], allowed));
        Assert.Throws<ArgumentException>(() =>
            MetadataFilterEngine.Validate(
                Enumerable.Range(0, 9).Select(_ => new MetadataFilter("contract_year", "eq", "2025")).ToList(),
                allowed));
    }

    [Fact]
    public void 元数据匹配_六种操作符与多条件AND()
    {
        const string meta = """{"contract_year":"2025","dept":"财务部","title":"设备采购合同"}""";

        Assert.True(MetadataFilterEngine.Matches(meta, [new MetadataFilter("contract_year", "eq", "2025")]));
        Assert.True(MetadataFilterEngine.Matches(meta, [new MetadataFilter("title", "contains", "采购")]));
        Assert.True(MetadataFilterEngine.Matches(meta, [new MetadataFilter("contract_year", "gte", "2025")]));
        Assert.True(MetadataFilterEngine.Matches(meta, [new MetadataFilter("contract_year", "gt", "2024")]));
        Assert.True(MetadataFilterEngine.Matches(meta, [new MetadataFilter("contract_year", "lt", "2026")]));
        Assert.True(MetadataFilterEngine.Matches(meta, [new MetadataFilter("contract_year", "lte", "2025")]));
        Assert.True(MetadataFilterEngine.Matches(meta,
        [
            new MetadataFilter("contract_year", "gte", "2025"),
            new MetadataFilter("dept", "eq", "财务部")
        ]));

        // AND 语义：任一条件不满足即排除
        Assert.False(MetadataFilterEngine.Matches(meta,
        [
            new MetadataFilter("contract_year", "eq", "2025"),
            new MetadataFilter("dept", "eq", "法务部")
        ]));
        Assert.False(MetadataFilterEngine.Matches(meta, [new MetadataFilter("missing", "eq", "x")]));
        Assert.False(MetadataFilterEngine.Matches("不是JSON", [new MetadataFilter("contract_year", "eq", "2025")]));
        Assert.False(MetadataFilterEngine.Matches("[]", [new MetadataFilter("contract_year", "eq", "2025")]));
        Assert.True(MetadataFilterEngine.Matches(meta, [])); // 无过滤条件不排除任何文档
    }

    [Fact]
    public void 元数据匹配_字段名大小写不敏感兜底()
    {
        const string meta = """{"ContractYear":2026}""";
        Assert.True(MetadataFilterEngine.Matches(meta, [new MetadataFilter("contractyear", "eq", "2026")]));
    }

    [Fact]
    public void RRF融合_双路共同命中排最前_且保留全部候选()
    {
        var fused = HybridFusion.RrfFuse([1L, 2L, 3L], [3L, 1L, 4L], k: 60);

        Assert.Equal(4, fused.Count);
        Assert.Equal(1L, fused[0].ChunkId); // 两路都靠前
        Assert.Equal(3L, fused[1].ChunkId);
        Assert.True(fused[0].Score > fused[1].Score);
        Assert.True(fused[1].Score > fused[2].Score);
    }

    [Fact]
    public void 单路融合_保持原始排名_分数递减()
    {
        var fused = HybridFusion.Single([10L, 20L, 30L], k: 60);

        Assert.Equal([10L, 20L, 30L], fused.Select(x => x.ChunkId));
        Assert.True(fused[0].Score > fused[1].Score);
        Assert.Empty(HybridFusion.RrfFuse([], [], 60));
    }

    [Fact]
    public void 关键词提取_保留编号型号_过滤虚词_并受上限约束()
    {
        var keywords = HybridFusion.ExtractKeywords("合同编号 HT-2025-001 的金额是多少？设备型号 AB/C-9", max: 8);

        Assert.Contains("HT-2025-001", keywords);
        Assert.Contains("合同编号", keywords);
        Assert.Contains("AB/C-9", keywords);
        Assert.DoesNotContain(keywords, k => k.Contains('的')); // 含虚词的中文词组被过滤
        Assert.True(keywords.Count <= 8);
        Assert.Empty(HybridFusion.ExtractKeywords("  "));
        Assert.Equal(2, HybridFusion.ExtractKeywords("合同 编号 金额 条款", max: 2).Count);
    }

    [Fact]
    public async Task 启发式重排_精确命中的片段压过融合分高但不贴题的片段()
    {
        var candidates = new List<RerankCandidate>
        {
            new(1, "与问题无关的一般性描述", 0.95f),
            new(2, "合同编号 HT-2025-001 的金额条款", 0.30f)
        };

        var ranked = await new HeuristicRerankProvider()
            .RerankAsync("HT-2025-001 合同金额", candidates, topN: 2);

        Assert.Equal(2L, ranked[0]);
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public async Task 启发式重排_topN截断_无关键词时退化为按融合分排序()
    {
        var provider = new HeuristicRerankProvider();
        var candidates = new List<RerankCandidate>
        {
            new(1, "甲内容", 0.1f), new(2, "乙内容", 0.9f), new(3, "丙内容", 0.5f)
        };

        var top1 = await provider.RerankAsync("HT-2025-001 合同", candidates, topN: 1);
        Assert.Single(top1);

        // 单字中文不产生关键词 → coverage 全 0，只剩融合分
        var byScore = await provider.RerankAsync("啊", candidates, topN: 3);
        Assert.Equal([2L, 3L, 1L], byScore);
        Assert.Empty(await provider.RerankAsync("x", [], topN: 3));
    }

    [Fact]
    public async Task 未知重排实现_回退启发式_不因配置写错而中断问答()
    {
        var service = new RerankService([new HeuristicRerankProvider()]);
        var ranked = await service.RerankAsync("NotExistProvider", "合同金额",
            [new RerankCandidate(7, "合同金额 500 万", 0.6f)], topN: 1);

        Assert.Equal([7L], ranked);
    }
}
