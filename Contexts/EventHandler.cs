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

namespace Sts2Agent.Contexts;

public class EventContextHandler : IContextHandler
{
    public ContextType Type => ContextType.Event;

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
            // commands.Add(new Dictionary<string, object> { ["type"] = "proceed" });
        }
        else
        {
            for (int i = 0; i < evt.CurrentOptions.Count; i++)
            {
                var opt = evt.CurrentOptions[i];
                if (!opt.IsLocked)
                {
                    // commands.Add(new Dictionary<string, object>
                    // {
                    //     ["type"] = "select_event_option",
                    //     ["optionIndex"] = i,
                    //     ["label"] = TextHelper.SafeLocString(() => opt.Title)
                    // });
                }
            }
        }

        return commands;
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
        var optionIndex = root.GetProperty("optionIndex").GetInt32();

        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ActionResult.Error("Cannot access scene tree");

        var allButtons = UiHelper.FindAll<NEventOptionButton>(sceneRoot);

        // Buttons are added to the container in CurrentOptions order,
        // so tree-order index matches the event option index
        var button = optionIndex < allButtons.Count ? allButtons[optionIndex] : null;

        if (button == null || button.Option.IsLocked)
        {
            Plugin.LogDebug($"Event button lookup: requested={optionIndex}, found={allButtons.Count} buttons");
            return ActionResult.Error($"Event option index {optionIndex} not found or locked");
        }

        await GodotMainThread.ClickAsync(button);
        Plugin.Log($"Selected event option {optionIndex}");
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

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

}
