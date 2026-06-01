using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;
using MiniArch.Core;

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

        foreach (var row in frame.ChunkQuery(CardQuery).EachSpan<CardZone, CardOrderValue>())
        {
            if (row.Get0().Value != zone)
            {
                continue;
            }

            if (!frame.TryGetParent(row.Entity, out MiniArch.Entity parent) || parent != owner)
            {
                continue;
            }

            cards.Add((row.Entity, row.Get1().Value));
        }

        return cards;
    }

    public static Dictionary<MiniArch.Entity, List<(MiniArch.Entity Card, int Order)>> GroupInZoneByOwner(FrameView frame, CardZoneKind zone)
    {
        Dictionary<MiniArch.Entity, List<(MiniArch.Entity Card, int Order)>> result = [];

        foreach (var row in frame.ChunkQuery(CardQuery).EachSpan<CardZone, CardOrderValue>())
        {
            if (row.Get0().Value != zone)
            {
                continue;
            }

            MiniArch.Entity card = row.Entity;

            if (!frame.TryGetParent(card, out MiniArch.Entity owner))
            {
                continue;
            }

            if (!result.TryGetValue(owner, out List<(MiniArch.Entity Card, int Order)>? list))
            {
                list = [];
                result[owner] = list;
            }

            list.Add((card, row.Get1().Value));
        }

        return result;
    }
}
