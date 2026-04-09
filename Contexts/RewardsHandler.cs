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

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return null;

        var buttons = GetEnabledRewardButtons(rewardsScreen);
        var rewards = buttons.Select((b, i) =>
        {
            var reward = b.Reward!;
            return new Dictionary<string, object>
            {
                ["index"] = i,
                ["type"] = reward switch
                {
                    GoldReward => "gold",
                    CardReward => "card",
                    PotionReward => "potion",
                    RelicReward => "relic",
                    CardRemovalReward => "card_removal",
                    _ => "unknown"
                },
                ["description"] = TextHelper.SafeLocString(() => reward.Description)
            };
        }).ToList();

        var overlay = new Dictionary<string, object>
        {
            ["type"] = "rewards",
            ["rewards"] = rewards
        };

        var proceedButton = UiHelper.FindFirst<NProceedButton>((Node)rewardsScreen);
        overlay["canProceed"] = proceedButton?.IsEnabled ?? false;

        return overlay;
    }

    public string GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();
        stringBuilder.AppendLine("## You are on a Rewards screen");

        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null) return stringBuilder.ToString();
        var buttons = GetEnabledRewardButtons(rewardsScreen);
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
        parsedData = data.Data;
        return ExecutionResult.Success();
    }
    public async Task<ActionResult.Result?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        GameStabilityDetector.ResetWasStable();
        return action.Name switch
        {
            "select_reward" => await SelectReward(root, ctx),
            "proceed" => await Proceed(ctx),
            _ => null
        };
    }

    private async Task<ActionResult.Result> SelectReward(JsonElement root, ContextInfo ctx)
    {
        var rewardIndex = root.GetProperty("rewardIndex").GetInt32();
        var rewardsScreen = ctx.RewardsScreen;
        if (rewardsScreen == null)
            return ActionResult.Error("No rewards screen");

        var buttons = GetEnabledRewardButtons(rewardsScreen);
        if (rewardIndex < 0 || rewardIndex >= buttons.Count)
            return ActionResult.Error($"Reward index {rewardIndex} out of range (available: {buttons.Count})");

        await GodotMainThread.ClickAsync(buttons[rewardIndex]);
        Plugin.Log($"Selected reward {rewardIndex}");
        return ActionResult.Ok("Reward selected");
    }

    private async Task<ActionResult.Result> Proceed(ContextInfo ctx)
    {
        // Try proceed button on rewards overlay first
        if (ctx.RewardsScreen != null)
        {
            var button = UiHelper.FindFirst<NProceedButton>((Node)ctx.RewardsScreen);
            if (button != null)
            {
                await GodotMainThread.ClickAsync(button);
                Plugin.Log("Clicked proceed on rewards");
                return ActionResult.Ok("Proceeded");
            }
        }

        return ActionResult.Error("No proceed button found");
    }

    private static List<NRewardButton> GetEnabledRewardButtons(NRewardsScreen screen)
    {
        return UiHelper.FindAll<NRewardButton>((Node)screen)
            .Where(b => b.IsEnabled && b.Reward != null)
            .ToList();
    }


}
