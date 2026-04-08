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

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        if (ctx.AvailableMapNodes == null) return commands;

        for (int i = 0; i < ctx.AvailableMapNodes.Count; i++)
        {
            var node = ctx.AvailableMapNodes[i];
            // commands.Add(new Dictionary<string, object>
            // {
            //     ["type"] = "select_map_node",
            //     ["index"] = i,
            //     ["nodeType"] = GetMapPointName(node.PointType),
            //     ["coord"] = new { row = node.coord.row, col = node.coord.col }
            // });
        }

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        if (action.Name != "select_map_node") return null;

        var index = root.GetProperty("index").GetInt32();
        var nodes = ctx.AvailableMapNodes;
        if (nodes == null || index < 0 || index >= nodes.Count)
            return ActionResult.Error($"Map node index {index} out of range (available: {nodes?.Count ?? 0})");

        var target = nodes[index];
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

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

}
