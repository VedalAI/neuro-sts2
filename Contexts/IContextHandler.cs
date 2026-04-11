using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public interface IContextHandler
{
    ContextType Type { get; }
    string GetContext(ContextInfo ctx);
    List<ConstructedAction> GetCommands(ContextInfo ctx);
    ExecutionResult Internal_Validate(ConstructedAction action, ActionJData data, out object parsedData, ContextInfo ctx)
    {
        parsedData = 0;
        Plugin.LogDebug($"Not implemented for context: {ctx.Type}");
        return ExecutionResult.Failure("Not implemented");
    }

    Task<ExecutionResult?>? Internal_TryExecute(ConstructedAction action, object ParsedData, ContextInfo ctx)
    {
        return null;
    }
}
public interface IContextHandler<T> : IContextHandler where T : class, new()
{
    new ExecutionResult Internal_Validate(ConstructedAction action, ActionJData data, out object parsedData, ContextInfo ctx)
    {
        var passedT = new T();
        var executionResult = Validate(action, data, ref passedT, ctx);
        parsedData = passedT;
        return executionResult;
    }

    new Task<ExecutionResult?>? Internal_TryExecute(ConstructedAction action, object ParsedData, ContextInfo ctx)
    {
        return TryExecute(action, (T)ParsedData, ctx);
    }
    ExecutionResult Validate(ConstructedAction action, ActionJData data, ref T parsedData, ContextInfo ctx);
    Task<ExecutionResult?>? TryExecute(ConstructedAction action, T ParsedData, ContextInfo ctx);
}
