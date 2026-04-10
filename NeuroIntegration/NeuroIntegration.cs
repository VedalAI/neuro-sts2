using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using NeuroSdk.Actions;
using NeuroSdk.Messages.Outgoing;
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
    Context.Send("You are Playing Slay the Spire 2");
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
        SendContext($"These Events happened During a Context Switch:\n{stringBuilder}");
      }
      lastContext = ctx.Type;
    }

    var handler = ActionExecutor.GetHandlers()
    .FirstOrDefault(h => h.Type == ctx.Type);

    if (handler == null)
    {
      Plugin.LogError($"Current selected Handler is unvalid. not found for type: {ctx?.Type}");
      return;
    }


    if (handler.GetCommands(ctx) is List<ConstructedAction> CommandsList && CommandsList.Count > 0)
    {
      if (ctx == null)
      {
        Plugin.LogError("Couldn't resolve current Context");
        GameStabilityDetector.ResetWasStable();
        GameStabilityDetector.ScheduleStabilityCheck();
        return;
      }
      var window = ActionWindow.Create(this).SetContext(handler.GetContext(ctx));
      var new_global_actions = new List<ConstructedAction>();
      foreach (var item in CommandsList)
      {
        if (!item.Persistant_action)
          window.AddAction(item);
        else
        {
          if (GlobalActions.Find((action) => action.Name == item.Name) == null)
          {
            GlobalActions.Add(item);
            new_global_actions.Add(item);
          }
        }
      }
      NeuroActionHandler.RegisterActions(new_global_actions);
      window.SetForce(1, "It's your Turn please do an Action", null);
      window.Register();
    }
    else
    {
      Plugin.LogError("Decision Point Reached without actual commands to run!");
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
