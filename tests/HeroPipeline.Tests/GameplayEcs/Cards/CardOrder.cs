using Hero.Ecs;

namespace Hero.GameplayEcs.Cards;

/// <summary>
/// Pure value helper to compute the next <see cref="CardOrderValue"/>
/// for cards entering a given zone owned by one participant. No side effects, no system.
/// </summary>
public static class CardOrder
{
    /// <summary>
    /// Scans the owner's cards in the target zone and returns one higher
    /// than the current maximum <see cref="CardOrderValue"/>,
    /// or <paramref name="seed"/> when no cards exist yet.
    /// </summary>
    public static int NextAfterMax(FrameView frame, MiniArch.Entity owner, CardZoneKind zone, int seed = 0)
    {
        int max = seed - 1;

        foreach ((_, int order) in CardCollector.CollectInZone(frame, owner, zone))
        {
            if (order > max)
            {
                max = order;
            }
        }

        return max + 1;
    }
}
