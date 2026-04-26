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

public class CombatHandler : AbstractQueuedHandler<CombatHandler.Result>, IOnContextSwitch
{
    public class Result : IContextResult
    {
        internal Creature? Target;
        internal CardModel? Card;
        internal PotionModel Potion;
    }
    public override ContextType Type => ContextType.Combat;
    bool firstContext = true;

    // Projected resource tracking for queued-but-not-yet-executed actions.
    // Synced to actual state on new rounds or when the action queue drains.
    int _projectedEnergyRemaining = int.MaxValue;
    int _projectedStarsRemaining = int.MaxValue;
    readonly HashSet<PotionModel> _projectedPotionsUsed = new();
    int _lastProjectedRound = -1;
    // Actions invalidated by projected resource tracking that should not be re-registered
    // until the action queue drains and fresh game state is available.
    readonly HashSet<string> _invalidatedActions = new();

    public override ContextReturn GetContext(ContextInfo ctx)
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

    public override CommandReturn GetCommands(ContextInfo ctx)
    {
        // Clear invalidated actions when the action queue has drained and game state is fresh
        if (ActionQueue.Count == 0)
            _invalidatedActions.Clear();

        var commands = new List<ConstructedAction>();
        var cm = CombatManager.Instance;
        if (cm == null || cm.PlayerActionsDisabled) return new CommandReturn();

        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        if (pcs == null || (pcs != null && pcs.Phase != PlayerTurnPhase.Play)) return new CommandReturn();

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
            prompt.AppendLine(GetAvailableCardsActionText(player));
        }


        var hittableEnemies = ctx.CombatState?.HittableEnemies.ToList();
        if (hittableEnemies != null && hittableEnemies.Count > 1)
        {
            prompt.AppendLine();
            prompt.AppendLine("## Enemy targets");
            for (int i = 0; i < hittableEnemies.Count; i++)
            {
                prompt.AppendLine($"- {hittableEnemies[i].GetUniqueName(false, i)}");
            }
        }

        var allyTargets = ctx.CombatState?.Allies
            .Where(ally => ally.IsAlive && ally.IsPlayer && ally != player.Creature)
            .ToList();
        if (allyTargets != null && allyTargets.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("## Ally targets");
            for (int i = 0; i < allyTargets.Count; i++)
            {
                prompt.AppendLine($"- `{i}`: {allyTargets[i].Name}");
            }
        }

