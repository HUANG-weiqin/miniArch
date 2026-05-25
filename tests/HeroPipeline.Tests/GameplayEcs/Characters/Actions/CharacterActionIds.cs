using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Actions;

public static class CharacterActionIds
{
    public static RuleId DispatchRule { get; } = new(3002);
}

public static class CharacterActionKinds
{
    public static ActionKind Move { get; } = new(3001);
    public static ActionKind Attack { get; } = new(3003);
}


