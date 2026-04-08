using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Agent.Contexts;
using Sts2Agent.Utilities;

namespace Sts2Agent;

public static class GameStateSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Dictionary<string, object> Serialize()
    {
        try
        {
            var ctx = GameContext.Resolve();
            if (ctx == null)
                return new() { { "error", "No Active Run" } };

            var state = new Dictionary<string, object>();

            // Pre-run screens have no run state — handle separately
            if (ctx.Type is ContextType.CharacterSelect or ContextType.MainMenu)
            {
                state["context"] = ctx.Type;
                var preRunHandler = ActionExecutor.GetHandlers()
                    .FirstOrDefault(h => h.Type == ctx.Type);
                if (preRunHandler != null)
                {
                    var preRunState = preRunHandler.SerializeState(ctx);
                    if (preRunState != null)
                        state[GetStateKey(ctx.Type)] = preRunState;
                    state["available_commands"] = preRunHandler.GetCommands(ctx);
                }
                // Drain any stale events on pre-run screens
                EventLog.Clear();
                return state;
            }

            // "context" reflects the underlying room type (preserving API format),
            // while overlays are reported separately via the "overlay" key.
            var isOverlay = ctx.Type is ContextType.CardSelection
                or ContextType.Rewards or ContextType.HandSelection
                or ContextType.BundleSelection;
            state["context"] = isOverlay
                ? GetRoomContext(ctx.RunState!.CurrentRoom)
                : GetContextString(ctx.Type);

            // Player info (always present)
            var localPlayer = LocalContext.GetMe(ctx.RunState!.Players);
            if (localPlayer != null)
                state["player"] = SerializePlayer(localPlayer);

            // Context-specific state from handler
            var handler = ActionExecutor.GetHandlers()
                .FirstOrDefault(h => h.Type == ctx.Type);

            if (isOverlay)
            {
                // Serialize the overlay into the "overlay" key
                if (handler != null)
                {
                    var overlayState = handler.SerializeState(ctx);
                    if (overlayState != null)
                        state["overlay"] = overlayState;
                }

                // Also serialize underlying room state (with event options suppressed)
                SerializeUnderlyingRoom(ctx, state);
            }
            else
            {
                if (handler != null)
                {
                    var contextState = handler.SerializeState(ctx);
                    if (contextState != null)
                        state[GetStateKey(ctx.Type)] = contextState;
                }

                // Rest site also includes map data
                if (ctx.Type == ContextType.RestSite && ctx.AvailableMapNodes != null)
                {
                    var mapHandler = ActionExecutor.GetHandlers()
                        .FirstOrDefault(h => h.Type == ContextType.Map);
                    if (mapHandler != null)
                    {
                        var mapCtx = new ContextInfo
                        {
                            Type = ContextType.Map,
                            RunState = ctx.RunState,
                            AvailableMapNodes = ctx.AvailableMapNodes
                        };
                        var mapState = mapHandler.SerializeState(mapCtx);
                        if (mapState != null)
                            state["map"] = mapState;
                    }
                }
            }

            // Available commands from handler
            if (handler != null)
                state["available_commands"] = handler.GetCommands(ctx);

            // Drain accumulated events
            var events = EventLog.DrainAll();
            if (events.Count > 0)
            {
                state["events"] = events.Select(e =>
                {
                    var dict = new Dictionary<string, object>
                    {
                        ["type"] = e.Type,
                        ["message"] = e.Message
                    };
                    if (e.Details != null)
                        dict["details"] = e.Details;
                    return dict;
                }).ToList();
            }

            return state;
        }
        catch (Exception e)
        {
            return new() { { "error", e.Message } };
        }
    }

    private static string GetContextString(ContextType type) => type switch
    {
        ContextType.Map => "map",
        ContextType.Combat => "combat",
        ContextType.Event => "event",
        ContextType.RestSite => "rest",
        ContextType.Shop => "shop",
        ContextType.Treasure => "treasure",
        ContextType.GameOver => "game_over",
        ContextType.CharacterSelect => "character_select",
        ContextType.MainMenu => "main_menu",
        _ => "unknown"
    };

    private static string GetRoomContext(AbstractRoom? room) => room switch
    {
        CombatRoom => "combat",
        EventRoom => "event",
        MerchantRoom => "shop",
        RestSiteRoom => "rest",
        TreasureRoom => "treasure",
        _ => "unknown"
    };

    private static string GetStateKey(ContextType type) => type switch
    {
        ContextType.Map => "map",
        ContextType.Combat => "combat",
        ContextType.Event => "event",
        ContextType.RestSite => "rest",
        ContextType.Shop => "shop",
        ContextType.Treasure => "treasure",
        ContextType.GameOver => "game_over",
        ContextType.CharacterSelect => "character_select",
        ContextType.MainMenu => "main_menu",
        _ => "state"
    };

    private static void SerializeUnderlyingRoom(ContextInfo ctx, Dictionary<string, object> state)
    {
        var room = ctx.RunState!.CurrentRoom;

        if (room is CombatRoom)
        {
            var combatHandler = ActionExecutor.GetHandlers()
                .FirstOrDefault(h => h.Type == ContextType.Combat);
            if (combatHandler != null)
            {
                var combatCtx = new ContextInfo
                {
                    Type = ContextType.Combat,
                    RunState = ctx.RunState,
                    CombatState = CombatManager.Instance?.DebugOnlyGetState()
                };
                var combatState = combatHandler.SerializeState(combatCtx);
                if (combatState != null)
                    state["combat"] = combatState;
            }
        }
        else if (room is EventRoom eventRoom)
        {
            var eventHandler = ActionExecutor.GetHandlers()
                .FirstOrDefault(h => h.Type == ContextType.Event);
            if (eventHandler != null)
            {
                var eventCtx = new ContextInfo
                {
                    Type = ContextType.Event,
                    RunState = ctx.RunState,
                    EventRoom = eventRoom
                };
                var eventState = eventHandler.SerializeState(eventCtx);
                if (eventState is Dictionary<string, object> eventDict)
                {
                    eventDict["options"] = new List<object>();
                    state["event"] = eventDict;
                }
            }
        }
    }

    private static Dictionary<string, object> SerializePlayer(Player player)
    {
        var result = new Dictionary<string, object>
        {
            ["hp"] = player.Creature.CurrentHp,
            ["maxHp"] = player.Creature.MaxHp,
            ["gold"] = player.Gold
        };

        result["relics"] = player.Relics.Select(r => new Dictionary<string, object>
        {
            ["name"] = TextHelper.SafeLocString(() => r.Title),
            ["description"] = TextHelper.GetRelicDescription(r)
        }).ToList();

        result["potions"] = player.PotionSlots
            .Select((p, i) => p == null ? null : new Dictionary<string, object>
            {
                ["slot"] = i,
                ["name"] = TextHelper.SafeLocString(() => p.Title),
                ["description"] = TextHelper.GetPotionDescription(p)
            })
            .Where(p => p != null)
            .ToList();

        result["deck"] = player.Deck.Cards.Select(SerializeCardBrief).ToList();

        return result;
    }

    private static Dictionary<string, object> SerializeCardBrief(CardModel card)
    {
        var result = new Dictionary<string, object>
        {
            ["name"] = card.Title,
            ["description"] = TextHelper.GetCardDescription(card)
        };

        if (card.EnergyCost != null)
        {
            if (card.EnergyCost.CostsX)
                result["cost"] = "X";
            else
                result["cost"] = card.EnergyCost.Canonical;
        }

        return result;
    }
}
