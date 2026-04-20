using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using STS2NeuroIntegration;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using NeuroSdk.Json;
using System.Text;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Context;

namespace Sts2Agent.Contexts;

//TODO:Handle potions if potionslots are full. pehaps allow usage or discarding if slots are full
public class RewardsHandler : IContextHandler<RewardsHandler.Result>, IOnContextSwitch
{
    public class Result : IContextResult
    {
        public NButton Button;
    }

    private sealed record RewardIdentity(string ActionName, string DisplayLabel);
    private sealed record RewardEntry(string ActionName, string DisplayLabel, string TypeLabel, string Description, NRewardButton Button);

    private readonly ActionQueue actionQueue = new();
    private readonly Dictionary<Reward, RewardIdentity> _rewardIdentities = [];
    private readonly Dictionary<string, int> _rewardTypeCounts = [];
    private readonly Dictionary<string, int> _rewardTypeNextOrdinal = [];
    private readonly HashSet<string> _reservedRewardActions = [];
    private ulong _trackedRewardsScreenId;
    private bool _isRevalidation;

    public ContextType Type => ContextType.Rewards;

    public ContextReturn GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();

        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return new ContextReturn(stringBuilder.ToString());
        var rewardEntries = GetRewardEntries(rewardsScreen);
        if (rewardEntries.Count <= 0)
        {
            return new ContextReturn(string.Empty);
        }

        stringBuilder.AppendLine("## You are on a Rewards screen");
        stringBuilder.AppendLine("You can claim rewards before proceeding.");
        stringBuilder.AppendLine("Once you proceed, every unclaimed reward is discarded.");
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("**Available rewards:**");
        foreach (var rewardEntry in rewardEntries)
        {
            stringBuilder.AppendLine($"- **{rewardEntry.DisplayLabel}**: {rewardEntry.Description}");
        }

