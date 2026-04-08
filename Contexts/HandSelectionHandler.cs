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

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var hand = ctx.Hand;
        if (hand == null) return commands;

        var holders = GetVisibleHolders(hand);
        for (int i = 0; i < holders.Count; i++)
        {
            var card = holders[i].CardNode!.Model;
            // commands.Add(new Dictionary<string, object>
            // {
            //     ["type"] = "choose_hand_cards",
            //     ["cardIndex"] = i,
            //     ["card"] = card.Title.ToString()
            // });
        }

        if (ReflectionCache.HandConfirmButton != null)
        {
            var confirmButton = ReflectionCache.HandConfirmButton.GetValue(hand) as NConfirmButton;
            // if (confirmButton != null && confirmButton.IsEnabled)
            //     commands.Add(new Dictionary<string, object> { ["type"] = "confirm_selection" });
        }

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        return action.Name switch
        {
            "choose_hand_cards" => await ChooseHandCard(root, ctx),
            "confirm_selection" => await ConfirmSelection(ctx),
            _ => null
        };
    }

    private async Task<ActionResult.Result> ChooseHandCard(JsonElement root, ContextInfo ctx)
    {
        var hand = ctx.Hand;
        if (hand == null || !hand.IsInCardSelection)
            return ActionResult.Error("Hand is not in card selection mode");

        var cardIndex = root.GetProperty("cardIndex").GetInt32();
        var holders = GetVisibleHolders(hand);

        if (cardIndex < 0 || cardIndex >= holders.Count)
            return ActionResult.Error($"Card index {cardIndex} out of range (available: {holders.Count})");

        var holder = holders[cardIndex];

        await GodotMainThread.RunAsync(() =>
        {
            Plugin.LogDebug($"ChooseHandCards: emitting Pressed on holder for '{holder.CardNode?.Model?.Title}'");
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        });

        Plugin.Log($"Chose hand card {cardIndex}");
        return ActionResult.Ok("Hand card selected");
    }

    private async Task<ActionResult.Result> ConfirmSelection(ContextInfo ctx)
    {
        var hand = ctx.Hand;
        if (hand == null || !hand.IsInCardSelection)
            return ActionResult.Error("No active selection to confirm");

        if (ReflectionCache.HandConfirmButton == null)
            return ActionResult.Error("Cannot access confirm button");

        var confirmButton = ReflectionCache.HandConfirmButton.GetValue(hand) as NConfirmButton;
        if (confirmButton == null || !confirmButton.IsEnabled)
            return ActionResult.Error("Confirm button is not enabled (need to select more cards?)");

        await GodotMainThread.ClickAsync(confirmButton);
        Plugin.LogDebug("ConfirmSelection: clicked hand select confirm button");
        return ActionResult.Ok("Selection confirmed");
    }

    private static List<NHandCardHolder> GetVisibleHolders(MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand hand)
    {
        return hand.CardHolderContainer.GetChildren()
            .OfType<NHandCardHolder>()
            .Where(h => h.Visible && h.CardNode?.Model != null)
            .ToList();
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

}
