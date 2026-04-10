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

namespace Sts2Agent.Contexts;

public class CharacterSelectHandler : IContextHandler
{
    private static readonly FieldInfo? SelectedButtonField =
        typeof(NCharacterSelectScreen).GetField("_selectedButton", BindingFlags.NonPublic | BindingFlags.Instance);

    public ContextType Type => ContextType.CharacterSelect;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        if (ctx.CharacterButtons == null) return null;

        var characters = new List<Dictionary<string, object>>();
        for (int i = 0; i < ctx.CharacterButtons.Count; i++)
        {
            var btn = ctx.CharacterButtons[i];
            if (!GodotObject.IsInstanceValid(btn)) continue;
            characters.Add(new Dictionary<string, object>
            {
                ["index"] = i,
                ["name"] = GetCharacterName(btn),
                ["locked"] = btn.IsLocked
            });
        }

        var result = new Dictionary<string, object>
        {
            ["characters"] = characters
        };

        // Show which character is currently selected
        if (ctx.CharacterSelectScreen != null && SelectedButtonField != null)
        {
            var selected = SelectedButtonField.GetValue(ctx.CharacterSelectScreen) as NCharacterSelectButton;
            if (selected != null && GodotObject.IsInstanceValid(selected))
                result["selected"] = GetCharacterName(selected);
        }

        return result;
    }


    public string GetContext(ContextInfo ctx)
    {
        StringBuilder starting_character = new();
        var first_character = ctx.CharacterButtons!.FirstOrDefault(c => c.IsSelected);
        first_character ??= ctx.CharacterButtons!.First();
        starting_character.AppendLine($"# Selected {TextHelper.StripBBCode(first_character.Character.Title.GetFormattedText())}");
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
        commands.Add(new ConstructedAction("select_character", "Selects a new Character", QJS.WrapObject(new Dictionary<string, JsonSchema>
        {
            ["character"] = QJS.Enum(character_names)
        }
        )));


        if (ctx.CharacterSelectScreen != null && GodotObject.IsInstanceValid(ctx.CharacterSelectScreen))
        {
            if (ctx.CharacterSelectScreen.GetNode<Godot.Control>("ConfirmButton") is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton embarkButton
                && embarkButton.IsEnabled)
                commands.Add(new ConstructedAction("embark", "Start a new Run with the current selected Character"));
        }

        return commands;
    }


    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        parsedData = data.Data;
        if (action.Name == "select_character")
        {
            var has_character_unlocked = ctx?.CharacterButtons?.Find((btn) => data.GetValue<string>("character") == GetCharacterName(btn));
            if (has_character_unlocked == null)
            {
                return ExecutionResult.Failure($"Couldn't find character with name: {data.GetValue<string>("character")}");
            }
            else
            {
                return ExecutionResult.Success();
            }
        }
        else if (action.Name == "embark")
        {

            if (ctx.CharacterSelectScreen != null && GodotObject.IsInstanceValid(ctx.CharacterSelectScreen))
            {
                if (ctx.CharacterSelectScreen.GetNode<Godot.Control>("ConfirmButton") is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton embarkButton
                    && embarkButton.IsEnabled)
                    return ExecutionResult.Success();
            }
            return ExecutionResult.Failure("Couldn't find Embark button");
        }
        return ExecutionResult.ModFailure("Unkown Action for Character Selection, only select_character and embark are valid actions");
    }
    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)
    {
        if (action.Name == "select_character")
        {
            var character_name = root.GetProperty("character").GetString();
            var btn = ctx?.CharacterButtons?.Find((btn) => GetCharacterName(btn) == character_name);
            if (!GodotObject.IsInstanceValid(btn))
                return ActionResult.Error("Character button is no longer valid");
            if (btn.IsLocked)
                return ActionResult.Error("Character is locked");

            await GodotMainThread.RunAsync(() => btn.Select());

            var name = GetCharacterName(btn);
            var character = btn.Character;
            StringBuilder character_descriptor = new();
            character_descriptor.AppendLine($"# Selected {name}");
            character_descriptor.RepresentStartingCharacter(character);

            GameStabilityDetector.ResetWasStable();
            Plugin.Log($"Selected character: {name}");
            NeuroIntegration.SendContext(character_descriptor.ToString());
            return ActionResult.Ok($"Selected character: {name}");
        }

        if (action.Name == "embark")
        {
            var screen = ctx.CharacterSelectScreen;
            if (screen == null || !GodotObject.IsInstanceValid(screen))
                return ActionResult.Error("Character select screen not found");

            var embarkButton = await GodotMainThread.RunAsync(() =>
                screen.GetNode<Godot.Control>("ConfirmButton") as MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton);
            if (embarkButton == null)
                return ActionResult.Error("Embark button not found");
            if (!embarkButton.IsEnabled)
                return ActionResult.Error("Embark button is not enabled (select a character first)");


            await GodotMainThread.ClickAsync(embarkButton);

            // Wait for the run to start (RunManager becomes active)
            for (int i = 0; i < 100; i++)
            {
                await Task.Delay(200);
                if (RunManager.Instance?.IsInProgress == true)
                    break;
            }

            Plugin.Log("Embarked on run");
            return ActionResult.Ok("Embarked on run");
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
