using System;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using Sts2Agent.Contexts;
using Sts2Agent.Utilities;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;

namespace Sts2Agent;

public static class GameStabilityDetector
{
    public static event Action? OnBecameStable;
    private static bool _pendingCheck;
    private static bool _wasStable;
    private static MegaCrit.Sts2.Core.GameActions.ActionExecutor? _subscribedExecutor;
    private static CombatManager? _subscribedCombat;
    private static readonly Callable StabilityCallable = Callable.From(CheckStability);

    public static void Initialize()
    {
        TrySubscribeToActionExecutor();
        Plugin.LogDebug("GameStabilityDetector initialized.");
    }

    private static void OnBeforeAction(GameAction action)
    {
        Plugin.LogDebug($"OnBeforeAction: {action.GetType().Name}, _wasStable was {_wasStable} → false");
        _wasStable = false;
    }

    private static void OnAfterAction(GameAction action)
    {
        Plugin.LogDebug($"OnAfterAction: {action.GetType().Name}, scheduling stability check");
        ScheduleStabilityCheck();
    }

    private static void TrySubscribeToActionExecutor()
    {
        try
        {
            var executor = RunManager.Instance?.ActionExecutor;
            if (executor == null || executor == _subscribedExecutor) return;
            if (_subscribedExecutor != null)
            {
                _subscribedExecutor.BeforeActionExecuted -= OnBeforeAction;
                _subscribedExecutor.AfterActionExecuted -= OnAfterAction;
            }
            executor.BeforeActionExecuted += OnBeforeAction;
            executor.AfterActionExecuted += OnAfterAction;
            _subscribedExecutor = executor;
            Plugin.LogDebug("Subscribed to ActionExecutor events.");
        }
        catch (Exception e)
        {
            Plugin.LogDebug($"ActionExecutor not ready yet: {e.Message}");
        }
    }

    public static void SubscribeToCombat(CombatManager cm)
    {
        TrySubscribeToActionExecutor();
        UnsubscribeFromCombat();
        _subscribedCombat = cm;
        cm.PlayerActionsDisabledChanged += OnCombatStateChanged;
        cm.TurnStarted += OnCombatStateChanged;
        cm.CombatEnded += OnCombatEnded;
    }

    public static void OnActionStarting()
    {
        _wasStable = false;
    }

    /// <summary>
    /// Reset _wasStable so the next CheckStability that finds stable=true
    /// will fire OnBecameStable.  Called by HttpServer after it resets its
    /// signal events and before it schedules a post-action stability check.
    /// </summary>
    public static void ResetWasStable()
    {
        Plugin.LogDebug($"ResetWasStable: _wasStable was {_wasStable}, setting to false");
        _wasStable = false;
    }

