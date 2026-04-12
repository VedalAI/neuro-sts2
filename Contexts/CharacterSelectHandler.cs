using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Localization;
using Sts2Agent.Utilities;
using STS2NeuroIntegration;
using NeuroSdk.Websocket;
using NeuroSdk.Actions;
using NeuroSdk.Json;
using System.Text;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace Sts2Agent.Contexts;

public class CharacterSelectHandler : IContextHandler<CharacterSelectHandler.Result>
{
    public class Result : IContextResult
    {
        internal NCharacterSelectButton SelectedCharacter;
        internal NConfirmButton EmbarkButton;
    }
    private static readonly FieldInfo? SelectedButtonField =
        typeof(NCharacterSelectScreen).GetField("_selectedButton", BindingFlags.NonPublic | BindingFlags.Instance);

    public ContextType Type => ContextType.CharacterSelect;

    public string GetContext(ContextInfo ctx)
    {
        StringBuilder starting_character = new();
        var first_character = ctx.CharacterButtons!.FirstOrDefault(c => c.IsSelected);
        first_character ??= ctx.CharacterButtons!.First();
        starting_character.AppendLine($"# Currently Selected Character is: {TextHelper.StripBBCode(first_character.Character.Title.GetFormattedText())}");
        starting_character.RepresentStartingCharacter(first_character.Character);

        return starting_character.ToString();
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        if (ctx.CharacterButtons == null) return commands;

        var character_names = new List<string>();

        for (int i = 0; i < ctx.CharacterButtons.Count; i++)
        {
            var btn = ctx.CharacterButtons[i];
            if (!GodotObject.IsInstanceValid(btn) || btn.IsLocked) continue;
            character_names.Add(GetCharacterName(btn));
        }
        commands.Add(new ConstructedAction("select_character", "Selects a different Character, Use this to select a different Character at the start of your run", QJS.WrapObject(new Dictionary<string, JsonSchema>
        {
            ["character"] = QJS.Enum(character_names)
        }
        )));


        if (ctx.CharacterSelectScreen != null && GodotObject.IsInstanceValid(ctx.CharacterSelectScreen))
        {
            if (ctx.CharacterSelectScreen.GetNode<Control>("ConfirmButton") is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton embarkButton
                && embarkButton.IsEnabled)
                commands.Add(new ConstructedAction("embark", "Start a new Run with the current selected Character"));
        }

        return commands;
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        if (action.Name == "select_character")
        {
            var character_name = data.GetValue<string>("character");
            var btn = ctx?.CharacterButtons?.Find((btn) => GetCharacterName(btn) == character_name);
            if (!GodotObject.IsInstanceValid(btn))
                return ExecutionResult.Failure("Character not found");
            if (btn.IsLocked)
                return ExecutionResult.Failure("Character is locked");

            parsedData.SelectedCharacter = btn;

            return ExecutionResult.Success();
        }
        else if (action.Name == "embark")
        {

            if (ctx.CharacterSelectScreen != null && GodotObject.IsInstanceValid(ctx.CharacterSelectScreen))
            {
                if (ctx.CharacterSelectScreen.GetNode<Control>("ConfirmButton") is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton embarkButton
                    && embarkButton.IsEnabled)
                {

                    parsedData.EmbarkButton = embarkButton;
                    return ExecutionResult.Success();
                }
            }
            return ExecutionResult.Failure("Couldn't find Embark button");
        }
        return ExecutionResult.ModFailure("Unkown Action for Character Selection, only select_character and embark are valid actions");
    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
    {
        if (action.Name == "select_character")
        {

            await GodotMainThread.RunAsync(() => result.SelectedCharacter.Select());

            var name = GetCharacterName(result.SelectedCharacter);
            var character = result.SelectedCharacter.Character;
            StringBuilder character_descriptor = new();
            character_descriptor.AppendLine($"# Selected {name}");
            character_descriptor.RepresentStartingCharacter(character);

            // GameStabilityDetector.ResetWasStable();
            Plugin.Log($"Selected character: {name}");
            NeuroIntegration.SendContext(character_descriptor.ToString());
            return ExecutionResult.Success($"Selected character: {name}");
        }

        if (action.Name == "embark")
        {
            var screen = ctx.CharacterSelectScreen;
            if (screen == null || !GodotObject.IsInstanceValid(screen))
                return ExecutionResult.Failure("Character select screen not found");

            var embarkButton = result.EmbarkButton;
            if (embarkButton == null)
                return ExecutionResult.Failure("Embark button not found");
            if (!embarkButton.IsEnabled)
                return ExecutionResult.Failure("Embark button is not enabled (select a character first)");


            await GodotMainThread.ClickAsync(embarkButton);

            // Wait for the run to start (RunManager becomes active)
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(200);
                if (RunManager.Instance?.IsInProgress == true)
                    break;
            }

            Plugin.Log("Embarked on run");
            return ExecutionResult.Success("Embarked on run");
        }

        return null;
    }

    private static string GetCharacterName(NCharacterSelectButton btn)
    {
        var character = btn.Character;
        if (character == null) return "unknown";
        return TextHelper.StripBBCode(character.Title.GetFormattedText());
    }
}
