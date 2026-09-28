using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using SmartKB.AI.Embedding;

namespace SmartKB.AI.Chat;

/// <summary>
/// 按 Llm:Mode 构建 IChatClient。
/// Agnes（apihub，OpenAI 兼容 /v1）、云 API（智谱 OpenAI 兼容 /v4）与本地推理（Ollama /v1）
/// 均为 OpenAI 协议，统一走 OpenAI SDK。
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(LlmOptions options)
    {
        if (options.Mode.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = options.Local.BaseUrl.TrimEnd('/');
            var endpoint = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? new Uri(baseUrl)
                : new Uri($"{baseUrl}/v1");

            var client = new OpenAIClient(
                new ApiKeyCredential("ollama"), // Ollama 不校验 Key，占位即可
                new OpenAIClientOptions { Endpoint = endpoint });

            return client.GetChatClient(options.Local.ChatModel).AsIChatClient();
        }

        // Agnes：OpenAI 兼容 /v1/chat/completions，Bearer 认证
        if (options.Mode.Equals("Agnes", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(options.Agnes.ApiKey))
                throw new InvalidOperationException(
                    "Llm:Agnes:ApiKey 未配置（应通过环境变量 Llm__Agnes__ApiKey 注入）");

            var agnes = new OpenAIClient(
                new ApiKeyCredential(options.Agnes.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(options.Agnes.BaseUrl) });

            return agnes.GetChatClient(options.Agnes.ChatModel).AsIChatClient();
        }

        var cloud = new OpenAIClient(
            new ApiKeyCredential(options.Cloud.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(options.Cloud.BaseUrl) });

        return cloud.GetChatClient(options.Cloud.ChatModel).AsIChatClient();
    }
}
