using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using NeuroSdk.Json;

namespace Sts2Agent.Contexts;

public class CardSelectionHandler : IContextHandler
{
    public ContextType Type => ContextType.CardSelection;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var cardHolders = ctx.CardHolders;
        if (cardHolders == null || cardHolders.Count == 0) return null;

        var cards = cardHolders
            .Select((h, i) =>
            {
                var cardNode = h.CardNode;
                var card = cardNode?.Model;
                if (card == null) return null;
                var result = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = card.Title,
                    ["description"] = TextHelper.GetCardDescriptionFromNode(cardNode!) ?? TextHelper.GetCardDescription(card)
                };
                if (card.EnergyCost != null)
                {
                    if (card.EnergyCost.CostsX)
                        result["cost"] = "X";
                    else
                        result["cost"] = card.EnergyCost.Canonical;
                }
                return result;
            })
            .Where(c => c != null)
            .ToList();

        if (cards.Count == 0) return null;

        var overlay = new Dictionary<string, object>
        {
            ["type"] = "card_selection",
            ["cards"] = cards
        };

        var canSkip = ctx.OverlayScreen is NCardRewardSelectionScreen;
        if (!canSkip && ctx.OverlayNode != null)
            canSkip = UiHelper.FindFirst<NChoiceSelectionSkipButton>(ctx.OverlayNode) != null;
        overlay["canSkip"] = canSkip;

        // Min/max select for multi-pick screens
        var prefsField = ctx.OverlayScreen?.GetType().GetField("_prefs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (prefsField != null)
        {
            try
            {
                dynamic prefs = prefsField.GetValue(ctx.OverlayScreen)!;
                overlay["minSelect"] = (int)prefs.MinSelect;
                overlay["maxSelect"] = (int)prefs.MaxSelect;
            }
            catch { }
        }

        return overlay;
    }


    public string GetContext(ContextInfo ctx)
    {
        return "You need to select a card";
    }
    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var cardHolders = ctx.CardHolders;
        if (cardHolders == null) return commands;

        // Don't offer commands until the screen is fully initialized (_completionSource set)
        if (ctx.OverlayScreen != null)
        {
            var tcsField = ctx.OverlayScreen.GetType().GetField("_completionSource",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (tcsField != null && tcsField.GetValue(ctx.OverlayScreen) == null)
                return commands;
        }
        var min_select = 1;
        var max_select = 1;
        var prefsField = ctx.OverlayScreen?.GetType().GetField("_prefs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (prefsField != null)
        {
            try
            {
                dynamic prefs = prefsField.GetValue(ctx.OverlayScreen)!;
                min_select = (int)prefs.MinSelect;
                max_select = (int)prefs.MaxSelect;
            }
            catch { }
        }
        if (min_select >= 0)
        {
            commands.Add(new("select_multiple_cards", min_select != max_select ? $"Select {min_select} up to {max_select} cards" : $"Select {max_select} cards", new()
            {
                Type = JsonSchemaType.Array,
                MinItems = min_select,
                MaxItems = max_select,
                Items = QJS.Enum(cardHolders.Select((x) => x.CardNode!.Model!.Title).Distinct()),
            }, true));
        }
        else
        {
            commands.Add(new("select_card", "Select a available card", new()
            {
                Type = JsonSchemaType.Object,
                Required = ["card"],
                Properties = {
                ["card"] = QJS.Enum(cardHolders.Select((x)=> x.CardNode!.Model!.Title).Distinct()),
                }
            }, true));

        }

        var canSkip = ctx.OverlayScreen is NCardRewardSelectionScreen;
        if (!canSkip && ctx.OverlayNode != null)
            canSkip = UiHelper.FindFirst<NChoiceSelectionSkipButton>(ctx.OverlayNode) != null;
        if (canSkip)
            commands.Add(new("skip", "Skip this selection, No card is going to be added to your deck"));

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)
    {
        return action.Name switch
        {
            "select_card" => await SelectCard(root, ctx),
            "select_multiple_cards" => await SelectMultipleCards(ctx, root),
            "skip" => await Skip(ctx),
            _ => ActionResult.Error("Unknown Action")
        };
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        parsedData = data.Data;
        if (action.Name == "skip")
        {
            return ExecutionResult.Success();
        }
        else if (action.Name == "select_card")
        {
            var cardIndex = data.Data?["card"]?.GetValue<string>();
            if (cardIndex == null)
            {
                return ExecutionResult.Failure("Missing Parameter card");
            }
            if (ctx?.OverlayScreen == null || ctx.OverlayNode == null)
                return ExecutionResult.Failure("No card selection screen open");
            var holders = ctx.CardHolders;
            if (holders == null)
                return ExecutionResult.Failure($"Card index {cardIndex} out of range (available: {holders?.Count ?? 0})");

            var holder = holders.First((x) => x.CardNode?.Model?.Title == cardIndex);
            if (holder == null)
            {
                return ExecutionResult.Failure($"Card name {cardIndex} not in deck");
            }
            return ExecutionResult.Success();
        }
        else if (action.Name == "select_multiple_cards")
        {

            var all_nodes = new List<NCardHolder>();
            var cardIndex = data.Data?.AsArray();
            if (cardIndex == null)
            {
                return ExecutionResult.Failure("Missing Parameter cards");
            }
            if (ctx?.OverlayScreen == null || ctx.OverlayNode == null)
                return ExecutionResult.Failure("No card selection screen open");
            var holders = ctx.CardHolders;
            if (holders == null)
                return ExecutionResult.Failure($"Card index {cardIndex} out of range (available: {holders?.Count ?? 0})");
            foreach (var item in cardIndex)
            {
                var cardName = item.GetValue<string>();

                var holder = holders.FirstOrDefault((x) => x.CardNode.Model.Title == cardName && !all_nodes.Contains(x));
                if (holder == null)
                {
                    return ExecutionResult.Failure($"Not Enough of {cardName} in Deck. Select fewer and or a different card");
                }
                all_nodes.Add(holder);
            }
            return ExecutionResult.Success();
        }

        return ExecutionResult.Failure("Unkown Action");
    }
    private async Task<ActionResult.Result> SelectCard(JsonElement root, ContextInfo ctx)
    {
        var cardIndex = root.GetProperty("card").GetString();

        if (ctx.OverlayScreen == null || ctx.OverlayNode == null)
            return ActionResult.Error("No card selection screen open");

        // Wait for _completionSource to be set
        var tcsField = ctx.OverlayScreen.GetType().GetField("_completionSource",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (tcsField != null)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (tcsField.GetValue(ctx.OverlayScreen) != null) break;
                await Task.Delay(100);
            }
            if (tcsField.GetValue(ctx.OverlayScreen) == null)
                return ActionResult.Error("Card selection screen not ready (_completionSource is null)");
        }

        var holders = ctx.CardHolders;
        if (holders == null)
            return ActionResult.Error($"Card index {cardIndex} out of range (available: {holders?.Count ?? 0})");

        var holder = holders.First((x) => x.CardNode.Model.Title == cardIndex);
        if (holder == null)
        {
            return ActionResult.Error($"Card name {cardIndex} not in deck");
        }
        var isGridScreen = ctx.IsGridScreen;
        var grid = isGridScreen && ctx.OverlayNode != null
            ? UiHelper.FindFirst<NCardGrid>(ctx.OverlayNode) : null;

        var selectedCardName = holder.CardNode?.Model?.Title.ToString() ?? "unknown";
        Plugin.LogDebug($"SelectCard: overlay type={ctx.OverlayScreen.GetType().Name}, cardIndex={cardIndex}, card={selectedCardName}, holderCount={holders.Count}");

        // Emit signal on main thread
        var completed = await Task.WhenAny(
            GodotMainThread.RunAsync(() =>
            {
                if (grid != null)
                    grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
                else
                    holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            }),
            Task.Delay(5000));

        if (completed is Task<bool> { IsCompletedSuccessfully: false })
            return ActionResult.Error("SelectCard timed out emitting signal");

        // Non-grid: click completes selection, wait for close
        if (!isGridScreen)
        {
            await WaitForOverlayClose(ctx.OverlayNode, ctx.OverlayScreen);
            Plugin.Log($"Selected card {cardIndex}");
            return ActionResult.Ok("Card selected");
        }

        // Grid: check if auto-closed
        await Task.Delay(200);
        if (!GodotObject.IsInstanceValid(ctx.OverlayNode) || NOverlayStack.Instance?.Peek() != ctx.OverlayScreen)
        {
            Plugin.LogDebug($"Selected card {cardIndex} (screen auto-closed)");
            return ActionResult.Ok("Card selected");
        }

        // Check for confirm button
        var confirmButtons = UiHelper.FindAll<NConfirmButton>(ctx.OverlayNode);
        NConfirmButton? enabledButton = confirmButtons.FirstOrDefault(b => b.IsEnabled);

        if (enabledButton == null)
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Delay(100);
                enabledButton = confirmButtons.FirstOrDefault(b => b.IsEnabled);
                if (enabledButton != null) break;
            }
        }

        if (enabledButton != null)
        {
            await Task.Delay(2000);
            await GodotMainThread.ClickAsync(enabledButton);
            Plugin.LogDebug("SelectCard: clicked confirm button");
            await WaitForOverlayClose(ctx.OverlayNode, ctx.OverlayScreen);
        }
        else
        {
            Plugin.LogDebug("SelectCard: partial selection (no confirm enabled yet)");
        }

        Plugin.Log($"Selected card {cardIndex}");
        return ActionResult.Ok("Card selected");
    }

    private async Task<ActionResult.Result> SelectMultipleCards(ContextInfo ctx, JsonElement root)
    {

        var all_selected_nodes = new List<NCardHolder>();
        foreach (var item in root.EnumerateArray())
        {
            Plugin.LogDebug(item.ToString());
            var cardName = item.GetString();


            if (ctx.OverlayScreen == null || ctx.OverlayNode == null)
                return ActionResult.Error("No card selection screen open");

            // Wait for _completionSource to be set
            var tcsField = ctx.OverlayScreen.GetType().GetField("_completionSource",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (tcsField != null)
            {
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (tcsField.GetValue(ctx.OverlayScreen) != null) break;
                    await Task.Delay(100);
                }
                if (tcsField.GetValue(ctx.OverlayScreen) == null)
                    return ActionResult.Error("Card selection screen not ready (_completionSource is null)");
            }

            var holders = ctx.CardHolders;
            if (holders == null)
                return ActionResult.Error($"Card index {cardName} out of range (available: {holders?.Count ?? 0})");

            var holder = holders.First((x) => x.CardNode.Model.Title == cardName && !all_selected_nodes.Contains(x));
            if (holder == null)
            {
                return ActionResult.Error($"Card name {cardName} not in deck");
            }
            all_selected_nodes.Add(holder);
            var isGridScreen = ctx.IsGridScreen;
            var grid = isGridScreen && ctx.OverlayNode != null
                ? UiHelper.FindFirst<NCardGrid>(ctx.OverlayNode) : null;

            // Emit signal on main thread
            var completed = await Task.WhenAny(
                GodotMainThread.RunAsync(() =>
                {
                    if (grid != null)
                        grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
                    else
                        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                }),
                Task.Delay(1000));

            if (completed is Task<bool> { IsCompletedSuccessfully: false })
                return ActionResult.Error("SelectCard timed out emitting signal");

            // Non-grid: click completes selection, wait for close
            if (!isGridScreen)
            {
                await WaitForOverlayClose(ctx.OverlayNode, ctx.OverlayScreen);
                Plugin.Log($"Selected card {cardName}");
                return ActionResult.Ok("Card selected");
            }

            // Grid: check if auto-closed
            await Task.Delay(200);
            if (!GodotObject.IsInstanceValid(ctx.OverlayNode) || NOverlayStack.Instance?.Peek() != ctx.OverlayScreen)
            {
                Plugin.LogDebug($"Selected card {cardName} (screen auto-closed)");
                return ActionResult.Ok("Card selected");
            }

            // Check for confirm button
            var confirmButtons = UiHelper.FindAll<NConfirmButton>(ctx.OverlayNode);
            NConfirmButton? enabledButton = confirmButtons.FirstOrDefault(b => b.IsEnabled);

            if (enabledButton == null)
            {
                for (int i = 0; i < 5; i++)
                {
                    await Task.Delay(100);
                    enabledButton = confirmButtons.FirstOrDefault(b => b.IsEnabled);
                    if (enabledButton != null) break;
                }
            }

            if (enabledButton != null)
            {
                await Task.Delay(2000);
                await GodotMainThread.ClickAsync(enabledButton);
                Plugin.LogDebug("SelectCard: clicked confirm button");
                await WaitForOverlayClose(ctx.OverlayNode, ctx.OverlayScreen);
            }
            else
            {
                Plugin.LogDebug("SelectCard: partial selection (no confirm enabled yet)");
            }

            Plugin.Log($"Selected card {cardName}");
        }
        return ActionResult.Ok("Cards selected");
    }

    private async Task<ActionResult.Result> Skip(ContextInfo ctx)
    {
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ActionResult.Error("Cannot access scene tree");

        var skipButton = UiHelper.FindFirst<NChoiceSelectionSkipButton>(sceneRoot);
        if (skipButton != null)
        {
            await GodotMainThread.ClickAsync(skipButton);
            Plugin.Log("Clicked skip button");
            return ActionResult.Ok("Skipped");
        }

        // Card reward screens use NCardRewardAlternativeButton instead of NChoiceSelectionSkipButton
        if (ctx.OverlayScreen is NCardRewardSelectionScreen cardRewardScreen)
        {
            var altButtons = UiHelper.FindAll<NCardRewardAlternativeButton>((Node)cardRewardScreen);
            if (altButtons.Count > 0)
            {
                // Click the first alternative button (Skip). Reroll is added second if present.
                await GodotMainThread.ClickAsync(altButtons[0]);
                await WaitForOverlayClose(ctx.OverlayNode!, ctx.OverlayScreen);
                Plugin.Log("Clicked skip on card reward screen");
                return ActionResult.Ok("Skipped");
            }
        }

        // Fallback: proceed button
        var proceedButton = UiHelper.FindFirst<MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton>(sceneRoot);
        if (proceedButton != null)
        {
            await GodotMainThread.ClickAsync(proceedButton);
            Plugin.Log("Clicked proceed (as skip fallback)");
            return ActionResult.Ok("Skipped (proceed)");
        }

        return ActionResult.Error("No skip or proceed button found");
    }

    private static async Task WaitForOverlayClose(Node overlayNode, object overlay, int timeoutMs = 5000)
    {
        var iterations = timeoutMs / 100;
        for (int i = 0; i < iterations; i++)
        {
            await Task.Delay(100);
            if (!GodotObject.IsInstanceValid(overlayNode) || NOverlayStack.Instance?.Peek() != overlay)
                return;
        }
        Plugin.LogDebug("WaitForOverlayClose: timed out");
    }


}
