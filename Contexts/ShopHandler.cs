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

namespace Sts2Agent.Contexts;

public class ShopHandler : IContextHandler
{
    public ContextType Type => ContextType.Shop;

    public Dictionary<string, object>? SerializeState(ContextInfo ctx)
    {
        var shopRoom = ctx.MerchantRoom;
        if (shopRoom == null) return null;

        var player = LocalContext.GetMe(ctx.RunState.Players);
        var result = new Dictionary<string, object>
        {
            ["isOpen"] = ctx.ShopIsOpen,
            ["gold"] = player.Gold
        };

        if (!ctx.ShopIsOpen || ctx.ShopItems == null)
        {
            result["items"] = new List<Dictionary<string, object>>();
            return result;
        }

        result["items"] = ctx.ShopItems.Select((entry, i) => SerializeShopEntry(entry, i)).ToList();
        return result;
    }

    public List<ConstructedAction> GetCommands(ContextInfo ctx)
    {
        var commands = new List<ConstructedAction>();

        if (!ctx.ShopIsOpen)
        {
            // commands.Add(new Dictionary<string, object> { ["type"] = "shop_open" });
            // commands.Add(new Dictionary<string, object> { ["type"] = "shop_leave" });
            return commands;
        }

        if (ctx.ShopItems != null)
        {
            for (int i = 0; i < ctx.ShopItems.Count; i++)
            {
                var entry = ctx.ShopItems[i];
                if (entry.EnoughGold)
                {
                    // commands.Add(new Dictionary<string, object>
                    // {
                    //     ["type"] = "shop_buy",
                    //     ["itemIndex"] = i,
                    //     ["name"] = GetEntryName(entry)
                    // });
                }
            }
        }

        // commands.Add(new Dictionary<string, object> { ["type"] = "shop_leave" });
        return commands;
    }

    public async Task<ExecutionResult?>? TryExecute(ConstructedAction action, JsonElement root, ContextInfo ctx)

    {
        return action.Name switch
        {
            "shop_open" => await ShopOpen(),
            "shop_buy" => await ShopBuy(root, ctx),
            "shop_leave" => await ShopLeave(),
            _ => null
        };
    }

    private async Task<ExecutionResult> ShopOpen()
    {
        var nRoom = NMerchantRoom.Instance;
        if (nRoom == null)
            return ExecutionResult.Failure("Not in shop");
        if (nRoom.Inventory?.IsOpen == true)
            return ExecutionResult.Failure("Shop already open");

        var merchantButton = nRoom.MerchantButton;
        if (merchantButton == null)
            return ExecutionResult.Failure("Merchant button not found");

        await GodotMainThread.ClickAsync(merchantButton);
        Plugin.Log("Opened shop inventory");
        return ExecutionResult.Success("Shop opened");
    }

    private async Task<ExecutionResult> ShopBuy(JsonElement root, ContextInfo ctx)
    {
        var itemIndex = root.GetProperty("itemIndex").GetInt32();
        var items = ctx.ShopItems;
        var inv = ctx.ShopInventory;

        if (items == null || inv == null)
            return ExecutionResult.Failure("No shop inventory");

        if (itemIndex < 0 || itemIndex >= items.Count)
            return ExecutionResult.Failure($"Shop item index {itemIndex} out of range (available: {items.Count})");

        var entry = items[itemIndex];
        if (!entry.EnoughGold)
            return ExecutionResult.Failure("Not enough gold");

        await GodotMainThread.RunAsync(() => entry.OnTryPurchaseWrapper(inv));
        await Task.Delay(300);

        Plugin.Log($"Bought shop item {itemIndex}");
        return ExecutionResult.Success("Item purchased");
    }

    private async Task<ExecutionResult> ShopLeave()
    {
        // Shop leave: find proceed button
        var sceneRoot = SceneHelper.GetSceneRoot();
        if (sceneRoot == null)
            return ExecutionResult.Failure("Cannot access scene tree");

        var button = UiHelper.FindFirst<NProceedButton>(sceneRoot);
        if (button != null)
        {
            await GodotMainThread.ClickAsync(button);
            Plugin.Log("Clicked proceed (shop leave)");
            return ExecutionResult.Success("Proceeded");
        }

        return ExecutionResult.Failure("No proceed button found");
    }

    private static Dictionary<string, object> SerializeShopEntry(MerchantEntry entry, int index)
    {
        var result = new Dictionary<string, object>
        {
            ["index"] = index,
            ["cost"] = entry.Cost,
            ["affordable"] = entry.EnoughGold
        };

        if (entry is MerchantCardEntry cardEntry)
        {
            var card = cardEntry.CreationResult.Card;
            result["type"] = "card";
            result["name"] = card.Title.ToString();
            result["description"] = TextHelper.GetCardDescription(card);
        }
        else if (entry is MerchantRelicEntry relicEntry)
        {
            result["type"] = "relic";
            result["name"] = TextHelper.SafeLocString(() => relicEntry.Model.Title);
            result["description"] = TextHelper.GetRelicDescription(relicEntry.Model);
        }
        else if (entry is MerchantPotionEntry potionEntry)
        {
            result["type"] = "potion";
            result["name"] = TextHelper.SafeLocString(() => potionEntry.Model.Title);
            result["description"] = TextHelper.GetPotionDescription(potionEntry.Model);
        }
        else
        {
            // Card removal or other
            result["type"] = "card_removal";
            result["name"] = "Remove Card";
        }

        return result;
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

    public ExecutionResult Validate(ConstructedAction action, ActionJData data, out object? parsedData, ContextInfo? ctx)
    {
        throw new NotImplementedException();
    }

    public string GetContext(ContextInfo ctx)
    {
        throw new NotImplementedException();
    }
}
