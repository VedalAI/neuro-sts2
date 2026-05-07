using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Localization;
using Godot;
using Sts2Agent.Utilities;
using STS2NeuroIntegration;
using NeuroSdk.Websocket;
using NeuroSdk.Actions;
using System.Text;
using NeuroSdk.Json;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Context;
using NeuroSdk.Messages.Outgoing;

namespace Sts2Agent.Contexts;

public class MapHandler : IContextHandler<MapHandler.Result>
{
    public class Result : IContextResult
    {
        public NMapPoint Target;
    }
    public ContextType Type => ContextType.Map;

    //TODO: Figure out how to best represent the paths. Like giving context where a choice can lead, i.e. shop or rest site
    public ContextReturn GetContext(ContextInfo ctx)
    {
        StringBuilder mapBuilder = new();
        mapBuilder.AppendLine($"# You are on the map and about to travel somewhere. Act {ctx.RunState.CurrentActIndex + 1}");
        mapBuilder.AppendLine($"Your current position is: {ctx.RunState?.CurrentMapCoord.ToString()}");
        mapBuilder.AppendLine($"You can travel to these locations:");
        foreach (var coords in ctx.AvailableMapNodes ?? [])
        {
            mapBuilder.AppendLine($"- [{coords.coord.row},{coords.coord.col}] {GetMapPointName(coords.PointType)}");
        }
        return new ContextReturn(mapBuilder.ToString());
    }

    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        if (ctx.AvailableMapNodes == null) return new CommandReturn();
        if (ctx.AvailableMapNodes.Count == 1)
        {
            ActionExecutor.EnqueueAction(new ConstructedAction("select_first_node", ""));
            NeuroIntegration.SendContext("Open map, Only a single travel point available, Moving to: " + GetMapPointName(ctx.AvailableMapNodes[0].PointType) + " Room , Automatically", true);
            return new();
        }
        if (Plugin.IsMultiplayer())
        {
            var ms = NMapScreen.Instance;
            var player = LocalContext.GetMe(ctx.RunState.Players);
            if (ms == null || player == null || (ms.PlayerVoteDictionary.TryGetValue(player, out var vote) && vote != null))
                return new CommandReturn();
        }


        commands.Add(new("select_map_node", "Select a point to travel to", QJS.WrapObject(new Dictionary<string, JsonSchema>
        {
            ["coordIndex"] = QJS.Type(JsonSchemaType.Integer)
        })));
        StringBuilder forceText = new();
        forceText.AppendLine("Please select a map node to travel to. use this index to choose the location you want to travel to:");
        int index = 0;
        foreach (var item in ctx.AvailableMapNodes)
        {
            var name = GetMapPointName(item.PointType);
            forceText.AppendLine($"- '{index}': {name} ({item.coord.row},{item.coord.col})");
            index++;
        }


        return new CommandReturn(commands, ForceText: forceText.ToString(), ForcePriority: ActionsForce.Priority.Medium);
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ExecutionResult.Failure("Cannot access scene tree");
        if (action.Name == "select_first_node")
        {
            if (ctx.AvailableMapNodes == null || ctx.AvailableMapNodes.Count == 0)
                return ExecutionResult.Unstable("No available map nodes to select");
            var target = ctx.AvailableMapNodes[0];
            var mapPointNodes = UiHelper.FindAll<NMapPoint>(sceneRoot);
            var targetNode = mapPointNodes.FirstOrDefault(mp =>
                mp.Point.coord.row == target.coord.row && mp.Point.coord.col == target.coord.col);

            if (targetNode == null)
                return ExecutionResult.Failure($"Map node UI element not found for ({target.coord.row}, {target.coord.col})");
            Plugin.Log($"Automatically selected the only available map node ({target.coord.row}, {target.coord.col})");
            parsedData.Target = targetNode;
            return ExecutionResult.Success();
        }
        if (action.Name != "select_map_node") return ExecutionResult.ModFailure("Unknown action called in the map");
        if (data.Data?["coordIndex"]?.GetValue<int>() is not int index)
        {
            return ExecutionResult.Failure("Missing parameter: coordIndex");
        }
        if (index < 0 || index >= ctx.AvailableMapNodes.Count)
        {
            return ExecutionResult.Failure("The coordIndex parameter is out of range");
        }
        try
        {
            var target = ctx.AvailableMapNodes[index];
            if (target == null)
            {
                return ExecutionResult.Failure("Couldn't find the specified node");
            }
            var mapPointNodes = UiHelper.FindAll<NMapPoint>(sceneRoot);
            var targetNode = mapPointNodes.FirstOrDefault(mp =>
                mp.Point.coord.row == target.coord.row && mp.Point.coord.col == target.coord.col);

            if (targetNode == null)
                return ExecutionResult.Failure($"Map node UI element not found for ({target.coord.row}, {target.coord.col})");
            Plugin.Log($"Selected map node {index} ({target.coord.row}, {target.coord.col})");
            parsedData.Target = targetNode;

        }
        catch (Exception e)
        {
            return ExecutionResult.Failure($"Failed to validate coord: {e.Message}");
        }
        return ExecutionResult.Success();
    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
    {
        await Task.Delay(100);
        await GodotMainThread.ClickAsync(result.Target);
        // Skip waiting in Multiplayer as we need to wait for all votes anyway
        if (!Plugin.IsMultiplayer())
        {
            // Wait for travel to start (IsTravelEnabled becomes false) or map to close
            for (int i = 0; i < 100; i++)
            {
                var ms = NMapScreen.Instance;
                if (ms == null || !ms.IsOpen || !ms.IsTravelEnabled)
                    break;
                await Task.Delay(100);
            }
        }
        return ExecutionResult.Success("Map node selected");
    }

    private static string GetMapPointName(MapPointType pointType)
    {
        var locKey = pointType switch
        {
            MapPointType.Unknown => "LEGEND_UNKNOWN",
            MapPointType.Shop => "LEGEND_MERCHANT",
            MapPointType.Treasure => "LEGEND_TREASURE",
            MapPointType.RestSite => "LEGEND_REST",
            MapPointType.Monster => "LEGEND_ENEMY",
            MapPointType.Elite => "LEGEND_ELITE",
            _ => null
        };
        if (locKey == null) return pointType.ToString();
        return TextHelper.StripBBCode(new LocString("map", locKey + ".title").GetFormattedText());
    }

}
