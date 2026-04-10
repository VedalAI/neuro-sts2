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
using System.Text;

namespace Sts2Agent.Contexts;

public class TreasureHandler : IContextHandler
{
    public ContextType Type => ContextType.Treasure;

    public string GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine("You are a Treasure Room");
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        var relics = sync?.CurrentRelics;
        if (relics != null)
        {
            stringBuilder.AppendLine($"In the Treasure Chest there were {relics.Count} relics:");
            foreach (var r in relics)
            {
                stringBuilder.AppendLine($"\t- {TextHelper.SafeLocString(() => r.Title)} - {TextHelper.GetRelicDescription(r)}");
            }
        }
        return stringBuilder.ToString();
    }
    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();

        var room = TreasureRoomAutoPatch.CurrentRoom;
        if (room != null && GodotObject.IsInstanceValid(room) && room.ProceedButton?.IsEnabled == true)
            commands.Add(new("proceed", "Proceed out of the treasure room"));

        return commands;
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        parsedData = data.Data;
        return ExecutionResult.Success();
    }

    public async Task<ExecutionResult?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        if (action.Name == "proceed")
            return await Proceed();
        return null;
    }

    private async Task<ExecutionResult> Proceed()
    {
        var room = TreasureRoomAutoPatch.CurrentRoom;
        if (room == null || !GodotObject.IsInstanceValid(room))
            return ExecutionResult.Failure("No treasure room");

        var button = room.ProceedButton;
        if (button == null || !button.IsEnabled)
            return ExecutionResult.Failure("Proceed button not available");

        await GodotMainThread.ClickAsync(button);
        Plugin.Log("Clicked proceed on treasure room");
        return ExecutionResult.Success("Proceeded from treasure room");
    }
}
