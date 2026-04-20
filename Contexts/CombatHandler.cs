#define ALTERNATIVE_ACTIONS

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
    readonly List<string> _projectedPotionsUsed = new();
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

        //If there is only 1 Target no need to make it more difficult for llms
        if (ctx.CombatState!.HittableEnemies.Count > 1)
        {
#if !ALTERNATIVE_ACTIONS
            var enemy_target_cards = pcs.Hand.Cards.Where((x) => x.TargetType == TargetType.AnyEnemy && x.CanPlay()).DistinctBy(x => x.Title);
            var none_target_cards = pcs.Hand.Cards.Where((x) => x.TargetType is not (TargetType.AnyAlly or TargetType.AnyEnemy) && x.CanPlay()).DistinctBy(x => x.Title);

            if (enemy_target_cards.Any())
            {
                commands.Add(new("play_enemy_target_card", "Select a card that requires a enemy Target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                {
                    ["card"] = QJS.Enum(enemy_target_cards.Select(x => x.Title)),
                    ["target"] = QJS.Enum(ctx.CombatState.HittableEnemies.GetUniqueNames())
                })));
            }
            if (none_target_cards.Any())
            {
                commands.Add(new("play_card", "Select a card to play", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                {
                    ["card"] = QJS.Enum(none_target_cards.Select(x => x.Title))
                })));
            }
            if (player.Potions.Any())
            {
                var targeted_potions = player.Potions.Where((p) => p.TargetType is (TargetType.AnyEnemy or TargetType.TargetedNoCreature));
                if (targeted_potions.Any())
                    commands.Add(new("use_target_potion", "Use a potion on a target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                    {
                        ["potion"] = QJS.Enum(targeted_potions.Select((x) => TextHelper.SafeLocString(() => x.Title)).Distinct()),
                        ["target"] = QJS.Enum(ctx.CombatState!.HittableEnemies.GetUniqueNames())
                    }
                    )));
                var none_targeted_potions = player.Potions.Where((p) => p.TargetType is not (TargetType.AnyEnemy or TargetType.TargetedNoCreature));

                if (none_targeted_potions.Any())
                    commands.Add(new("use_potion", "Use a potion", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                    {
                        ["potion"] = QJS.Enum(none_targeted_potions.Select((x) => TextHelper.SafeLocString(() => x.Title)).Distinct()),
                    }
                    )));
            }
#else
            foreach (var card in pcs.Hand.Cards.Where(x => x.CanPlay()).DistinctBy(x => x.Title))
            {
                var actionName = $"play_card_{TextHelper.GetActionNameFor(card.Title)}";
                if (_invalidatedActions.Contains(actionName))
                    continue;

                var action = new ConstructedAction(actionName, $"{TextHelper.GetCardDescriptionFor(card, PileType.Hand).AsSingleLine()}", persistant_action: true);

                if (card.TargetType == TargetType.AnyEnemy)
                {
                    action.SetSchema(QJS.WrapObject(new Dictionary<string, JsonSchema>()
                    {
                        ["target"] = QJS.Enum(ctx.CombatState!.HittableEnemies.GetUniqueNames())
                    }));
                }
                else if (card.TargetType == TargetType.AnyAlly)
                {
                    action.SetSchema(QJS.WrapObject(new Dictionary<string, JsonSchema>()
                    {
                        ["target"] = QJS.Enum(ctx.CombatState.Allies.GetUniqueNames())
                    }));
                }

                commands.Add(action);
            }
            foreach (var potion in player.Potions)
            {
                var actionName = $"use_potion_{potion.Title.GetActionName()}";
                if (_invalidatedActions.Contains(actionName))
                    continue;

                var action = new ConstructedAction(actionName, $"{potion.DynamicDescription.AsSingleLine()}", persistant_action: true);

                if (potion.TargetType == TargetType.AnyEnemy)
                {
                    action.SetSchema(QJS.WrapObject(new Dictionary<string, JsonSchema>()
                    {
                        ["target"] = QJS.Enum(ctx.CombatState!.HittableEnemies.GetUniqueNames())
                    }));
                }
                else if (potion.TargetType == TargetType.AnyAlly)
                {
                    action.SetSchema(QJS.WrapObject(new Dictionary<string, JsonSchema>()
                    {
                        ["target"] = QJS.Enum(ctx.CombatState.Allies.GetUniqueNames())
                    }));
                }
                commands.Add(action);
            }
#endif

        }
        else
        {
#if !ALTERNATIVE_ACTIONS
            if (pcs.Hand.Cards.Any(x => x.CanPlay()))
                commands.Add(new("play_card", "Select a card to play", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                {
                    ["card"] = QJS.Enum(pcs.Hand.Cards.Where(x => x.CanPlay()).Select(x => x.Title).Distinct())
                }
                )));
            if (player.Potions.Any())
            {

                commands.Add(new("use_potion", "Use a potion", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                {
                    ["potion"] = QJS.Enum(player.Potions.Select((x) => TextHelper.SafeLocString(() => x.Title)).Distinct()),
                }
                )));
            }
