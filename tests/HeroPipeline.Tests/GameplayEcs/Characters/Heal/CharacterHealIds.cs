using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Heal;

public static class CharacterHealIds
{
    public static EffectId HealEffect { get; } = new(3301);
    public static RuleId HealRule { get; } = new(3302);
}
