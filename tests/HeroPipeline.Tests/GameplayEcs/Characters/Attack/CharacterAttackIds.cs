using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Attack;

public static class CharacterAttackIds
{
    public static RuleId HitConsequenceRule { get; } = new(2101);
    public static RuleId AttackRule { get; } = new(2101);
    public static EffectId DamageEffect { get; } = new(2102);
    public static EffectId SwingEffect { get; } = new(2103);
}


