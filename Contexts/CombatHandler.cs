using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;
using Sts2Agent.Utilities;
using STS2NeuroIntegration;
using NeuroSdk.Websocket;
using NeuroSdk.Actions;
using NeuroSdk.Json;
using System.Text;
using MegaCrit.Sts2.Core.Saves;
using System.Collections.Immutable;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Hooks;

namespace Sts2Agent.Contexts;

public class CombatHandler : IContextHandler<CombatHandler.Result>, IOnContextSwitch
{
    public class Result : IContextResult
    {
        internal Creature? Target;
        internal CardModel? Card;
        internal PotionModel Potion;
    }
    public ContextType Type => ContextType.Combat;
    bool firstContext = true;
    readonly ActionQueue actionQueue = new();

    // Projected resource tracking for queued-but-not-yet-executed actions.
    // Synced to actual state on new rounds or when the action queue drains.
    int _projectedEnergyRemaining = int.MaxValue;
    int _projectedStarsRemaining = int.MaxValue;
    readonly HashSet<PotionModel> _projectedPotionsUsed = new();
    int _lastProjectedRound = -1;
    bool _isRevalidation = false;
    // Actions invalidated by projected resource tracking that should not be re-registered
    // until the action queue drains and fresh game state is available.
    readonly HashSet<string> _invalidatedActions = new();

