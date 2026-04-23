using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using NeuroSdk.Actions;
using NeuroSdk.Json;
using NeuroSdk.Websocket;
using STS2NeuroIntegration;
using Sts2Agent.Utilities;

namespace Sts2Agent.Contexts;

public class RewardsHandler : IContextHandler<RewardsHandler.Result>, IOnContextSwitch
{
    public class Result : IContextResult
    {
        public NButton Button;
        public ulong RewardButtonId;
        public bool RequiresPotionSlot;
        public string RewardLabel = "reward";
    }

    private sealed record RewardEntry(int Index, string DisplayLabel, string Description, NRewardButton Button);

    private readonly ActionQueue actionQueue = new();
    private readonly HashSet<ulong> _reservedRewardButtonIds = [];
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

        stringBuilder.AppendLine("## You are on a rewards screen");
        stringBuilder.AppendLine("You can claim rewards before proceeding.");
        stringBuilder.AppendLine("Once you proceed, every unclaimed reward is discarded.");
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("**Available rewards:**");
        foreach (var rewardEntry in rewardEntries)
        {
            stringBuilder.AppendLine($"- **[{rewardEntry.Index}] {rewardEntry.DisplayLabel}**: {rewardEntry.Description}");
        }
        return new ContextReturn(stringBuilder.ToString());
    }

    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return new CommandReturn();

        var rewardEntries = GetRewardEntries(rewardsScreen);
        var forceText = new StringBuilder();
        GetForceText(forceText, rewardEntries);

        if (rewardEntries.Count > 0)
        {
            commands.Add(new("claim_reward", "Claim a reward. Use the index of the reward to claim it.", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["reward_index"] = QJS.Type(JsonSchemaType.Integer)
            })));
        }
        bool persistant = true;

        var proceedButton = UiHelper.FindFirst<NProceedButton>((Node)rewardsScreen);
        if (proceedButton?.IsEnabled == true)
            if (rewardEntries.Count > 0)
                commands.Add(new("skip_rewards", "Skip any unclaimed rewards. Be sure to collect the ones you want first."));
            else
            {
                commands.Add(new("proceed", "Proceed from the rewards room"));
                persistant = false;
            }

        return new CommandReturn(commands, persistant, ForceText: forceText.ToString());
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result result, ContextInfo ctx)
    {
        if (!_isRevalidation)
        {
            NeuroIntegration.EndForce();
        }

        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null)
            return ExecutionResult.Failure("No rewards screen");

        if (action.Name == "claim_reward")
        {
            if (_isRevalidation)
            {
                if (result.Button == null)
                {
                    return ExecutionResult.Failure("That reward is no longer available. It was likely already claimed by a previous queued action.");
                }

                if (result.Button is NRewardButton rewardButton && rewardButton.Reward == null)
                {
                    return ExecutionResult.Failure("That reward is no longer available. It was likely already claimed by a previous queued action.");
                }

                if (result.RequiresPotionSlot)
                {
                    var player = LocalContext.GetMe(ctx.RunState!.Players);
                    if (player != null && player.PotionSlots.All(x => x != null))
                    {
                        return ExecutionResult.Failure("Potion slots are full, can't pick up more potions");
                    }
                }

                return ExecutionResult.Success();
            }

            var rewardIndex = data.GetValue("reward_index", -1);
            if (rewardIndex < 0)
                return ExecutionResult.Failure("Missing Parameter: reward_index");

            var rewardEntries = GetRewardEntries(rewardsScreen);
            if (rewardIndex >= rewardEntries.Count)
            {
                return ExecutionResult.Failure($"Reward index {rewardIndex} out of range (remaining rewards: {rewardEntries.Count})");
            }

            var rewardEntry = rewardEntries[rewardIndex];
            result.Button = rewardEntry.Button;
            result.RewardButtonId = rewardEntry.Button.GetInstanceId();
            result.RequiresPotionSlot = rewardEntry.Button.Reward is PotionReward;
            result.RewardLabel = rewardEntry.DisplayLabel;

            if (result.RequiresPotionSlot)
            {
                var player = LocalContext.GetMe(ctx.RunState!.Players);
                if (player != null && player.PotionSlots.All(x => x != null))
                {
                    return ExecutionResult.Failure("Potion slots are full, can't pick up more potions");
                }
            }

            _reservedRewardButtonIds.Add(result.RewardButtonId);
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
            ReleaseReservedReward(result);
            actionQueue.AddToDiscardedActionText($"- couldn't {GetActionDescription(action, result)} due to {queueResult.Message}");
            if (actionQueue.Count == 0)
                actionQueue.SendDiscardedActionText();
            return queueResult;
        }

        var freshCtx = GameContext.Resolve();
        if (freshCtx == null || freshCtx.Type != ContextType.Rewards)
        {
            Plugin.LogDebug($"ActionQueue: context changed while waiting for '{action.Name}'");
            ReleaseReservedReward(result);
            actionQueue.ActionFinished();
            return ExecutionResult.Failure("Context changed, no longer on rewards");
        }

        _isRevalidation = true;
        var revalidation = Validate(action, action.Data, result, freshCtx);
        _isRevalidation = false;
        if (!revalidation.Successful)
        {
            Plugin.LogDebug($"ActionQueue: action '{action.Name}' failed revalidation: {revalidation.Message}");
            ReleaseReservedReward(result);
            actionQueue.ActionFinished();
            actionQueue.AddToDiscardedActionText($"- {GetActionDescription(action, result)} was cancelled due to {revalidation.Message}");
            if (actionQueue.Count == 0)
                actionQueue.SendDiscardedActionText();
            return revalidation;
        }

        actionQueue.MarkExecuting();

        try
        {
            return action.Name switch
            {
                "proceed" or "skip_rewards" => await Proceed(result),
                "claim_reward" => await SelectReward(result),
                _ => null
            };
        }
        finally
        {
            if (action.Name is not ("proceed" or "skip_rewards") && actionQueue.Count <= 1)
            {
                var currentCtx = GameContext.Resolve();
                if (currentCtx?.Type == ContextType.Rewards && currentCtx.RewardsScreen != null)
                {
                    var forceText = new StringBuilder();
                    GetForceText(forceText, GetRewardEntries(currentCtx.RewardsScreen));
                    NeuroIntegration.Reforce(forceText.ToString());
                }
            }
            actionQueue.ActionFinished();
        }
    }

    private async Task<ExecutionResult> SelectReward(Result result)
    {
        try
        {
            await GodotMainThread.ClickAsync(result.Button);
            await Task.Delay(500); // Wait for the reward to be claimed and the button to be disabled/removed
            Plugin.Log($"Selected reward {result.RewardLabel}");
            return ExecutionResult.Success("Reward selected");
        }
        catch
        {
            ReleaseReservedReward(result);
            return ExecutionResult.Failure("Failed to select reward");
        }
    }

    private async Task<ExecutionResult> Proceed(Result result)
    {
        // Try proceed button on rewards overlay first
        actionQueue.Clear();
        _reservedRewardButtonIds.Clear();
        await GodotMainThread.ClickAsync(result.Button);
        await Task.Delay(1000); // Wait for the next room to load
        Plugin.Log("Clicked proceed on rewards");
        GameStabilityDetector.ResetWasStable();
        return ExecutionResult.Success("Proceeded");
    }

    private static List<NRewardButton> GetEnabledRewardButtons(NRewardsScreen screen)
    {
        return UiHelper.FindAll<NRewardButton>((Node)screen)
            .Where(b => b.IsEnabled && b.Reward != null)
            .ToList();
    }

    private void GetForceText(StringBuilder forceText, IReadOnlyList<RewardEntry> rewardEntries)
    {
        if (rewardEntries.Count > 0)
        {
            forceText.AppendLine("Claim rewards or skip the remaining ones.");
            forceText.AppendLine("Available rewards:");
            foreach (var rewardEntry in rewardEntries)
            {
                forceText.AppendLine($"- [{rewardEntry.Index}] {rewardEntry.DisplayLabel}: {rewardEntry.Description}");
            }
            return;
        }

        forceText.AppendLine("All rewards have been claimed. Proceed from the rewards room.");
    }

    private static string GetActionDescription(ConstructedAction action, Result result)
    {
        return action.Name switch
        {
            "claim_reward" => $"claim {result.RewardLabel}",
            "skip_rewards" => "skip the remaining rewards",
            "proceed" => "proceed from rewards",
            _ => action.Name
        };
    }

    private List<RewardEntry> GetRewardEntries(NRewardsScreen screen)
    {
        var rewardEntries = new List<RewardEntry>();
        foreach (var button in GetEnabledRewardButtons(screen))
        {
            if (_reservedRewardButtonIds.Contains(button.GetInstanceId()))
            {
                continue;
            }

            var reward = button.Reward!;
            string description = TextHelper.SafeLocString(() => reward.Description).AsSingleLine();

            rewardEntries.Add(new RewardEntry(
                rewardEntries.Count,
                $"{GetRewardTypeLabel(reward)} reward",
                description,
                button));
        }

        return rewardEntries;
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

    private void ReleaseReservedReward(Result result)
    {
        if (result.RewardButtonId == 0)
        {
            return;
        }

        _reservedRewardButtonIds.Remove(result.RewardButtonId);
        result.RewardButtonId = 0;
    }

    public void OnContextSwitch(ContextType newContext)
    {

        actionQueue.Clear();
        NeuroIntegration.UnregisterAllActions();
        _reservedRewardButtonIds.Clear();
        _isRevalidation = false;
    }
}
