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

namespace Sts2Agent.Contexts;

public class MapHandler : IContextHandler
{
    public ContextType Type => ContextType.Map;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var runState = ctx.RunState;
        var result = new Dictionary<string, object>
        {
            ["act"] = runState.CurrentActIndex + 1
        };

        var coord = runState.CurrentMapCoord;
        if (coord.HasValue)
            result["currentCoord"] = new { row = coord.Value.row, col = coord.Value.col };

        if (ctx.AvailableMapNodes != null)
        {
            result["availableNodes"] = ctx.AvailableMapNodes.Select(p => new Dictionary<string, object>
            {
                ["coord"] = new { row = p.coord.row, col = p.coord.col },
                ["type"] = GetMapPointName(p.PointType)
            }).ToList();
        }

        return result;
    }

    public string GetContext(ContextInfo ctx)
    {
        StringBuilder mapBuilder = new();
        mapBuilder.AppendLine($"# You are on the map about to travel somewhere, Act {ctx.RunState.CurrentActIndex + 1}");
        mapBuilder.AppendLine($"Your current position is: {ctx.RunState?.CurrentMapCoord.ToString()}");
        mapBuilder.AppendLine($"You can travel to these locations:");
        foreach (var coords in ctx.AvailableMapNodes ?? [])
        {
            mapBuilder.AppendLine($"- [{coords.coord.row},{coords.coord.col}] {GetMapPointName(coords.PointType)}");
        }
        return mapBuilder.ToString();
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        if (ctx.AvailableMapNodes == null) return commands;

        commands.Add(new("select_map_node", "Select a point to travel to", QJS.WrapObject(new Dictionary<string, JsonSchema>
        {
            ["coord"] = QJS.Enum(ctx.AvailableMapNodes.Select((node) => $"{node.coord.row},{node.coord.col}"))
        })));


        return commands;
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        parsedData = data.Data;
        if (data.Data?["coord"]?.GetValue<string>() == null)
        {
            return ExecutionResult.Failure("missing parameter \"coord\"");
        }
        var index = data.Data["coord"]!.GetValue<string>();
        var coord = index.Split(",");
        if (coord.Length <= 0 || coord.Length > 2)
        {
            return ExecutionResult.Failure("coord is malformed");
        }
        try
        {
            var target = ctx.AvailableMapNodes.Find((x) => x.coord.row == int.Parse(coord[0]) && x.coord.col == int.Parse(coord[1]));
            if (target == null)
            {
                return ExecutionResult.Failure("Couldn't find specified node");
            }

        }
        catch (Exception e)
        {
            return ExecutionResult.Failure($"Failed to validate coord {e.Message}");
        }
        return ExecutionResult.Success();
    }
    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        if (action.Name != "select_map_node") return null;

        var index = root.GetProperty("coord").GetString();
        var coord = index.Split(",");
        var nodes = ctx.AvailableMapNodes;

        var target = nodes.Find((x) => x.coord.row == int.Parse(coord[0]) && x.coord.col == int.Parse(coord[1]));
        if (target == null)
        {
            return ActionResult.Error("Couldn't find specified node");
        }
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ActionResult.Error("Cannot access scene tree");

        var mapPointNodes = UiHelper.FindAll<NMapPoint>(sceneRoot);
        var targetNode = mapPointNodes.FirstOrDefault(mp =>
            mp.Point.coord.row == target.coord.row && mp.Point.coord.col == target.coord.col);

        if (targetNode == null)
            return ActionResult.Error($"Map node UI element not found for ({target.coord.row}, {target.coord.col})");

        await GodotMainThread.ClickAsync(targetNode);

        // Wait for travel to start (IsTravelEnabled becomes false) or map to close
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            var ms = NMapScreen.Instance;
            if (ms == null || !ms.IsOpen || !ms.IsTravelEnabled)
                break;
        }

        Plugin.Log($"Selected map node {index} ({target.coord.row}, {target.coord.col})");
        return ActionResult.Ok("Map node selected");
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
