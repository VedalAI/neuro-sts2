using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class RestSiteHandler : IContextHandler
{
    public ContextType Type => ContextType.RestSite;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var restRoom = ctx.RestSiteRoom;
        if (restRoom == null) return null;

        return new Dictionary<string, object>
        {
            ["options"] = restRoom.Options.Select((opt, i) => new Dictionary<string, object>
            {
                ["index"] = i,
                ["id"] = opt.OptionId,
                ["name"] = TextHelper.SafeLocString(() => opt.Title),
                ["description"] = TextHelper.SafeLocString(() => opt.Description),
                ["enabled"] = opt.IsEnabled
            }).ToList()
        };
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var restRoom = ctx.RestSiteRoom;
        if (restRoom == null) return commands;

        foreach (var opt in restRoom.Options)
        {
            if (opt.IsEnabled)
            {
                // commands.Add(new Dictionary<string, object>
                // {
                //     ["type"] = "rest_option",
                //     ["option"] = opt.OptionId,
                //     ["name"] = TextHelper.SafeLocString(() => opt.Title)
                // });
            }
        }

        var nRestSiteRoom = FindNRestSiteRoom();
        // if (nRestSiteRoom?.ProceedButton?.IsEnabled == true)
        //     commands.Add(new Dictionary<string, object> { ["type"] = "proceed" });

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        if (action.Name == "proceed") return await Proceed();
        if (action.Name != "rest_option") return null;

        var option = root.GetProperty("option").GetString();

        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ActionResult.Error("Cannot access scene tree");

        var buttons = UiHelper.FindAll<NRestSiteButton>(sceneRoot)
            .Where(b => b.Option.IsEnabled)
            .ToList();

        if (buttons.Count == 0)
            return ActionResult.Error("No rest site options available");

        var match = buttons.FirstOrDefault(b =>
            b.Option.OptionId.Equals(option, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            if (int.TryParse(option, out var idx) && idx >= 0 && idx < buttons.Count)
                match = buttons[idx];
            else
                return ActionResult.Error($"Rest option '{option}' not found. Available: {string.Join(", ", buttons.Select(b => b.Option.OptionId))}");
        }

        await GodotMainThread.ClickAsync(match);
        Plugin.Log($"Selected rest option: {option}");

        // Wait for the animation to finish: proceed button becomes enabled or an overlay appears
        // (e.g., Smith opens card selection). Mirrors the game's AutoSlay RestSiteRoomHandler logic.
        var nRoom = FindNRestSiteRoom();
        if (nRoom != null)
        {
            for (int i = 0; i < 40; i++) // up to ~10s (40 * 250ms)
            {
                await Task.Delay(250);
                var ready = await GodotMainThread.RunAsync(() =>
                {
                    if (nRoom.ProceedButton?.IsEnabled == true) return true;
                    var overlay = NOverlayStack.Instance;
                    if (overlay != null && overlay.ScreenCount > 0) return true;
                    return false;
                });
                if (ready) break;
            }
        }

        return ActionResult.Ok("Rest option selected");
    }

    private async Task<ActionResult.Result> Proceed()
    {
        var nRestSiteRoom = FindNRestSiteRoom();
        if (nRestSiteRoom?.ProceedButton?.IsEnabled != true)
            return ActionResult.Error("Proceed button not available");

        await GodotMainThread.ClickAsync(nRestSiteRoom.ProceedButton);
        Plugin.Log("Clicked proceed (rest site)");
        return ActionResult.Ok("Proceeded");
    }

    private static NRestSiteRoom? FindNRestSiteRoom()
    {
        var sceneRoot = SceneHelper.GetSceneRoot();
        return sceneRoot == null ? null : UiHelper.FindFirst<NRestSiteRoom>(sceneRoot);
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

    public string GetContext(ContextInfo ctx)
    {
        throw new NotImplementedException();
    }
}
