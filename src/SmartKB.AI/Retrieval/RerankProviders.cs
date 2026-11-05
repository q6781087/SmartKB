using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SmartKB.AI.Retrieval;

/// <summary>重排候选（融合后的片段池）</summary>
public record RerankCandidate(long ChunkId, string Content, float Score);

/// <summary>重排提供者（Name 与配置 rag.rerank_provider 对应）</summary>
public interface IRerankProvider
{
    string Name { get; }
    Task<List<long>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, int topN, CancellationToken ct = default);
}

/// <summary>按配置选择重排实现；外部 API 失败自动降级启发式，不影响问答主流程</summary>
public interface IRerankService
{
    Task<List<long>> RerankAsync(
        string providerName, string query, IReadOnlyList<RerankCandidate> candidates,
        int topN, CancellationToken ct = default);
}

/// <summary>
/// 启发式重排（零外部依赖、默认实现）：
/// 精确词覆盖率（编号/型号/术语命中越多越靠前）+ RRF 融合分 + 长度惩罚。
/// 不追求 SOTA，只解决"融合后第 1 名不是最贴题那段"的常见退化。
/// </summary>
public class HeuristicRerankProvider : IRerankProvider
{
    public string Name => "Heuristic";

    public Task<List<long>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, int topN, CancellationToken ct = default)
    {
        var keywords = HybridFusion.ExtractKeywords(query, max: 8);
        var ranked = candidates.Select(c =>
        {
            var coverage = keywords.Count == 0
                ? 0.0
                : keywords.Count(k => c.Content.Contains(k, StringComparison.OrdinalIgnoreCase)) / (double)keywords.Count;
            // 超长片段容易"沾边但不聚焦"，轻微惩罚。
            // 融合分经 RRF 归一化后普遍在 0.5–1.0，权重必须远小于覆盖率，
            // 否则"融合分高的泛泛片段"永远压住"精确命中的片段"，重排等于没做。
            var lengthPenalty = c.Content.Length > 1200 ? 0.05 : 0.0;
            var score = coverage + c.Score * 0.2 - lengthPenalty;
            return new { c.ChunkId, score };
        })
        .OrderByDescending(x => x.score)
        .ThenBy(x => x.ChunkId)
        .Select(x => x.ChunkId)
        .Take(Math.Max(1, topN))
        .ToList();

        return Task.FromResult(ranked);
    }
}

/// <summary>外部 rerank 模型配置（ApiKey 仅环境变量注入，不落配置文件）</summary>
public class RerankApiOptions
{
    public string BaseUrl { get; set; } = "https://api.siliconflow.cn/v1";
    public string Model { get; set; } = "BAAI/bge-reranker-v2-m3";
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>OpenAI 兼容 /rerank 接口（SiliconFlow / 智谱等），失败降级启发式</summary>
public class ApiRerankProvider(
    IHttpClientFactory factory, RerankApiOptions options, HeuristicRerankProvider fallback,
    ILogger<ApiRerankProvider> logger) : IRerankProvider
{
    public string Name => "Api";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<List<long>> RerankAsync(
        string query, IReadOnlyList<RerankCandidate> candidates, int topN, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            logger.LogWarning("Rerank ApiKey 未配置，降级启发式重排");
            return await fallback.RerankAsync(query, candidates, topN, ct);
        }

        try
        {
            var client = factory.CreateClient("llm-cloud");
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);

            var body = new
            {
                model = options.Model,
                query,
                documents = candidates.Select(c => c.Content).ToArray(),
                top_n = Math.Min(Math.Max(1, topN), candidates.Count)
            };

            using var resp = await client.PostAsJsonAsync("rerank", body, JsonOpts, ct);
            resp.EnsureSuccessStatusCode();
            var payload = await resp.Content.ReadFromJsonAsync<RerankResponse>(JsonOpts, ct)
                ?? throw new InvalidOperationException("Rerank 响应为空");

            var ids = payload.Results
                .OrderByDescending(r => r.RelevanceScore)
                .Where(r => r.Index >= 0 && r.Index < candidates.Count)
                .Select(r => candidates[r.Index].ChunkId)
                .Take(Math.Max(1, topN))
                .ToList();

            return ids.Count > 0 ? ids : await fallback.RerankAsync(query, candidates, topN, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "外部 Rerank 调用失败，降级启发式重排");
            return await fallback.RerankAsync(query, candidates, topN, ct);
        }
    }

    private sealed class RerankResponse
    {
        public List<Item> Results { get; set; } = [];

        public sealed class Item
        {
            public int Index { get; set; }
            [JsonPropertyName("relevance_score")]
            public double RelevanceScore { get; set; }
        }
    }
}

/// <summary>按名称分发；未知名称一律回退 Heuristic，绝不因配置写错让问答失败</summary>
public class RerankService(IEnumerable<IRerankProvider> providers) : IRerankService
{
    public Task<List<long>> RerankAsync(
        string providerName, string query, IReadOnlyList<RerankCandidate> candidates,
        int topN, CancellationToken ct = default)
    {
        var provider = providers.FirstOrDefault(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase))
            ?? providers.First(p => p.Name == "Heuristic");
        return provider.RerankAsync(query, candidates, topN, ct);
    }
}
