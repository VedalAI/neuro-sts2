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

namespace Sts2Agent.Contexts;

public class RewardsHandler : IContextHandler
{
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
        return stringBuilder.ToString();
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return commands;

        var buttons = GetEnabledRewardButtons(rewardsScreen);
        commands.Add(new("select_reward", "Select a reward", QJS.WrapObject(new Dictionary<string, JsonSchema>()
        {
            ["rewardIndex"] = new()
            {
                Type = JsonSchemaType.Integer,
                Minimum = 0,
                Maximum = buttons.Count - 1
            }
        })));

        var proceedButton = UiHelper.FindFirst<NProceedButton>((Node)rewardsScreen);
        if (proceedButton?.IsEnabled == true)
            commands.Add(new("proceed", "Proceed, This skips any unclaimed rewards! be sure to collect them all if they are interesting"));

        return commands;
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        //TODO: Proper Validation
        parsedData = data.Data;
        return ExecutionResult.Success();
    }
    public async Task<ExecutionResult?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        return action.Name switch
        {
            "select_reward" => await SelectReward(root, ctx),
            "proceed" => await Proceed(ctx),
            _ => null
        };
    }

    private async Task<ExecutionResult> SelectReward(JsonElement root, ContextInfo ctx)
    {
        var rewardIndex = root.GetProperty("rewardIndex").GetInt32();
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null)
            return ExecutionResult.Failure("No rewards screen");

        var buttons = GetEnabledRewardButtons(rewardsScreen);
        if (rewardIndex < 0 || rewardIndex >= buttons.Count)
            return ExecutionResult.Failure($"Reward index {rewardIndex} out of range (available: {buttons.Count})");

        await GodotMainThread.ClickAsync(buttons[rewardIndex]);
        GameStabilityDetector.ResetWasStable();
        Plugin.Log($"Selected reward {rewardIndex}");
        return ExecutionResult.Success("Reward selected");
    }

    private async Task<ExecutionResult> Proceed(ContextInfo ctx)
    {
        // Try proceed button on rewards overlay first
        if (ctx.RewardsScreen != null)
        {
            var button = UiHelper.FindFirst<NProceedButton>((Node)ctx.RewardsScreen);
            if (button != null)
            {
                await GodotMainThread.ClickAsync(button);
                Plugin.Log("Clicked proceed on rewards");
                GameStabilityDetector.ResetWasStable();
                return ExecutionResult.Success("Proceeded");
            }
        }

        GameStabilityDetector.ResetWasStable();
        return ExecutionResult.Failure("No proceed button found");
    }

    private static List<NRewardButton> GetEnabledRewardButtons(NRewardsScreen screen)
    {
        return UiHelper.FindAll<NRewardButton>((Node)screen)
            .Where(b => b.IsEnabled && b.Reward != null)
            .ToList();
    }


}
