using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class GameOverHandler : IContextHandler
{
    public ContextType Type => ContextType.GameOver;

    private enum Phase { Continue, MainMenu }

    private Phase GetPhase(NGameOverScreen screen)
    {
        var mainMenuBtn = UiHelper.FindFirst<NReturnToMainMenuButton>(screen);
        if (mainMenuBtn != null && mainMenuBtn.Visible && mainMenuBtn.IsEnabled)
            return Phase.MainMenu;
        return Phase.Continue;
    }

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var result = new Dictionary<string, object>();

        var history = RunManager.Instance?.History;
        if (history == null)
        {
            result["victory"] = false;
            return result;
        }

        result["victory"] = history.Win;
        result["seed"] = history.Seed;
        result["ascension"] = history.Ascension;
        result["run_time"] = history.RunTime;
        result["floor_reached"] = history.MapPointHistory.Sum(act => act.Count);

        if (!history.Win)
        {
            if (history.KilledByEncounter != ModelId.none)
            {
                var encounter = ModelDb.GetByIdOrNull<EncounterModel>(history.KilledByEncounter);
                result["killed_by"] = encounter != null
                    ? TextHelper.SafeLocString(() => encounter.Title)
                    : history.KilledByEncounter.ToString();
            }
            else if (history.KilledByEvent != ModelId.none)
            {
                var evt = ModelDb.GetByIdOrNull<EventModel>(history.KilledByEvent);
                result["killed_by"] = evt != null
                    ? TextHelper.SafeLocString(() => evt.Title)
                    : history.KilledByEvent.ToString();
            }
        }

        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState != null)
        {
            result["score"] = ScoreUtility.CalculateScore(runState, LocalContext.NetId ?? 0, history.Win);
        }

        if (history.Players.Count > 0)
        {
            var player = (LocalContext.NetId.HasValue
                ? history.Players.FirstOrDefault(p => p.Id == LocalContext.NetId.Value)
                : null) ?? history.Players[0];
            var charModel = ModelDb.GetByIdOrNull<CharacterModel>(player.Character);
            result["character"] = charModel != null
                ? TextHelper.SafeLocString(() => charModel.Title)
                : player.Character.ToString();
            result["deck_size"] = player.Deck.Count();
            result["relic_count"] = player.Relics.Count();
        }

        return result;
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();

        var screen = NOverlayStack.Instance?.Peek() as NGameOverScreen;
        if (screen == null)
            return commands;

        var phase = GetPhase(screen);
        if (phase == Phase.MainMenu)
        {
            // commands.Add(new() { ["type"] = "continue" });
        }
        else
        {
            var continueBtn = UiHelper.FindFirst<NGameOverContinueButton>(screen);
            // if (continueBtn != null && continueBtn.IsEnabled)
            //     commands.Add(new() { ["type"] = "continue" });
        }

        return commands;
    }

    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        if (action.Name == "continue")
            return await AdvanceGameOver();
        return null;
    }

    private async Task<ActionResult.Result> AdvanceGameOver()
    {
        var screen = await GodotMainThread.RunAsync(() =>
        {
            var overlay = NOverlayStack.Instance?.Peek();
            return overlay as NGameOverScreen;
        });

        if (screen == null)
            return ActionResult.Error("Game over screen not found");

        var phase = await GodotMainThread.RunAsync(() => GetPhase(screen));

        if (phase == Phase.Continue)
        {
            var continueBtn = await GodotMainThread.RunAsync(() => UiHelper.FindFirst<NGameOverContinueButton>(screen));
            if (continueBtn == null)
                return ActionResult.Error("Continue button not found");

            var enabled = await GodotMainThread.RunAsync(() => continueBtn.IsEnabled);
            if (!enabled)
                return ActionResult.Error("Continue button not yet enabled");

            await GodotMainThread.ClickAsync(continueBtn);
            Plugin.Log("Clicked continue on game over screen");
            return ActionResult.Ok("Clicked continue, summary playing");
        }
        else
        {
            var mainMenuBtn = await GodotMainThread.RunAsync(() => UiHelper.FindFirst<NReturnToMainMenuButton>(screen));
            if (mainMenuBtn == null)
                return ActionResult.Error("Main menu button not found");

            await GodotMainThread.ClickAsync(mainMenuBtn);
            Plugin.Log("Clicked return to main menu on game over screen");
            return ActionResult.Ok("Returning to main menu");
        }
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

}
