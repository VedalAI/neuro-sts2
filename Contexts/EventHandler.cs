using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using NeuroSdk.Json;
using System.Text;

namespace Sts2Agent.Contexts;

public class EventContextHandler : IContextHandler<EventContextHandler.Result>
{
    public class Result
    {
        internal NButton? Button;
    }
    public ContextType Type => ContextType.Event;

    //TODO: make this more robust. Context is a bit odd in some events
    public ContextReturn GetContext(ContextInfo ctx)
    {
        var eventRoom = ctx.EventRoom;
        var evt = eventRoom.LocalMutableEvent;
        if (evt == null)
        {
            return new ContextReturn($"You are in the Event: {eventRoom.CanonicalEvent.Title.GetUnformatedText()}");
        }

        StringBuilder eventBuilder = new();
        eventBuilder.AppendLine("## You are in an Event");

        eventBuilder.AppendLine("Event name: " + TextHelper.SafeLocString(() => evt.Title));


        try
        {
            var desc = evt.Description;
            if (desc != null)
            {
                eventBuilder.AppendLine("Event Description: ");
                eventBuilder.AppendLine(desc.GetUnformatedText());
            }
            else
                eventBuilder.AppendLine(TextHelper.SafeLocString(() => evt.InitialDescription));
        }
        catch
        {
        }

        if (evt.CurrentOptions.Count > 0)
            eventBuilder.AppendLine("Available options are: ");
        foreach (var eventoption in evt.CurrentOptions)
        {

            eventBuilder.Append($"- {TextHelper.SafeLocString(() => eventoption.Title)}: ");
            try
            {
                var optDesc = eventoption.Description;
                if (optDesc != null)
                {
                    evt.DynamicVars.AddTo(optDesc);
                    eventBuilder.AppendLine(optDesc.GetUnformatedText());
                }
                else
                {
                    eventBuilder.AppendLine("No Description");
                }

            }
            catch
            {

                eventBuilder.AppendLine("No Description");
            }
        }

        return new ContextReturn(eventBuilder.ToString());
    }

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var eventRoom = ctx.EventRoom;
        if (eventRoom == null) return null;

        var evt = eventRoom.LocalMutableEvent;
        if (evt == null)
        {
            return new Dictionary<string, object>
            {
                ["title"] = TextHelper.SafeLocString(() => eventRoom.CanonicalEvent.Title),
                ["description"] = "",
                ["options"] = new List<object>()
            };
        }

        var result = new Dictionary<string, object>
        {
            ["title"] = TextHelper.SafeLocString(() => evt.Title)
        };

        try
        {
            var desc = evt.Description;
            if (desc != null)
                result["description"] = TextHelper.StripBBCode(desc.GetFormattedText());
            else
                result["description"] = TextHelper.SafeLocString(() => evt.InitialDescription);
        }
        catch
        {
            result["description"] = "";
        }

        result["options"] = evt.CurrentOptions.Select((opt, i) =>
        {
            var optDict = new Dictionary<string, object>
            {
                ["index"] = i,
                ["label"] = TextHelper.SafeLocString(() => opt.Title),
                ["locked"] = opt.IsLocked
            };
            try
            {
                var optDesc = opt.Description;
                if (optDesc != null)
                {
                    evt.DynamicVars.AddTo(optDesc);
                    optDict["description"] = TextHelper.StripBBCode(optDesc.GetFormattedText());
                }
                else
                {
                    optDict["description"] = "";
                }
            }
            catch
            {
                optDict["description"] = "";
            }
            return optDict;
        }).ToList();

