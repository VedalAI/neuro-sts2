using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using Sts2Agent.Utilities;
using MegaCrit.Sts2.Core.Nodes.Relics;
using System.Text;
using Sts2Agent.Contexts;
using STS2NeuroIntegration;

namespace Sts2Agent;

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
public static class CombatSetupPatch
{
    [HarmonyPostfix]
    public static void Postfix(CombatManager __instance)
    {
        try
        {
            GameStabilityDetector.SubscribeToCombat(__instance);
            Plugin.LogDebug("Combat events wired to stability detector.");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Error in CombatSetupPatch: {e}");
        }
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCombatInternal))]
public static class CombatEndInternalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        try
        {
            Plugin.LogDebug("Combat ending internally — clearing combat actions and scheduling context refresh");
            NeuroIntegration.EndForce();
            NeuroIntegration.UnregisterAllActions();
            ActionExecutor.ClearActions();
            GameStabilityDetector.OnScreenTransition();
            NeuroIntegration.SendContext("Combat has Ended. Don't use any Combat related actions from now on.", true);
        }
        catch (Exception e)
        {
            Plugin.LogError($"Error in CombatEndInternalPatch: {e}");
        }
    }
}

[HarmonyPatch(typeof(NOverlayStack), nameof(NOverlayStack.Push))]
public static class OverlayPushPatch
{
    [HarmonyPostfix]
    public static void Postfix() => GameStabilityDetector.OnOverlayChanged();
}

[HarmonyPatch(typeof(NOverlayStack), nameof(NOverlayStack.Remove))]
public static class OverlayRemovePatch
{
    [HarmonyPostfix]
    public static void Postfix() => GameStabilityDetector.OnOverlayChanged();
}

[HarmonyPatch(typeof(EventModel), "SetEventState")]
public static class EventStateChangedPatch
{
    [HarmonyPostfix]
    public static void Postfix() => GameStabilityDetector.ScheduleStabilityCheck();
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Open))]
public static class MapScreenOpenPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        Plugin.LogDebug("Map screen opened — scheduling stability check");
        GameStabilityDetector.OnRoomEntered();
    }
}

[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
public static class HandSelectCardsPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        // SelectCards sets CurrentMode before awaiting, so this fires
        // while the hand is already in selection mode
        GameStabilityDetector.OnHandSelectionEntered();
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
public static class RoomEnteredPatch
{
    [HarmonyPostfix]
    public static void Postfix(AbstractRoom room)
    {
        Plugin.LogDebug($"Room entered: {room.GetType().Name} — scheduling stability check");
        GameStabilityDetector.OnRoomEntered();
    }
}

[HarmonyPatch(typeof(NTreasureRoom), nameof(NTreasureRoom._Ready))]
public static class TreasureRoomAutoPatch
{
    public static bool AutoClickInProgress { get; private set; }
    public static NTreasureRoom? CurrentRoom { get; private set; }


    [HarmonyPostfix]
    public static void Postfix(NTreasureRoom __instance)
    {
        try
        {
            Plugin.LogDebug("Treasure room ready — auto-opening chest");
            AutoClickInProgress = true;
            CurrentRoom = __instance;
            _ = AutoOpenTreasure(__instance);
        }
        catch (Exception e)
        {
            AutoClickInProgress = false;
            Plugin.LogError($"Error in TreasureRoomAutoPatch: {e}");
        }
    }

    private static async Task AutoOpenTreasure(NTreasureRoom room)
    {
        try
        {
            // Click the chest button
            var chest = await GodotMainThread.RunAsync(() => room.GetNode<NClickableControl>("Chest"));
            if (chest == null)
            {
                Plugin.LogError("Treasure room: chest button not found");
                return;
            }
            await GodotMainThread.ClickAsync(chest);
            Plugin.LogDebug("Treasure room: chest clicked, waiting 3s for relics");

            // Wait 3 seconds for the chest animation and relics to appear
            await Task.Delay(3000);

            // Find and click all visible relic holders
            var holders = await GodotMainThread.RunAsync(() =>
            {
                var found = new List<NTreasureRoomRelicHolder>();
                FindAll(room, found);
                return found;
            });

            StringBuilder stringBuilder = new();
            stringBuilder.AppendLine("Received relics in Treasure Room:");
            foreach (var holder in holders)
            {
                var canClick = await GodotMainThread.RunAsync(() =>
                    GodotObject.IsInstanceValid(holder) && holder.IsEnabled && holder.Visible);
                if (canClick)
                {
                    stringBuilder.AppendLine($"- {TextHelper.SafeLocString(() => holder.Relic.Model.Title)}: {TextHelper.GetRelicDescription(holder.Relic.Model)}");
                    Plugin.LogDebug("Treasure room: clicking relic");
                    await GodotMainThread.ClickAsync(holder);
                    await Task.Delay(500);
                }
            }

            // Wait for the proceed button to become enabled (relic awarding is async)
            for (int i = 0; i < 20; i++)
            {
                var proceedReady = await GodotMainThread.RunAsync(() =>
                    GodotObject.IsInstanceValid(room) && room.ProceedButton?.IsEnabled == true);
                if (proceedReady)
                    break;
                await Task.Delay(500);
            }
            EventLog.DrainAll();
            NeuroIntegration.SendContext(stringBuilder.ToString());

            Plugin.LogDebug("Treasure room: auto-open complete");
        }
        catch (Exception e)
        {
            Plugin.LogError($"Error in AutoOpenTreasure: {e}");
        }
        finally
        {
            AutoClickInProgress = false;
            GameStabilityDetector.ScheduleStabilityCheck();
        }
    }

    private static void FindAll<T>(Node parent, List<T> results) where T : Node
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is T match)
                results.Add(match);
            FindAll(child, results);
        }
    }
}

[HarmonyPatch(typeof(NGameOverContinueButton), "OnEnable")]
public static class GameOverContinueEnabledPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        Plugin.LogDebug("Game over continue button enabled — scheduling stability check");
        GameStabilityDetector.ScheduleStabilityCheck();
    }
}

[HarmonyPatch(typeof(NReturnToMainMenuButton), "OnEnable")]
public static class GameOverMainMenuEnabledPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        Plugin.LogDebug("Game over main menu button enabled — scheduling stability check");
        GameStabilityDetector.ScheduleStabilityCheck();
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public static class MainMenuReadyPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        Plugin.LogDebug("Main menu ready — scheduling stability check");
        GameStabilityDetector.OnScreenTransition();
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.OnSubmenuOpened))]
public static class CharacterSelectOpenedPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        Plugin.LogDebug("Character select screen opened — scheduling stability check");
        GameStabilityDetector.OnScreenTransition();
    }
}
