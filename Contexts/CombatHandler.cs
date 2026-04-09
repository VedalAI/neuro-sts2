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

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var combatState = ctx.CombatState;
        if (combatState == null) return null;

        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        var result = new Dictionary<string, object>
        {
            ["round"] = combatState.RoundNumber,
            ["currentSide"] = combatState.CurrentSide.ToString()
        };

        if (pcs != null)
        {
            result["energy"] = pcs.Energy;
            result["stars"] = pcs.Stars;
            result["hand"] = pcs.Hand.Cards
                .Select((c, i) => SerializeCardInHand(c, i))
                .ToList();
            result["drawPileCount"] = pcs.DrawPile.Cards.Count;
            result["discardPileCount"] = pcs.DiscardPile.Cards.Count;
            result["exhaustPileCount"] = pcs.ExhaustPile.Cards.Count;

            var orbQueue = pcs.OrbQueue;
            if (orbQueue != null && orbQueue.Capacity > 0)
            {
                result["orbSlots"] = orbQueue.Capacity;
                result["orbs"] = orbQueue.Orbs.Select((orb, i) => new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = TextHelper.SafeLocString(() => orb.Title),
                    ["passiveValue"] = orb.PassiveVal,
                    ["evokeValue"] = orb.EvokeVal
                }).ToList();
            }
        }

        var playerCreature = player.Creature;
        result["playerBlock"] = playerCreature.Block;
        result["playerPowers"] = SerializePowers(playerCreature.Powers);

        result["enemies"] = combatState.Enemies
            .Where(e => e.IsAlive)
            .Select((e, i) => SerializeEnemy(e, i, combatState))
            .ToList();

        // Companions (allied creatures that aren't the local player)
        var companions = combatState.Allies
            .Where(c => c.IsAlive && c != playerCreature)
            .Select(c =>
            {
                var comp = new Dictionary<string, object>
                {
                    ["name"] = c.Name,
                    ["hp"] = c.CurrentHp,
                    ["maxHp"] = c.MaxHp,
                    ["block"] = c.Block
                };
                if (c.Powers.Count > 0)
                    comp["powers"] = SerializePowers(c.Powers);
                return comp;
            })
            .ToList();
        if (companions.Count > 0)
            result["companions"] = companions;

        return result;
    }

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
        foreach (var power in player.Creature.Powers)
        {
            stringBuilder.AppendLine($"\t- A {TextHelper.SafeLocString(() => power.Title)} on you with {power.Amount} which does: \"{TextHelper.SafeLocString(() => power.Description)}\"");
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
        stringBuilder.AppendLine($"You currently have {pcs.Hand.Cards.Count} Cards in hand");
        stringBuilder.RepresentDeck(pcs.Hand.Cards);
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
        foreach (var eventBetweenRounds in EventLog.DrainAll())
        {
            stringBuilder.AppendLine($"- {eventBetweenRounds.Message}");
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
                var is_distinct = ctx.CombatState!.HittableEnemies.Count == ctx.CombatState.HittableEnemies.DistinctBy((e) => e.Name).Count();
                commands.Add(new("play_enemy_target_card", "Select a card that requires a enemy Target", QJS.WrapObject(new Dictionary<string, JsonSchema>()
                {
                    ["card"] = QJS.Enum(enemy_target_cards),
                    ["target"] = QJS.Enum(ctx.CombatState!.HittableEnemies.Select(
                        (e, i) =>
                        {
                            if (is_distinct)
                                return e.Name;
                            else
                                return $"[{i}] " + e.Name;
                        }))
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
                        ["target"] = QJS.Enum(ctx.CombatState!.HittableEnemies.Select((c) => c.Name))//TODO: require collision checks
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
                ["target"] = QJS.Enum(ctx.CombatState.Allies.Select((a) => a.Name)) //TODO: better way to identify which one
            })));
        }

        commands.Add(new("end_turn", "Ends your current turn"));




        return commands;
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        parsedData = data.Data;
        return ExecutionResult.Success();
    }
    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        GameStabilityDetector.ResetWasStable();
        firstContext = false;
        return action.Name switch
        {
            "play_enemy_target_card" or "play_ally_target_card" or "play_card" => await PlayCard(root, ctx),
            "end_turn" => EndTurn(ctx),
            "use_potion" or "use_target_potion" => UsePotion(root, ctx),
            _ => null
        };
    }

    private async Task<ActionResult.Result> PlayCard(JsonElement root, ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ActionResult.Error("Not in combat");
        if (cm.IsOverOrEnding) return ActionResult.Error("Combat is ending");
        if (!cm.IsPlayPhase) return ActionResult.Error("Not in play phase");

        var cardIndex = root.GetProperty("card").GetString();
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        if (pcs == null) return ActionResult.Error("No player combat state");

        var hand = pcs.Hand.Cards;
        var card = hand.FirstOrDefault((x) => x.Title == cardIndex);
        if (card == null || !card.CanPlay())
            return ActionResult.Error($"Card '{card.Title}' cannot be played");

        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;

        // Use the same alive-enemy list as serialization so indices match
        var aliveEnemies = combatState.HittableEnemies.ToList();

        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy)
        {
            if (root.TryGetProperty("target", out var targetProp))
            {
                var targetIndex = targetProp.GetString();
                target = aliveEnemies.FirstOrDefault((e) => e.Name == targetIndex);
            }
            else
            {
                target = aliveEnemies.FirstOrDefault();
            }
            if (target == null)
                return ActionResult.Error("No valid target available");
        }
        else if (card.TargetType == TargetType.AnyAlly)
        {
            var allies = combatState.Allies.Where(c => c != null && c.IsAlive && c.IsPlayer && c != card.Owner.Creature);
            target = root.TryGetProperty("target", out var tp)
                ? allies.FirstOrDefault((a) => a.Name == tp.GetString())
                : allies.FirstOrDefault();
        }

        var played = await GodotMainThread.RunAsync(() => card.TryManualPlay(target));
        if (!played)
            return ActionResult.Error($"Card '{card.Title}' play was rejected by the game");

        Plugin.Log($"Played card '{card.Title}'" + (target != null ? " targeting enemy" : ""));
        return ActionResult.Ok("Card played");
    }

    private ActionResult.Result EndTurn(ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ActionResult.Error("Not in combat");
        if (!cm.IsPlayPhase || !cm.IsInProgress) return ActionResult.Error("Not in play phase");

        var player = LocalContext.GetMe(ctx.RunState.Players);
        if (cm.IsPlayerReadyToEndTurn(player))
            return ActionResult.Error("Turn already ended");

        var roundNumber = player.Creature.CombatState.RoundNumber;
        Callable.From(() =>
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                new MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction(player, roundNumber));
        }).CallDeferred();

        Plugin.Log("Ended turn");
        firstContext = true;
        return ActionResult.Ok("Turn ended");
    }

    private ActionResult.Result UsePotion(JsonElement root, ContextInfo ctx)
    {
        var slot = root.GetProperty("potion").GetString();
        var player = LocalContext.GetMe(ctx.RunState.Players);

        var potions = player.PotionSlots;

        var potion = potions.FirstOrDefault((p) => TextHelper.SafeLocString(() => p.Title) == slot);
        if (potion == null)
            return ActionResult.Error($"No potion in slot {slot}");

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
                    var is_distinct = aliveEnemies.DistinctBy((e) => e.Name).Count() != aliveEnemies.Count;
                    target = (Creature?)aliveEnemies.Select((e, i) =>
                    {
                        if (is_distinct && e.Name == targetIndex)
                            return e;
                        if ($"[{i}] " + e.Name == targetIndex)
                            return e;
                        else { return null; }
                    });
                }
                else
                {
                    target = aliveEnemies.FirstOrDefault();
                }
                if (target == null)
                    return ActionResult.Error("No valid target for potion");
            }
        }
        else
        {
            // Self-targeting potions: game UI passes Owner.Creature
            target = player.Creature;
        }

        Callable.From(() => potion.EnqueueManualUse(target)).CallDeferred();
        Plugin.Log($"Used potion in slot {slot}");
        return ActionResult.Ok("Potion used");
    }

    // Serialization helpers

    private static Dictionary<string, object> SerializeCardInHand(CardModel card, int index)
    {
        var result = new Dictionary<string, object>
        {
            ["index"] = index,
            ["name"] = card.Title,
            ["description"] = TextHelper.GetCardDescription(card),
            ["targetType"] = card.TargetType.ToString(),
            ["playable"] = card.CanPlay()
        };

        if (card.EnergyCost != null)
        {
            if (card.EnergyCost.CostsX)
                result["cost"] = "X";
            else
                result["cost"] = card.EnergyCost.GetWithModifiers(CostModifiers.All);
        }

        return result;
    }



    private static void PrettyRenderEnemies(StringBuilder stringBuilder, IReadOnlyList<Creature> enemies, CombatState combatState)
    {

        var enemies_are_distinct = enemies.Count == enemies.DistinctBy((e) => e.Name).Count();
        for (int i = 0; i < enemies.Count; i++)
        {
            Creature? enemy = enemies[i];
            var enemy_name = "";
            if (enemies_are_distinct)
                enemy_name = enemy.Name;
            else
                enemy_name = $"[{i}] " + enemy.Name;

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
            stringBuilder.Append("\t");

            if (enemy?.Monster is MonsterModel monster)
            {
                var intents = monster.NextMove.Intents;
                if (intents != null && intents.Count > 0)
                {
                    stringBuilder.AppendLine($"The {enemy_name} is intending to:");
                    foreach (var intent in intents)
                    {
                        stringBuilder.Append("\t\t\t- ");
                        switch (intent)
                        {
                            case AttackIntent attackIntent:
                                try
                                {
                                    var allies = combatState.Creatures.Where(c => c.Player != null);
                                    stringBuilder.Append($"Attack with {attackIntent.GetTotalDamage(allies, enemy)} Damage");
                                    if (attackIntent.Repeats > 1)
                                    {
                                        stringBuilder.AppendLine($" {attackIntent.Repeats} Times");
                                    }
                                    else
                                    {
                                        stringBuilder.AppendLine($"");
                                    }
                                }
                                catch
                                {
                                }
                                break;
                            default:
                                stringBuilder.AppendLine($"{intent.IntentType}");
                                break;
                        }
                    }
                }
            }
        }
    }

    private static Dictionary<string, object> SerializeEnemy(Creature enemy, int index, CombatState combatState)
    {
        var result = new Dictionary<string, object>
        {
            ["index"] = index,
            ["hp"] = enemy.CurrentHp,
            ["maxHp"] = enemy.MaxHp,
            ["block"] = enemy.Block,
            ["powers"] = SerializePowers(enemy.Powers)
        };

        var monster = enemy.Monster;
        if (monster != null)
        {
            result["name"] = TextHelper.SafeLocString(() => monster.Title);

            var intents = monster.NextMove?.Intents;
            if (intents != null && intents.Count > 0)
            {
                result["intents"] = intents.Select(intent =>
                {
                    var intentDict = new Dictionary<string, object>
                    {
                        ["type"] = intent.IntentType.ToString()
                    };

                    if (intent is AttackIntent attackIntent)
                    {
                        try
                        {
                            var allies = combatState.Creatures.Where(c => c.Player != null);
                            intentDict["damage"] = attackIntent.GetTotalDamage(allies, enemy);
                            if (attackIntent.Repeats > 1)
                                intentDict["hits"] = attackIntent.Repeats;
                        }
                        catch { }
                    }

                    return intentDict;
                }).ToList();
            }
        }

        return result;
    }

    private static List<Dictionary<string, object>> SerializePowers(IReadOnlyList<MegaCrit.Sts2.Core.Models.PowerModel> powers)
    {
        return powers.Select(p => new Dictionary<string, object>
        {
            ["name"] = TextHelper.SafeLocString(() => p.Title),
            ["amount"] = p.Amount,
            ["description"] = TextHelper.GetPowerDescription(p)
        }).ToList();
    }

}
