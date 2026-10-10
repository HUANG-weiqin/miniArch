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
        .With<CardOrderValue>()
        .With<ActionOwner>();

    public static List<(MiniArch.Entity Card, int Order)> CollectInZone(FrameView frame, MiniArch.Entity owner, CardZoneKind zone)
    {
        List<(MiniArch.Entity Card, int Order)> cards = [];

        foreach (var chunk in frame.ChunkQuery(CardQuery).GetChunks())
        {
            var zones = chunk.GetSpan<CardZone>();
            var orders = chunk.GetSpan<CardOrderValue>();
            var owners = chunk.GetSpan<ActionOwner>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (zones[i].Value != zone)
                {
                    continue;
                }

                if (owners[i].Value != owner)
                {
                    continue;
                }

                cards.Add((entities[i], orders[i].Value));
            }
        }

        return cards;
    }

    public static Dictionary<MiniArch.Entity, List<(MiniArch.Entity Card, int Order)>> GroupInZoneByOwner(FrameView frame, CardZoneKind zone)
    {
        Dictionary<MiniArch.Entity, List<(MiniArch.Entity Card, int Order)>> result = [];

        foreach (var chunk in frame.ChunkQuery(CardQuery).GetChunks())
        {
            var zones = chunk.GetSpan<CardZone>();
            var orders = chunk.GetSpan<CardOrderValue>();
            var owners = chunk.GetSpan<ActionOwner>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (zones[i].Value != zone)
                {
                    continue;
                }

                MiniArch.Entity card = entities[i];
                MiniArch.Entity owner = owners[i].Value;

                if (!result.TryGetValue(owner, out List<(MiniArch.Entity Card, int Order)>? list))
                {
                    list = [];
                    result[owner] = list;
                }

                list.Add((card, orders[i].Value));
            }
        }

        return result;
    }
}
