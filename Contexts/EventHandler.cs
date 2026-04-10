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

public class EventContextHandler : IContextHandler
{
    public ContextType Type => ContextType.Event;

    public string GetContext(ContextInfo ctx)
    {
        var eventRoom = ctx.EventRoom;
        var evt = eventRoom.LocalMutableEvent;
        if (evt == null)
        {
            return $"You are in the Event: {eventRoom.CanonicalEvent.Title.GetUnformatedText()}";
        }

        StringBuilder eventBuilder = new();
        eventBuilder.AppendLine("## You are in an Event");

        eventBuilder.AppendLine("Event name: " + TextHelper.SafeLocString(() => evt.Title));


        try
        {
            eventBuilder.Append("Event Description: ");
            var desc = evt.Description;
            if (desc != null)
                eventBuilder.AppendLine(desc.GetUnformatedText());
            else
                eventBuilder.AppendLine(TextHelper.SafeLocString(() => evt.InitialDescription));
        }
        catch
        {
        }
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

        return eventBuilder.ToString();
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

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var eventRoom = ctx.EventRoom;
        if (eventRoom == null) return commands;

        var evt = eventRoom.LocalMutableEvent;
        if (evt == null) return commands;

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

        return commands;
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        parsedData = data.Data;
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ExecutionResult.ModFailure("Cannot access scene tree");

        if (action.Name == "proceed")
        {
            // Try proceed button
            var proceedButton = UiHelper.FindFirst<NProceedButton>(sceneRoot);
            if (proceedButton != null)
            {
                return ExecutionResult.Success();
            }

            // Finished events use NEventOptionButton with IsProceed=true
            var eventProceed = UiHelper.FindAll<NEventOptionButton>(sceneRoot)
                .FirstOrDefault(b => b.Option.IsProceed);
            if (eventProceed != null)
            {
                return ExecutionResult.Success();
            }

            return ExecutionResult.ModFailure("Couldn't find a Proceed button. You are mostlikely stuck here...");
        }
        var optionName = data?.Data?["option"]?.GetValue<string>() ?? ""; //TODO: Figure out if this is good enough.


        var allButtons = UiHelper.FindAll<NEventOptionButton>(sceneRoot);

        // Buttons are added to the container in CurrentOptions order,
        // so tree-order index matches the event option index
        var button = allButtons.Find((btn) => btn.Option.Title.GetUnformatedText() == optionName);

        if (button == null)
        {
            Plugin.LogDebug($"Event button lookup: requested={optionName}, found={allButtons.Count} buttons");
            return ExecutionResult.Failure($"Event option index {optionName} not found");
        }
        if (button.Option.IsLocked)
        {
            return ExecutionResult.Failure($"Event option index {optionName} is Locked");
        }

        return ExecutionResult.Success();
    }
    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        return action.Name switch
        {
            "select_event_option" => await SelectEventOption(root, ctx),
            "proceed" => await Proceed(),
            _ => null
        };
    }

    private async Task<ActionResult.Result> SelectEventOption(JsonElement root, ContextInfo ctx)
    {
        var optionName = root.GetProperty("option").GetString();

        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ActionResult.Error("Cannot access scene tree");

        var allButtons = UiHelper.FindAll<NEventOptionButton>(sceneRoot);

        // Buttons are added to the container in CurrentOptions order,
        // so tree-order index matches the event option index
        var button = allButtons.Find((btn) => btn.Option.Title.GetUnformatedText() == optionName);

        if (button == null || button.Option.IsLocked)
        {
            Plugin.LogDebug($"Event button lookup: requested={optionName}, found={allButtons.Count} buttons");
            return ActionResult.Error($"Event option index {optionName} not found or locked");
        }

        GameStabilityDetector.ResetWasStable();

        await GodotMainThread.ClickAsync(button);
        Plugin.Log($"Selected event option {optionName}");
        return ActionResult.Ok("Event option selected");
    }

    private async Task<ActionResult.Result> Proceed()
    {
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ActionResult.Error("Cannot access scene tree");

        // Try proceed button
        var proceedButton = UiHelper.FindFirst<NProceedButton>(sceneRoot);
        if (proceedButton != null)
        {
            await GodotMainThread.ClickAsync(proceedButton);
            Plugin.Log("Clicked proceed");
            return ActionResult.Ok("Proceeded");
        }

        // Finished events use NEventOptionButton with IsProceed=true
        var eventProceed = UiHelper.FindAll<NEventOptionButton>(sceneRoot)
            .FirstOrDefault(b => b.Option.IsProceed);
        if (eventProceed != null)
        {
            await GodotMainThread.ClickAsync(eventProceed);
            Plugin.Log("Clicked event proceed");
            return ActionResult.Ok("Proceeded");
        }

        return ActionResult.Error("No proceed button found");
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