    public static void OnHandSelectionEntered()
    {
        Plugin.LogDebug("Hand selection entered — scheduling stability check");
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    public static void OnRoomEntered()
    {
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    public static void OnScreenTransition()
    {
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    public static void OnOverlayChanged()
    {
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    public static void ScheduleStabilityCheck()
    {
        if (_pendingCheck) return;
        _pendingCheck = true;
        GodotMainThread.RunAsync(() => StabilityCallable.Call());
    }

    private static void CheckStability()
    {
        _pendingCheck = false;
        var stable = IsStable();
        if (stable && !_wasStable)
        {
            _wasStable = true;
            Plugin.Log("=== GAME STABLE ===");
            // Plugin.LogDebug(GameStateSerializer.Serialize().ToString());
            OnBecameStable?.Invoke();
        }
        else if (stable && _wasStable)
        {
            Plugin.LogDebug("CheckStability: stable but _wasStable already true — no event fired (expected after initial signal)");
        }
        else if (!stable)
        {
            _wasStable = false;
            // Re-poll: game actions (animations, transitions) may still be running.
            // Schedule another check so we don't depend solely on events.
            ScheduleDelayedCheck();
        }
    }

    private static void OnDelayedCheckTimeout()
    {
        _pendingCheck = false;
        ScheduleStabilityCheck();
    }

    private static void ScheduleDelayedCheck()
    {
        if (_pendingCheck) return;
        _pendingCheck = true;
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Plugin.LogDebug("ScheduleDelayedCheck: SceneTree is null, clearing _pendingCheck to avoid deadlock");
            _pendingCheck = false;
            return;
        }
        var timer = tree.CreateTimer(0.5);
        timer.Timeout += OnDelayedCheckTimeout;
    }

    public static bool IsStable()
    {
        var ctx = GameContext.Resolve();
        if (ctx == null)
        {
            Plugin.LogDebug("IsStable: no context → false");
            return false;
        }

        switch (ctx.Type)
        {
            case ContextType.Unknown:
                Plugin.LogDebug("IsStable: unknown context → false");
                return false;

            case ContextType.Map:
                {
                    var ms = NMapScreen.Instance;
                    if (ms is { IsTravelEnabled: true })
                    {
                        Plugin.LogDebug("IsStable: map screen, travel enabled → true");
                        return true;
                    }
                    // Map is open but not interactive — if travel finished, close it
                    // so the room underneath becomes visible to viewers and agent
                    if (ms is { IsTraveling: false })
                    {
                        Plugin.Log("Map overlay stuck open after travel — closing");
                        ms.Close();
                    }
                    if (ms != null)
                        Plugin.LogDebug($"IsStable: map screen, travel not enabled (traveling={ms?.IsTraveling}) → false");
                    return false;
                }

            case ContextType.BundleSelection:
            case ContextType.CardSelection:
            case ContextType.CrstalBallEvent:
            case ContextType.Rewards:
                Plugin.LogDebug($"IsStable: overlay {ctx.Type} → true");
                return true;

            case ContextType.HandSelection:
                Plugin.LogDebug("IsStable: hand card selection → true");
                return true;

            case ContextType.Combat:
                {
                    var cm = CombatManager.Instance;
                    var result = cm != null
                        && cm.IsPlayPhase
                        && !cm.PlayerActionsDisabled
                        && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null;
                    if (cm != null)
                        Plugin.LogDebug($"IsStable: combat → IsPlayPhase={cm?.IsPlayPhase}, ActionsDisabled={cm?.PlayerActionsDisabled}, RunningAction={RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.GetType().Name ?? "null"} → {result}");
                    return result;
                }

            case ContextType.Event:
                {
                    var evt = ctx.EventRoom?.LocalMutableEvent;
                    if ((evt is AncientEventModel && EventContextHandler.TryAdvanceAncientDialogue()) || evt == null)
                        return false;
                    var evtResult = evt != null && (evt.CurrentOptions.Count > 0 || evt.IsFinished);
                    if (evt != null)
                        Plugin.LogDebug($"IsStable: event → hasEvent={evt != null}, options={evt?.CurrentOptions.Count ?? 0}, finished={evt?.IsFinished} → {evtResult}");
                    return evtResult;
                }

            case ContextType.RestSite:
                if (ctx.RestSiteRoom != null && NRestSiteRoom.Instance is NRestSiteRoom restSite)
                {
                    if (UiHelper.FindFirst<NProceedButton>(restSite) is NProceedButton proceedBtn && proceedBtn.Visible && proceedBtn.IsEnabled)
                    {
                        var proceedEnabled = proceedBtn.IsEnabled;
                        Plugin.LogDebug($"IsStable Restsite: proceed button enabled={proceedEnabled} → {proceedEnabled}");
                        return proceedEnabled;
                    }
                    if (UiHelper.FindAll<NRestSiteButton>(restSite) is IEnumerable<NRestSiteButton> anyBtn && anyBtn.Any(b => b.Visible && b.IsEnabled))
                    {
                        var anyBtnEnabled = anyBtn.Any(b => b.Visible && b.IsEnabled);
                        Plugin.LogDebug($"IsStable Restsite: any button enabled={anyBtnEnabled} → {anyBtnEnabled}");
                        return anyBtnEnabled;
                    }
                }

                Plugin.LogDebug($"IsStable Restsite: {ctx.Type} → false");
                return false;
            case ContextType.Shop:
                Plugin.LogDebug($"IsStable: {ctx.Type} → true");
                return true;

            case ContextType.GameOver:
                {
                    var goScreen = MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Peek()
                        as MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen;
                    if (goScreen == null)
                    {
                        Plugin.LogDebug("IsStable: game over screen not found → false");
                        return false;
                    }
                    var mainMenuBtn = UiHelper.FindFirst<MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NReturnToMainMenuButton>(goScreen);
                    if (mainMenuBtn != null && mainMenuBtn.Visible && mainMenuBtn.IsEnabled)
                    {
                        Plugin.LogDebug("IsStable: game over main menu button ready → true");
                        return true;
                    }
                    var continueBtn = UiHelper.FindFirst<MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverContinueButton>(goScreen);
                    var ready = continueBtn != null && continueBtn.IsEnabled;
                    Plugin.LogDebug($"IsStable: game over continue enabled={ready} → {ready}");
                    return ready;
                }

            case ContextType.MainMenu:
                Plugin.LogDebug("IsStable: main menu → true");
                return true;
            case ContextType.TimelinesEvent:
                Plugin.LogDebug("IsStable: timelines event → true");
                return true;
            case ContextType.CharacterSelect:
                Plugin.LogDebug("IsStable: character select → true");
                return true;

            case ContextType.Treasure:
                {
                    if (TreasureRoomAutoPatch.AutoClickInProgress)
                    {
                        Plugin.LogDebug("IsStable: treasure auto-click in progress → false");
                        return false;
                    }
                    var treasureRoom = TreasureRoomAutoPatch.CurrentRoom;
                    var proceedEnabled = treasureRoom != null
                        && GodotObject.IsInstanceValid(treasureRoom)
                        && treasureRoom.ProceedButton?.IsEnabled == true;
                    Plugin.LogDebug($"IsStable: treasure proceed={proceedEnabled} → {proceedEnabled}");
                    return proceedEnabled;
                }
            default:
                return false;
        }
    }

    private static void OnCombatStateChanged(CombatState _) => ScheduleStabilityCheck();

    private static void OnCombatEnded(CombatRoom _)
    {
        UnsubscribeFromCombat();
        _wasStable = false;
        ScheduleStabilityCheck();
    }

    private static void UnsubscribeFromCombat()
    {
        if (_subscribedCombat == null) return;
        _subscribedCombat.PlayerActionsDisabledChanged -= OnCombatStateChanged;
        _subscribedCombat.TurnStarted -= OnCombatStateChanged;
        _subscribedCombat.CombatEnded -= OnCombatEnded;
        _subscribedCombat = null;
    }
}
