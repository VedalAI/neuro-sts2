using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
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
using MegaCrit.Sts2.Core.Localization.DynamicVars;

namespace Sts2Agent.Contexts;

public class HandSelectionHandler : IContextHandler<HandSelectionHandler.Result>
{
    public class Result : IContextResult
    {
        internal NHandCardHolder Card;
        internal NConfirmButton ConfirmButton;
    }
    public ContextType Type => ContextType.HandSelection;

    public ContextReturn GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine("## Select Cards from Your Hand");
        var hand = ctx.Hand;
        if (hand == null) return new ContextReturn(stringBuilder.ToString());
        if (ReflectionCache.HandPrefs != null)
        {
            try
            {
                dynamic prefs = ReflectionCache.HandPrefs.GetValue(hand)!;
                stringBuilder.AppendLine(TextHelper.StripBBCode(
                    ((MegaCrit.Sts2.Core.Localization.LocString)prefs.Prompt).GetFormattedText()));
                stringBuilder.AppendLine(prefs.MinSelect != prefs.MaxSelect ? $"Select {prefs.MinSelect} up to {prefs.MaxSelect} cards." : $"Select {prefs.MaxSelect} card.");
            }
            catch { }
        }

        var holders = GetVisibleHolders(hand);
        if (holders.Count > 0)
        {
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("**Selectable cards:**");
            stringBuilder.RepresentDeck(holders.Select(holder => holder.CardNode!.Model!), PileType.Hand);
        }
        return new ContextReturn(stringBuilder.ToString());

    }

    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var hand = ctx.Hand;
        if (hand == null) return new CommandReturn();

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
        commands.Add(new("choose_hand_cards", min_select != max_select ? $"Select {min_select} up to {max_select} cards" : $"Select {max_select} cards", QJS.WrapObject(new Dictionary<string, JsonSchema>()
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
            {
                commands.Clear();
                commands.Add(new("confirm_selection", "Confirm your selection of cards and proceed"));
            }
        }

        return new CommandReturn(commands);
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        if (action.Name == "choose_hand_cards")
        {
            var hand = ctx.Hand;
            if (hand == null || !hand.IsInCardSelection)
                return ExecutionResult.ModFailure("Hand is not in card selection mode");

            var cardIndex = data.Data?["cards"]?.AsArray().GetValues<string>();
            if (cardIndex == null)
                return ExecutionResult.Failure("Missing parameter: cards");
            var cardname = cardIndex.FirstOrDefault();
            if (cardIndex == null)
                return ExecutionResult.Failure("Missing a card value in the array");
            var holders = GetVisibleHolders(hand);
            if (holders == null)
                return ExecutionResult.ModFailure("Couldn't find the hand to select from");

            var holder = holders.FirstOrDefault(e => e.CardNode?.Model?.Title == cardname);
            if (holder == null)
                return ExecutionResult.Failure($"Couldn't find card with name {cardname}");
            parsedData.Card = holder;
            return ExecutionResult.Success("Hand card selected");
        }
        else
        {
            var hand = ctx.Hand;
            if (hand == null || !hand.IsInCardSelection)
                return ExecutionResult.Failure("No active selection to confirm");

            if (ReflectionCache.HandConfirmButton == null)
                return ExecutionResult.Failure("Cannot access confirm button");

            var confirmButton = ReflectionCache.HandConfirmButton.GetValue(hand) as NConfirmButton;
            if (confirmButton == null || !confirmButton.IsEnabled)
                return ExecutionResult.Failure("Confirm button is not enabled (need to select more cards?)");
            parsedData.ConfirmButton = confirmButton;

        }
        return ExecutionResult.Success();
    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)

    {
        return action.Name switch
        {
            "choose_hand_cards" => await ChooseHandCard(result, ctx),
            "confirm_selection" => await ConfirmSelection(result),
            _ => null
        };
    }

    private async Task<ExecutionResult> ChooseHandCard(Result root, ContextInfo ctx)
    {

        await GodotMainThread.RunAsync(() =>
        {
            Plugin.LogDebug($"ChooseHandCards: emitting Pressed on holder for '{root.Card.CardNode?.Model?.Title}'");
            root.Card.EmitSignal(NCardHolder.SignalName.Pressed, root.Card);
        });
        Plugin.Log($"Chose hand card {root.Card.CardNode?.Model?.Title}");
        return ExecutionResult.Success("Hand card selected");
    }

    private async Task<ExecutionResult> ConfirmSelection(Result result)
    {
        await GodotMainThread.ClickAsync(result.ConfirmButton);
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
