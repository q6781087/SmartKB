using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartKB.AI.Chunking;
using SmartKB.AI.Embedding;
using SmartKB.AI.Ingestion;
using SmartKB.AI.Parsing;

namespace SmartKB.AI;

public static class DependencyInjection
{
    /// <summary>注册 AI 层：解析器、切分器、Embedding（按 Llm:Mode 切换）、入库流水线与后台服务</summary>
    public static IServiceCollection AddAiInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        var llm = configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
        services.AddSingleton(llm);

        // 解析器（按 DocType 分发）
        services.AddSingleton<IDocumentParser, DocxDocumentParser>();
        services.AddSingleton<IDocumentParser, PdfDocumentParser>();
        services.AddSingleton<IDocumentParser, XlsxDocumentParser>();
        services.AddSingleton<IDocumentParser>(sp =>
            new PlainTextDocumentParser(SmartKB.Domain.Enums.DocumentType.Txt));
        services.AddSingleton<IDocumentParser>(sp =>
            new PlainTextDocumentParser(SmartKB.Domain.Enums.DocumentType.Md));

        services.AddSingleton<TextChunker>();

        // OCR sidecar（可选，默认关闭；解析文本过少的 PDF 触发客户自备 OCR 服务）
        services.Configure<Parsing.OcrOptions>(configuration.GetSection("Ocr"));
        services.AddHttpClient("ocr", c =>
            c.Timeout = TimeSpan.FromSeconds(
                Math.Clamp(configuration.GetSection("Ocr").Get<Parsing.OcrOptions>()?.TimeoutSeconds ?? 60, 5, 300)));
        services.AddSingleton<Parsing.IOcrClient, Parsing.HttpOcrClient>();

        // HttpClient 工厂
        services.AddHttpClient("llm-cloud");
        services.AddHttpClient("llm-local");

        // 配置化 HTTP 工具：禁止自动跟随重定向，避免绕过 Tools:AllowedHosts 白名单
        services.AddHttpClient("agent-tools")
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AllowAutoRedirect = false
            });

        // Embedding 模式切换：Local 用 Ollama；Agnes 无 Embedding 模型，回退智谱 Cloud embeddings
        if (llm.Mode.Equals("Local", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmbeddingProvider, LocalEmbeddingProvider>();
        else
            services.AddSingleton<IEmbeddingProvider, CloudEmbeddingProvider>();

        // 入库流水线：队列模式按 SmartKB:QueueMode 切换（Memory=单实例默认；Database=多实例安全）
        var queueMode = QueueModeOptions.Resolve(configuration["SmartKB:QueueMode"]);
        services.AddSingleton<DocumentQueue>();
        services.AddSingleton<DatabaseDocumentQueue>();
        services.AddSingleton<IDocumentQueue>(sp => queueMode == QueueMode.Database
            ? sp.GetRequiredService<DatabaseDocumentQueue>()
            : sp.GetRequiredService<DocumentQueue>());
        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
        services.AddHostedService<DocumentProcessingHost>();

        // ---------- 运行时配置（依赖倒置：接口在 AI 层，实现在 Application 层） ----------
        // TryAdd：Application 先注册了 SettingsService（读 sys_settings）时用它，
        // 否则退化为静态默认，保证 AI 层可独立运行/单测。
        services.TryAddSingleton<Configuration.IRagSettingsProvider>(
            Configuration.StaticRagSettingsProvider.Default);

        // ---------- 重排（默认关闭；外部 API 失败自动降级启发式） ----------
        var rerankApi = new Retrieval.RerankApiOptions
        {
            BaseUrl = configuration["RAG:Rerank:Api:BaseUrl"] ?? "https://api.siliconflow.cn/v1",
            Model = configuration["RAG:Rerank:Api:Model"] ?? "BAAI/bge-reranker-v2-m3",
            ApiKey = configuration["RAG:Rerank:Api:ApiKey"] ?? string.Empty
        };
        services.AddSingleton(rerankApi);
        services.AddSingleton<Retrieval.HeuristicRerankProvider>();
        services.AddSingleton<Retrieval.ApiRerankProvider>();
        services.AddSingleton<Retrieval.IRerankProvider>(sp =>
            sp.GetRequiredService<Retrieval.HeuristicRerankProvider>());
        services.AddSingleton<Retrieval.IRerankProvider>(sp =>
            sp.GetRequiredService<Retrieval.ApiRerankProvider>());
        services.AddSingleton<Retrieval.IRerankService, Retrieval.RerankService>();

        // ---------- 配置化 HTTP 工具（P2） ----------
        services.AddSingleton<Tools.AgentToolCache>();
        services.AddSingleton<Tools.IHttpToolExecutor, Tools.HttpToolExecutor>();
        services.AddScoped<Tools.IAgentToolRegistry, Tools.AgentToolRegistry>();

        // ---------- 问答编排 ----------
        services.AddSingleton<IChatClient>(_ => Chat.ChatClientFactory.Create(llm));
        services.AddScoped<Retrieval.IKnowledgeRetrievalService, Retrieval.KnowledgeRetrievalService>();
        services.AddScoped<Retrieval.ILedgerQueryService, Retrieval.LedgerQueryService>();
        services.AddScoped<Agent.IChatOrchestrator, Agent.ChatOrchestrator>();

        return services;
    }
}