#else
            foreach (var card in pcs.Hand.Cards.Where((x) => x.CanPlay()).DistinctBy(x => x.Title))
            {
                var actionName = $"play_card_{TextHelper.GetActionNameFor(card.Title)}";
                if (_invalidatedActions.Contains(actionName))
                    continue;
                commands.Add(new(actionName, $"{TextHelper.GetCardDescriptionFor(card, PileType.Hand).AsSingleLine()}", persistant_action: true));
            }
            foreach (var potion in player.Potions)
            {
                var actionName = $"use_potion_{potion.Title.GetActionName()}";
                if (_invalidatedActions.Contains(actionName))
                    continue;
                commands.Add(new(actionName, $"{potion.DynamicDescription.AsSingleLine()}", persistant_action: true));

            }

#endif
        }
#if !ALTERNATIVE_ACTIONS
        //TODO: this might require changes for multiplayer as. there are TargetType.AnyPlayer too
        var ally_target_cards = pcs.Hand.Cards.Where((x) => x.TargetType == TargetType.AnyAlly && x.CanPlay()).Select(x => x.Title).Distinct();
        if (ally_target_cards.Any() && ctx.CombatState?.Allies.Count > 0)
        {

            commands.Add(new("play_ally_target_card", "Select a card that requires an allied target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["card"] = QJS.Enum(ally_target_cards),
                ["target"] = QJS.Enum(ctx.CombatState.Allies.GetUniqueNames())
            })));
        }
#endif

        if (!_invalidatedActions.Contains("end_turn"))
            commands.Add(new("end_turn", "Ends your current turn", persistant_action: true));

        // if (firstContext)
        // {
        //     var context = getContext(ctx);
        //     NeuroIntegration.SendContext(context.Message, false);
        // }

        return new CommandReturn(commands, ForceWindow: false);
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ExecutionResult.Failure("Not in combat");
        if (cm.IsOverOrEnding) return ExecutionResult.Failure("Combat is ending");
        if (!cm.IsPlayPhase || !cm.IsInProgress) return ExecutionResult.ModFailure("Not in play phase");
        var player = LocalContext.GetMe(ctx.RunState.Players);
        if (player == null) return ExecutionResult.ModFailure("Player not found");
#if !ALTERNATIVE_ACTIONS
        if (action.Name == "play_enemy_target_card" || action.Name == "play_ally_target_card" || action.Name == "play_card")
#else
        if (action.Name.StartsWith("play_card_"))
#endif
        {
            var result = ValidateSingleCard(action, data, ref parsedData, ctx);
            if (result.Successful && !_isRevalidation)
                InvalidateFutureActions(action, parsedData, player, ctx);
            return result;
        }
#if !ALTERNATIVE_ACTIONS
        else if (action.Name == "use_potion" || action.Name == "use_target_potion")
#else
        else if (action.Name.StartsWith("use_potion_"))
