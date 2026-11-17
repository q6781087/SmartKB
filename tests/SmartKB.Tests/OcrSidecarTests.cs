using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartKB.AI.Chunking;
using SmartKB.AI.Configuration;
using SmartKB.AI.Embedding;
using SmartKB.AI.Ingestion;
using SmartKB.AI.Parsing;
using SmartKB.Domain.Entities;
using SmartKB.Domain.Enums;
using SmartKB.Infrastructure;

namespace SmartKB.Tests;

/// <summary>OCR sidecar（v1.6 可选）：门槛纯函数 + 响应解析 + 入库流水线降级闭环</summary>
public class OcrSidecarTests
{
    private static OcrOptions Opts(bool enabled = true, string endpoint = "http://ocr.local/api") => new()
    {
        Enabled = enabled, Endpoint = endpoint, MinTextChars = 120
    };

    [Theory]
    [InlineData(true, true, 2, 119, true)]   // 启用 + PDF + 文本不足 → 触发
    [InlineData(true, true, 2, 120, false)]  // 达到阈值 → 不触发
    [InlineData(true, true, 1, 10, false)]   // 非 PDF（docx）→ 不触发
    [InlineData(false, true, 2, 10, false)]  // 未启用 → 不触发
    [InlineData(true, false, 2, 10, false)]  // 未配置端点 → 不触发
    public void 触发门槛_按文档类型与文本长度判定(
        bool enabled, bool hasEndpoint, int docType, int textChars, bool expected)
    {
        var opts = Opts(enabled, hasEndpoint ? "http://ocr.local/api" : "");
        Assert.Equal(expected, OcrGate.ShouldAttempt(opts, (DocumentType)docType, textChars));
    }

    [Fact]
    public void 响应解析_兼容JSON的text字段与纯文本_并截断()
    {
        Assert.Equal("你好", HttpOcrClient.ParseText("""{"text": "你好"}""", 100));
        Assert.Equal("纯文本结果", HttpOcrClient.ParseText("纯文本结果", 100));
        Assert.Equal(new string('x', 50), HttpOcrClient.ParseText(new string('x', 80), 50));
        Assert.Null(HttpOcrClient.ParseText("", 100));
        Assert.Null(HttpOcrClient.ParseText("   ", 100));
        Assert.Null(HttpOcrClient.ParseText("""{"other": 1}""", 100));
    }

    [Fact]
    public async Task 入库流水线_解析文本过少的PDF触发OCR补文本_状态Indexed()
    {
        var (db, ingest, ocr) = SetupAsync(ocrText: new string('扫', 500));
        await ingest.IngestAsync(5);

        Assert.Equal(DocumentStatus.Indexed, (await db.Documents.SingleAsync()).Status);
        Assert.Equal(1, ocr.Calls);
        // OCR 文本已进入分块（不同标题的块独立成块，OCR 块含识别文本）
        var contents = await db.Chunks.Select(c => c.Content).ToListAsync();
        Assert.Contains(contents, c => c.Contains('扫'));
    }

    [Fact]
    public async Task 入库流水线_未启用OCR_短文本仍正常入库且不触发OCR()
    {
        var (db, ingest, ocr) = SetupAsync(ocrText: null, ocrEnabled: false);
        await ingest.IngestAsync(5);

        // OCR 只影响"文本不足"的补文本，不改变原有失败语义：短文本仍按原路径入库
        Assert.Equal(DocumentStatus.Indexed, (await db.Documents.SingleAsync()).Status);
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task 入库流水线_完全无文本且未启用OCR_按未提取到有效文本失败()
    {
        var (db, ingest, ocr) = SetupAsync(ocrText: null, ocrEnabled: false, emptyParse: true);
        await Assert.ThrowsAnyAsync<Exception>(() => ingest.IngestAsync(5));

        var doc = await db.Documents.SingleAsync();
        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.Contains("有效文本", doc.ErrorMessage);
        Assert.Equal(0, ocr.Calls);
    }

    private static (SmartKbDbContext, IDocumentIngestionService, CountingOcr) SetupAsync(
        string? ocrText, bool ocrEnabled = true, bool emptyParse = false)
    {
        var db = TestDb.Create();
        db.KnowledgeBases.Add(new KnowledgeBase { Id = 10, Name = "设备库" });
        db.Documents.Add(new KbDocument
        {
            Id = 5, KbId = 10, FileName = "scan.pdf", FilePath = "/tmp/scan.pdf",
            DocType = DocumentType.Pdf, Status = DocumentStatus.Uploaded
        });
        db.SaveChanges();

        var ocr = new CountingOcr(ocrText);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ocr:Enabled"] = ocrEnabled ? "true" : "false",
                ["Ocr:Endpoint"] = "http://ocr.local/api",
                ["Ocr:MinTextChars"] = "120"
            }).Build();
        var options = Microsoft.Extensions.Options.Options.Create(OcrOptions.FromConfiguration(configuration));

        var ingest = new DocumentIngestionService(
            db, [new ShortTextParser(emptyParse)],
            new TextChunker(),
            new FixedEmbedding(),
            Microsoft.Extensions.Options.Options.Create(new LlmOptions()),
            ocr, configuration, NullLogger<DocumentIngestionService>.Instance);
        return (db, ingest, ocr);
    }

    /// <summary>模拟扫描件：PDF 解析只吐出不足阈值的文本（emptyParse 时完全无文本）</summary>
    private sealed class ShortTextParser(bool empty = false) : IDocumentParser
    {
        public DocumentType DocType => DocumentType.Pdf;

        public Task<ParsedDocument> ParseAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(empty
                ? new ParsedDocument()
                : new ParsedDocument { Blocks = [new ParsedBlock("扫描件", "模糊不清", 1)] });
    }

    private sealed class CountingOcr(string? text) : IOcrClient
    {
        public int Calls { get; private set; }

        public Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(text);
        }
    }

    private sealed class FixedEmbedding : IEmbeddingProvider
    {
        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult(texts.Select(_ => new float[] { 0.1f, 0.2f, 0.3f, 0.4f }).ToArray());
    }
}
