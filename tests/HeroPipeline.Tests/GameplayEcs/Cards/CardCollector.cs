using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;

namespace Hero.GameplayEcs.Cards;

public static class CardCollector
{
    private static readonly MiniArch.QueryDescription CardQuery = new MiniArch.QueryDescription()
        .With<ActionEntity>()
        .With<CardZone>()
        .With<CardOrderValue>();

    public static List<(MiniArch.Entity Card, int Order)> CollectInZone(FrameView frame, MiniArch.Entity owner, CardZoneKind zone)
    {
        List<(MiniArch.Entity Card, int Order)> cards = [];

        foreach (MiniArch.Entity card in frame.Each(CardQuery))
        {
            if (frame.Get<CardZone>(card).Value != zone)
            {
                continue;
            }

            if (!frame.TryGetParent(card, out MiniArch.Entity parent) || parent != owner)
            {
                continue;
            }

            cards.Add((card, frame.Get<CardOrderValue>(card).Value));
        }

        return cards;
    }

    public static Dictionary<MiniArch.Entity, List<(MiniArch.Entity Card, int Order)>> GroupInZoneByOwner(FrameView frame, CardZoneKind zone)
    {
        Dictionary<MiniArch.Entity, List<(MiniArch.Entity Card, int Order)>> result = [];

        foreach (MiniArch.Entity card in frame.Each(CardQuery))
        {
            if (frame.Get<CardZone>(card).Value != zone)
            {
                continue;
            }

            if (!frame.TryGetParent(card, out MiniArch.Entity owner))
            {
                continue;
            }

            if (!result.TryGetValue(owner, out List<(MiniArch.Entity Card, int Order)>? list))
            {
                list = [];
                result[owner] = list;
            }

            list.Add((card, frame.Get<CardOrderValue>(card).Value));
        }

        return result;
    }
}
