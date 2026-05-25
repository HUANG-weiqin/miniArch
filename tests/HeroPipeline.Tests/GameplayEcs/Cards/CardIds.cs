using Hero.Ecs;

namespace Hero.GameplayEcs.Cards;

public static class CardIds
{
    public static RuleId DrawRule { get; } = new(3203);
    public static EffectId CardDrawnEffect { get; } = new(3204);
}

/// <summary>
/// Marker component that identifies an effect as a CardDrawn observation fact.
/// Used to narrow trigger condition queries to only card-drawn effects.
/// </summary>
public readonly record struct CardDrawnEffectMarker;