    public ContextReturn GetContext(ContextInfo ctx)
    {
        if (!firstContext)
        {
            // After the first Context, new context is send by cards played
            return new();
        }
        var context = getContext(ctx);
        return new ContextReturn(context.Message);

    }
    private ContextReturn getContext(ContextInfo ctx, bool afterPlayed = false)
    {

        StringBuilder stringBuilder = new();
        var combatState = ctx.CombatState;
        if (!afterPlayed)
            stringBuilder.AppendLine("## You are in combat");
        if (combatState == null)
        {
            Plugin.LogError("Combat state is null in combat context, this should never happen");
            return new ContextReturn("");// Return empty context if combat state is null, this should never happen but just in case
        }
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        if (!afterPlayed)
            stringBuilder.AppendLine($"### It is currently round {combatState.RoundNumber}, and it's your turn");
        if (pcs != null)
        {
            stringBuilder.AppendLine($"# You currently have {pcs.Energy} Energy and {pcs.Stars} Stars to use");
            stringBuilder.AppendLine($"## You have {pcs.DrawPile.Cards.Count} cards in the draw pile, {pcs.DiscardPile.Cards.Count} cards in the discard pile, and {pcs.ExhaustPile.Cards.Count} cards in the exhaust pile");
            if (player.Relics.Count > 0 && !afterPlayed)
            {
                stringBuilder.AppendLine($"You have {player.Relics.Count} relics. They are:");
                stringBuilder.RepresentRelics(player.Relics);
            }

            if (pcs.OrbQueue != null && pcs.OrbQueue.Capacity > 0)
            {
                stringBuilder.AppendLine($"You have {pcs.OrbQueue.Capacity} Orb slots");
                if (pcs.OrbQueue.Orbs.Count > 0)
                {
                    stringBuilder.AppendLine("You currently have these orbs in order of use:");
                    foreach (var orb in pcs.OrbQueue.Orbs)
                    {
                        stringBuilder.AppendLine($"- {TextHelper.SafeLocString(() => orb.Title)} deals {orb.PassiveVal} passive damage and {orb.EvokeVal} damage when evoked");
                    }
                }
                else
                {
                    stringBuilder.AppendLine("Currently there are no orbs in the slots");
                }
            }
        }
        stringBuilder.AppendLine($"# You currently have {player.Creature.CurrentHp} HP out of {player.Creature.MaxHp} max HP and {player.Creature.Block} Block");
        if (player.Creature.Powers.Count > 0)
        {
            stringBuilder.AppendLine($"## You have {player.Creature.Powers.Count} powers applied to yourself. They are:");
            foreach (var power in player.Creature.Powers)
            {
                stringBuilder.AppendLine($"\t- A {TextHelper.SafeLocString(() => power.Title)} on you with {power.Amount} which does: \"{TextHelper.SafeLocString(() => power.Description)}\"");
            }
        }

        stringBuilder.AppendLine("");
        stringBuilder.AppendLine($"There are {combatState.Enemies.Count} enemies:");
        //Do a safety check if round is finished due to killing an enemy
        if (combatState.Enemies.Count <= 0)
        {
            var newStringBuilder = new StringBuilder();
            var afterEndEvents = EventLog.DrainAll();
            if (afterEndEvents.Count > 0)
            {
                newStringBuilder.AppendLine($"After killing the last enemy, this happened:");
                newStringBuilder.RepresentEvents(afterEndEvents);
            }
            return new ContextReturn(newStringBuilder.ToString(), true);
        }
        PrettyRenderEnemies(stringBuilder, combatState.Enemies, combatState);
        var allies = combatState.Allies.Where(c => c.IsAlive && c != player.Creature);
        if (allies.Any())
        {
            stringBuilder.AppendLine($"You have {allies.Count()} allies:");
            foreach (var ally in allies)
            {
                stringBuilder.Append($"- {ally.Name} has {ally.CurrentHp} HP out of {ally.MaxHp} max HP");
                if (ally.Block > 0)
                {
                    stringBuilder.Append($", and they have {ally.Block} Block");
                }
                if (ally.Powers.Count > 0)
                {
                    stringBuilder.AppendLine($", and they have {ally.Powers.Count} powers:");
                    foreach (var power in ally.Powers)
                    {
                        stringBuilder.AppendLine($"\t- A {TextHelper.SafeLocString(() => power.Title)} with {power.Amount} which does: {TextHelper.SafeLocString(() => power.Description)}");
                    }
                }
                stringBuilder.AppendLine();

            }
        }
        stringBuilder.AppendLine();
        stringBuilder.AppendLine($"You currently have {pcs?.Hand.Cards.Count} cards in hand");
        stringBuilder.RepresentDeck(pcs.Hand.Cards, PileType.Hand);
        var events = EventLog.DrainAll();
        if (events.Count > 0)
        {

            stringBuilder.AppendLine();
            if (firstContext)
            {
                if (combatState.RoundNumber > 1)
                {
                    stringBuilder.AppendLine("After your last turn, this happened:");
                }
                else
                {
                    stringBuilder.AppendLine("This happened at the start of combat:");
                }
            }
            else
            {
                stringBuilder.AppendLine("This happened after you played a card:");
            }
            stringBuilder.RepresentEvents(events);
        }
        if (pcs.Hand.Cards.Count > 0 && pcs.Hand.Cards.Any(card => card.CanPlay()))
        {
            stringBuilder.AppendLine("");
            stringBuilder.AppendLine("## Cards in hand");
            stringBuilder.AppendLine("Use these indices when choosing a `play_card` action.");
            for (int i = 0; i < pcs.Hand.Cards.Count; i++)
            {
                var card = pcs.Hand.Cards[i];
                if (card.CanPlay())
                    stringBuilder.AppendLine($"- `{i}`: {TextHelper.StripBBCode(card.Title)}");
            }
        }

        return new ContextReturn(stringBuilder.ToString().TrimEnd(), true);
    }

    public CommandReturn GetCommands(ContextInfo ctx)
    {
        // Clear invalidated actions when the action queue has drained and game state is fresh
        if (actionQueue.Count == 0)
            _invalidatedActions.Clear();

        var commands = new List<ConstructedAction>();
        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsPlayPhase || cm.PlayerActionsDisabled) return new CommandReturn();

        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        if (pcs == null) return new CommandReturn();

