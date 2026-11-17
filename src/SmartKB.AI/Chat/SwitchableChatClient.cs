using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SmartKB.AI.Configuration;
using SmartKB.AI.Embedding;

namespace SmartKB.AI.Chat;

/// <summary>
/// 可切换的 IChatClient 单例包装：每次调用前解析运行时模型配置（后台 llm.* 键），
/// 按「模式 + 端点 + 模型名」做键缓存——配置未变复用已建客户端，变更才重建。
/// 配置解析经 IServiceScopeFactory（ILlmSettingsProvider 实现是 Scoped），
/// 借助 SettingsCache 的 10 秒 TTL，稳态下每个 TTL 只查一次库。
/// 构建失败（如 ApiKey 未配）抛出原始错误且保留上一个可用客户端。
/// </summary>
public sealed class SwitchableChatClient(
    IServiceScopeFactory scopeFactory,
    LlmOptions staticOptions) : IChatClient, IDisposable
{
    private sealed record Resolved(string Key, IChatClient Inner);

    private volatile Resolved? _current;
    private readonly object _sync = new();

    private async Task<Resolved> ResolveAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = await scope.ServiceProvider
            .GetRequiredService<ILlmSettingsProvider>()
            .GetAsync(ct);

        var merged = LlmDefaults.Merge(staticOptions, settings);
        var key = $"{merged.Mode}|{ModelOf(merged)}|{BaseUrlOf(merged)}";

        var current = _current;
        if (current is not null && current.Key == key) return current;

        lock (_sync)
        {
            current = _current;
            if (current is not null && current.Key == key) return current;

            var inner = ChatClientFactory.Create(merged); // ApiKey 缺失等配置错误在此抛出
            _current = current = new Resolved(key, inner);
            return current;
        }
    }

    private static string ModelOf(LlmOptions o) => o.Mode switch
    {
        var m when m.Equals("Local", StringComparison.OrdinalIgnoreCase) => o.Local.ChatModel,
        var m when m.Equals("Agnes", StringComparison.OrdinalIgnoreCase) => o.Agnes.ChatModel,
        _ => o.Cloud.ChatModel
    };

    private static string BaseUrlOf(LlmOptions o) => o.Mode switch
    {
        var m when m.Equals("Local", StringComparison.OrdinalIgnoreCase) => o.Local.BaseUrl,
        var m when m.Equals("Agnes", StringComparison.OrdinalIgnoreCase) => o.Agnes.BaseUrl,
        _ => o.Cloud.BaseUrl
    };

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inner = (await ResolveAsync(cancellationToken)).Inner;
        return await inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inner = (await ResolveAsync(cancellationToken)).Inner;
        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _current?.Inner.GetService(serviceType, serviceKey);

    public void Dispose() => _current?.Inner.Dispose();
}