        //If there is only 1 Target no need to make it more difficult for llms
        if (!_invalidatedActions.Contains("play_card") && pcs.Hand.Cards.Any(card => card.CanPlay()))
        {
            var schema = QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["card_index"] = QJS.Type(JsonSchemaType.Integer),
                ["enemy_index"] = QJS.Type(JsonSchemaType.Integer),
                ["ally_index"] = QJS.Type(JsonSchemaType.Integer)
            }, false);
            schema.Required = ["card_index"];
            commands.Add(new("play_card", "Play a card, optionally supply enemy_index or ally_index to specify a target otherwise the first target will be chosen", schema));
        }
        if (!_invalidatedActions.Contains("use_potion") && player.Potions.Any(potion => potion != null))
        {
            var schema = QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["potion_index"] = QJS.Type(JsonSchemaType.Integer),
                ["enemy_index"] = QJS.Type(JsonSchemaType.Integer),
                ["ally_index"] = QJS.Type(JsonSchemaType.Integer)
            }, false);
            schema.Required = ["potion_index"];
            commands.Add(new("use_potion", "Use a potion, optionally supply enemy_index or ally_index to specify a target otherwise the first target will be chosen", schema));
        }
        if (!_invalidatedActions.Contains("end_turn"))
            commands.Add(new("end_turn", "Ends your current turn"));

        return new CommandReturn(commands, true, ForceText: prompt.ToString());
    }


    public override ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {

        var cm = CombatManager.Instance;
        if (cm == null) return ExecutionResult.Failure("Not in combat");
        if (cm.IsOverOrEnding) return ExecutionResult.Failure("Combat is ending");
        var pcs = LocalContext.GetMe(ctx.RunState.Players)?.PlayerCombatState;

        if (pcs == null) return ExecutionResult.Failure("Player combat state not found");
        if (pcs.Phase != PlayerTurnPhase.Play || !cm.IsInProgress) return ExecutionResult.ModFailure("Not in play phase");
        var player = LocalContext.GetMe(ctx.RunState.Players);
        if (player == null) return ExecutionResult.ModFailure("Player not found");
        if (action.Name.StartsWith("play_card"))
        {
            var result = ValidateSingleCard(action, data, ref parsedData, ctx);
            if (result.Successful && !IsRevalidation)
                InvalidateFutureActions(action, parsedData, player, ctx);
            return result;
        }
        else if (action.Name.StartsWith("use_potion"))
        {
            var result = ValidatePotion(action, data, ref parsedData, ctx);
            if (result.Successful && !IsRevalidation)
                InvalidateFutureActions(action, parsedData, player, ctx);
            return result;
        }
        else if (action.Name == "end_turn")
        {
            if (cm.IsPlayerReadyToEndTurn(player))
                return ExecutionResult.Failure("Turn already ended");
            if (!IsRevalidation)
                NeuroIntegration.UnregisterAllActions();
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
        if (currentRound != _lastProjectedRound || ActionQueue.Count == 0)
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

        var toUnregister = new HashSet<string>();
        var candidates = pcs.Hand.Cards.Where(card => card.CanPlay());
        if (parsedData.Card != null)
            candidates = candidates.Where(card => card != parsedData.Card);

        if (!candidates.Any(card => CanAffordProjectedCost(card, canPayEnergyWithStars)))
            toUnregister.Add("play_card");

        if (!player.Potions.Any(potion => !_projectedPotionsUsed.Contains(potion)))
            toUnregister.Add("use_potion");

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
                if (data.GetValue("enemy_index", -1) is int targetIndex && targetIndex >= 0)
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
            return ExecutionResult.Failure($"Card at index '{cardIndex}' cannot be played. Please choose a different card from: {GetAvailableCardsActionText(player)}");
        }

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
            if (data.GetValue("enemy_index", -1) is int targetIndex && targetIndex >= 0)
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
    protected override void OnQueuedActionReceived(ConstructedAction action, Result result, ContextInfo ctx)
    {
        firstContext = false;
    }

    protected override Result GetRevalidationResult(ConstructedAction action, Result result, ContextInfo freshCtx) => new();

    protected override string GetQueueRejectedDiscardMessage(ConstructedAction action, Result result, ExecutionResult queueResult)
        => $"- {DescribeAction(action, result)} was cancelled due to {queueResult.Message}";

    protected override string GetContextChangedMessage(ConstructedAction action, Result result)
        => "Context changed, no longer in combat";

    protected override async Task<ExecutionResult?> ExecuteQueuedAction(ConstructedAction action, Result result, ContextInfo ctx)
    {
        if (action.Name.StartsWith("play_card"))
        {
            return await PlayCard(result, ctx);
        }
        if (action.Name.StartsWith("use_potion"))
        {
            return UsePotion(result, ctx);
        }
        if (action.Name == "end_turn")
        {
            return await EndTurn(ctx);
        }
        return null;
    }

    protected override void OnAfterQueuedAction(ConstructedAction action, Result result, ContextInfo freshCtx)
    {
        if (action.Name == "end_turn")
        {
            return;
        }

        var freshContext = GameContext.Resolve();
        if (freshContext == null || freshContext.Type != ContextType.Combat)
        {
            Plugin.LogDebug($"Not sending context after '{action.Name}' because context changed");
            return;
        }

        var context = getContext(freshCtx, afterPlayed: true);
        NeuroIntegration.SendContext(context.Message, context.Silent);

        if (ActionQueue.Count <= 1)
        {
            var commandReturn = GetCommands(freshContext);
            if (!commandReturn.Commands.All(x => x.Name == "end_turn"))
                NeuroIntegration.Reforce(commandReturn.ForceText);
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
        await Task.Delay(500); // Small delay to make it a better viewing experience
        Plugin.Log($"Played card");
        return ExecutionResult.Success("Card played");
    }

    private async Task<ExecutionResult> EndTurn(ContextInfo ctx)
    {
        // Ending the turn invalidates any remaining queued actions
        ActionQueue.Clear();
        NeuroIntegration.UnregisterAllActions();

        var pcs = LocalContext.GetMe(ctx.RunState.Players)?.PlayerCombatState;

        if (pcs == null) return ExecutionResult.Failure("Player combat state not found");
        if (pcs.Phase != PlayerTurnPhase.Play)
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

    private string GetAvailableCardsActionText(Player player)
    {
        var pcs = player.PlayerCombatState;
        if (pcs == null) return "No player combat state";
        if (pcs.Hand.Cards.Count == 0) return "Your hand is empty";
        var text = new StringBuilder();
        text.AppendLine("You can play the following cards:");
        for (int i = 0; i < pcs.Hand.Cards.Count; i++)
        {
            if (pcs.Hand.Cards[i].CanPlay())
                text.AppendLine($"- `{i}`: {TextHelper.StripBBCode(pcs.Hand.Cards[i].Title)}");
        }
        return text.ToString();
    }

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
        ActionQueue.Clear();
        NeuroIntegration.UnregisterAllActions();
        _invalidatedActions.Clear();
        _projectedPotionsUsed.Clear();
        ResetQueuedHandlerState();
        firstContext = true;
    }
}