        var prompt = new StringBuilder();
        prompt.AppendLine("# It's your turn");
        prompt.AppendLine();
        prompt.AppendLine("Choose an action to perform.");
        prompt.AppendLine();
        prompt.AppendLine("## Cards in hand");
        prompt.AppendLine("Use these indices when choosing a `play_card` action.");
        if (pcs.Hand.Cards.Count == 0)
        {
            prompt.AppendLine("- Your hand is empty.");
        }
        else
        {
            for (int i = 0; i < pcs.Hand.Cards.Count; i++)
            {
                var card = pcs.Hand.Cards[i];
                if (card.CanPlay())
                    prompt.AppendLine($"- `{i}`: {TextHelper.StripBBCode(card.Title)}");
            }
        }

        var enemyTargetCardsAvailable = GetAvailableCardsForAction("play_card_on_enemy", player, pcs, ctx).Any();
        var genericCardsAvailable = GetAvailableCardsForAction("play_card", player, pcs, ctx).Any();
        var enemyTargetPotionsAvailable = GetAvailablePotionsForAction("use_potion_on_enemy", player, ctx).Any();
        var genericPotionsAvailable = GetAvailablePotionsForAction("use_potion", player, ctx).Any();
        var allyTargetCardsAvailable = GetAvailableCardsForAction("play_card_on_ally", player, pcs, ctx).Any();

        var hittableEnemies = ctx.CombatState?.HittableEnemies.ToList();
        if ((enemyTargetCardsAvailable || enemyTargetPotionsAvailable) && hittableEnemies != null && hittableEnemies.Count > 1)
        {
            prompt.AppendLine();
            prompt.AppendLine("## Enemy targets");
            for (int i = 0; i < hittableEnemies.Count; i++)
            {
                prompt.AppendLine($"- `{i}`: {hittableEnemies[i].GetUniqueName(false, i)}");
            }
        }

        var allyTargets = ctx.CombatState?.Allies
            .Where(ally => ally.IsAlive && ally.IsPlayer && ally != player.Creature)
            .ToList();
        if (allyTargetCardsAvailable && allyTargets != null && allyTargets.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("## Ally targets");
            for (int i = 0; i < allyTargets.Count; i++)
            {
                prompt.AppendLine($"- `{i}`: {allyTargets[i].Name}");
            }
        }

