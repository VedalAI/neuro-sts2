using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class BundleSelectionHandler : IContextHandler
{
    public ContextType Type => ContextType.BundleSelection;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var bundles = ctx.Bundles;
        if (bundles == null || bundles.Count == 0) return null;

        var bundleList = bundles
            .Select((b, i) =>
            {
                if (b.Bundle == null) return null;
                var cards = b.Bundle
                    .Select(card =>
                    {
                        var d = new Dictionary<string, object>
                        {
                            ["name"] = card.Title,
                            ["description"] = TextHelper.GetCardDescription(card)
                        };
                        if (card.EnergyCost != null)
                        {
                            d["cost"] = card.EnergyCost.CostsX ? "X" : (object)card.EnergyCost.Canonical;
                        }
                        return d;
                    })
                    .ToList();
                return new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["cards"] = cards
                };
            })
            .Where(b => b != null)
            .ToList();

        return new Dictionary<string, object>
        {
            ["type"] = "bundle_selection",
            ["bundles"] = bundleList
        };
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var bundles = ctx.Bundles;
        if (bundles == null) return commands;

        for (int i = 0; i < bundles.Count; i++)
        {
            var bundle = bundles[i];
            if (bundle.Bundle == null) continue;

            var cardNames = string.Join(", ", bundle.Bundle.Select(c => c.Title));
            // commands.Add(new Dictionary<string, object>
            // {
            //     ["type"] = "select_bundle",
            //     ["bundleIndex"] = i,
            //     ["cards"] = cardNames
            // });
        }

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        return action.Name switch
        {
            "select_bundle" => await SelectBundle(root, ctx),
            _ => null
        };
    }

    private async Task<ActionResult.Result> SelectBundle(JsonElement root, ContextInfo ctx)
    {
        var bundleIndex = root.GetProperty("bundleIndex").GetInt32();
        var bundles = ctx.Bundles;

        if (bundles == null || bundleIndex < 0 || bundleIndex >= bundles.Count)
            return ActionResult.Error($"Bundle index {bundleIndex} out of range (available: {bundles?.Count ?? 0})");

        var bundle = bundles[bundleIndex];
        var overlayNode = ctx.OverlayNode;
        var overlayScreen = ctx.OverlayScreen;

        // Click the bundle to open preview
        await GodotMainThread.RunAsync(() =>
        {
            bundle.EmitSignal(NCardBundle.SignalName.Clicked, bundle);
        });

        // Wait for preview to appear with confirm button
        await Task.Delay(2000);

        // Find and click confirm button
        NConfirmButton? confirmButton = null;
        for (int i = 0; i < 20; i++)
        {
            if (overlayNode == null || !GodotObject.IsInstanceValid(overlayNode)) break;
            confirmButton = UiHelper.FindFirst<NConfirmButton>(overlayNode);
            if (confirmButton != null && confirmButton.IsEnabled) break;
            confirmButton = null;
            await Task.Delay(100);
        }

        if (confirmButton == null)
            return ActionResult.Error("Confirm button not found or not enabled after selecting bundle");

        await GodotMainThread.ClickAsync(confirmButton);
        Plugin.LogDebug("BundleSelection: clicked confirm button");

        // Wait for overlay to close
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            if (overlayNode == null || !GodotObject.IsInstanceValid(overlayNode)
                || NOverlayStack.Instance?.Peek() != overlayScreen)
            {
                Plugin.Log($"Selected bundle {bundleIndex}");
                return ActionResult.Ok("Bundle selected");
            }
        }

        Plugin.Log($"Selected bundle {bundleIndex} (overlay may still be closing)");
        return ActionResult.Ok("Bundle selected");
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

}
