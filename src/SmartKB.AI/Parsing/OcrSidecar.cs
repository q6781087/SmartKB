using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartKB.Domain.Enums;

namespace SmartKB.AI.Parsing;

/// <summary>
/// OCR sidecar 配置（P2 可选项，默认关闭）。
/// 客户自备 HTTP OCR 服务（如 PaddleOCR serving）：multipart/form-data 上传文件，
/// 响应取 JSON 的 text 字段或纯文本。密钥只登记环境变量名，不入库不回显。
/// </summary>
public sealed class OcrOptions
{
    public bool Enabled { get; set; }

    /// <summary>OCR 服务地址，POST multipart/form-data，字段名 file</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>可选：鉴权头的环境变量名（头名固定 X-Api-Key）</summary>
    public string? ApiKeyEnv { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>解析出的文本少于该字符数才触发 OCR（识别扫描件/纯图 PDF）</summary>
    public int MinTextChars { get; set; } = 120;

    /// <summary>OCR 文本上限，防止超长文档挤爆切分器</summary>
    public int MaxTextChars { get; set; } = 200_000;

    public static OcrOptions FromConfiguration(IConfiguration configuration) =>
        configuration.GetSection("Ocr").Get<OcrOptions>() ?? new OcrOptions();
}

public interface IOcrClient
{
    /// <summary>调用 OCR sidecar 提取文本；未启用/失败返回 null（降级，不中断入库流水线）</summary>
    Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default);
}

public sealed class HttpOcrClient(
    IHttpClientFactory httpClientFactory,
    IOptions<OcrOptions> options,
    ILogger<HttpOcrClient> logger) : IOcrClient
{
    public async Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        var opt = options.Value;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.Endpoint)) return null;

        try
        {
            var client = httpClientFactory.CreateClient("ocr");

            using var content = new MultipartFormDataContent();
            await using var fs = File.OpenRead(filePath);
            var fileContent = new StreamContent(fs);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", Path.GetFileName(filePath));

            // 鉴权头从环境变量取，值不进日志
            if (!string.IsNullOrWhiteSpace(opt.ApiKeyEnv) &&
                Environment.GetEnvironmentVariable(opt.ApiKeyEnv) is { Length: > 0 } key)
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", key);

            using var resp = await client.PostAsync(opt.Endpoint, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("OCR sidecar 返回 {Status}，跳过 OCR 补文本", (int)resp.StatusCode);
                return null;
            }

            var raw = await resp.Content.ReadAsStringAsync(ct);
            return ParseText(raw, opt.MaxTextChars);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OCR sidecar 调用失败，跳过 OCR 补文本");
            return null;
        }
    }

    /// <summary>响应兼容两种格式：{"text": "..."} 或纯文本；JSON 对象缺 text 字段视为无效输出</summary>
    public static string? ParseText(string raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc.RootElement.TryGetProperty("text", out var t) &&
                       t.ValueKind == JsonValueKind.String
                    ? Truncate(t.GetString(), maxChars)
                    : null; // 结构化 JSON 却没有 text 字段：按无效输出处理，不把原始 JSON 喂给切分器
            }
        }
        catch (JsonException) { /* 非 JSON，按纯文本处理 */ }
        return Truncate(raw, maxChars);
    }

    private static string? Truncate(string? s, int maxChars) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= maxChars ? s : s[..maxChars];
}

/// <summary>OCR 触发门槛（纯函数，便于单测）：目前仅 PDF 触发——纯图片 PDF 是解析器唯一的已知盲区</summary>
public static class OcrGate
{
    public static bool ShouldAttempt(OcrOptions options, DocumentType docType, int parsedTextChars) =>
        options.Enabled &&
        !string.IsNullOrWhiteSpace(options.Endpoint) &&
        docType == DocumentType.Pdf &&
        parsedTextChars < Math.Max(1, options.MinTextChars);
}
