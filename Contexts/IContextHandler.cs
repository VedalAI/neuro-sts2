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

    ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx);
    Task<ExecutionResult?>? TryExecute(ConstructedAction action, JsonElement ParsedData, ContextInfo ctx);
}
