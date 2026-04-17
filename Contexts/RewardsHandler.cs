using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
using System.Runtime.CompilerServices;
using System.Text;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Context;

namespace Sts2Agent.Contexts;

//TODO:Handle potions if potionslots are full. pehaps allow usage or discarding if slots are full
public class RewardsHandler : IContextHandler<RewardsHandler.Result>
{
    public class Result : IContextResult
    {
        public NButton Button;
    }
    public ContextType Type => ContextType.Rewards;

    public string GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();

        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return stringBuilder.ToString();
        var buttons = GetEnabledRewardButtons(rewardsScreen);
        if (buttons.Count <= 0)
        {
            return stringBuilder.ToString();
        }
        stringBuilder.AppendLine("## You are on a Rewards screen");
        stringBuilder.AppendLine("The Following are the available rewards, You can choose as many as you want. The moment you use proceed the rest are discarded and you can't pick them anymore");
        for (int i = 0; i < buttons.Count; i++)
        {
            NRewardButton? rewardbutton = buttons[i];
            var reward = rewardbutton.Reward!;
            var type = reward switch
            {
                GoldReward => "Gold",
                CardReward => "Card",
                PotionReward => "Potion",
                RelicReward => "Relic",
                CardRemovalReward => "Card Removal",
                _ => "unknown"
            };
            stringBuilder.AppendLine($"- [{i}] {type} Reward: {TextHelper.SafeLocString(() => reward.Description)}");
        }
        if (buttons.Count <= 0)
        {
            var button = UiHelper.FindFirst<NProceedButton>(rewardsScreen);
            if (button == null)
                GameStabilityDetector.ResetWasStable(); // the Rewards screen on an event might not be populated yet.
        }
        return stringBuilder.ToString();
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return commands;

        var buttons = GetEnabledRewardButtons(rewardsScreen);
        if (buttons.Count > 0)
        {
            commands.Add(new("select_reward", "Select a reward", QJS.WrapObject(new Dictionary<string, JsonSchema>()
            {
                ["rewardIndex"] = new()
                {
                    Type = JsonSchemaType.Integer,
                    Minimum = 0,
                    Maximum = buttons.Count - 1
                }
            })));
        }

        var proceedButton = UiHelper.FindFirst<NProceedButton>((Node)rewardsScreen);
        if (proceedButton?.IsEnabled == true)
            if (buttons.Count > 0)
                commands.Add(new("skip_rewards", "This skips any unclaimed rewards! be sure to collect them all if they are interesting"));
            else
                commands.Add(new("proceed", "Proceed out of the Rewards room"));

        return commands;
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result result, ContextInfo ctx)
    {
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null)
            return ExecutionResult.Failure("No rewards screen");

        if (action.Name == "select_reward")
        {
            var rewardIndex = data.Data?["rewardIndex"]?.GetValue<int>();
            if (rewardIndex == null)
                return ExecutionResult.Failure("No reward index specified");
            var buttons = GetEnabledRewardButtons(rewardsScreen);
            if (rewardIndex < 0 || rewardIndex >= buttons.Count)
                return ExecutionResult.Failure($"Reward index {rewardIndex} out of range (available: {buttons.Count})");

            var rewardButton = buttons[(int)rewardIndex];
            result.Button = rewardButton;
            if (rewardButton.Reward is PotionReward potionReward)
            {
                var player = LocalContext.GetMe(ctx.RunState!.Players);
                if (player != null && player.PotionSlots.All(x => x != null))
                {
                    return ExecutionResult.Failure("Potion slots are full, can't pick up more potions");
                }
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
        return action.Name switch
        {
            "select_reward" => await SelectReward(result),
            "proceed" or "skip_rewards" => await Proceed(result),
            _ => null
        };
    }

    private async Task<ExecutionResult> SelectReward(Result root)
    {
        await GodotMainThread.ClickAsync(root.Button);
        // GameStabilityDetector.ResetWasStable();
        Plugin.Log($"Selected reward");
        return ExecutionResult.Success("Reward selected");
    }

    private async Task<ExecutionResult> Proceed(Result result)
    {
        // Try proceed button on rewards overlay first
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


}
