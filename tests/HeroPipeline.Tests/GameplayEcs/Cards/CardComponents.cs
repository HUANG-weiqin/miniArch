namespace Hero.GameplayEcs.Cards;

public enum CardZoneKind
{
    Deck = 0,
    Discard = 1,
    Hand = 2,
}

public readonly record struct CardZone(CardZoneKind Value);

/// <summary>
/// Stable ordering index for a card within its current zone.
/// Lower values are closer to the bottom; higher values are closer to the top.
/// Assigned at spawn and updated on zone transitions.
/// </summary>
public readonly record struct CardOrderValue(int Value);

/// <summary>
/// Marker that prevents card draw for the participant it is attached to.
/// </summary>
public readonly record struct DrawBlock;

/// <summary>
/// Number of cards to draw, attached to a draw request.
/// </summary>
public readonly record struct DrawCountValue(int Value);