        return result;
    }

    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var eventRoom = ctx.EventRoom;
        if (eventRoom == null) return new CommandReturn();

        var evt = eventRoom.LocalMutableEvent;
        if (evt == null) return new CommandReturn();

        if (evt.IsFinished)
        {
            commands.Add(new("proceed", "Finish the current Event"));
        }
        else
        {
            commands.Add(new("select_event_option", "Select a option in the Event", QJS.WrapObject(new Dictionary<string, JsonSchema>
            {
                ["option"] = QJS.Enum(evt.CurrentOptions.Where((x) => !x.IsLocked).Select((x) => x.Title.GetUnformatedText()))
            })));
        }

        return new CommandReturn(commands);
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ExecutionResult.ModFailure("Cannot access scene tree");

        if (action.Name == "proceed")
        {
            // Try proceed button
            var proceedButton = UiHelper.FindFirst<NProceedButton>(sceneRoot);
            if (proceedButton != null && proceedButton.IsVisibleInTree())
            {
                parsedData.Button = proceedButton;
                Plugin.LogDebug("Initial Proceed button");

                return ExecutionResult.Success();
            }

            // Finished events use NEventOptionButton with IsProceed=true
            var eventProceed = UiHelper.FindAll<NEventOptionButton>(sceneRoot)
                .FirstOrDefault(b => b.Option.IsProceed && b.IsVisibleInTree());
            if (eventProceed != null)
            {
                parsedData.Button = eventProceed;
                Plugin.LogDebug("After proceed button");
                return ExecutionResult.Success();
            }

            return ExecutionResult.Unstable("Couldn't find a Proceed button. You are mostlikely stuck here...");
        }
        var optionName = data?.Data?["option"]?.GetValue<string>() ?? ""; //TODO: Figure out if this is good enough.


        var allButtons = UiHelper.FindAll<NEventOptionButton>(sceneRoot);

        // Buttons are added to the container in CurrentOptions order,
        // so tree-order index matches the event option index
        var button = allButtons.Find((btn) => btn.Option.Title.GetUnformatedText() == optionName);

        if (button == null)
        {
            Plugin.LogDebug($"Event button lookup: requested={optionName}, found={allButtons.Count} buttons");
            return ExecutionResult.Unstable($"Event option index {optionName} not found");
        }
        if (button.Option.IsLocked)
        {
            return ExecutionResult.Failure($"Event option index {optionName} is Locked");
        }
        parsedData.Button = button;

        return ExecutionResult.Success();
    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)

    {
        return action.Name switch
        {
            "select_event_option" => await SelectEventOption(result, ctx),
            "proceed" => await Proceed(result),
            _ => null
        };
    }

    private async Task<ExecutionResult> SelectEventOption(Result result, ContextInfo ctx)
    {

        // GameStabilityDetector.ResetWasStable();
        await GodotMainThread.ClickAsync(result.Button);
        Plugin.Log($"Selected event option {result.Button?.Name ?? "unknown"}");
        return ExecutionResult.Success("Event option selected");
    }

    private async Task<ExecutionResult> Proceed(Result result)
    {
        await GodotMainThread.ClickAsync(result.Button);
        Plugin.Log("Clicked event proceed");
        return ExecutionResult.Success("Proceeded");

    }

    /// <summary>
    /// If an ancient event is showing dialogue (hitbox visible), click it to advance.
    /// Returns true if dialogue was advanced.
    /// </summary>
    public static bool TryAdvanceAncientDialogue()
    {
        try
        {
            var sceneRoot = SceneHelper.GetSceneRoot();
            if (sceneRoot == null) return false;

            var ancientLayout = UiHelper.FindFirst<NAncientEventLayout>(sceneRoot);
            if (ancientLayout == null) return false;

            var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
            if (hitbox == null || !hitbox.Visible || !hitbox.IsEnabled) return false;

            Plugin.LogDebug("Ancient dialogue detected, auto-advancing...");
            hitbox.EmitSignal(NClickableControl.SignalName.Released, hitbox);

            var timer = ancientLayout.GetTree().CreateTimer(0.6);
            timer.Connect("timeout", Callable.From(GameStabilityDetector.ScheduleStabilityCheck));
            return true;
        }
        catch (Exception e)
        {
            Plugin.LogError($"TryAdvanceAncientDialogue error: {e.Message}");
            return false;
        }
    }
}
