
using System.Reflection;
using System.Text;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using MegaCrit.Sts2.Core.Timeline;
using NeuroSdk.Actions;
using NeuroSdk.Json;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using STS2NeuroIntegration;

namespace Sts2Agent.Contexts;

public class TimelinesHandler : IContextHandler<TimelinesHandler.Result>
{
    public class Result : IContextResult
    {
        // Empty result class
        internal NButton ProceedButton;
        internal NEpochSlot EpochButton;
        internal NEpochInspectScreen InspectScreen;
        internal NUnlockScreen UnlockScreen;
    }

    private sealed class PendingEpochUnlock(EpochModel epoch)
    {
        internal EpochModel Epoch { get; } = epoch;
        internal List<UnlockEntry> Entries { get; } = [];
    }

    private readonly record struct UnlockEntry(string Title, string Content);

    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo? CharacterScreenCharacterField = typeof(NUnlockCharacterScreen).GetField("_character", PrivateInstance);
    private static readonly FieldInfo? CardsScreenCardsField = typeof(NUnlockCardsScreen).GetField("_cards", PrivateInstance);
    private static readonly FieldInfo? RelicsScreenRelicsField = typeof(NUnlockRelicsScreen).GetField("_relics", PrivateInstance);
    private static readonly FieldInfo? PotionsScreenPotionsField = typeof(NUnlockPotionsScreen).GetField("_potions", PrivateInstance);
    private static readonly FieldInfo? MiscScreenTextField = typeof(NUnlockMiscScreen).GetField("_textToSet", PrivateInstance);
    private static readonly FieldInfo? TimelineScreenEpochsField = typeof(NUnlockTimelineScreen).GetField("_erasToUnlock", PrivateInstance);
    private static readonly FieldInfo? EpochScreenEpochsField = typeof(NUnlockEpochScreen).GetField("_unlockedEpochs", PrivateInstance);

    private PendingEpochUnlock? _pendingEpochUnlock;