        //If there is only 1 Target no need to make it more difficult for llms
        if (enemyTargetCardsAvailable && !_invalidatedActions.Contains("play_card_on_enemy"))
        {
            commands.Add(new("play_card_on_enemy", "Play a card on the target enemy", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["card_index"] = QJS.Type(JsonSchemaType.Integer),
                ["target_index"] = QJS.Type(JsonSchemaType.Integer)
            }), persistant_action: true));
        }
        if (genericCardsAvailable && !_invalidatedActions.Contains("play_card"))
        {
            commands.Add(new("play_card", "Play a card", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["card_index"] = QJS.Type(JsonSchemaType.Integer)
            }), persistant_action: true));
        }
        if (enemyTargetPotionsAvailable && !_invalidatedActions.Contains("use_potion_on_enemy"))
            commands.Add(new("use_potion_on_enemy", "Use a potion on a target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["potion_index"] = QJS.Type(JsonSchemaType.Integer),
                ["target_index"] = QJS.Type(JsonSchemaType.Integer)
            }), persistant_action: true));

        if (genericPotionsAvailable && !_invalidatedActions.Contains("use_potion"))
            commands.Add(new("use_potion", "Use a potion", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["potion_index"] = QJS.Type(JsonSchemaType.Integer),
            }), persistant_action: true));

        //TODO: this might require changes for multiplayer as. there are TargetType.AnyPlayer too
        if (allyTargetCardsAvailable && !_invalidatedActions.Contains("play_card_on_ally"))
        {

            commands.Add(new("play_card_on_ally", "Select a card that requires an allied target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["card_index"] = QJS.Type(JsonSchemaType.Integer),
                ["ally_index"] = QJS.Type(JsonSchemaType.Integer)
            }), persistant_action: true));
        }
        if (!_invalidatedActions.Contains("end_turn"))
            commands.Add(new("end_turn", "Ends your current turn", persistant_action: true));

        return new CommandReturn(commands, ForceWindow: false, ForceText: prompt.ToString());
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ExecutionResult.Failure("Not in combat");
        if (cm.IsOverOrEnding) return ExecutionResult.Failure("Combat is ending");
        if (!cm.IsPlayPhase || !cm.IsInProgress) return ExecutionResult.ModFailure("Not in play phase");
        var player = LocalContext.GetMe(ctx.RunState.Players);
        if (player == null) return ExecutionResult.ModFailure("Player not found");
        if (action.Name.StartsWith("play_card"))
        {
            var result = ValidateSingleCard(action, data, ref parsedData, ctx);
            if (result.Successful && !_isRevalidation)
                InvalidateFutureActions(action, parsedData, player, ctx);
            return result;
        }
        else if (action.Name.StartsWith("use_potion"))
        {
            var result = ValidatePotion(action, data, ref parsedData, ctx);
            if (result.Successful && !_isRevalidation)
                InvalidateFutureActions(action, parsedData, player, ctx);
            return result;
        }
        else if (action.Name == "end_turn")
        {
            if (cm.IsPlayerReadyToEndTurn(player))
                return ExecutionResult.Failure("Turn already ended");
            if (!_isRevalidation)
                InvalidateAllCombatActions();
            return ExecutionResult.Success();
        }
        return ExecutionResult.Unstable("Unknown action");
    }

    /// <summary>
    /// After validating the current action, update projected resource state and unregister
    /// any persistent actions that would become unplayable.
    /// </summary>
    private void InvalidateFutureActions(ConstructedAction currentAction, Result parsedData, Player player, ContextInfo ctx)
    {
        var instance = NeuroIntegration.Instance;
        if (instance == null) return;

        var pcs = player.PlayerCombatState;
        if (pcs == null) return;

        var globalActionNames = instance.GlobalActions
            .Select(action => action.Name)
            .ToHashSet();

        var combatState = ctx.CombatState;
        int currentRound = combatState?.RoundNumber ?? -1;
        bool canPayEnergyWithStars = combatState != null && Hook.ShouldPayExcessEnergyCostWithStars(combatState, player);

        // Reset projected state on new round or when the action queue has fully drained
        if (currentRound != _lastProjectedRound || actionQueue.Count == 0)
        {
            _projectedEnergyRemaining = pcs.Energy;
            _projectedStarsRemaining = pcs.Stars;
            _projectedPotionsUsed.Clear();
            _invalidatedActions.Clear();
            _lastProjectedRound = currentRound;
        }
        else
        {
            // Within the same round, sync down to actual state to avoid over-counting
            // after previously queued actions have executed and reduced real resources
            _projectedEnergyRemaining = Math.Min(_projectedEnergyRemaining, pcs.Energy);
            _projectedStarsRemaining = Math.Min(_projectedStarsRemaining, pcs.Stars);
        }

        // Subtract the current action's resource cost from projected state
        if (parsedData.Card != null)
        {
            var card = parsedData.Card;
            if (card.EnergyCost.CostsX)
                _projectedEnergyRemaining = 0;
            else
            {
                int energyCost = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
                int starCost = Math.Max(0, card.GetStarCostWithModifiers());

                if (energyCost > _projectedEnergyRemaining && canPayEnergyWithStars)
                {
                    starCost += (energyCost - _projectedEnergyRemaining) * 2;
                    energyCost = _projectedEnergyRemaining;
                }

                _projectedEnergyRemaining = Math.Max(0, _projectedEnergyRemaining - energyCost);
                _projectedStarsRemaining = Math.Max(0, _projectedStarsRemaining - starCost);
            }

            if (card.HasStarCostX)
                _projectedStarsRemaining = 0;
        }

        if (parsedData.Potion != null)
            _projectedPotionsUsed.Add(parsedData.Potion);

        var candidateActionNames = GetCurrentActionNames(player, pcs, ctx);
        var toUnregister = new HashSet<string>();
        foreach (var actionName in candidateActionNames)
        {
            if (actionName.StartsWith("play_card"))
            {
                var candidates = GetAvailableCardsForAction(actionName, player, pcs, ctx);
                if (parsedData.Card != null)
                    candidates = candidates.Where(card => card != parsedData.Card);

                if (!candidates.Any(card => CanAffordProjectedCost(card, canPayEnergyWithStars)))
                    toUnregister.Add(actionName);
            }
            else if (actionName.StartsWith("use_potion"))
            {
                if (!GetAvailablePotionsForAction(actionName, player, ctx).Any(potion => !_projectedPotionsUsed.Contains(potion)))
                    toUnregister.Add(actionName);
            }
        }

        var actionsRemaining = candidateActionNames.Except(toUnregister);
        if (candidateActionNames.Contains("end_turn") && actionsRemaining.All(x => x == "end_turn"))
        {
            toUnregister.Add("end_turn");
        }

        foreach (var name in toUnregister)
            _invalidatedActions.Add(name);

        var globalActionsToUnregister = toUnregister
            .Where(globalActionNames.Contains)
            .ToArray();
        if (globalActionsToUnregister.Length > 0)
            NeuroIntegration.UnregisterActions(globalActionsToUnregister);

    }
    public ExecutionResult ValidatePotion(ConstructedAction action, ActionJData data, ref Result parsedData, ContextInfo ctx)
    {
        var slot = data.GetValue("potion_index", -1);
        if (slot < 0)
            return ExecutionResult.Failure("No potion specified");
        var player = LocalContext.GetMe(ctx.RunState!.Players);
        if (player == null)
            return ExecutionResult.ModFailure("Couldn't find player");
        var potions = player.PotionSlots;
        var potion = potions.ElementAtOrDefault(slot);
        if (potion == null)
            return ExecutionResult.Failure($"No potion at index '{slot}'");
        if (!GetAvailablePotionsForAction(action.Name, player, ctx).Contains(potion))
            return ExecutionResult.Failure($"Potion at index '{slot}' is not valid for action '{action.Name}'");

        Plugin.LogDebug("Setting potion");
        parsedData.Potion = potion;

        // Resolve target based on potion's target type
        Creature? target = null;
        var targetType = potion.TargetType;
        if (targetType == TargetType.AnyEnemy || targetType == TargetType.TargetedNoCreature)
        {
            var combatState = ctx.CombatState;
            if (combatState != null)
            {
                var aliveEnemies = combatState.HittableEnemies.ToList();
                if (data.GetValue("target_index", -1) is int targetIndex && targetIndex >= 0)
                {
                    target = aliveEnemies.ElementAtOrDefault(targetIndex);
                }
                else
                {
                    target = aliveEnemies.FirstOrDefault();
                }
                if (target == null)
                    return ExecutionResult.Unstable("No valid target for potion");
            }
        }
        else
        {
            // Self-targeting potions: game UI passes Owner.Creature
            Plugin.LogDebug("setting player as target");
            target = player.Creature;
        }
        Plugin.LogDebug("Setting target");
        parsedData.Target = target;
        return ExecutionResult.Success();
    }
    public ExecutionResult ValidateSingleCard(ConstructedAction action, ActionJData data, ref Result parsedData, ContextInfo ctx)
    {

        var cardIndex = data.GetValue("card_index", -1);
        if (cardIndex < 0)
            return ExecutionResult.Failure("No card index specified");
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player?.PlayerCombatState;
        if (pcs == null) return ExecutionResult.Failure("No player combat state");

        var hand = pcs.Hand.Cards;
        if (hand == null || hand.Count <= 0)
            return ExecutionResult.Failure("The hand is not valid");
        if (cardIndex >= hand.Count)
            return ExecutionResult.Failure($"Card index {cardIndex} out of range for hand size {hand.Count}");
        var card = hand[cardIndex];
        if (card == null || !card.CanPlay())
        {
            return ExecutionResult.Failure($"Card at index '{cardIndex}' cannot be played");
        }
        if (!GetAvailableCardsForAction(action.Name, player!, pcs, ctx).Contains(card))
            return ExecutionResult.Failure($"Card at index '{cardIndex}' is not valid for action '{action.Name}' because cards targettype is {card.TargetType}");

        var combatState = card?.CombatState;
        if (combatState == null)
        {
            return ExecutionResult.Failure("The card's combat state is null");
        }

        parsedData.Card = card;

        var aliveEnemies = combatState.HittableEnemies.ToList();

        Creature? target = null;
        if (card!.TargetType == TargetType.AnyEnemy)
        {
            if (data.GetValue("target_index", -1) is int targetIndex && targetIndex >= 0)
            {
                Plugin.LogDebug($"target: {targetIndex}");
                if (targetIndex >= aliveEnemies.Count)
                    return ExecutionResult.Failure($"Target index {targetIndex} out of range for alive enemies count {aliveEnemies.Count}");
                target = aliveEnemies[targetIndex];
                var unique = aliveEnemies.CreaturesAreDistinct();
                Plugin.LogDebug($"found target: {target}");
            }
            else
            {
                target = aliveEnemies.FirstOrDefault();
            }
            if (target == null)
                return ExecutionResult.Unstable("No valid target available, try again");
        }
        else if (card.TargetType == TargetType.AnyAlly)
        {
            var allies = combatState.Allies.Where(c => c != null && c.IsAlive && c.IsPlayer && c != card.Owner.Creature);
            target = data.GetValue("ally_index", -1) is int tp && tp >= 0
                ? allies.ElementAtOrDefault(tp)
                : allies.FirstOrDefault();
        }
        parsedData.Target = target;

        return ExecutionResult.Success();

    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
    {
        firstContext = false;

        // Wait for our turn in the queue
        var queueResult = await actionQueue.GetExecution();
        if (!queueResult.Successful)
        {
            Plugin.LogDebug($"ActionQueue: action '{action.Name}' was rejected or cancelled: {queueResult.Message}");
            actionQueue.AddToDiscardedActionText($"- {action.Name} was cancelled due to {queueResult.Message}");
            if (actionQueue.Count == 0)
                actionQueue.SendDiscardedActionText();
            return queueResult;
        }

        // Revalidate with fresh game state — things may have changed while waiting
        var freshCtx = GameContext.Resolve();
        if (freshCtx == null || freshCtx.Type != ContextType.Combat)
        {
            Plugin.LogDebug($"ActionQueue: context changed while waiting for '{action.Name}'");
            actionQueue.ActionFinished();
            return ExecutionResult.Failure("Context changed, no longer in combat");
        }

        var freshResult = new Result();
        _isRevalidation = true;
        var revalidation = Validate(action, action.Data, freshResult, freshCtx);
        _isRevalidation = false;
        if (!revalidation.Successful)
        {
            Plugin.LogDebug($"ActionQueue: action '{action.Name}' failed revalidation: {revalidation.Message}");
            actionQueue.ActionFinished();
            actionQueue.AddToDiscardedActionText($"- {action.Name} was cancelled due to {revalidation.Message}");
            if (actionQueue.Count == 0)
                actionQueue.SendDiscardedActionText();
            return revalidation;
        }

        actionQueue.MarkExecuting();

        try
        {
            ExecutionResult? execResult;
            if (action.Name.StartsWith("play_card"))
            {
                execResult = await PlayCard(freshResult, freshCtx);
            }
            else if (action.Name.StartsWith("use_potion"))
            {
                execResult = UsePotion(freshResult, freshCtx);
            }
            else if (action.Name == "end_turn")
            {
                execResult = await EndTurn(freshCtx);
            }
            else
            {
                execResult = null;
            }
            return execResult;
        }
        finally
        {
            actionQueue.ActionFinished();
            if (action.Name != "end_turn") // Don't send a new context after ending the turn, we'll get a new one when the next combat round starts
            {
                var freshContext = GameContext.Resolve();
                if (freshContext == null || freshContext.Type != ContextType.Combat)
                {
                    Plugin.LogDebug($"Not sending context after '{action.Name}' because context changed");
                }
                else
                {
                    var context = getContext(freshCtx, afterPlayed: true);
                    NeuroIntegration.SendContext(context.Message, context.Silent);
                }
            }
        }
    }

    private async Task<ExecutionResult> PlayCard(Result root, ContextInfo ctx)
    {
        if (root.Card == null)
        {
            return ExecutionResult.ModFailure("Couldn't find the card even though it passed validation");
        }
        try
        {
            var played = root.Card.TryManualPlay(root.Target);
            if (!played)
                return ExecutionResult.Failure($"Card play was rejected by the game");

        }
        catch (Exception e)
        {
            Plugin.LogDebug(e.Message);
            return ExecutionResult.Failure("Playing the card threw an exception");
        }
        await Task.Delay(1000); // Small delay to make it a better viewing experience
        Plugin.Log($"Played card");
        return ExecutionResult.Success("Card played");
    }

    private async Task<ExecutionResult> EndTurn(ContextInfo ctx)
    {
        // Ending the turn invalidates any remaining queued actions
        actionQueue.Clear();
        NeuroIntegration.UnregisterAllActions();

        var cm = CombatManager.Instance;
        if (!cm.IsPlayPhase)
            return ExecutionResult.Failure("Not in play phase");
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var roundNumber = player.Creature.CombatState.RoundNumber;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
            new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(player, roundNumber));

        Plugin.Log("Ended turn");
        firstContext = true;
        return ExecutionResult.Success("Turn ended");
    }

    private ExecutionResult UsePotion(Result root, ContextInfo ctx)
    {

        Callable.From(() => root.Potion.EnqueueManualUse(root.Target)).CallDeferred();
        Plugin.Log($"Used potion");
        return ExecutionResult.Success("Potion used");
    }

    private void InvalidateAllCombatActions()
    {
        var actionNames = new[]
        {
            "play_card",
            "play_card_on_enemy",
            "play_card_on_ally",
            "use_potion",
            "use_potion_on_enemy",
            "end_turn"
        };

        foreach (var actionName in actionNames)
            _invalidatedActions.Add(actionName);

        NeuroIntegration.UnregisterActions(actionNames);
    }

    private HashSet<string> GetCurrentActionNames(Player player, PlayerCombatState pcs, ContextInfo ctx)
    {
        var actionNames = new HashSet<string>();

        if (GetAvailableCardsForAction("play_card_on_enemy", player, pcs, ctx).Any())
            actionNames.Add("play_card_on_enemy");
        if (GetAvailableCardsForAction("play_card", player, pcs, ctx).Any())
            actionNames.Add("play_card");
        if (GetAvailableCardsForAction("play_card_on_ally", player, pcs, ctx).Any())
            actionNames.Add("play_card_on_ally");
        if (GetAvailablePotionsForAction("use_potion_on_enemy", player, ctx).Any())
            actionNames.Add("use_potion_on_enemy");
        if (GetAvailablePotionsForAction("use_potion", player, ctx).Any())
            actionNames.Add("use_potion");

        actionNames.Add("end_turn");
        return actionNames;
    }

    private IEnumerable<CardModel> GetAvailableCardsForAction(string actionName, Player player, PlayerCombatState pcs, ContextInfo ctx)
    {
        var handCards = pcs.Hand.Cards.Where(card => card.CanPlay());
        bool hasMultipleEnemyTargets = ctx.CombatState?.HittableEnemies.Count > 1;
        bool hasAllies = ctx.CombatState?.Allies.Any(ally => ally.IsAlive && ally != player.Creature) == true;

        return actionName switch
        {
            "play_card_on_enemy" when hasMultipleEnemyTargets => handCards.Where(card => card.TargetType is not (TargetType.AnyAlly or TargetType.AllAllies)),
            "play_card_on_ally" when hasAllies => handCards.Where(card => card.TargetType is not (TargetType.AnyEnemy or TargetType.AllEnemies)),
            "play_card" when hasMultipleEnemyTargets => handCards.Where(card => card.TargetType is not (TargetType.AnyEnemy or TargetType.AnyAlly)),
            "play_card" => handCards,
            _ => Enumerable.Empty<CardModel>()
        };
    }

    private IEnumerable<PotionModel> GetAvailablePotionsForAction(string actionName, Player player, ContextInfo ctx)
    {
        bool hasMultipleEnemyTargets = ctx.CombatState?.HittableEnemies.Count > 1;
        var potions = player.Potions.AsEnumerable();

        return actionName switch
        {
            "use_potion_on_enemy" when hasMultipleEnemyTargets => potions.Where(IsEnemyTargetPotion),
            "use_potion" when hasMultipleEnemyTargets => potions.Where(potion => !IsEnemyTargetPotion(potion)),
            "use_potion" => potions,
            _ => Enumerable.Empty<PotionModel>()
        };
    }

    private bool CanAffordProjectedCost(CardModel card, bool canPayEnergyWithStars)
    {
        int energyCost = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
        int starCost = card.HasStarCostX ? 0 : Math.Max(0, card.GetStarCostWithModifiers());

        if (energyCost > _projectedEnergyRemaining && canPayEnergyWithStars)
        {
            starCost += (energyCost - _projectedEnergyRemaining) * 2;
            energyCost = _projectedEnergyRemaining;
        }

        return energyCost <= _projectedEnergyRemaining && starCost <= _projectedStarsRemaining;
    }

    private static bool IsEnemyTargetPotion(PotionModel potion)
        => potion.TargetType is TargetType.AnyEnemy or TargetType.TargetedNoCreature;

    private static void PrettyRenderEnemies(StringBuilder stringBuilder, IReadOnlyList<Creature> enemies, CombatState combatState)
    {

        var enemies_are_distinct = enemies.CreaturesAreDistinct();
        for (int i = 0; i < enemies.Count; i++)
        {
            Creature? enemy = enemies[i];
            var enemy_name = enemy.GetUniqueName(enemies_are_distinct, i);

            stringBuilder.Append($"\t- {enemy_name} has {enemy.CurrentHp} HP out of {enemy.MaxHp} max HP");
            stringBuilder.Append($", and it has {enemy.Block} Block");
            if (enemy.Powers.Count > 0)
            {
                stringBuilder.AppendLine($", and it has {enemy.Powers.Count} powers. They are:");
                foreach (var power in enemy.Powers)
                {
                    stringBuilder.AppendLine($"\t\t- A {TextHelper.SafeLocString(() => power.Title)} with {power.Amount} which does: {TextHelper.SafeLocString(() => power.Description)}");
                }
            }
            else
            {
                stringBuilder.AppendLine($".");
            }
            stringBuilder.Append("\t\t");

            if (enemy?.Monster is MonsterModel monster)
            {
                var intents = monster.NextMove.Intents;
                if (intents != null && intents.Count > 0)
                {
                    stringBuilder.AppendLine($"The {enemy_name} is intending to:");
                    foreach (var intent in intents)
                    {
                        stringBuilder.Append("\t\t\t- ");
                        var allies = combatState.Creatures.Where(c => c.Player != null);
                        var hoverTip = intent.GetHoverTip(allies, enemy);
                        stringBuilder.AppendLine($"{(hoverTip.Title != null ? ("[" + TextHelper.StripBBCode(hoverTip.Title) + "] ") : "")}{TextHelper.StripBBCode(hoverTip.Description)}");
                    }
                }
            }
        }
    }

    public void OnContextSwitch(ContextType newContext)
    {
        actionQueue.Clear();
        NeuroIntegration.UnregisterAllActions();
    }
}
