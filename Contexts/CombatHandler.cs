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

namespace Sts2Agent.Contexts;

public class CombatHandler : IContextHandler<CombatHandler.Result>
{
    public class Result : IContextResult
    {
        internal Creature? Target;
        internal CardModel? Card;
        internal PotionModel Potion;
    }
    public ContextType Type => ContextType.Combat;
    bool firstContext = true;

    public string GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        var combatState = ctx.CombatState;
        stringBuilder.AppendLine("## You are in combat");
        if (combatState == null)
        {
            return stringBuilder.ToString();
        }
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        stringBuilder.AppendLine($"### It is currently Round {combatState.RoundNumber} and its your turn");
        if (pcs != null)
        {
            stringBuilder.AppendLine($"You currently have {pcs.Energy} Energy and {pcs.Stars} Stars to use");
            stringBuilder.AppendLine($"You have {pcs.DrawPile.Cards.Count} Cards in the Drawpile, {pcs.DiscardPile.Cards.Count} Cards in the Discardpile and {pcs.ExhaustPile.Cards.Count} Cards in the Exhausted pile");
            if (player.Relics.Count > 0)
            {
                stringBuilder.AppendLine($"You have {player.Relics.Count} Relics, The Relics are:");
                stringBuilder.RepresentRelics(player.Relics);
            }

            if (pcs.OrbQueue != null && pcs.OrbQueue.Capacity > 0)
            {
                stringBuilder.AppendLine($"You have {pcs.OrbQueue.Capacity} Orb slots");
                if (pcs.OrbQueue.Orbs.Count > 0)
                {
                    stringBuilder.AppendLine($"Currently you have these orbs in order of usage:");
                    foreach (var orb in pcs.OrbQueue.Orbs)
                    {
                        stringBuilder.AppendLine($"- {TextHelper.SafeLocString(() => orb.Title)} it does {orb.PassiveVal} passive and {orb.EvokeVal} on Evoked");
                    }

                }
                else
                {
                    stringBuilder.AppendLine("Currently there are no orbs in the slots");
                }
            }
        }
        stringBuilder.AppendLine($"You currently have {player.Creature.CurrentHp} HP out of {player.Creature.MaxHp} maxhp and {player.Creature.Block} Block");
        if (player.Creature.Powers.Count > 0)
        {
            stringBuilder.AppendLine($"You have {player.Creature.Powers.Count} Applied on yourself. The Powers are the following:");
            foreach (var power in player.Creature.Powers)
            {
                stringBuilder.AppendLine($"\t- A {TextHelper.SafeLocString(() => power.Title)} on you with {power.Amount} which does: \"{TextHelper.SafeLocString(() => power.Description)}\"");
            }
        }

