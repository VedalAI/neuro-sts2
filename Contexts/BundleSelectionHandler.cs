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
using NeuroSdk.Json;
using System.Text;
using MegaCrit.Sts2.Core.Models.Cards;

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


    public string GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        var bundles = ctx.Bundles;
        if (bundles == null) return stringBuilder.ToString();
        if (bundles.Count == 0) return stringBuilder.ToString();
        stringBuilder.AppendLine($"You have to select a bundle of cards");
        stringBuilder.AppendLine($"You have {bundles.Count} bundles available to choose from:");

        for (int i = 0; i < bundles.Count; i++)
        {
            NCardBundle? bundle = bundles[i];
            if (bundle.Bundle == null) continue;

            stringBuilder.AppendLine($"[{i}] bundle: (");
            foreach (var card in bundle.Bundle)
            {
                stringBuilder.Append($", {card.Title} - {TextHelper.GetCardDescription(card)}");
            }
            stringBuilder.AppendLine(")");
        }
        return stringBuilder.ToString();
    }
    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var bundles = ctx.Bundles;
        if (bundles == null) return commands;

        commands.Add(new("select_bundle", "Select a bundle of cards", QJS.WrapObject(new Dictionary<string, JsonSchema>
        {
            ["bundleIndex"] = new()
            {
                Type = JsonSchemaType.Integer,
                Minimum = 0,
                Maximum = bundles.Count - 1
            }
        })));

        return commands;
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        //TODO: Proper Validation
        parsedData = data.Data;
        return ExecutionResult.Success();
    }
    public async Task<ExecutionResult?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        return action.Name switch
        {
            "select_bundle" => await SelectBundle(root, ctx),
            _ => null
        };
    }

    private async Task<ExecutionResult> SelectBundle(JsonElement root, ContextInfo ctx)
    {
        var bundleIndex = root.GetProperty("bundleIndex").GetInt32();
        var bundles = ctx.Bundles;

        if (bundles == null || bundleIndex < 0 || bundleIndex >= bundles.Count)
            return ExecutionResult.Failure($"Bundle index {bundleIndex} out of range (available: {bundles?.Count ?? 0})");

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
            return ExecutionResult.Failure("Confirm button not found or not enabled after selecting bundle");

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
                return ExecutionResult.Success("Bundle selected");
            }
        }

        Plugin.Log($"Selected bundle {bundleIndex} (overlay may still be closing)");
        return ExecutionResult.Success("Bundle selected");
    }

}
