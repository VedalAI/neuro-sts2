using System.Text;
using MegaCrit.Sts2.Core.Models;
using Sts2Agent;
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

    public static void RepresentStartingCharacter(this StringBuilder stringBuilder, CharacterModel character)
    {

        stringBuilder.AppendLine($"\"{TextHelper.GetCharacterDescription(character)}\"");
        stringBuilder.AppendLine("");
        stringBuilder.AppendLine($"Starts with {character.StartingHp} hp and {character.StartingGold} gold");
        stringBuilder.AppendLine($"Starting deck:");
        RepresentDeck(stringBuilder, character.StartingDeck);
        stringBuilder.AppendLine($"Starting with relics:");
        RepresentRelics(stringBuilder, character.StartingRelics);
    }

    public static void RepresentEvents(this StringBuilder stringBuilder, List<GameEvent> events)
    {

        var combinedEvents = events.GroupBy(x => x.Message);
        foreach (var eventBetweenRounds in combinedEvents)
        {
            var firstEvent = eventBetweenRounds.First();
            var count = eventBetweenRounds.Count();
            if (count > 1)
            {
                stringBuilder.AppendLine($"- {firstEvent.Message}, {count} Times ");

            }
            else
            {
                stringBuilder.AppendLine($"- {firstEvent.Message}");
            }
        }
    }
}