using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SmartKB.AI.Tools;

/// <summary>
/// 把一个后台登记的 HTTP 工具包装成 MAF/AIFunction 可识别的函数。
/// 与 AIFunctionFactory.Create(委托) 不同：参数 schema 完全来自 sys_agent_tools 的配置，
/// 客户加一个工具不需要改代码、不需要重新发版。
/// </summary>
public sealed class ConfiguredHttpToolFunction(
    AgentToolDefinition definition,
    IHttpToolExecutor executor,
    Action? onInvoked = null) : AIFunction
{
    private readonly JsonElement _schema = AgentToolValidator.BuildJsonSchema(definition);

    public override string Name => definition.Name;

    public override string Description => definition.Description;

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        onInvoked?.Invoke();
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in arguments)
            args[pair.Key] = pair.Value;

        return await executor.InvokeAsync(definition, args, cancellationToken);
    }
}
