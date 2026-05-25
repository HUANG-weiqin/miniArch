using Hero.GameplayEcs.Characters.Actions;

namespace Hero.GameplayEcs.Cards;

public static class CardActionKinds
{
    public static ActionKind Attack { get; } = new(3101);
    public static ActionKind Defend { get; } = new(3102);
    public static ActionKind Heal { get; } = new(3103);
    public static ActionKind Move { get; } = new(3104);
}