        if (rewardEntries.Count <= 0)
        {
            var button = UiHelper.FindFirst<NProceedButton>(rewardsScreen);
            if (button == null)
                GameStabilityDetector.ResetWasStable(); // the Rewards screen on an event might not be populated yet.
        }
        return new ContextReturn(stringBuilder.ToString());
    }

    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return new CommandReturn();

        SyncRewardActions(rewardsScreen);

        var rewardEntries = GetRewardEntries(rewardsScreen);
        foreach (var rewardEntry in rewardEntries)
        {
            commands.Add(new ConstructedAction(
                rewardEntry.ActionName,
                $"Claim {rewardEntry.DisplayLabel}: {rewardEntry.Description}",
                persistant_action: true));
        }

        var proceedButton = UiHelper.FindFirst<NProceedButton>((Node)rewardsScreen);
        if (proceedButton?.IsEnabled == true)
            if (rewardEntries.Count > 0)
                commands.Add(new("skip_rewards", "This skips any unclaimed rewards! be sure to collect them all if they are interesting", persistant_action: true));
            else
                commands.Add(new("proceed", "Proceed out of the Rewards room", persistant_action: true));

        return new CommandReturn(commands, ForceWindow: false);
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result result, ContextInfo ctx)
    {
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null)
            return ExecutionResult.Failure("No rewards screen");

        if (IsRewardClaimAction(action.Name))
        {
            SyncRewardActions(rewardsScreen);

            if (!_isRevalidation && _reservedRewardActions.Contains(action.Name))
            {
                return ExecutionResult.Failure("That reward is already queued");
            }

            RewardEntry? rewardEntry = FindRewardEntry(rewardsScreen, action.Name);
            if (rewardEntry == null)
            {
                NeuroIntegration.UnregisterAction(action.Name);
                return ExecutionResult.Failure("That reward is no longer available, Either it was claimed already or the rewards screen changed");
            }

            result.Button = rewardEntry.Button;
            if (rewardEntry.Button.Reward is PotionReward)
            {
                var player = LocalContext.GetMe(ctx.RunState!.Players);
                if (player != null && player.PotionSlots.All(x => x != null))
                {
                    return ExecutionResult.Failure("Potion slots are full, can't pick up more potions");
                }
            }

            if (!_isRevalidation)
            {
                _reservedRewardActions.Add(action.Name);
                NeuroIntegration.UnregisterAction(action.Name);
            }
            return ExecutionResult.Success();
        }
        else
        {
            var button = UiHelper.FindFirst<NProceedButton>(rewardsScreen);
            if (button == null)
                return ExecutionResult.Failure("No proceed button found");
            result.Button = button;
        }
        return ExecutionResult.Success();
    }
    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
    {
        var queueResult = await actionQueue.GetExecution();
        if (!queueResult.Successful)
        {
            Plugin.LogDebug($"ActionQueue: action '{action.Name}' was rejected or cancelled: {queueResult.Message}");
            return queueResult;
        }

        var freshCtx = GameContext.Resolve();
        if (freshCtx == null || freshCtx.Type != ContextType.Rewards)
        {
            Plugin.LogDebug($"ActionQueue: context changed while waiting for '{action.Name}'");
            actionQueue.ActionFinished();
            return ExecutionResult.Failure("Context changed, no longer on rewards");
        }

        var freshResult = new Result();
        _isRevalidation = true;
        var revalidation = Validate(action, action.Data, freshResult, freshCtx);
        _isRevalidation = false;
        if (!revalidation.Successful)
        {
            Plugin.LogDebug($"ActionQueue: action '{action.Name}' failed revalidation: {revalidation.Message}");
            _reservedRewardActions.Remove(action.Name);
            actionQueue.ActionFinished();
            return revalidation;
        }

        actionQueue.MarkExecuting();

        try
        {
            return action.Name switch
            {
                "proceed" or "skip_rewards" => await Proceed(freshResult),
                _ when IsRewardClaimAction(action.Name) => await SelectReward(action, freshResult, freshCtx),
                _ => null
            };
        }
        finally
        {
            actionQueue.ActionFinished();
        }
    }

    private async Task<ExecutionResult> SelectReward(ConstructedAction action, Result root, ContextInfo ctx)
    {
        await GodotMainThread.ClickAsync(root.Button);
        if (ctx.RewardsScreen != null)
        {
            SyncRewardActions(ctx.RewardsScreen);
        }
        _reservedRewardActions.Remove(action.Name);
        // GameStabilityDetector.ResetWasStable();
        Plugin.Log($"Selected reward with action {action.Name}");
        return ExecutionResult.Success("Reward selected");
    }

    private async Task<ExecutionResult> Proceed(Result result)
    {
        // Try proceed button on rewards overlay first
        actionQueue.Clear();
        ClearRewardActions();
        _reservedRewardActions.Clear();
        await GodotMainThread.ClickAsync(result.Button);
        Plugin.Log("Clicked proceed on rewards");
        // GameStabilityDetector.ResetWasStable();
        return ExecutionResult.Success("Proceeded");
    }

    private static List<NRewardButton> GetEnabledRewardButtons(NRewardsScreen screen)
    {
        return UiHelper.FindAll<NRewardButton>((Node)screen)
            .Where(b => b.IsEnabled && b.Reward != null)
            .ToList();
    }

    private static bool IsRewardClaimAction(string actionName)
    {
        return actionName.StartsWith("claim_");
    }

    private RewardEntry? FindRewardEntry(NRewardsScreen screen, string actionName)
    {
        return GetRewardEntries(screen).FirstOrDefault(entry => entry.ActionName == actionName);
    }

    private List<RewardEntry> GetRewardEntries(NRewardsScreen screen)
    {
        EnsureRewardIdentityCache(screen);

        var rewardEntries = new List<RewardEntry>();
        foreach (var button in GetEnabledRewardButtons(screen))
        {
            var reward = button.Reward!;
            string typeLabel = GetRewardTypeLabel(reward);
            string description = TextHelper.SafeLocString(() => reward.Description).AsSingleLine();

            if (!_rewardIdentities.TryGetValue(reward, out var rewardIdentity))
            {
                rewardIdentity = AddRewardIdentity(reward, typeLabel);
            }

            rewardEntries.Add(new RewardEntry(
                rewardIdentity.ActionName,
                rewardIdentity.DisplayLabel,
                typeLabel,
                description,
                button));
        }

        return rewardEntries
            .Where(entry => _isRevalidation || !_reservedRewardActions.Contains(entry.ActionName))
            .ToList();
    }

    private void EnsureRewardIdentityCache(NRewardsScreen screen)
    {
        ulong screenId = screen.GetInstanceId();
        if (_trackedRewardsScreenId == screenId)
        {
            return;
        }

        _trackedRewardsScreenId = screenId;
        _rewardIdentities.Clear();
        _rewardTypeCounts.Clear();
        _rewardTypeNextOrdinal.Clear();

        var buttons = GetEnabledRewardButtons(screen);
        foreach (var group in buttons.GroupBy(button => GetRewardTypeLabel(button.Reward!)))
        {
            _rewardTypeCounts[group.Key] = group.Count();
            _rewardTypeNextOrdinal[group.Key] = 0;
        }

        foreach (var button in buttons)
        {
            var reward = button.Reward!;
            AddRewardIdentity(reward, GetRewardTypeLabel(reward));
        }
    }

    private RewardIdentity AddRewardIdentity(Reward reward, string typeLabel)
    {
        bool hasDuplicates = _rewardTypeCounts.GetValueOrDefault(typeLabel) > 1;
        string actionBaseName = $"claim_{TextHelper.GetActionNameFor(typeLabel)}_reward";
        string displayLabel = $"{typeLabel} reward";
        if (hasDuplicates)
        {
            int ordinal = _rewardTypeNextOrdinal[typeLabel] + 1;
            _rewardTypeNextOrdinal[typeLabel] = ordinal;
            actionBaseName += $"_{ordinal}";
            displayLabel += $" #{ordinal}";
        }

        var rewardIdentity = new RewardIdentity(actionBaseName, displayLabel);
        _rewardIdentities[reward] = rewardIdentity;
        return rewardIdentity;
    }

    private static string GetRewardTypeLabel(Reward reward)
    {
        return reward switch
        {
            GoldReward => "Gold",
            CardReward => "Card",
            PotionReward => "Potion",
            RelicReward => "Relic",
            CardRemovalReward => "Card Removal",
            _ => "Unknown"
        };
    }

    private void SyncRewardActions(NRewardsScreen screen)
    {
        var instance = NeuroIntegration.Instance;
        if (instance == null)
        {
            return;
        }

        var activeRewardActionNames = GetRewardEntries(screen)
            .Select(entry => entry.ActionName)
            .ToHashSet();
        var staleActionNames = instance.GlobalActions
            .Where(action => IsRewardClaimAction(action.Name) && !activeRewardActionNames.Contains(action.Name))
            .Select(action => action.Name)
            .ToArray();
        if (staleActionNames.Length > 0)
        {
            NeuroIntegration.UnregisterActions(staleActionNames);
        }
    }

    private static void ClearRewardActions()
    {
        var instance = NeuroIntegration.Instance;
        if (instance == null)
        {
            return;
        }

        var rewardActionNames = instance.GlobalActions
            .Where(action => IsRewardClaimAction(action.Name))
            .Select(action => action.Name)
            .ToArray();
        if (rewardActionNames.Length > 0)
        {
            NeuroIntegration.UnregisterActions(rewardActionNames);
        }
    }

    public void OnContextSwitch(ContextType newContext)
    {

        actionQueue.Clear();
        NeuroIntegration.UnregisterAllActions();
        _trackedRewardsScreenId = 0;
        _rewardIdentities.Clear();
        _rewardTypeCounts.Clear();
        _rewardTypeNextOrdinal.Clear();
        _reservedRewardActions.Clear();
        _isRevalidation = false;
    }
}
