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
                return handler.Internal_Validate(action, data, out parsedData, ctx);
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
                var json_elements = JsonDocument.Parse(JsonSerializer.Serialize(ParsedData, NeuroIntegration.JsonOptions));
                _ = (GodotMainThread.RunAsync(async () =>
                {
                    var result = await handler?.Internal_TryExecute(action, json_elements?.RootElement ?? new(), ctx) ?? ExecutionResult.ModFailure("handler didn't return awaitable");
                    if (result.Successful)
                    {
                        Plugin.LogDebug("Action Successful with message: " + result.Message);
                    }
                    else
                    {
                        Plugin.LogError($"[CRITICAL] Action has Errored during Execution with Message: {result.Message}, The Validation or Execution are wrong for handler of action: {action.Name}");
                    }
                    GameStabilityDetector.ScheduleStabilityCheck();
                })
                ?.ContinueWith(async (something) =>
                {
                }));
                return;
            }

            Plugin.LogError($"Unknown action '{action.Name}' for context '{ctx.Type}'");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Action execution error: {e}");
        }

    }


    /// <summary>
    /// Get the handler registry for use by GameStateSerializer.
    /// </summary>
    public static IReadOnlyList<IContextHandler> GetHandlers() => Handlers;
}
