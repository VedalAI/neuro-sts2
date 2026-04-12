using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using System.Text;
using MegaCrit.Sts2.Core.Context;

namespace Sts2Agent.Contexts;

public class MainMenuHandler : IContextHandler<MainMenuHandler.Result>
{
    public class Result : IContextResult
    {

    }
    public ContextType Type => ContextType.MainMenu;

    public string GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.Append("You are on the Main Menu of Slay the Spire 2, ");
        if (SaveManager.Instance.HasRunSave)
            stringBuilder.AppendLine("You have a already ongoing Run, you can choose to continue your last adventure or abandon it to start fresh with a new character");
        else
            stringBuilder.AppendLine("Start a new Run to select a Character and Start your Adventure! And Conquer the Spire");
        return stringBuilder.ToString();
    }
    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();

        var sceneRoot = SceneHelper.GetSceneRoot();
        var mainMenu = sceneRoot != null ? UiHelper.FindFirst<NMainMenu>(sceneRoot) : null;
        if (mainMenu == null) return commands;

        if (SaveManager.Instance.HasRunSave)
        {
            var continueBtn = mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/ContinueButton");
            if (continueBtn != null && continueBtn.IsEnabled)
                commands.Add(new("continue_run", "Continue your Last run"));

            var abandonBtn = mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/AbandonRunButton");
            if (abandonBtn != null && abandonBtn.IsEnabled)
                commands.Add(new("abandon_run", "Abandon your Last run"));
        }
        else
        {
            var spButton = mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/SingleplayerButton");
            if (spButton != null && spButton.IsEnabled)
                commands.Add(new("start_run", "Start a new run"));
        }

        return commands;
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        var sceneRoot = SceneHelper.GetSceneRoot();
        var mainMenu = sceneRoot != null ? UiHelper.FindFirst<NMainMenu>(sceneRoot) : null;
        if (mainMenu == null) return ExecutionResult.Failure("Not in Main Menu can't call this action");
        switch (action.Name)
        {
            case "continue_run":
                var continueBtn = mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/ContinueButton");
                if (continueBtn != null && continueBtn.IsEnabled)
                    return ExecutionResult.Success();
                else
                    return ExecutionResult.ModFailure("Can't continue run if no previous run exists");
            case "abandon_run":
                var abandonBtn = mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/AbandonRunButton");
                if (abandonBtn != null && abandonBtn.IsEnabled)
                    return ExecutionResult.Success();
                else
                    return ExecutionResult.ModFailure("Can't abandon run if no previous run exists");
            case "start_run":
                var spButton = mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/SingleplayerButton");
                if (spButton != null && spButton.IsEnabled)
                    return ExecutionResult.Success();
                else
                    return ExecutionResult.ModFailure("can't start run. A run is already active");
        }

        return ExecutionResult.Failure("Unknown Action called in Main Menu");
    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
    {
        Plugin.LogDebug($"Action: {action},result {result}, contextinfo {ctx}");
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ExecutionResult.Failure("Cannot access scene tree");

        var mainMenu = UiHelper.FindFirst<NMainMenu>(sceneRoot);
        if (mainMenu == null)
            return ExecutionResult.Failure("Main menu not found");

        if (action.Name == "continue_run")
        {
            var continueBtn = await GodotMainThread.RunAsync(() =>
                mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/ContinueButton"));
            if (continueBtn == null || !continueBtn.IsEnabled)
                return ExecutionResult.Failure("Continue button not available");

            await GodotMainThread.ClickAsync(continueBtn);

            // Wait for the run to load
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(200);
                if (RunManager.Instance?.IsInProgress == true)
                    break;
            }

            Plugin.Log("Continued saved run");
            return ExecutionResult.Success("Continued saved run");
        }

        if (action.Name == "abandon_run")
        {
            var abandonBtn = await GodotMainThread.RunAsync(() =>
                mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/AbandonRunButton"));
            if (abandonBtn == null || !abandonBtn.IsEnabled)
                return ExecutionResult.Failure("Abandon run button not available");

            await GodotMainThread.ClickAsync(abandonBtn);

            // The abandon button shows a confirmation popup — find and click "Yes"
            await Task.Delay(500);
            var yesBtn = await GodotMainThread.RunAsync(() =>
            {
                var popup = UiHelper.FindFirst<NAbandonRunConfirmPopup>(sceneRoot);
                return popup?.GetNode<NClickableControl>("VerticalPopup/YesButton");
            });

            if (yesBtn != null)
            {
                await GodotMainThread.ClickAsync(yesBtn);
                await Task.Delay(500);
            }

            Plugin.Log("Abandoned saved run");
            // GameStabilityDetector.ResetWasStable();
            return ExecutionResult.Success("Abandoned saved run");
        }

        if (action.Name == "start_run")
        {
            // Click the singleplayer button
            var spButton = await GodotMainThread.RunAsync(() =>
                mainMenu.GetNode<NClickableControl>("MainMenuTextButtons/SingleplayerButton"));
            if (spButton == null)
                return ExecutionResult.Failure("Singleplayer button not found");
            if (!spButton.IsEnabled)
                return ExecutionResult.Failure("Singleplayer button is not enabled");

            await GodotMainThread.ClickAsync(spButton);
            await Task.Delay(500);

            // Check if we landed on character select directly (first-time player)
            var charScreen = await GodotMainThread.RunAsync(() =>
                UiHelper.FindFirst<NCharacterSelectScreen>(sceneRoot));
            if (charScreen != null && charScreen.Visible)
            {
                Plugin.Log("Navigated directly to character select");
                return ExecutionResult.Success("Navigated to character select");
            }

            // Click the Standard button on the singleplayer submenu
            var spSubmenu = await GodotMainThread.RunAsync(() =>
                UiHelper.FindFirst<NSingleplayerSubmenu>(sceneRoot));
            if (spSubmenu == null || !spSubmenu.Visible)
                return ExecutionResult.Failure("Singleplayer submenu not found");

            var stdButton = await GodotMainThread.RunAsync(() =>
                spSubmenu.GetNode<NClickableControl>("StandardButton"));
            if (stdButton == null)
                return ExecutionResult.Failure("Standard button not found");

            await GodotMainThread.ClickAsync(stdButton);

            // Wait for character select screen to appear
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                var cs = await GodotMainThread.RunAsync(() =>
                    UiHelper.FindFirst<NCharacterSelectScreen>(sceneRoot));
                if (cs != null && cs.Visible)
                {
                    Plugin.Log("Navigated to character select via singleplayer submenu");
                    await Task.Delay(100);

                    return ExecutionResult.Success("Navigated to character select");
                }
            }

            return ExecutionResult.Failure("Timed out waiting for character select screen");
        }

        return null;
    }

}
