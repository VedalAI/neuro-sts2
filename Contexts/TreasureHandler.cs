using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class TreasureHandler : IContextHandler
{
    public ContextType Type => ContextType.Treasure;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var result = new Dictionary<string, object>();
        try
        {
            var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            var relics = sync?.CurrentRelics;
            if (relics != null)
            {
                result["relics"] = relics.Select((r, i) => new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = TextHelper.SafeLocString(() => r.Title),
                    ["description"] = TextHelper.GetRelicDescription(r)
                }).ToList();
            }
        }
        catch { }

        var room = TreasureRoomAutoPatch.CurrentRoom;
        result["proceedAvailable"] = room != null
            && GodotObject.IsInstanceValid(room)
            && room.ProceedButton?.IsEnabled == true;

        return result;
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();

        var room = TreasureRoomAutoPatch.CurrentRoom;
        // if (room != null && GodotObject.IsInstanceValid(room) && room.ProceedButton?.IsEnabled == true)
        //     commands.Add(new Dictionary<string, object> { ["type"] = "proceed" });

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        if (action.Name == "proceed")
            return await Proceed();
        return null;
    }

    private async Task<ActionResult.Result> Proceed()
    {
        var room = TreasureRoomAutoPatch.CurrentRoom;
        if (room == null || !GodotObject.IsInstanceValid(room))
            return ActionResult.Error("No treasure room");

        var button = room.ProceedButton;
        if (button == null || !button.IsEnabled)
            return ActionResult.Error("Proceed button not available");

        await GodotMainThread.ClickAsync(button);
        Plugin.Log("Clicked proceed on treasure room");
        return ActionResult.Ok("Proceeded from treasure room");
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

}