        stringBuilder.AppendLine("");
        stringBuilder.AppendLine($"There are {combatState.Enemies.Count} Enemies:");
        PrettyRenderEnemies(stringBuilder, combatState.Enemies, combatState);
        var allies = combatState.Allies.Where(c => c.IsAlive && c != player.Creature);
        if (allies.Any())
        {
            stringBuilder.AppendLine($"You have: {allies.Count()} allies");
            foreach (var ally in allies)
            {
                stringBuilder.Append($"- {ally.Name} who has {ally.CurrentHp} hp out of {ally.MaxHp} maxhp");
                if (ally.Block > 0)
                {
                    stringBuilder.Append($", it has {ally.Block} block ");
                }
                if (ally.Powers.Count > 0)
                {
                    stringBuilder.AppendLine($", they have {ally.Powers.Count} powers: ");
                    foreach (var power in ally.Powers)
                    {
                        stringBuilder.AppendLine($"\t- A {TextHelper.SafeLocString(() => power.Title)} with {power.Amount} which does: {TextHelper.SafeLocString(() => power.Description)}");
                    }
                }
                stringBuilder.AppendLine();

            }
        }
        stringBuilder.AppendLine();
        stringBuilder.AppendLine($"You currently have {pcs?.Hand.Cards.Count} Cards in hand");
        stringBuilder.RepresentDeck(pcs.Hand.Cards);
        var events = EventLog.DrainAll();
        if (events.Count > 0)
        {

            stringBuilder.AppendLine();
            if (firstContext)
            {
                if (combatState.RoundNumber > 1)
                {
                    stringBuilder.AppendLine($"After Your last turn, this happened:");
                }
                else
                {
                    stringBuilder.AppendLine($"this happend at the start of combat:");
                }
            }
            else
            {
                stringBuilder.AppendLine($"This has happend after you played a card:");
            }
            stringBuilder.RepresentEvents(events);
        }
        return stringBuilder.ToString();

    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsPlayPhase || cm.PlayerActionsDisabled) return commands;

        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        if (pcs == null) return commands;
        //TODO: this might require changes for multiplayer as. there are TargetType.AnyPlayer too
        var ally_target_cards = pcs.Hand.Cards.Where((x) => x.TargetType == TargetType.AnyAlly && x.CanPlay()).Select(x => x.Title).Distinct();

        //If there is only 1 Target no need to make it more difficult for llms
        if (ctx.CombatState!.HittableEnemies.Count > 1)
        {

            var enemy_target_cards = pcs.Hand.Cards.Where((x) => x.TargetType == TargetType.AnyEnemy && x.CanPlay()).Select(x => x.Title).Distinct();
            var none_target_cards = pcs.Hand.Cards.Where((x) => x.TargetType is not (TargetType.AnyAlly or TargetType.AnyEnemy) && x.CanPlay()).Select(x => x.Title).Distinct();

            if (enemy_target_cards.Any())
            {
                commands.Add(new("play_enemy_target_card", "Select a card that requires a enemy Target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                {
                    ["card"] = QJS.Enum(enemy_target_cards),
                    ["target"] = QJS.Enum(ctx.CombatState.HittableEnemies.GetUniqueNames())
                })));
            }
            if (none_target_cards.Any())
            {
                commands.Add(new("play_card", "Select a card to play", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                {
                    ["card"] = QJS.Enum(none_target_cards)
                })));
            }
            if (player.Potions.Any())
            {
                var targeted_potions = player.Potions.Where((p) => p.TargetType is (TargetType.AnyEnemy or TargetType.TargetedNoCreature));
                if (targeted_potions.Any())
                    commands.Add(new("use_target_potion", "use a potion on a target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
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

        }
        else
        {
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

        }
        if (ally_target_cards.Any() && ctx.CombatState?.Allies.Count > 0)
        {

            commands.Add(new("play_ally_target_card", "Select a card that requires a allied Target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["card"] = QJS.Enum(ally_target_cards),
                ["target"] = QJS.Enum(ctx.CombatState.Allies.GetUniqueNames())
            })));
        }

        commands.Add(new("end_turn", "Ends your current turn"));




        return commands;
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ExecutionResult.Failure("Not in combat");
        if (cm.IsOverOrEnding) return ExecutionResult.Failure("Combat is ending");
        if (!cm.IsPlayPhase || !cm.IsInProgress) return ExecutionResult.ModFailure("Not in play phase");
        var player = LocalContext.GetMe(ctx.RunState.Players);
        if (player == null) return ExecutionResult.ModFailure("Player not found");

        if (action.Name == "play_enemy_target_card" || action.Name == "play_ally_target_card" || action.Name == "play_card")
        {
            return ValidateSingleCard(data, ref parsedData, ctx);
        }
        else if (action.Name == "use_potion" || action.Name == "use_target_potion")
        {
            return ValidatePotion(data, ref parsedData, ctx);
        }
        else if (action.Name == "end_turn")
        {
            if (cm.IsPlayerReadyToEndTurn(player))
                return ExecutionResult.Failure("Turn already ended");
        }
        return ExecutionResult.Success();
    }
    public ExecutionResult ValidatePotion(ActionJData data, ref Result parsedData, ContextInfo ctx)
    {
        var slot = data.Data?["potion"]?.GetValue<string>();
        if (slot == null)
            return ExecutionResult.Failure("No potion specified");
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var potions = player.PotionSlots;
        var potion = potions.FirstOrDefault((p) => TextHelper.SafeLocString(() => p.Title) == slot);
        if (potion == null)
            return ExecutionResult.Failure($"No potion named {slot}");

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
                    return ExecutionResult.Failure("No valid target for potion");
            }
        }
        else
        {
            // Self-targeting potions: game UI passes Owner.Creature
            target = player.Creature;
        }
        parsedData.Target = target;
        return ExecutionResult.Success();
    }
    public ExecutionResult ValidateSingleCard(ActionJData data, ref Result parsedData, ContextInfo ctx)
    {

        var cardIndex = data.Data?["card"]?.GetValue<string>();
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player?.PlayerCombatState;
        if (pcs == null) return ExecutionResult.Failure("No player combat state");

        var hand = pcs.Hand.Cards;
        if (hand == null || hand.Count <= 0)
            return ExecutionResult.Failure($"Hand isn't valid");
        var card = hand.FirstOrDefault((x) => x.Title == cardIndex);
        if (card == null || !card.CanPlay())
            return ExecutionResult.Failure($"Card '{cardIndex}' cannot be played");

        var combatState = card?.CombatState;
        if (combatState == null)
        {
            return ExecutionResult.Failure("card's Combat state is null");
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
                return ExecutionResult.Failure("No valid target available");
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
        return action.Name switch
        {
            "play_enemy_target_card" or "play_ally_target_card" or "play_card" => await PlayCard(result, ctx),
            "end_turn" => EndTurn(ctx),
            "use_potion" or "use_target_potion" => UsePotion(result, ctx),
            _ => null
        };
    }

    private async Task<ExecutionResult> PlayCard(Result root, ContextInfo ctx)
    {
        try
        {
            var played = root.Card.TryManualPlay(root.Target);
            if (!played)
                return ExecutionResult.Failure($"Card '{root.Card.Title}' play was rejected by the game");

        }
        catch (Exception e)
        {
            Plugin.LogDebug(e.Message);
            return ExecutionResult.Failure("Playing the card threw");
        }
        Plugin.Log($"Played card '{root.Card.Title}'" + (root.Target != null ? " targeting enemy" : ""));
        return ExecutionResult.Success("Card played");
    }

    private ExecutionResult EndTurn(ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var roundNumber = player.Creature.CombatState.RoundNumber;
        Callable.From(() =>
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(player, roundNumber));
        }).CallDeferred();

        Plugin.Log("Ended turn");
        firstContext = true;
        return ExecutionResult.Success("Turn ended");
    }

    private ExecutionResult UsePotion(Result root, ContextInfo ctx)
    {

        Callable.From(() => root.Potion.EnqueueManualUse(root.Target)).CallDeferred();
        Plugin.Log($"Used potion {root.Potion.Title}");
        return ExecutionResult.Success("Potion used");
    }

    private static void PrettyRenderEnemies(StringBuilder stringBuilder, IReadOnlyList<Creature> enemies, CombatState combatState)
    {

        var enemies_are_distinct = enemies.CreaturesAreDistinct();
        for (int i = 0; i < enemies.Count; i++)
        {
            Creature? enemy = enemies[i];
            var enemy_name = enemy.GetUniqueName(enemies_are_distinct, i);

            stringBuilder.Append($"\t- {enemy_name} has {enemy.CurrentHp} hp out of {enemy.MaxHp} maxhp ");
            stringBuilder.Append($", It has {enemy.Block} Block");
            if (enemy.Powers.Count > 0)
            {
                stringBuilder.AppendLine($" and {enemy.Powers.Count} powers, The powers are:");
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

}
