using SmartKB.AI.Chunking;
using SmartKB.AI.Parsing;

namespace SmartKB.Tests;

/// <summary>切分边界：超长文本必须切块、每块不超硬上限、无标点文本硬切不丢内容</summary>
public class TextChunkerTests
{
    [Fact]
    public void 长文本_切成多块_且每块不超硬上限()
    {
        var text = string.Concat(Enumerable.Repeat("这是合同第一条款的约定内容。", 120));
        var doc = new ParsedDocument { Blocks = [new ParsedBlock("第一章", text, null)] };

        var chunks = new TextChunker().Split(doc,
            new ChunkOptions { TargetSize = 300, OverlapSize = 50, MaxSize = 600 });

        Assert.True(chunks.Count > 1, "长文本应被切成多块");
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 600, $"块长 {c.Text.Length} 超过硬上限"));
        Assert.All(chunks, c => Assert.Equal("第一章", c.TitlePath));
    }

    [Fact]
    public void 无标点超长文本_硬切且不丢内容()
    {
        var text = new string('甲', 1500);
        var doc = new ParsedDocument { Blocks = [new ParsedBlock("附件", text, null)] };

        var chunks = new TextChunker().Split(doc,
            new ChunkOptions { TargetSize = 300, OverlapSize = 50, MaxSize = 600 });

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 600));
        Assert.True(chunks.Sum(c => c.Text.Length) >= text.Length, "重叠切分后总长度应覆盖原文");
    }

    [Fact]
    public void 中英混排_句界切分保留分隔符()
    {
        var doc = new ParsedDocument
        {
            Blocks = [new ParsedBlock("T", "第一句。Second sentence! 第三句？", null)]
        };

        var chunks = new TextChunker().Split(doc, new ChunkOptions());

        Assert.Single(chunks);
        Assert.Contains("第一句。", chunks[0].Text);
        Assert.Contains("Second sentence!", chunks[0].Text);
        Assert.Contains("第三句？", chunks[0].Text);
    }

    [Fact]
    public void 同标题短块_合并且不超上限()
    {
        var doc = new ParsedDocument
        {
            Blocks =
            [
                new ParsedBlock("第二章", "甲条款内容。", null),
                new ParsedBlock("第二章", "乙条款内容。", null)
            ]
        };

        var chunks = new TextChunker().Split(doc, new ChunkOptions());

        Assert.Single(chunks);
        Assert.Contains("甲条款内容。", chunks[0].Text);
        Assert.Contains("乙条款内容。", chunks[0].Text);
    }
}
