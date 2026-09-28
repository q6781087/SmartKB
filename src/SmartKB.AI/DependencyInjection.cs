using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // HttpClient 工厂
        services.AddHttpClient("llm-cloud");
        services.AddHttpClient("llm-local");

        // Embedding 模式切换：Local 用 Ollama；Agnes 无 Embedding 模型，回退智谱 Cloud embeddings
        if (llm.Mode.Equals("Local", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmbeddingProvider, LocalEmbeddingProvider>();
        else
            services.AddSingleton<IEmbeddingProvider, CloudEmbeddingProvider>();

        // 入库流水线
        services.AddSingleton<DocumentQueue>();
        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
        services.AddHostedService<DocumentProcessingHost>();

        // M3：聊天编排（IChatClient 按 Llm:Mode 构建；MAF Agent + 检索/台账工具）
        services.AddSingleton<IChatClient>(_ => Chat.ChatClientFactory.Create(llm));
        services.AddScoped<Retrieval.IKnowledgeRetrievalService, Retrieval.KnowledgeRetrievalService>();
        services.AddScoped<Retrieval.ILedgerQueryService, Retrieval.LedgerQueryService>();
        services.AddScoped<Agent.IChatOrchestrator, Agent.ChatOrchestrator>();

        return services;
    }
}
