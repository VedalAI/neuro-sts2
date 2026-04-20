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
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace Sts2Agent.Contexts;

public class TreasureHandler : IContextHandler<TreasureHandler.Result>
{
    public class Result : IContextResult
    {
        public NProceedButton ProceedButton;
    }
    public ContextType Type => ContextType.Treasure;

    public ContextReturn GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine("You are in a treasure room.");
        var sync = RunManager.Instance.TreasureRoomRelicSynchronizer;
        var relics = sync?.CurrentRelics;
        if (relics != null)
        {
            stringBuilder.AppendLine($"The treasure chest contained {relics.Count} relics:");
            foreach (var r in relics)
            {
                stringBuilder.AppendLine($"\t- {TextHelper.SafeLocString(() => r.Title)} - {TextHelper.GetRelicDescription(r)}");
            }
        }
        return new ContextReturn(stringBuilder.ToString());
    }
    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();

        var room = TreasureRoomAutoPatch.CurrentRoom;
        if (room != null && GodotObject.IsInstanceValid(room) && room.ProceedButton?.IsEnabled == true)
            commands.Add(new("proceed", "Leave the treasure room"));

        return new CommandReturn(commands);
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        var room = TreasureRoomAutoPatch.CurrentRoom;
        if (room == null || !GodotObject.IsInstanceValid(room))
            return ExecutionResult.Failure("No treasure room");

        var button = room.ProceedButton;
        if (button == null || !button.IsEnabled)
            return ExecutionResult.Failure("Proceed button not available");
        parsedData.ProceedButton = button;
        return ExecutionResult.Success();
    }

    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)

    {
        if (action.Name == "proceed")
            return await Proceed(result);
        return null;
    }

    private async Task<ExecutionResult> Proceed(Result result)
    {

        await GodotMainThread.ClickAsync(result.ProceedButton);
        Plugin.Log("Clicked proceed on treasure room");
        return ExecutionResult.Success("Left the treasure room");
    }
}
