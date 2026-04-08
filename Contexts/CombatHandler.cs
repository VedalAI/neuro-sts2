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

namespace Sts2Agent.Contexts;

public class CombatHandler : IContextHandler
{
    public ContextType Type => ContextType.Combat;

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

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        return new();
        var commands = new List<ConstructedAction>();
        var cm = CombatManager.Instance;
        if (cm == null || !cm.IsPlayPhase || cm.PlayerActionsDisabled) return commands;

        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        if (pcs == null) return commands;

        for (int i = 0; i < pcs.Hand.Cards.Count; i++)
        {
            var card = pcs.Hand.Cards[i];
            if (card.CanPlay())
            {
                // commands.Add(new Dictionary<string, object>
                // {
                //     ["type"] = "play_card",
                //     ["cardIndex"] = i,
                //     ["card"] = card.Title.ToString(),
                //     ["requiresTarget"] = card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly
                // });
            }
        }

        // commands.Add(new Dictionary<string, object> { ["type"] = "end_turn" });

        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            var potion = player.PotionSlots[i];
            if (potion != null)
            {
                // commands.Add(new Dictionary<string, object>
                // {
                //     ["type"] = "use_potion",
                //     ["slot"] = i,
                //     ["potion"] = TextHelper.SafeLocString(() => potion.Title)
                // });
            }
        }

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        return action.Name switch
        {
            "play_card" => await PlayCard(root, ctx),
            "end_turn" => EndTurn(ctx),
            "use_potion" => UsePotion(root, ctx),
            _ => null
        };
    }

    private async Task<ActionResult.Result> PlayCard(JsonElement root, ContextInfo ctx)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return ActionResult.Error("Not in combat");
        if (cm.IsOverOrEnding) return ActionResult.Error("Combat is ending");
        if (!cm.IsPlayPhase) return ActionResult.Error("Not in play phase");

        var cardIndex = root.GetProperty("cardIndex").GetInt32();
        var player = LocalContext.GetMe(ctx.RunState.Players);
        var pcs = player.PlayerCombatState;
        if (pcs == null) return ActionResult.Error("No player combat state");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return ActionResult.Error($"Card index {cardIndex} out of range (hand size: {hand.Count})");

        var card = hand[cardIndex];
        if (!card.CanPlay())
            return ActionResult.Error($"Card '{card.Title}' cannot be played");

        var combatState = card.CombatState ?? card.Owner.Creature.CombatState;

        // Use the same alive-enemy list as serialization so indices match
        var aliveEnemies = combatState.Enemies.Where(e => e.IsAlive).ToList();

        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy)
        {
            if (root.TryGetProperty("targetIndex", out var targetProp))
            {
                var targetIndex = targetProp.GetInt32();
                if (targetIndex < 0 || targetIndex >= aliveEnemies.Count)
                    return ActionResult.Error($"Target index {targetIndex} out of range (alive: {aliveEnemies.Count})");
                target = aliveEnemies[targetIndex];
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
            target = root.TryGetProperty("targetIndex", out var tp)
                ? allies.ElementAtOrDefault(tp.GetInt32())
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
        return ActionResult.Ok("Turn ended");
    }

    private ActionResult.Result UsePotion(JsonElement root, ContextInfo ctx)
    {
        var slot = root.GetProperty("slot").GetInt32();
        var player = LocalContext.GetMe(ctx.RunState.Players);

        var potions = player.PotionSlots;
        if (slot < 0 || slot >= potions.Count)
            return ActionResult.Error($"Potion slot {slot} out of range");

        var potion = potions[slot];
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
                var aliveEnemies = combatState.Enemies.Where(e => e.IsAlive).ToList();
                if (root.TryGetProperty("targetIndex", out var targetProp))
                {
                    var targetIndex = targetProp.GetInt32();
                    if (targetIndex < 0 || targetIndex >= aliveEnemies.Count)
                        return ActionResult.Error($"Target index {targetIndex} out of range (alive: {aliveEnemies.Count})");
                    target = aliveEnemies[targetIndex];
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

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

}
