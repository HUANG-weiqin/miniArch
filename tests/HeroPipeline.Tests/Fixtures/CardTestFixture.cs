using System;
using Hero.Ecs;
using Hero.GameplayEcs.Cards;
using Hero.GameplayEcs.TurnSerial;

namespace Hero.Tests.Fixtures;

/// <summary>
/// Card domain fixture: runtime + card draw registrations.
/// </summary>
public sealed class CardTestFixture
{
    public CoreTestFixture Core { get; }

    public MiniArchRuntime Runtime => Core.Runtime;

    public CardTestFixture(CoreTestFixture? core = null, Action<RuleTable>? ruleOverride = null, Action<EffectTable>? effectOverride = null)
    {
        Core = core ?? new CoreTestFixture();
        _ = TurnSerialCardPlayRegistrations.Register(Core.RuleTable);
        _ = CardDrawRegistrations.Register(Core.RuleTable);
        _ = CardDrawRegistrations.Register(Core.EffectTable);

        ruleOverride?.Invoke(Core.RuleTable);
        effectOverride?.Invoke(Core.EffectTable);
    }

    /// <summary>
    /// Adds core pipeline systems (validation, rule, effect, modifier).
    /// </summary>
    public void AddCoreSystems()
        => Core.AddCoreSystems();

    public void StepUntilStable(int maxTicks = 20)
        => Core.StepUntilStable(maxTicks);
}
