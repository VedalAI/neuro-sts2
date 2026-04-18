using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using Sts2Agent.Utilities;
using STS2NeuroIntegration;
using NeuroSdk.Websocket;
using NeuroSdk.Actions;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using System.Text;

namespace Sts2Agent.Contexts;

public class ShopHandler : IContextHandler<ShopHandler.Result>
{
    public class Result : IContextResult
    {
        public MerchantEntry BuyItem;
        internal NButton Button;
    }
    public ContextType Type => ContextType.Shop;

    public ContextReturn GetContext(ContextInfo ctx)
    {
        StringBuilder stringBuilder = new();

        if (!ctx.ShopIsOpen)
        {
            stringBuilder.AppendLine("You reached a shop");
            return new ContextReturn(stringBuilder.ToString());
        }
        var player = LocalContext.GetMe(ctx.RunState.Players);
        stringBuilder.AppendLine($"You are inside of a shop. You have {player!.Gold} gold to spend");
        stringBuilder.AppendLine("In the shop you can buy cards,relics or remove a card from your deck");
        stringBuilder.AppendLine("Once you are done with all your purchases you can leave the shop and proceed with your adventure");
        return new ContextReturn(stringBuilder.ToString());
    }
    public CommandReturn GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();

        if (!ctx.ShopIsOpen)
        {
            commands.Add(new("shop_open", "Open the Shop"));
            commands.Add(new("shop_leave", "Leave the Shop"));
            return new CommandReturn(commands);
        }

        if (ctx.ShopItems != null)
        {
            foreach (var entry in ctx.ShopItems)
            {
                if (entry.EnoughGold)
                {
                    commands.Add(new($"shop_buy_{TextHelper.GetActionNameFor(GetEntryName(entry))}", $"Cost {entry.Cost} gold, Description: \"{GetEntryDescription(entry).AsSingleLine()}\""));
                }
            }
        }
        commands.Add(new("shop_leave", "Leave the Shop"));

        return new CommandReturn(commands);
    }

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result parsedData, ContextInfo ctx)
    {
        if (action.Name == "shop_open")
        {

            var nRoom = NMerchantRoom.Instance;
            if (nRoom == null)
                return ExecutionResult.Failure("Not in shop");
            if (nRoom.Inventory?.IsOpen == true)
                return ExecutionResult.Failure("Shop already open");

            var merchantButton = nRoom.MerchantButton;
            if (merchantButton == null)
                return ExecutionResult.Failure("Merchant button not found");
            parsedData.Button = merchantButton;

        }
        else if (action.Name == "shop_leave")
        {
            var sceneRoot = SceneHelper.GetSceneRoot();
            if (sceneRoot == null)
                return ExecutionResult.Failure("Cannot access scene tree");

            var button = UiHelper.FindFirst<NProceedButton>(sceneRoot);
            if (button == null)
                return ExecutionResult.Failure("Cannot find Leave button");
            parsedData.Button = button;

        }
        else if (action.Name.StartsWith("shop_buy_"))
        {
            var itemIndex = action.Name.Replace("shop_buy_", "");
            if (string.IsNullOrWhiteSpace(itemIndex))
                return ExecutionResult.Failure("Trying to buy a non item");
            var items = ctx.ShopItems;
            var inv = ctx.ShopInventory;

            if (items == null || inv == null)
                return ExecutionResult.Failure("No shop inventory");

            var entry = items.Find(x => TextHelper.GetActionNameFor(GetEntryName(x)) == itemIndex);
            if (entry == null)
                return ExecutionResult.Failure("Item not found");
            if (!entry.EnoughGold)
                return ExecutionResult.Failure("Not enough gold");
            parsedData.BuyItem = entry;

        }
        return ExecutionResult.Success();
    }

    public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)

    {
        if (action.Name == "shop_open") return await ShopOpen(result);
        if (action.Name.StartsWith("shop_buy_")) return await ShopBuy(result, ctx);
        if (action.Name == "shop_leave") return await ShopLeave(result);
        return ExecutionResult.Failure("Invalid shop action!");

    }

    private async Task<ExecutionResult> ShopOpen(Result result)
    {
        await GodotMainThread.ClickAsync(result.Button);
        Plugin.Log("Opened shop inventory");
        return ExecutionResult.Success("Shop opened");
    }

    private async Task<ExecutionResult> ShopBuy(Result root, ContextInfo ctx)
    {

        await GodotMainThread.RunAsync(() => root.BuyItem.OnTryPurchaseWrapper(ctx.ShopInventory));
        await Task.Delay(300);
        Plugin.Log($"Bought shop item {root.BuyItem}");
        return ExecutionResult.Success("Item purchased");
    }

    private async Task<ExecutionResult> ShopLeave(Result result)
    {
        // Shop leave: find proceed button
        await GodotMainThread.ClickAsync(result.Button);
        Plugin.Log("Clicked proceed (shop leave)");
        return ExecutionResult.Success("Proceeded");
    }


    private static string GetEntryName(MerchantEntry entry)
    {
        if (entry is MerchantCardEntry cardEntry)
            return cardEntry.CreationResult.Card.Title.ToString();
        if (entry is MerchantRelicEntry relicEntry)
            return TextHelper.SafeLocString(() => relicEntry.Model.Title);
        if (entry is MerchantPotionEntry potionEntry)
            return TextHelper.SafeLocString(() => potionEntry.Model.Title);
        return "Remove Card";
    }
    private static string GetEntryDescription(MerchantEntry entry)
    {
        if (entry is MerchantCardEntry cardEntry)
            return TextHelper.GetCardDescription(cardEntry.CreationResult.Card);
        if (entry is MerchantRelicEntry relicEntry)
            return TextHelper.GetRelicDescription(relicEntry.Model);
        if (entry is MerchantPotionEntry potionEntry)
            return TextHelper.GetPotionDescription(potionEntry.Model);
        return "???";
    }
}
