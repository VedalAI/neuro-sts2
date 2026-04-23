using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public struct ContextReturn(string message, bool silent = false)
{
    public string Message = message;
    public bool Silent = silent;
}
public struct CommandReturn(List<ConstructedAction> commands, bool persistant = false, string ForceText = "Its your turn to act!")
{
    public List<ConstructedAction> Commands = commands;
    public string ForceText = ForceText;
    public bool Persistant = persistant;
}
public interface IContextResult { }
public interface IContextHandler
{
    ContextType Type { get; }
    ContextReturn GetContext(ContextInfo ctx);
    CommandReturn GetCommands(ContextInfo ctx);
    ExecutionResult Internal_Validate(ConstructedAction action, ActionJData data, out object parsedData, ContextInfo ctx);

    Task<ExecutionResult?> Internal_TryExecute(ConstructedAction action, object ParsedData, ContextInfo ctx);
}
public interface IContextHandler<T> : IContextHandler where T : class, new()
{
    ExecutionResult IContextHandler.Internal_Validate(ConstructedAction action, ActionJData data, out object parsedData, ContextInfo ctx)
    {
        Plugin.LogDebug("Validating " + action.Name);
        var passedT = new T();
        var executionResult = Validate(action, data, passedT, ctx);
        parsedData = passedT;
        return executionResult;
    }

    Task<ExecutionResult?> IContextHandler.Internal_TryExecute(ConstructedAction action, object ParsedData, ContextInfo ctx)
    {
        Plugin.LogDebug("Executing " + action.Name + $"in type: {Type}, {this}");
        Plugin.LogDebug($"ParsedData runtime type: {ParsedData?.GetType()}");
        Plugin.LogDebug($"Expected type: {typeof(T)}");
        return TryExecute(action, (T)ParsedData, ctx);
    }
    public ExecutionResult Validate(ConstructedAction action, ActionJData data, T parsedData, ContextInfo ctx);
    public Task<ExecutionResult?> TryExecute(ConstructedAction action, T ParsedData, ContextInfo ctx);
}

public interface IOnContextSwitch
{
    void OnContextSwitch(ContextType newContext);
}
