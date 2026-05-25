using Hero.Ecs;

namespace Hero.GameplayEcs.TurnSerial;

public static class TurnSerialIds
{
    public static RuleId EndTurnRule { get; } = new(4401);
    public static RuleId CardPlayRule { get; } = new(4402);
    public static EffectId RestoreApEffect { get; } = new(4302);
}
