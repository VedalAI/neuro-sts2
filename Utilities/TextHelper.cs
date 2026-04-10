using System;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace Sts2Agent.Utilities;

public static class TextHelper
{
    private static readonly Regex ImgTagRegex = new(@"\[img[^\]]*\](.*?)\[/img\]", RegexOptions.Compiled);
    private static readonly Regex BbCodeRegex = new(@"\[/?[^\]]+\]", RegexOptions.Compiled);

    public static string StripBBCode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        text = ImgTagRegex.Replace(text, match => GetLocalizedIconText(match.Groups[1].Value));
        return BbCodeRegex.Replace(text, "").Replace("\n", " ").Trim();
    }

    public static string GetUnformatedText(this LocString locString)
    {
        return StripBBCode(locString.GetFormattedText());
    }

    public static string SafeLocString(Func<object> getter)
    {
        try
        {
            var val = getter();
            if (val is LocString loc)
                return StripBBCode(loc.GetFormattedText());
            return StripBBCode(val?.ToString() ?? "");
        }
        catch
        {
            return "???";
        }
    }
    public static string GetCharacterDescription(CharacterModel character)
    {
        try
        {
            var table = LocManager.Instance.GetTable("characters");
            return StripBBCode(table.GetRawText(character.CharacterSelectDesc));
        }
        catch
        {
            return SafeLocString(() => character.CharacterSelectDesc);
        }
    }

    public static string GetCardDescription(CardModel card)
    {
        try
        {
            LocString desc = card.Description;
            card.DynamicVars.AddTo(desc);
            desc.Add("OnTable", true);
            desc.Add("InCombat", CombatManager.Instance?.IsInProgress ?? false);
            return StripBBCode(desc.GetFormattedText());
        }
        catch
        {
            Plugin.LogError($"Couldn't resolve Card description for card: {card.Title}");
            return SafeLocString(() => card.Description);
        }
    }

    public static string? GetCardDescriptionFromNode(NCard cardNode)
    {
        try
        {
            var label = cardNode.GetNode<Godot.RichTextLabel>("%DescriptionLabel");
            if (label == null) return null;
            var text = label.Text;
            if (string.IsNullOrEmpty(text)) return null;
            return StripBBCode(text);
        }
        catch
        {
            return null;
        }
    }

    public static string GetRelicDescription(RelicModel relic)
    {
        try
        {
            return StripBBCode(relic.DynamicDescription.GetFormattedText());
        }
        catch
        {
            return SafeLocString(() => relic.DynamicDescription);
        }
    }

    public static string GetPotionDescription(PotionModel potion)
    {
        try
        {
            return StripBBCode(potion.DynamicDescription.GetFormattedText());
        }
        catch
        {
            return SafeLocString(() => potion.DynamicDescription);
        }
    }

    public static string GetPowerDescription(PowerModel power)
    {
        try
        {
            LocString desc = power.Description;
            power.DynamicVars.AddTo(desc);
            desc.Add("Amount", power.Amount);
            return StripBBCode(desc.GetFormattedText());
        }
        catch
        {
            return "???";
        }
    }

    //TODO: Fix Icon text just being duplicated. convert StarsStarsStart to 3x Stars. and so on
    private static string GetLocalizedIconText(string iconPath)
    {
        try
        {
            var table = LocManager.Instance.GetTable("static_hover_tips");
            if (iconPath.Contains("energy_icon"))
                return table.GetRawText("ENERGY.title");
            if (iconPath.Contains("star_icon"))
                return table.GetRawText("STAR_COUNT.title");
        }
        catch { }
        return "";
    }
}
