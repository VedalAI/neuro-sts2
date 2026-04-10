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

namespace Sts2Agent.Contexts;

public class CombatHandler : IContextHandler
{
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


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        parsedData = data.Data;
        if (action.Name == "play_enemy_target_card" || action.Name == "play_ally_target_card" || action.Name == "play_card")
        {
            var cm = CombatManager.Instance;
            if (cm == null) return ExecutionResult.Failure("Not in combat");
            if (cm.IsOverOrEnding) return ExecutionResult.Failure("Combat is ending");
            if (!cm.IsPlayPhase) return ExecutionResult.Failure("Not in play phase");

            var cardIndex = data.Data?["card"]?.GetValue<string>();
            if (cardIndex == null)
                return ExecutionResult.Failure("card field is missing");
            if (ctx == null)
                return ExecutionResult.ModFailure("Invalid Ctx");
            if (ctx.RunState == null)
                return ExecutionResult.ModFailure("Runstate is invalid currently");

            var player = LocalContext.GetMe(ctx.RunState.Players);
            var pcs = player?.PlayerCombatState;
            if (pcs == null) return ExecutionResult.Failure("No player combat state");

            var hand = pcs.Hand.Cards;
            if (hand == null || hand.Count <= 0)
                return ExecutionResult.Failure($"Hand isn't valid");
            var card = hand.FirstOrDefault((x) => x?.Title == cardIndex);
            if (card == null || !card.CanPlay())
                return ExecutionResult.Failure($"Card '{cardIndex}' cannot be played");
        }
        return ExecutionResult.Success();
    }
    public async Task<ExecutionResult?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)
    {
        firstContext = false;
        return action.Name switch
        {
            "play_enemy_target_card" or "play_ally_target_card" or "play_card" => await PlayCard(root, ctx),
            "end_turn" => EndTurn(ctx),
            "use_potion" or "use_target_potion" => UsePotion(root, ctx),
            _ => null
        };
    }

    private async Task<ExecutionResult> PlayCard(JsonElement root, ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ExecutionResult.Failure("Not in combat");
        if (cm.IsOverOrEnding) return ExecutionResult.Failure("Combat is ending");
        if (!cm.IsPlayPhase) return ExecutionResult.Failure("Not in play phase");

        var cardIndex = root.GetProperty("card").GetString();
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player?.PlayerCombatState;
        if (pcs == null) return ExecutionResult.Failure("No player combat state");

        var hand = pcs.Hand.Cards;
        if (hand == null || hand.Count <= 0)
            return ExecutionResult.Failure($"Hand isn't valid");
        var card = hand.FirstOrDefault((x) => x.Title == cardIndex);
        if (card == null || !card.CanPlay())
            return ActionResult.Error($"Card '{card.Title}' cannot be played");
            return ExecutionResult.Failure($"Card '{cardIndex}' cannot be played");

        var combatState = card?.CombatState;
        if (combatState == null)
        {
            return ExecutionResult.Failure("card's Combat state is null");
        }

        var aliveEnemies = combatState.HittableEnemies.ToList();
        Plugin.LogDebug("test");

        Creature? target = null;
        if (card!.TargetType == TargetType.AnyEnemy)
        {
            if (root.TryGetProperty("target", out var targetProp))
            {
                var targetIndex = targetProp.GetString();
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
            target = root.TryGetProperty("target", out var tp)
                ? allies.FirstOrDefault((a) => a.Name == tp.GetString())
                : allies.FirstOrDefault();
        }

        try
        {

            var played = card.TryManualPlay(target);
            if (!played)
                return ExecutionResult.Failure($"Card '{card.Title}' play was rejected by the game");

        }
        catch (Exception e)
        {
            Plugin.LogDebug(e.Message);
            return ExecutionResult.Failure("Playing the card threw");
        }
        Plugin.Log($"Played card '{card.Title}'" + (target != null ? " targeting enemy" : ""));
        return ExecutionResult.Success("Card played");
    }

    private ExecutionResult EndTurn(ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ExecutionResult.Failure("Not in combat");
        if (!cm.IsPlayPhase || !cm.IsInProgress) return ExecutionResult.Failure("Not in play phase");

        var player = LocalContext.GetMe(ctx.RunState.Players);
        if (cm.IsPlayerReadyToEndTurn(player))
            return ExecutionResult.Failure("Turn already ended");

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

    private ExecutionResult UsePotion(JsonElement root, ContextInfo ctx)
    {
        var slot = root.GetProperty("potion").GetString();
        var player = LocalContext.GetMe(ctx.RunState.Players);

        var potions = player.PotionSlots;

        var potion = potions.FirstOrDefault((p) => TextHelper.SafeLocString(() => p.Title) == slot);
        if (potion == null)
            return ExecutionResult.Failure($"No potion in slot {slot}");

        // Resolve target based on potion's target type
        Creature? target = null;
        var targetType = potion.TargetType;
        if (targetType == TargetType.AnyEnemy || targetType == TargetType.TargetedNoCreature)
        {
            var combatState = ctx.CombatState;
            if (combatState != null)
            {
                var aliveEnemies = combatState.HittableEnemies.ToList();
                if (root.TryGetProperty("target", out var targetProp))
                {
                    var targetIndex = targetProp.GetString();
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

        Callable.From(() => potion.EnqueueManualUse(target)).CallDeferred();
        Plugin.Log($"Used potion in slot {slot}");
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