    public ContextType Type => ContextType.TimelinesEvent;
    public ContextReturn GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        // stringBuilder.AppendLine("You are at the Timelines Event");
        if (UiHelper.FindFirst<NTimelineTutorial>(ctx.TimelineScreen) is NTimelineTutorial tutorial)
        {
            stringBuilder.AppendLine(TextHelper.StripBBCode(tutorial.Get(NTimelineTutorial.PropertyName._text).As<MegaRichTextLabel>().Text.AsSingleLine()));
        }
        var tounlockEpochs = UiHelper.FindAll<NEpochSlot>(ctx.TimelineScreen);
        if (tounlockEpochs.Count > 0)
        {
            var tounlock = tounlockEpochs.Where(x => x.State == EpochSlotState.Obtained);
            // stringBuilder.AppendLine($"You have {tounlock.Count()} epochs to unlocked");
        }
        return new ContextReturn(stringBuilder.ToString());
    }

    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        if (UiHelper.FindFirst<NTimelineTutorial>(ctx.TimelineScreen) is not null)
        {
            commands.Add(new("proceed", "Continue"));
        }

        var tounlockEpochs = UiHelper.FindAll<NEpochSlot>(ctx.TimelineScreen);
        if (tounlockEpochs.Count > 0)
        {
            var tounlock = tounlockEpochs.Where(x => x.State == EpochSlotState.Obtained);
            if (tounlock.Any())
            {
                commands.Add(new("unlock_epoch", "Unlock the next obtained epoch. This gives you its rewards and story context for Slay the Spire 2."));
            }
        }
        var inspectScreen = UiHelper.FindFirst<NEpochInspectScreen>(ctx.TimelineScreen);
        if (inspectScreen != null && inspectScreen.Visible)
        {
            commands.Add(new("proceed_epoch", "Close the unlocked epoch"));
        }
        var unlockScreen = UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen);
        if (unlockScreen != null && unlockScreen.Visible)
        {
            var button = UiHelper.FindFirst<NUnlockConfirmButton>(unlockScreen);
            if (button != null)
            {
                commands.Add(new("close_unlock", "Close the unlock screen"));
            }
        }
        var backButton = ctx.TimelineScreen.Get(NTimelineScreen.PropertyName._backButton).As<NBackButton>();
        if (backButton != null && backButton.IsEnabled && backButton.Visible)
        {
            commands.Add(new("back_to_main_menu", "Close the timeline and return to the main menu"));
        }
        return new CommandReturn(commands, true);
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result result, ContextInfo ctx)
    {
        if (action.Name == "proceed")
        {
            var timelineTutorial = UiHelper.FindFirst<NTimelineTutorial>(SceneHelper.GetSceneRoot());
            if (timelineTutorial != null && UiHelper.FindFirst<NAcknowledgeButton>(timelineTutorial) is NAcknowledgeButton acknowledgeButton)
            {
                result.ProceedButton = acknowledgeButton;
                return ExecutionResult.Success();
            }
            return ExecutionResult.Unstable("Couldn't find the proceed button");
        }
        if (action.Name == "unlock_epoch")
        {
            var tounlockEpochs = UiHelper.FindAll<NEpochSlot>(ctx.TimelineScreen);
            if (tounlockEpochs.Count > 0)
            {
                var tounlock = tounlockEpochs.Where(x => x.State == EpochSlotState.Obtained);
                if (tounlock.Any())
                {
                    result.EpochButton = tounlock.First();
                    return ExecutionResult.Success();
                }
            }
            return ExecutionResult.Unstable("No epochs to unlock");
        }
        if (action.Name == "proceed_epoch")
        {
            var inspectScreen = UiHelper.FindFirst<NEpochInspectScreen>(ctx.TimelineScreen);
            if (inspectScreen != null && inspectScreen.Visible)
            {
                result.InspectScreen = inspectScreen;
                return ExecutionResult.Success();
            }
            return ExecutionResult.Unstable("No proceed button found");
        }
        if (action.Name == "close_unlock")
        {

            var unlockScreen = UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen);
            if (unlockScreen != null)
            {
                var button = UiHelper.FindFirst<NUnlockConfirmButton>(unlockScreen);
                if (button != null)
                {
                    result.ProceedButton = button;
                    result.UnlockScreen = unlockScreen;
                    return ExecutionResult.Success();
                }
            }
            return ExecutionResult.ModFailure("Couldn't find the close button on the unlock screen");
        }

        if (action.Name == "back_to_main_menu")
        {
            var backButton = ctx.TimelineScreen.Get(NTimelineScreen.PropertyName._backButton).As<NBackButton>();
            if (backButton != null && backButton.IsEnabled && backButton.Visible)
            {
                result.ProceedButton = backButton;
                return ExecutionResult.Success();
            }
            return ExecutionResult.ModFailure("Couldn't find the Back button on the timeline");
        }

        return ExecutionResult.Unstable("Unknown action");
    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
    {
        await Task.Delay(1000);
        if (action.Name == "proceed" || action.Name == "back_to_main_menu")
        {
            await GodotMainThread.ClickAsync(result.ProceedButton);
            await Task.Delay(1000);// wait for the screen to fade
            return ExecutionResult.Success();
        }
        if (action.Name == "unlock_epoch")
        {
            await GodotMainThread.ClickAsync(result.EpochButton);
            if (result.EpochButton.model is EpochModel epochModel)
            {
                _pendingEpochUnlock = new PendingEpochUnlock(epochModel);
            }
            await Task.Delay(1000);// wait for the screen to fade
            return ExecutionResult.Success();
        }
        if (action.Name == "proceed_epoch")
        {
            await GodotMainThread.RunAsync(result.InspectScreen.Close);
            await Task.Delay(1000);// wait for the screen to fade
            if (_pendingEpochUnlock != null && UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen) is not { Visible: true })
            {
                FlushPendingEpochUnlock();
            }
            return ExecutionResult.Success();
        }
        if (action.Name == "close_unlock")
        {
            AddUnlockEntry(result.UnlockScreen);
            await GodotMainThread.ClickAsync(result.ProceedButton);
            await Task.Delay(1000);// wait for the screen to fade
            var nextUnlockScreen = await WaitForFollowUpUnlockScreen(ctx, result.UnlockScreen);
            if (nextUnlockScreen is NUnlockTimelineScreen)
            {
                AddUnlockEntry(nextUnlockScreen);
                await WaitForUnlockScreensToFinish(ctx);
                FlushPendingEpochUnlock();
                return ExecutionResult.Success();
            }

            if (nextUnlockScreen is null || !nextUnlockScreen.Visible)
            {
                FlushPendingEpochUnlock();
            }
            return ExecutionResult.Success();
        }
        return null;
    }

    private static T? GetFieldValue<T>(FieldInfo? field, object instance)
    {
        if (field?.GetValue(instance) is T value)
        {
            return value;
        }
        return default;
    }

    private void AddUnlockEntry(NUnlockScreen unlockScreen)
    {
        if (_pendingEpochUnlock == null)
        {
            return;
        }

        UnlockEntry? entry = CreateUnlockEntry(unlockScreen);
        if (entry is { Content.Length: > 0 } unlockEntry)
        {
            _pendingEpochUnlock.Entries.Add(unlockEntry);
        }
    }

    private UnlockEntry? CreateUnlockEntry(NUnlockScreen unlockScreen)
    {
        return unlockScreen switch
        {
            NUnlockCharacterScreen characterScreen => CreateCharacterUnlockEntry(characterScreen),
            NUnlockCardsScreen cardsScreen => CreateCardsUnlockEntry(cardsScreen),
            NUnlockRelicsScreen relicsScreen => CreateRelicsUnlockEntry(relicsScreen),
            NUnlockPotionsScreen potionsScreen => CreatePotionsUnlockEntry(potionsScreen),
            NUnlockMiscScreen miscScreen => CreateMiscUnlockEntry(miscScreen),
            NUnlockTimelineScreen timelineScreen => CreateTimelineUnlockEntry(timelineScreen),
            NUnlockEpochScreen epochScreen => CreateEpochsUnlockEntry(epochScreen),
            _ => null
        };
    }

    private static UnlockEntry? CreateCharacterUnlockEntry(NUnlockCharacterScreen unlockScreen)
    {
        CharacterModel? character = GetFieldValue<CharacterModel>(CharacterScreenCharacterField, unlockScreen);
        if (character == null)
        {
            return null;
        }

        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine(TextHelper.StripBBCode(character.Title.GetFormattedText()));
        stringBuilder.RepresentStartingCharacter(character);
        return new UnlockEntry("Character", stringBuilder.ToString().TrimEnd());
    }

    private static UnlockEntry? CreateCardsUnlockEntry(NUnlockCardsScreen unlockScreen)
    {
        IReadOnlyList<CardModel>? cards = GetFieldValue<IReadOnlyList<CardModel>>(CardsScreenCardsField, unlockScreen);
        if (cards == null || cards.Count == 0)
        {
            return null;
        }

        StringBuilder stringBuilder = new();
        stringBuilder.RepresentDeck(cards);
        return new UnlockEntry("Cards", stringBuilder.ToString().TrimEnd());
    }

    private static UnlockEntry? CreateRelicsUnlockEntry(NUnlockRelicsScreen unlockScreen)
    {
        IReadOnlyList<RelicModel>? relics = GetFieldValue<IReadOnlyList<RelicModel>>(RelicsScreenRelicsField, unlockScreen);
        if (relics == null || relics.Count == 0)
        {
            return null;
        }

        StringBuilder stringBuilder = new();
        stringBuilder.RepresentRelics(relics);
        return new UnlockEntry("Relics", stringBuilder.ToString().TrimEnd());
    }

    private static UnlockEntry? CreatePotionsUnlockEntry(NUnlockPotionsScreen unlockScreen)
    {
        IReadOnlyList<PotionModel>? potions = GetFieldValue<IReadOnlyList<PotionModel>>(PotionsScreenPotionsField, unlockScreen);
        if (potions == null || potions.Count == 0)
        {
            return null;
        }

        StringBuilder stringBuilder = new();
        foreach (var potionGroup in potions.GroupBy(potion => TextHelper.StripBBCode(potion.Title.GetFormattedText())))
        {
            PotionModel firstPotion = potionGroup.First();
            stringBuilder.AppendLine($"- {potionGroup.Count()}x {TextHelper.StripBBCode(firstPotion.Title.GetFormattedText())} \"{TextHelper.GetPotionDescription(firstPotion)}\"");
        }
        return new UnlockEntry("Potions", stringBuilder.ToString().TrimEnd());
    }

    private static UnlockEntry? CreateMiscUnlockEntry(NUnlockMiscScreen unlockScreen)
    {
        string? text = GetFieldValue<string>(MiscScreenTextField, unlockScreen);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new UnlockEntry("Misc", TextHelper.StripBBCode(text));
    }

    private static UnlockEntry? CreateTimelineUnlockEntry(NUnlockTimelineScreen unlockScreen)
    {
        List<EpochSlotData>? epochSlots = GetFieldValue<List<EpochSlotData>>(TimelineScreenEpochsField, unlockScreen);
        if (epochSlots == null || epochSlots.Count == 0)
        {
            return null;
        }

        StringBuilder stringBuilder = new();
        foreach (EpochSlotData epochSlot in epochSlots.OrderBy(slot => slot.EraPosition))
        {
            stringBuilder.AppendLine($"- {GetEpochHeading(epochSlot.Model, markdownHeading: false)}");
            string slotText = GetEpochSlotText(epochSlot.Model, isRevealed: false);
            if (!string.IsNullOrWhiteSpace(slotText))
            {
                stringBuilder.AppendLine($"  {slotText}");
            }
        }
        return new UnlockEntry("Timeline", stringBuilder.ToString().TrimEnd());
    }

    private static UnlockEntry? CreateEpochsUnlockEntry(NUnlockEpochScreen unlockScreen)
    {
        IReadOnlyList<EpochModel>? epochs = GetFieldValue<IReadOnlyList<EpochModel>>(EpochScreenEpochsField, unlockScreen);
        if (epochs == null || epochs.Count == 0)
        {
            return null;
        }

        StringBuilder stringBuilder = new();
        foreach (EpochModel epoch in epochs)
        {
            stringBuilder.AppendLine($"- {GetEpochHeading(epoch, markdownHeading: false)}");
            string slotText = GetEpochSlotText(epoch, isRevealed: false);
            if (!string.IsNullOrWhiteSpace(slotText))
            {
                stringBuilder.AppendLine($"  {slotText}");
            }
        }
        return new UnlockEntry("Epochs", stringBuilder.ToString().TrimEnd());
    }

    private static string GetEpochHeading(EpochModel epoch, bool markdownHeading = true)
    {
        string prefix = markdownHeading ? "## " : "";
        string chapterTitle = TextHelper.StripBBCode(epoch.Title.GetFormattedText());
        if (epoch.ChapterIndex > 0)
        {
            return $"{prefix}Chapter {epoch.ChapterIndex} - {chapterTitle}";
        }

        return $"{prefix}{chapterTitle}";
    }

    private static string GetEpochSlotText(EpochModel epoch, bool isRevealed)
    {
        LocString unlockInfo = epoch.UnlockInfo;
        unlockInfo.Add("IsRevealed", variable: isRevealed);
        return TextHelper.StripBBCode(unlockInfo.GetFormattedText());
    }

    private void FlushPendingEpochUnlock()
    {
        if (_pendingEpochUnlock == null)
        {
            return;
        }

        string message = BuildEpochUnlockMessage(_pendingEpochUnlock);
        _pendingEpochUnlock = null;
        if (!string.IsNullOrWhiteSpace(message))
        {
            NeuroIntegration.SendContext(message);
        }
    }

    private static string BuildEpochUnlockMessage(PendingEpochUnlock pendingEpochUnlock)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine(GetEpochHeading(pendingEpochUnlock.Epoch));

        string epochText = GetEpochSlotText(pendingEpochUnlock.Epoch, isRevealed: true);
        if (!string.IsNullOrWhiteSpace(epochText))
        {
            stringBuilder.AppendLine(epochText);
        }

        if (pendingEpochUnlock.Entries.Count > 0)
        {
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("## Unlocks:");
            foreach (UnlockEntry entry in pendingEpochUnlock.Entries)
            {
                stringBuilder.AppendLine($"### {entry.Title}");
                stringBuilder.AppendLine(entry.Content);
                stringBuilder.AppendLine();
            }
        }

        return stringBuilder.ToString().TrimEnd();
    }

    private static async Task<NUnlockScreen?> WaitForFollowUpUnlockScreen(ContextInfo ctx, NUnlockScreen closingScreen)
    {
        ulong closingScreenId = GodotObject.IsInstanceValid(closingScreen) ? closingScreen.GetInstanceId() : 0;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            NUnlockScreen? unlockScreen = UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen);
            if (unlockScreen == null || !unlockScreen.Visible)
            {
                continue;
            }

            if (closingScreenId != 0 && unlockScreen.GetInstanceId() == closingScreenId)
            {
                continue;
            }

            return unlockScreen;
        }

        return UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen);
    }

    private static async Task WaitForUnlockScreensToFinish(ContextInfo ctx)
    {
        for (int i = 0; i < 40; i++)
        {
            NUnlockScreen? unlockScreen = UiHelper.FindFirst<NUnlockScreen>(ctx.TimelineScreen);
            if (unlockScreen == null || !unlockScreen.Visible)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

}
