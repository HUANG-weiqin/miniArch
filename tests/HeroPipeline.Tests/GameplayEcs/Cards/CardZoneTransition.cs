using Hero.Ecs;

namespace Hero.GameplayEcs.Cards;

/// <summary>
/// Reusable helper that moves a card entity to a new zone
/// and assigns the caller-provided <see cref="CardOrderValue"/>.
/// </summary>
public static class CardZoneTransition
{
    public static void MoveTo(FrameContext context, MiniArch.Entity card, CardZoneKind targetZone, int order)
    {
        context.Commands.Set(card, new CardZone(targetZone));
        context.Commands.Set(card, new CardOrderValue(order));
    }
}
