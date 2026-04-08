using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Contexts;
using Sts2Agent.Utilities;

namespace Sts2Agent;

public static class ActionExecutor
{
    private static readonly List<IContextHandler> Handlers =
    [
        new MapHandler(),
        new HandSelectionHandler(),
        new CardSelectionHandler(),
        new BundleSelectionHandler(),
        new RewardsHandler(),
        new CombatHandler(),
        new EventContextHandler(),
        new RestSiteHandler(),
        new ShopHandler(),
        new TreasureHandler(),
        new GameOverHandler(),
        new CharacterSelectHandler(),
        new MainMenuHandler()
    ];


    public static ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData)
    {

        parsedData = null;
        Plugin.LogDebug($"Validating action: {action.Name} with data: {data.Data?.ToJsonString()} parsing {parsedData?.ToString()}");
        try
        {

            var ctx = GameContext.Resolve();
            if (ctx == null)
                return ExecutionResult.Failure("No active run or interactive screen");

            // Dispatch to the handler matching the current context
            var handler = Handlers.FirstOrDefault(h => h.Type == ctx.Type);
            if (handler != null)
            {
                return handler.Validate(action, data, out parsedData, ctx);
            }

            return ExecutionResult.Failure($"Unknown action '{action.Name}' for context '{ctx.Type}'");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Action execution error: {e}");
            return ExecutionResult.Failure(e.Message);
        }
    }
    public static void Execute(ConstructedAction action, object? ParsedData)
    {

        Plugin.LogDebug($"Executing Action: {action.Name} with parsedData: {ParsedData?.ToString()}");
        try
        {

            var ctx = GameContext.Resolve();
            if (ctx == null)
            {
                Plugin.LogError("No active run or interactive screen");
                return;
            }

            // Dispatch to the handler matching the current context
            var handler = Handlers.FirstOrDefault(h => h.Type == ctx.Type);
            if (handler != null)
            {
                var json_elements = JsonDocument.Parse(JsonSerializer.Serialize(ParsedData, STS2NeuroIntegration.NeuroIntegration.JsonOptions));
                handler.TryExecute(action, json_elements.RootElement, ctx)?.ContinueWith(async (something) =>
                {
                    var task = await something;
                    if (task != null && task.HasValue && task.Value is ActionResult.Result result)
                    {
                        if (result.ok)
                        {
                            Plugin.LogDebug("Action Successful with message: " + result.message);
                        }
                        else
                        {
                            Plugin.LogError($"[CRITICAL] Action has Errored during Execution with Message: {result.message}, The Validation or Execution are wrong for handler of action: {action.Name}");
                        }
                    }
                });
                return;
            }

            Plugin.LogError($"Unknown action '{action.Name}' for context '{ctx.Type}'");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Action execution error: {e}");
        }

    }

    // public static async Task<string> Execute(string actionJson)
    // {
    //     try
    //     {
    //         using var doc = JsonDocument.Parse(actionJson);
    //         var root = doc.RootElement;

    //         if (!root.TryGetProperty("type", out var typeProp))
    //             return ActionResult.Error("Missing 'type' field");

    //         var type = typeProp.GetString()!;
    //         Plugin.LogDebug($"Executing action: {type}");

    //         var ctx = GameContext.Resolve();
    //         if (ctx == null)
    //             return ActionResult.Error("No active run or interactive screen");

    //         // Dispatch to the handler matching the current context
    //         var handler = Handlers.FirstOrDefault(h => h.Type == ctx.Type);
    //         if (handler != null)
    //         {
    //             var task = handler.TryExecute(type, root, ctx);
    //             if (task != null)
    //             {
    //                 var result = await task;
    //                 if (result != null)
    //                     return result;
    //             }
    //         }

    //         return ActionResult.Error($"Unknown action type '{type}' for context '{ctx.Type}'");
    //     }
    //     catch (JsonException)
    //     {
    //         return ActionResult.Error("Invalid JSON");
    //     }
    //     catch (Exception e)
    //     {
    //         Plugin.LogError($"Action execution error: {e}");
    //         return ActionResult.Error(e.Message);
    //     }
    // }

    /// <summary>
    /// Get the handler registry for use by GameStateSerializer.
    /// </summary>
    public static IReadOnlyList<IContextHandler> GetHandlers() => Handlers;
}
