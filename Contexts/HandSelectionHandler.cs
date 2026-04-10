using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using NeuroSdk.Json;
using System.Text;

namespace Sts2Agent.Contexts;

public class HandSelectionHandler : IContextHandler
{
    public ContextType Type => ContextType.HandSelection;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var hand = ctx.Hand;
        if (hand == null) return null;

        var overlay = new Dictionary<string, object>
        {
            ["type"] = "hand_select"
        };

        if (ReflectionCache.HandPrefs != null)
        {
            try
            {
                dynamic prefs = ReflectionCache.HandPrefs.GetValue(hand)!;
                overlay["prompt"] = TextHelper.StripBBCode(
                    ((MegaCrit.Sts2.Core.Localization.LocString)prefs.Prompt).GetFormattedText());
                overlay["minSelect"] = (int)prefs.MinSelect;
                overlay["maxSelect"] = (int)prefs.MaxSelect;
            }
            catch { }
        }

        if (ReflectionCache.HandSelectedCards != null)
        {
            try
            {
                var selected = (List<CardModel>)ReflectionCache.HandSelectedCards.GetValue(hand)!;
                overlay["selectedCount"] = selected.Count;
            }
            catch { }
        }

        overlay["cards"] = GetVisibleHolders(hand)
            .Select((h, i) =>
            {
                var card = h.CardNode!.Model;
                return new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = card.Title.ToString(),
                    ["description"] = TextHelper.GetCardDescription(card)
                };
            })
            .ToList();

        return overlay;
    }


    public string GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine("You need to Select Cards from your hand.");
        var hand = ctx.Hand;
        if (hand == null) return stringBuilder.ToString();
        if (ReflectionCache.HandPrefs != null)
        {
            try
            {
                dynamic prefs = ReflectionCache.HandPrefs.GetValue(hand)!;
                stringBuilder.AppendLine(TextHelper.StripBBCode(
                    ((MegaCrit.Sts2.Core.Localization.LocString)prefs.Prompt).GetFormattedText()));
                stringBuilder.AppendLine(prefs.MinSelect != prefs.MaxSelect ? $"Select {prefs.MinSelect} up to {prefs.MinSelect} cards" : $"Select {prefs.MaxSelect} cards");
            }
            catch { }
        }
        return stringBuilder.ToString();

    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var hand = ctx.Hand;
        if (hand == null) return commands;

        var holders = GetVisibleHolders(hand);
        var min_select = 0;
        var max_select = 1;

        if (ReflectionCache.HandPrefs != null)
        {
            try
            {
                dynamic prefs = ReflectionCache.HandPrefs.GetValue(hand)!;
                min_select = (int)prefs.MinSelect;
                max_select = (int)prefs.MaxSelect;
            }
            catch
            {
                Plugin.LogError("Couldn't Figure out how many Cards needed to be selected, falling back to maximum selection");
                max_select = holders.Count;
            }
        }
        commands.Add(new("select_multiple_cards", min_select != max_select ? $"Select {min_select} up to {max_select} cards" : $"Select {max_select} cards", QJS.WrapObject(new Dictionary<string, JsonSchema>()
        {
            ["cards"] = new()
            {
                Type = JsonSchemaType.Array,
                MinItems = min_select,
                MaxItems = max_select,
                Items = QJS.Enum(holders.Select((x) => x.CardNode!.Model!.Title).Distinct()),
            }
        })));


        if (ReflectionCache.HandConfirmButton != null)
        {
            if (ReflectionCache.HandConfirmButton.GetValue(hand) is NConfirmButton confirmButton && confirmButton.IsEnabled)
                commands.Add(new("confirm_selection", "Confirm your Selection of Cards and Proceed"));
        }

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
            "choose_hand_cards" => await ChooseHandCard(root, ctx),
            "confirm_selection" => await ConfirmSelection(ctx),
            _ => null
        };
    }

    private async Task<ExecutionResult> ChooseHandCard(JsonElement root, ContextInfo ctx)
    {
        var hand = ctx.Hand;
        if (hand == null || !hand.IsInCardSelection)
            return ExecutionResult.Failure("Hand is not in card selection mode");

        var cardIndex = root.GetProperty("cardIndex").GetInt32();
        var holders = GetVisibleHolders(hand);

        if (cardIndex < 0 || cardIndex >= holders.Count)
            return ExecutionResult.Failure($"Card index {cardIndex} out of range (available: {holders.Count})");

        var holder = holders[cardIndex];

        await GodotMainThread.RunAsync(() =>
        {
            Plugin.LogDebug($"ChooseHandCards: emitting Pressed on holder for '{holder.CardNode?.Model?.Title}'");
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        });

        Plugin.Log($"Chose hand card {cardIndex}");
        return ExecutionResult.Success("Hand card selected");
    }

    private async Task<ExecutionResult> ConfirmSelection(ContextInfo ctx)
    {
        var hand = ctx.Hand;
        if (hand == null || !hand.IsInCardSelection)
            return ExecutionResult.Failure("No active selection to confirm");

        if (ReflectionCache.HandConfirmButton == null)
            return ExecutionResult.Failure("Cannot access confirm button");

        var confirmButton = ReflectionCache.HandConfirmButton.GetValue(hand) as NConfirmButton;
        if (confirmButton == null || !confirmButton.IsEnabled)
            return ExecutionResult.Failure("Confirm button is not enabled (need to select more cards?)");

        await GodotMainThread.ClickAsync(confirmButton);
        Plugin.LogDebug("ConfirmSelection: clicked hand select confirm button");
        return ExecutionResult.Success("Selection confirmed");
    }

    private static List<NHandCardHolder> GetVisibleHolders(NPlayerHand hand)
    {
        return hand.CardHolderContainer.GetChildren()
            .OfType<NHandCardHolder>()
            .Where(h => h.Visible && h.CardNode?.Model != null)
            .ToList();
    }

}
