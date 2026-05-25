using System;
using System.Collections.Generic;
using Hero.Ecs;

namespace Hero.GameplayEcs.Cards;

public static class CardDrawRegistrations
{
    private static readonly Random SharedRandom = new();

    public static RuleTable Register(RuleTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Register(CardIds.DrawRule, ApplyDraw);
    }

    public static EffectTable Register(EffectTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.RegisterObservationOnly(CardIds.CardDrawnEffect);
    }

    private static void ApplyDraw(FrameContext context, MiniArch.Entity request)
    {
        FrameView frame = context.Frame;
        MiniArch.Entity participant = frame.Get<RequestTarget>(request).Target;

        if (frame.TryGet(participant, out DrawBlock _))
        {
            context.Commands.Add(request, new Rejected());
            return;
        }

        int count = frame.Get<DrawCountValue>(request).Value;

        List<(MiniArch.Entity Card, int Order)> deckCards = CardCollector.CollectInZone(frame, participant, CardZoneKind.Deck);
        deckCards.Sort((a, b) => b.Order.CompareTo(a.Order));

        int deficit = count - deckCards.Count;
        if (deficit > 0)
        {
            List<(MiniArch.Entity Card, int Order)> discardCards = CardCollector.CollectInZone(frame, participant, CardZoneKind.Discard);

            int nextDeckOrder = CardOrder.NextAfterMax(frame, participant, CardZoneKind.Deck, 0);
            foreach ((MiniArch.Entity card, _) in discardCards)
            {
                CardZoneTransition.MoveTo(context, card, CardZoneKind.Deck, nextDeckOrder++);
                deckCards.Add((card, nextDeckOrder - 1));
            }

            CardShuffle.ShuffleAndReorder(context, deckCards, SharedRandom);
        }

        int toDraw = Math.Min(count, deckCards.Count);
        int nextOrder = CardOrder.NextAfterMax(frame, participant, CardZoneKind.Hand, 0);

        for (int i = 0; i < toDraw; i++)
        {
            MiniArch.Entity card = deckCards[i].Card;

            CardZoneTransition.MoveTo(context, card, CardZoneKind.Hand, nextOrder++);

            MiniArch.Entity effect = context.Commands.Create();
            context.Commands.Add(effect, new Effect());
            context.Commands.Add(effect, CardIds.CardDrawnEffect);
            context.Commands.Add(effect, new CardDrawnEffectMarker());
            context.Commands.Add(effect, new EffectSource(participant));
            context.Commands.Add(effect, new EffectTarget(card));
        }
    }
}
