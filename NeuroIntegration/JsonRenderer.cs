using System.Text;
using MegaCrit.Sts2.Core.Models;
using Sts2Agent.Utilities;

namespace STS2NeuroIntegration;

public static class Renderer
{
    public static void RepresentDeck(this StringBuilder stringBuilder, IEnumerable<CardModel> deck)
    {
        var countedGroup = deck.GroupBy((card) => card.Title);
        foreach (var cards in countedGroup)
        {
            var firstCard = cards.First();
            stringBuilder.AppendLine($"- {cards.Count()}x {TextHelper.StripBBCode(firstCard.Title)} \"{TextHelper.GetCardDescription(firstCard)}\"");
        }
    }
    public static void RepresentRelics(this StringBuilder stringBuilder, IEnumerable<RelicModel> relics)
    {

        var countedGroup = relics.GroupBy((relic) => relic.Title);
        foreach (var relic in countedGroup)
        {
            var firstRelic = relic.First();
            stringBuilder.AppendLine($"- {relic.Count()}x {TextHelper.StripBBCode(firstRelic.Title.GetFormattedText())} \"{TextHelper.GetRelicDescription(firstRelic)}\"");
        }
    }

    public static void ReprecentStartingCharacter(this StringBuilder stringBuilder, CharacterModel character)
    {

        stringBuilder.AppendLine($"\"{TextHelper.GetCharacterDescription(character)}\"");
        stringBuilder.AppendLine("");
        stringBuilder.AppendLine($"Starts with {character.StartingHp}hp and {character.StartingGold} gold");
        stringBuilder.AppendLine($"Starting deck:");
        RepresentDeck(stringBuilder, character.StartingDeck);
        stringBuilder.AppendLine($"Starting with relics:");
        RepresentRelics(stringBuilder, character.StartingRelics);
    }
}