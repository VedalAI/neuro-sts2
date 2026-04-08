using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using NeuroSdk.Actions;
using NeuroSdk.Messages.Outgoing;
using Sts2Agent;
namespace STS2NeuroIntegration;

public class NeuroIntegration : Node
{
  public static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

  public static void SendContext(string message, bool silent = false)
  {
    Context.Send(message, silent);
  }

  public void HandleDecisionPoint()
  {

    var CurrentState = GameStateSerializer.Serialize();

    if (CurrentState.TryGetValue("error", out var error))
    {
      Context.Send(JsonSerializer.Serialize(error, JsonOptions));
      //TODO: Handle Error Properly
    }

    if (CurrentState.TryGetValue("available_commands", out var cmds) && cmds is List<ConstructedAction> CommandsList)
    {
      var window = ActionWindow.Create(this).SetContext("You are in the Main Menu"); // TODO: replace this with the proper context from above values.
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
      window.Register();
    }
    else
    {
      Plugin.LogError("Decision Point Reached without actual commands to run!");
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
