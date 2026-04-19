
using System.Text;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using NeuroSdk.Actions;
using NeuroSdk.Json;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using STS2NeuroIntegration;

namespace Sts2Agent.Contexts;

public class TimelinesHandler : IContextHandler<TimelinesHandler.Result>
{
  public class Result : IContextResult
  {
    // Empty result class
    internal NButton ProceedButton;
    internal NEpochSlot EpochButton;
    internal NEpochInspectScreen InspectScreen;
  }
  public ContextType Type => ContextType.TimelinesEvent;
  public ContextReturn GetContext(ContextInfo ctx)
  {
    StringBuilder stringBuilder = new();
    // stringBuilder.AppendLine("You are at the Timelines Event");
    if (UiHelper.FindFirst<NTimelineTutorial>(ctx.TimelineScreen) is NTimelineTutorial tutorial)
    {
      stringBuilder.AppendLine(TextHelper.StripBBCode(tutorial.Get(NTimelineTutorial.PropertyName._text).As<MegaRichTextLabel>().Text.AsSingleLine()));
    }
    var tounlockEpochs = UiHelper.FindAll<NEpochSlot>(ctx.TimelineScreen);
    if (tounlockEpochs.Count > 0)
    {
      var tounlock = tounlockEpochs.Where(x => x.State == EpochSlotState.Obtained);
      // stringBuilder.AppendLine($"You have {tounlock.Count()} epochs to unlocked");
    }
    return new ContextReturn(stringBuilder.ToString());
  }

  public CommandReturn GetCommands(ContextInfo ctx)
  {
    var commands = new List<ConstructedAction>();
    if (UiHelper.FindFirst<NTimelineTutorial>(ctx.TimelineScreen) is not null)
    {
      commands.Add(new("proceed", "Continue"));
    }

    var tounlockEpochs = UiHelper.FindAll<NEpochSlot>(ctx.TimelineScreen);
    if (tounlockEpochs.Count > 0)
    {
      var tounlock = tounlockEpochs.Where(x => x.State == EpochSlotState.Obtained);
      if (tounlock.Any())
      {
        commands.Add(new("unlock_epoch", "Unlock the next Obtained Epoch, This will give you their rewards and context what they are for the Story of Slay the Spire 2"));
      }
    }
    var inspectScreen = UiHelper.FindFirst<NEpochInspectScreen>(ctx.TimelineScreen);
    if (inspectScreen != null && inspectScreen.Visible)
    {
      commands.Add(new("proceed_epoch", "Close the unlocked Epoch"));
    }
    var unlockScreen = UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen);
    if (unlockScreen != null && unlockScreen.Visible)
    {
      //TODO: a switch for each type of unlock screen to send better context to neuro
      var button = UiHelper.FindFirst<NUnlockConfirmButton>(unlockScreen);
      if (button != null)
        commands.Add(new("close_unlock", "Close the Unlock Screen"));
    }
    var backButton = ctx.TimelineScreen.Get(NTimelineScreen.PropertyName._backButton).As<NBackButton>();
    if (backButton != null && backButton.IsEnabled && backButton.Visible)
    {
      commands.Add(new("back_to_main_menu", "Closes the Timeline and brings you back to the Main Menu"));
    }
    return new CommandReturn(commands, true);
  }

  public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result result, ContextInfo ctx)
  {
    if (action.Name == "proceed")
    {
      var timelineTutorial = UiHelper.FindFirst<NTimelineTutorial>(SceneHelper.GetSceneRoot());
      if (timelineTutorial != null && UiHelper.FindFirst<NAcknowledgeButton>(timelineTutorial) is NAcknowledgeButton acknowledgeButton)
      {
        result.ProceedButton = acknowledgeButton;
        return ExecutionResult.Success();
      }
      return ExecutionResult.Unstable("Couldn't find proceed button");
    }
    if (action.Name == "unlock_epoch")
    {
      var tounlockEpochs = UiHelper.FindAll<NEpochSlot>(ctx.TimelineScreen);
      if (tounlockEpochs.Count > 0)
      {
        var tounlock = tounlockEpochs.Where(x => x.State == EpochSlotState.Obtained);
        if (tounlock.Any())
        {
          result.EpochButton = tounlock.First();
          return ExecutionResult.Success();
        }
      }
      return ExecutionResult.Unstable("No epochs to unlock");
    }
    if (action.Name == "proceed_epoch")
    {
      var inspectScreen = UiHelper.FindFirst<NEpochInspectScreen>(ctx.TimelineScreen);
      if (inspectScreen != null && inspectScreen.Visible)
      {
        result.InspectScreen = inspectScreen;
        return ExecutionResult.Success();
      }
      return ExecutionResult.Unstable("No proceed button found");
    }
    if (action.Name == "close_unlock")
    {

      var unlockScreen = UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen);
      if (unlockScreen != null)
      {
        var button = UiHelper.FindFirst<NUnlockConfirmButton>(unlockScreen);
        if (button != null)
        {
          result.ProceedButton = button;
          return ExecutionResult.Success();
        }
      }
      return ExecutionResult.ModFailure("Couldn't Find Close button on Unlock screen");
    }

    if (action.Name == "back_to_main_menu")
    {
      var backButton = ctx.TimelineScreen.Get(NTimelineScreen.PropertyName._backButton).As<NBackButton>();
      if (backButton != null && backButton.IsEnabled && backButton.Visible)
      {
        result.ProceedButton = backButton;
        return ExecutionResult.Success();
      }
      return ExecutionResult.ModFailure("Couldn't Find Back button on Timeline");
    }

    return ExecutionResult.Unstable("Unkown Action");
  }
  //TODO: aggregate Epoch Information and send it to neuro when the Epoch is Fully unlocked with the rewards and the rest together as 1 context
  public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
  {
    await Task.Delay(1000);
    if (action.Name == "proceed" || action.Name == "back_to_main_menu")
    {
      await GodotMainThread.ClickAsync(result.ProceedButton);
      await Task.Delay(1000);// wait for the screen to fade
      return ExecutionResult.Success();
    }
    if (action.Name == "unlock_epoch")
    {
      await GodotMainThread.ClickAsync(result.EpochButton);
      NeuroIntegration.SendContext($"Unlocked Epoch: {TextHelper.StripBBCode(result.EpochButton.model?.Description ?? "")}");
      await Task.Delay(1000);// wait for the screen to fade
      return ExecutionResult.Success();
    }
    if (action.Name == "proceed_epoch")
    {
      await GodotMainThread.RunAsync(result.InspectScreen.Close);
      await Task.Delay(1000);// wait for the screen to fade
      return ExecutionResult.Success();
    }
    if (action.Name == "close_unlock")
    {
      await GodotMainThread.ClickAsync(result.ProceedButton);
      await Task.Delay(1000);// wait for the screen to fade
      return ExecutionResult.Success();
    }
    return null;
  }

}