#endif
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

        var globalActions = instance.GlobalActions;
        if (globalActions.Count == 0) return;

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
            _projectedPotionsUsed.Add(currentAction.Name);

        // Build potion availability: actual count minus projected uses
        Dictionary<string, int>? potionAvailability = null;
        if (_projectedPotionsUsed.Count > 0)
        {
            potionAvailability = new Dictionary<string, int>();
            foreach (var p in player.Potions)
            {
                var key = $"use_potion_{p.Title.GetActionName()}";
                potionAvailability[key] = potionAvailability.GetValueOrDefault(key, 0) + 1;
            }
            foreach (var usedName in _projectedPotionsUsed)
            {
                if (potionAvailability.ContainsKey(usedName))
                    potionAvailability[usedName]--;
            }
        }

        var toUnregister = new List<string>();
        foreach (var otherAction in globalActions)
        {
            if (otherAction.Name.StartsWith("play_card_"))
            {
                var cardIndex = otherAction.Name.Replace("play_card_", "");
                var hand = pcs.Hand.Cards;

                // When checking the same card name being played, exclude the exact instance
                // being consumed so we only find remaining copies
                var candidates = hand
                    .Where(x => TextHelper.GetActionNameFor(x.Title) == cardIndex && x.CanPlay());
                if (parsedData.Card != null && otherAction.Name == currentAction.Name)
                    candidates = candidates.Where(x => x != parsedData.Card);

                var otherCard = candidates
                    .OrderBy(x => x.EnergyCost.GetAmountToSpend() + x.CurrentStarCost)
                    .FirstOrDefault();

                if (otherCard == null)
                {
                    toUnregister.Add(otherAction.Name);
                    continue;
                }

                // X-cost cards always have effective cost 0 for playability (GetWithModifiers returns 0)
                int otherEnergy = Math.Max(0, otherCard.EnergyCost.GetWithModifiers(CostModifiers.All));
                int otherStars = otherCard.HasStarCostX ? 0 : Math.Max(0, otherCard.GetStarCostWithModifiers());

                if (otherEnergy > _projectedEnergyRemaining && canPayEnergyWithStars)
                {
                    otherStars += (otherEnergy - _projectedEnergyRemaining) * 2;
                    otherEnergy = _projectedEnergyRemaining;
                }

                if (otherEnergy > _projectedEnergyRemaining || otherStars > _projectedStarsRemaining)
                {
                    toUnregister.Add(otherAction.Name);
                }
            }
            else if (otherAction.Name.StartsWith("use_potion_") && potionAvailability != null)
            {
                if (potionAvailability.GetValueOrDefault(otherAction.Name, 0) <= 0)
                {
                    toUnregister.Add(otherAction.Name);
                }
            }
        }

        var actionsRemaining = globalActions.Select(a => a.Name).Except(toUnregister);
        if (actionsRemaining.All(x => x == "end_turn"))
        {
            toUnregister.Add("end_turn");
        }

        foreach (var name in toUnregister)
            _invalidatedActions.Add(name);

        NeuroIntegration.UnregisterActions(toUnregister.ToArray());

    }
    public ExecutionResult ValidatePotion(ConstructedAction action, ActionJData data, ref Result parsedData, ContextInfo ctx)
    {
#if !ALTERNATIVE_ACTIONS
        var slot = data.Data?["potion"]?.GetValue<string>();
#else
        var slot = action.Name.Replace("use_potion_", "");
#endif
        if (slot == null)
            return ExecutionResult.Failure("No potion specified");
        var player = LocalContext.GetMe(ctx.RunState!.Players);
        if (player == null)
            return ExecutionResult.ModFailure("Couldn't find player");
        var potions = player.PotionSlots;
#if !ALTERNATIVE_ACTIONS
        var potion = potions.FirstOrDefault((p) => TextHelper.SafeLocString(() => p?.Title ?? new("","")) == slot);
#else
        var potion = potions.FirstOrDefault(p => TextHelper.GetActionNameFor(TextHelper.SafeLocString(() => p?.Title ?? new("", ""))) == slot);
#endif
        if (potion == null)
            return ExecutionResult.Failure($"No potion named '{slot}'");

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
                if (data.Data?["target"]?.GetValue<string>() is string targetIndex)
                {
                    target = aliveEnemies.GetUniqueCreature(targetIndex!);
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

#if !ALTERNATIVE_ACTIONS
        var cardIndex = data.Data?["card"]?.GetValue<string>();
#else
        var cardIndex = action.Name.Replace("play_card_", "");
#endif
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player?.PlayerCombatState;
        if (pcs == null) return ExecutionResult.Failure("No player combat state");

        var hand = pcs.Hand.Cards;
        if (hand == null || hand.Count <= 0)
            return ExecutionResult.Failure("The hand is not valid");
#if !ALTERNATIVE_ACTIONS
        var card = hand.FirstOrDefault((x) => x.Title == cardIndex && x.CanPlay());
#else
        // select the card that is the cheapest and if it can be played and is named correctly
        var card = hand.OrderBy(x => x.EnergyCost.GetAmountToSpend() + x.CurrentStarCost).FirstOrDefault((x) => TextHelper.GetActionNameFor(x.Title) == cardIndex && x.CanPlay());
#endif
        if (card == null || !card.CanPlay())
            return ExecutionResult.Failure($"Card '{cardIndex}' cannot be played");

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
            if (data.Data?["target"]?.GetValue<string>() is string targetIndex)
            {
                Plugin.LogDebug($"target: {targetIndex}");
                target = aliveEnemies.GetUniqueCreature(targetIndex!);
                var unique = aliveEnemies.CreaturesAreDistinct();
                for (int i = 0; i < aliveEnemies.Count; i++)
                {
                    Creature? targets = aliveEnemies[i];
                    Plugin.LogDebug($"all_enemies: {targets.GetUniqueName(unique, i)}");
                }
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
            target = data.Data?["target"]?.GetValue<string>() is string tp
                ? allies.FirstOrDefault((a) => a.Name == tp)
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
#if !ALTERNATIVE_ACTIONS
            var execResult = action.Name switch
            {
                "play_enemy_target_card" or "play_ally_target_card" or "play_card" => await PlayCard(freshResult, freshCtx),
                "end_turn" => await EndTurn(freshCtx),
                "use_potion" or "use_target_potion" => UsePotion(freshResult, freshCtx),
                _ => null
            };
#else
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
#endif
            return execResult;
        }
        finally
        {
            actionQueue.ActionFinished();
            if (action.Name != "end_turn") // Don't send a new context after ending the turn, we'll get a new one when the next combat round starts
            {
                var context = getContext(freshCtx, afterPlayed: true);
                NeuroIntegration.SendContext(context.Message, context.Silent);
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
