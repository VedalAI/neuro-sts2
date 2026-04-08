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

    string GetContext(ContextInfo ctx)
    {
        return $"You are in {Type}";
    }
    [Obsolete("Get Context is now used to Serialize the state for Neuro, As Json isn't pretty")]
    Dictionary<string, object>? SerializeState(ContextInfo ctx);

    List<ConstructedAction> GetCommands(ContextInfo ctx);

    ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx);
    Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement ParsedData, ContextInfo ctx);
}
