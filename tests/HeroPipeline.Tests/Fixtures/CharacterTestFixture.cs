using System;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Defense;
using Hero.GameplayEcs.Characters.Heal;
using Hero.GameplayEcs.Characters.Movement;
using Hero.GameplayEcs.Collision;

namespace Hero.Tests.Fixtures;

/// <summary>
/// Character domain fixture: runtime + character-related registrations
/// (movement, action, collision, attack, armor, heal).
/// </summary>
public sealed class CharacterTestFixture
{
    public CoreTestFixture Core { get; }

    public MiniArchRuntime Runtime => Core.Runtime;

    public RuleTable RuleTable => Core.RuleTable;

    public EffectTable EffectTable => Core.EffectTable;

    public CharacterTestFixture(CoreTestFixture? core = null, Action<RuleTable>? ruleOverride = null, Action<EffectTable>? effectOverride = null)
    {
        Core = core ?? new CoreTestFixture();
        _ = CharacterMovementRegistrations.Register(Core.RuleTable);
        _ = CharacterMovementRegistrations.Register(Core.EffectTable);

        _ = CharacterActionRegistrations.Register(Core.RuleTable);

        _ = CharacterAttackRegistrations.Register(Core.RuleTable);
        _ = CharacterAttackRegistrations.Register(Core.EffectTable);

        _ = CharacterArmorRegistrations.Register(Core.EffectTable);

        _ = CharacterHealRegistrations.Register(Core.RuleTable);
        _ = CharacterHealRegistrations.Register(Core.EffectTable);

        ruleOverride?.Invoke(Core.RuleTable);
        effectOverride?.Invoke(Core.EffectTable);
    }

    /// <summary>
    /// Adds core pipeline systems: validation → rule → collision → effect → modifier.
    /// </summary>
    public void AddCoreSystems()
    {
        Core.AddValidationSystem();
        Core.AddRuleDispatchSystem();
        Runtime.AddSystem(new CollisionSystem());
        Core.AddEffectDispatchSystem();
        Core.AddModifierApplySystem();
    }

    public void StepUntilStable(int maxTicks = 20)
        => Core.StepUntilStable(maxTicks);
}
