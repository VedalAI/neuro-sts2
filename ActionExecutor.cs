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
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using System.Reflection;
using HarmonyLib;

namespace Sts2Agent;

public static class ActionExecutor
{

    private static Queue<ConstructedAction> enqueuedActions = new Queue<ConstructedAction>();
    public static bool HasEnqueuedActions()
    {
        return enqueuedActions.Count > 0;
    }
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
            if (Handlers.TryGetValue(ctx.Type, out var handler))
            {
                var result = handler.Internal_Validate(action, data, out parsedData, ctx);
                //This can happen if situation is stable. but GetCommands returned stale actions, like buttons not found due to overlay not being populated yet (game async population)
                //Should be used very very sparingly
                if (result.IsUnstable)
                {
                    Plugin.LogError("Stable situation but Action Window has Stale Actions, reseting");
                    action.ActionWindow?.End();
                    GameStabilityDetector.ResetWasStable();
                    NeuroIntegration.UnregisterAllActions();
                    GodotMainThread.RunAsync(async () => { await Task.Delay(200); GameStabilityDetector.ScheduleStabilityCheck(); });
                }
                return result;
            }

            return ExecutionResult.Failure($"Unknown action '{action.Name}' for context '{ctx.Type}'");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Action execution error: {e}");
            return ExecutionResult.Failure(e.Message);
        }
    }
    public static void EnqueueAction(ConstructedAction constructedAction)
    {
        Plugin.LogDebug($"Enqueuing Action: {constructedAction.Name}");
        constructedAction.Data ??= new();
        if (enqueuedActions.Contains(constructedAction))
        {
            Plugin.LogWarning($"Action: {constructedAction.Name} is already enqueued, skipping enqueue");
            return;
        }
        else
        {

            enqueuedActions.Enqueue(constructedAction);
        }
        GameStabilityDetector.ResetWasStable();
        GodotMainThread.RunAsync(async () => { await Task.Delay(200); GameStabilityDetector.ScheduleStabilityCheck(); });
    }
    public static bool ProcessEnqueuedActions()
    {
        if (enqueuedActions.Count == 0)
            return false;

        var action = enqueuedActions.Dequeue();
        var validationResult = Validate(action, action.Data, out var parsedData);
        if (validationResult.Successful)
        {
            Execute(action, parsedData);
            return true;
        }
        else
        {
            return false;
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
            if (Handlers.TryGetValue(ctx.Type, out var handler))
            {
                _ = GodotMainThread.RunAsync(async () =>
                {
                    GameStabilityDetector.ResetWasStable();
                    var task = handler?.Internal_TryExecute(action, ParsedData, ctx);
                    if (task == null)
                    {
                        Plugin.LogWarning($"[CRITICAL] Action Returned not awaitable task");
                        GameStabilityDetector.ScheduleStabilityCheck();
                        return;
                    }
                    var result = await task;
                    if (result == null)
                    {
                        GameStabilityDetector.ScheduleStabilityCheck();
                        return;
                    }

                    if (result.Successful)
                    {
                        Plugin.LogDebug("Action Successful with message: " + result.Message);
                    }
                    else
                    {
                        Plugin.LogError($"[CRITICAL] Action has Errored during Execution with Message: {result.Message}, The Validation or Execution are wrong for handler of action: {action.Name}");
                    }
                    GameStabilityDetector.ScheduleStabilityCheck();
                });
                return;
            }

            Plugin.LogError($"Unknown action '{action.Name}' for context '{ctx.Type}'");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Action execution error: {e}");
            GameStabilityDetector.ScheduleStabilityCheck();
        }

    }


    private static Dictionary<ContextType, IContextHandler>? _handlers;
    public static Dictionary<ContextType, IContextHandler> Handlers
    {
        get
        {
            if (_handlers != null)
                return _handlers;
            var handlers = new Dictionary<ContextType, IContextHandler>();
            foreach (var item in ReflectionHelper.GetSubtypesInMods<IContextHandler>())
            {
                if (Activator.CreateInstance(item) is IContextHandler contextHandler)
                {
                    if (!handlers.TryAdd(contextHandler.Type, contextHandler))
                    {
                        Plugin.LogError($"[CRITICAL] Found multiple Handlers for the same type: {contextHandler.Type}");
                    }
                }
            }

            Plugin.LogDebug($"Found {handlers.Count} handlers for action system:\n{string.Join(", ", handlers.Select(h => h.GetType().Name))}");
            _handlers = handlers;
            return _handlers;
        }
    }
    /// <summary>
    /// Get the handler registry for use by GameStateSerializer.
    /// </summary>
    public static IReadOnlyDictionary<ContextType, IContextHandler> GetHandlers() => Handlers;

}
