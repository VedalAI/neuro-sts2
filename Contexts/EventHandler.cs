using System;
using System.Collections.Generic;
using System.Linq;
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
        eventBuilder.AppendLine("## You are in an event");
        eventBuilder.AppendLine($"**Event name:** {TextHelper.SafeLocString(() => evt.Title)}");


        try
        {
            var desc = evt.Description;
            if (desc != null)
            {
                eventBuilder.AppendLine();
                eventBuilder.AppendLine("**Event description:**");
                eventBuilder.AppendLine(desc.GetUnformatedText());
            }
            else
            {
                eventBuilder.AppendLine();
                eventBuilder.AppendLine("**Event description:**");
                eventBuilder.AppendLine(TextHelper.SafeLocString(() => evt.InitialDescription));
            }
        }
        catch
        {
        }

        if (evt.CurrentOptions.Count > 0)
        {
            eventBuilder.AppendLine();
            eventBuilder.AppendLine("**Options:**");
        }
        foreach (var eventoption in evt.CurrentOptions)
        {
            string optionTitle = TextHelper.SafeLocString(() => eventoption.Title);
            string optionState = eventoption.IsLocked ? "(Locked)" : "";
            eventBuilder.Append($"- **{optionTitle}** {optionState}: ");

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
                    eventBuilder.AppendLine("No description");
                }

            }
            catch
            {

                eventBuilder.AppendLine("No description");
            }
        }

        var lockedOptions = evt.CurrentOptions.Where(option => option.IsLocked).ToList();
        if (lockedOptions.Count > 0)
        {
            eventBuilder.AppendLine();
            eventBuilder.AppendLine("**Locked options cannot currently be selected.**");
        }

        return new ContextReturn(eventBuilder.ToString());
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
            commands.Add(new("proceed", "Finish the current event"));
        }
        else
        {
            commands.Add(new("select_event_option", "Select an option in the event", QJS.WrapObject(new Dictionary<string, JsonSchema>
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

            return ExecutionResult.Unstable("Couldn't find a proceed button. You are most likely stuck here...");
        }
        var optionName = data?.Data?["option"]?.GetValue<string>() ?? ""; //TODO: Figure out if this is good enough.


        var allButtons = UiHelper.FindAll<NEventOptionButton>(sceneRoot);

        // Buttons are added to the container in CurrentOptions order,
        // so tree-order index matches the event option index
        var button = allButtons.Find((btn) => btn.Option.Title.GetUnformatedText() == optionName);

        if (button == null)
        {
            Plugin.LogDebug($"Event button lookup: requested={optionName}, found={allButtons.Count} buttons");
            return ExecutionResult.Unstable($"Event option '{optionName}' not found");
        }
        if (button.Option.IsLocked)
        {
            return ExecutionResult.Failure($"Event option '{optionName}' is locked");
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
