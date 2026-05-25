using System;
using System.Collections.Generic;
using Hero.Ecs;

namespace Hero.GameplayEcs.Cards;

public static class CardShuffle
{
    public static void ShuffleAndReorder(FrameContext context, List<(MiniArch.Entity Card, int Order)> cards, Random random)
    {
        for (int i = cards.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (cards[j], cards[i]) = (cards[i], cards[j]);
        }

        for (int i = 0; i < cards.Count; i++)
        {
            context.Commands.Set(cards[i].Card, new CardOrderValue(i));
            cards[i] = (cards[i].Card, i);
        }
    }
}
