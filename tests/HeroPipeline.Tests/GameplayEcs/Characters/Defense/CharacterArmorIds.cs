using Hero.Ecs;

namespace Hero.GameplayEcs.Characters.Defense;

public static class CharacterArmorIds
{
    public static RuleId GainArmorRule { get; } = new(2301);
    public static EffectId GainArmorEffect { get; } = new(2302);
}
