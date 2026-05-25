using System;
using Hero.Ecs;
using Hero.GameplayEcs.Trigger;

namespace Hero.Tests.Fixtures;

/// <summary>
/// Trigger domain fixture: runtime + trigger tables + trigger system.
/// </summary>
public sealed class TriggerTestFixture
{
    public CoreTestFixture Core { get; }

    public MiniArchRuntime Runtime => Core.Runtime;

    public TriggerConditionTable ConditionTable { get; } = new();

    public TriggerActionTable ActionTable { get; } = new();

    public TriggerTestFixture(CoreTestFixture? core = null, Action<TriggerConditionTable>? conditionOverride = null, Action<TriggerActionTable>? actionOverride = null)
    {
        Core = core ?? new CoreTestFixture();
        _ = TriggerRegistrations.Register(ConditionTable);
        _ = TriggerRegistrations.Register(ActionTable);

        conditionOverride?.Invoke(ConditionTable);
        actionOverride?.Invoke(ActionTable);
    }

    public void AddTriggerSystem()
        => Core.Runtime.AddSystem(new TriggerSystem(ConditionTable, ActionTable));

    public void StepUntilStable(int maxTicks = 20)
        => Core.StepUntilStable(maxTicks);
}
