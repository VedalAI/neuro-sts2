using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using NeuroSdk.Actions;
using NeuroSdk.Messages.Outgoing;
using NeuroSdk.Websocket;
using Sts2Agent;
using Sts2Agent.Contexts;
using Sts2Agent.Utilities;
namespace STS2NeuroIntegration;

public class NeuroIntegration : Node
{
  public static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };
  public static NeuroIntegration? Instance
  {
    get; private set;
  }

  private ActionWindow? lastWindow;

  /// <summary>
  /// These are Actions that are always available for neuro.
  /// if they are one-time use the Action itself has to handle this
  /// </summary>
  public List<ConstructedAction> GlobalActions = [];
  public static void Setup(NeuroIntegration integration)
  {
    if (Instance != null)
    {
      Plugin.LogError("Neuro Integration has been Initialized Twice");
      integration.QueueFree();
      return;
    }
    Instance = integration;
  }

  public new void Ready()
  {
    Context.Send("You are playing Slay the Spire 2");
  }

  public void Processs()
  {

  }

  public void Quit()
  {

  }

  public static void UnregisterAction(string action_name)
  {
    var action = Instance?.GlobalActions.Find((action) => action.Name == action_name);
    if (action != null)
    {
      NeuroActionHandler.UnregisterActions(action);
      Instance?.GlobalActions.Remove(action);
    }
  }

  public static void UnregisterActions(params string[] action_names)
  {
    if (Instance == null)
    {
      Plugin.LogError("Neuro Integration isn't Ready yet");
      return;
    }
    var actions_to_remove = Instance.GlobalActions.Where((action) => action_names.Contains(action.Name)).ToArray();
    NeuroActionHandler.UnregisterActions(actions_to_remove);
    foreach (var action in actions_to_remove)
    {
      Instance.GlobalActions.Remove(action);
    }
  }

  public static void UnregisterAllActions()
  {
    if (Instance == null)
    {
      Plugin.LogError("Neuro Integration isn't Ready yet");
      return;
    }
    if (Instance.GlobalActions.Count == 0)
    {
      // Plugin.LogDebug("No Global Actions to Unregister");
      return;
    }
    NeuroActionHandler.UnregisterActions(Instance.GlobalActions.ToArray());
    Instance.GlobalActions.Clear();
  }

  /// <summary>
  /// Simple Wrapper for NeuroSdk.Messages.Outgoing.Context to make namespace collisions not happen when importing Context.
  /// </summary>
  /// <param name="message"></param>
  /// <param name="silent"></param>
  public static void SendContext(string message, bool silent = false)
  {
    Context.Send(message, silent);
  }

  ContextType lastContext = ContextType.Unknown;
  public static Action OnContextSwitch;

  public void HandleDecisionPoint()
  {

    var ctx = GameContext.Resolve();
    if (ctx == null)
    {
      Plugin.LogError("Context is Invalid");
      return;
    }
    if (lastContext != ctx.Type)
    {

      StringBuilder stringBuilder = new();
      //Drain Any pending Events that happened. can happen when switching Context at the end of a Action. like Combat
      stringBuilder.RepresentEvents(EventLog.DrainAll());
      if (stringBuilder.Length > 0)
      {
        SendContext($"These events happened during a context switch:\n{stringBuilder}", true);
      }
      var oldhandler = ActionExecutor.GetHandlers().GetValueOrDefault(lastContext);
      if (oldhandler != null && oldhandler is IOnContextSwitch switchHandler)
      {
        switchHandler.OnContextSwitch(ctx.Type);
        Plugin.LogDebug($"Context Switch: Called OnContextSwitch for {lastContext} -> {ctx.Type}");
      }
      UnregisterAllActions();
      lastContext = ctx.Type;
    }

    var handler = ActionExecutor.GetHandlers().GetValueOrDefault(ctx.Type);

    if (handler == null)
    {
      Plugin.LogError($"Current selected Handler is unvalid. not found for type: {ctx?.Type}");
      return;
    }

    if (ctx == null)
    {
      Plugin.LogError("Couldn't resolve current Context");
      GameStabilityDetector.ResetWasStable();
      GodotMainThread.RunAsync(async () => { await Task.Delay(200); GameStabilityDetector.ScheduleStabilityCheck(); });
      return;
    }

    if (ActionExecutor.ProcessEnqueuedActions())
    {
      return;
    }
    if (lastWindow?.CurrentState == ActionWindow.State.Forced)
    {
      return;
    }
    if (handler.GetCommands(ctx) is CommandReturn CommandsList)
    {
      var contextReturn = handler.GetContext(ctx);
      lastWindow?.End();
      if (CommandsList.Commands.Count == 0)
      {
        if (!string.IsNullOrEmpty(contextReturn.Message))
          SendContext(contextReturn.Message, contextReturn.Silent);
        Plugin.LogDebug("No Commands to run at this Decision Point");
        GameStabilityDetector.ResetWasStable();
        GodotMainThread.RunAsync(async () => { await Task.Delay(200); GameStabilityDetector.ScheduleStabilityCheck(); });
        return;
      }
      Plugin.LogDebug($"Commands list contains {CommandsList.Commands.Count} commands. SkipActionWindow: {CommandsList.SkipActionWindow}");
      if (CommandsList.Commands.Count == 1 && !CommandsList.Commands[0].HasSchema())
      {
        ActionExecutor.EnqueueAction(CommandsList.Commands[0]);
        if (!string.IsNullOrEmpty(contextReturn.Message))
          SendContext(contextReturn.Message, contextReturn.Silent);
        Plugin.LogDebug("Only one command without parameters, enqueuing directly");
        UnregisterAllActions();
        return;
      }
      if (CommandsList.SkipActionWindow && !CommandsList.Commands.Any(cmd => cmd.HasSchema()) && CommandsList.Commands.Count <= 1)
      {
        if (!string.IsNullOrEmpty(contextReturn.Message))
          SendContext(contextReturn.Message, contextReturn.Silent);
        Plugin.LogDebug("Context Handler requested to Skip, not sending Action Window");
        return;
      }
      lastWindow = ActionWindow.Create(this).SetContext(contextReturn.Message, contextReturn.Silent);
      var new_global_actions = new List<ConstructedAction>();
      var hasNonPersistant = false;
      foreach (var item in CommandsList.Commands)
      {
        if (!item.Persistant_action)
        {
          hasNonPersistant = true;
          lastWindow.AddAction(item);
        }
        else
        {
          if (GlobalActions.Find((action) => action.Name == item.Name) == null && !new_global_actions.Any(action => action.Name == item.Name))
          {
            GlobalActions.Add(item);
            new_global_actions.Add(item);
          }
          else
          {
            //Check if the Schema's are the same, if not unregister the old one and register the new one, to prevent issues with changing parameters of global actions
            var existing = GlobalActions.Find((action) => action.Name == item.Name);
            if (existing != null && existing.HasSchema() && item.HasSchema() && existing.ToString() != item.ToString())
            {
              Plugin.LogWarning($"Global Action '{item.Name}' is being updated with a different schema, registering");
              UnregisterAction(item.Name);
              new_global_actions.Add(item);
            }
          }
        }
      }
      if (new_global_actions.Count > 0)
        NeuroActionHandler.RegisterActions(new_global_actions);
      if (CommandsList.ForceActionWindow)
      {
        lastWindow.SetForce(1, "It's your turn. Please take an action.", null);
        if (hasNonPersistant)
          lastWindow.Register();
      }
      else
      {
        // Force an action for every action
        GodotMainThread.RunAsync(async () =>
         {
           var currentCommandslist = CommandsList.Commands.ToList();
           var oldGlobalActions = GlobalActions.ToList();
           var force = new ActionsForce("It's your turn. Please take an action.", null, true, ActionsForce.Priority.Low, currentCommandslist.ToList());
           await Task.Delay(1000);
           if (currentCommandslist.Except(CommandsList.Commands).Any() || !currentCommandslist.All(cmd => CommandsList.Commands.Contains(cmd)))
           {
             Plugin.LogDebug("Not sending force, Action Window has changed");
             return;
           }
           if (!oldGlobalActions.All(action => GlobalActions.Contains(action)))
           {
             Plugin.LogDebug("Not sending force, Global Actions have changed");
             return;
           }
           if (!string.IsNullOrEmpty(contextReturn.Message))
             SendContext(contextReturn.Message, contextReturn.Silent);
           WebsocketConnection.Instance!.Send(force);
           if (hasNonPersistant)
             lastWindow.Register();
         });
      }

    }
    else
    {
      Plugin.LogWarning("Decision Point Reached without actual commands to run!");
      GameStabilityDetector.ResetWasStable();
      GodotMainThread.RunAsync(async () => { await Task.Delay(200); GameStabilityDetector.ScheduleStabilityCheck(); });
    }
  }


  public static void SignalDecisionPoint()
  {
    Plugin.LogDebug("Decision Point Reached");

    if (Instance == null || !IsInstanceValid(Instance))
    {
      Plugin.LogError("Integration isn't Ready yet");
      return;
    }

    Instance.HandleDecisionPoint();
  }

}
