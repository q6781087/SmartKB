using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartKB.AI.Embedding;

/// <summary>LLM 部署配置（与 appsettings Llm 节对应；ApiKey 仅环境变量注入）</summary>
public class LlmOptions
{
    /// <summary>Agnes（默认）/ Cloud（智谱）/ Local（Ollama）</summary>
    public string Mode { get; set; } = "Agnes";

    public AgnesLlmOptions Agnes { get; set; } = new();
    public CloudLlmOptions Cloud { get; set; } = new();
    public LocalLlmOptions Local { get; set; } = new();
}

/// <summary>Agnes AI（OpenAI 兼容协议；仅 Chat，无 Embedding，向量化回退 Cloud 智谱）</summary>
public class AgnesLlmOptions
{
    public string BaseUrl { get; set; } = "https://apihub.agnes-ai.com/v1";
    public string ChatModel { get; set; } = "agnes-3.0-flash";
    public string ApiKey { get; set; } = string.Empty;
}

public class CloudLlmOptions
{
    public string BaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4";
    public string ChatModel { get; set; } = "glm-4.7";
    public string EmbeddingModel { get; set; } = "embedding-3";
    public int EmbeddingDims { get; set; } = 1024;
    public string ApiKey { get; set; } = string.Empty;
}

public class LocalLlmOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "qwen3:14b";
    public string EmbeddingModel { get; set; } = "bge-m3";
}

/// <summary>Embedding 服务抽象：云 API（智谱）与本地推理（Ollama）双实现，配置切换</summary>
public interface IEmbeddingProvider
{
    /// <summary>单批向量化（调用方控制批量大小，建议 ≤32）</summary>
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>智谱 / OpenAI 兼容 embeddings 接口</summary>
public class CloudEmbeddingProvider(IHttpClientFactory factory, LlmOptions options) : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(options.Cloud.ApiKey))
            throw new InvalidOperationException("Llm:Cloud:ApiKey 未配置（应通过环境变量注入）");

        var client = factory.CreateClient("llm-cloud");
        // BaseAddress 必须以 / 结尾，否则相对路径拼接会丢弃最后一段（.../v4 + embeddings → .../paas/embeddings 404）
        client.BaseAddress = new Uri(options.Cloud.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Cloud.ApiKey);

        var body = new
        {
            model = options.Cloud.EmbeddingModel,
            input = texts,
            dimensions = options.Cloud.EmbeddingDims
        };

        // 429/5xx 指数退避重试（智谱低并发档位容易触发限流）
        for (var attempt = 1; ; attempt++)
        {
            using var resp = await client.PostAsJsonAsync("embeddings", body, JsonOpts, ct);

            if (!resp.IsSuccessStatusCode && ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500))
            {
                if (attempt >= 4)
                    resp.EnsureSuccessStatusCode(); // 重试耗尽，抛出原始错误
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); // 2s/4s/8s
                continue;
            }
            resp.EnsureSuccessStatusCode();

            var payload = await resp.Content.ReadFromJsonAsync<OpenAiEmbeddingResponse>(JsonOpts, ct)
                ?? throw new InvalidOperationException("Embedding 响应为空");

            // 按 index 归位，保证与输入顺序一致
            var data = payload.Data.OrderBy(d => d.Index).ToArray();
            if (data.Length != texts.Count)
                throw new InvalidOperationException($"Embedding 返回数量不符：期望 {texts.Count}，实际 {data.Length}");

            return data.Select(d => d.Embedding).ToArray();
        }
    }

    private sealed class OpenAiEmbeddingResponse
    {
        public List<Item> Data { get; set; } = [];

        public sealed class Item
        {
            public int Index { get; set; }
            public float[] Embedding { get; set; } = [];
        }
    }
}

/// <summary>Ollama /api/embed（本地推理，数据不出内网）</summary>
public class LocalEmbeddingProvider(IHttpClientFactory factory, LlmOptions options) : IEmbeddingProvider
{
    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var client = factory.CreateClient("llm-local");
        client.BaseAddress = new Uri(options.Local.BaseUrl.TrimEnd('/') + "/");

        var body = new { model = options.Local.EmbeddingModel, input = texts };

        using var resp = await client.PostAsJsonAsync("api/embed", body, ct);
        resp.EnsureSuccessStatusCode();

        var payload = await resp.Content.ReadFromJsonAsync<OllamaEmbedResponse>(JsonContext.Opts, ct)
            ?? throw new InvalidOperationException("Embedding 响应为空");

        if (payload.Embeddings.Count != texts.Count)
            throw new InvalidOperationException($"Embedding 返回数量不符：期望 {texts.Count}，实际 {payload.Embeddings.Count}");

        return payload.Embeddings.ToArray();
    }

    private sealed class OllamaEmbedResponse
    {
        public List<float[]> Embeddings { get; set; } = [];
    }
}

internal static class JsonContext
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
}
