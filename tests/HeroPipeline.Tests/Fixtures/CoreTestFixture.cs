using System;
using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Characters.Slots;
using Hero.GameplayEcs.Spawn;

namespace Hero.Tests.Fixtures;

/// <summary>
/// Core pipeline fixture: runtime, tables, and base systems (validation, rule dispatch,
/// effect dispatch, modifier apply, spawn).
/// </summary>
public sealed class CoreTestFixture
{
    public MiniArchRuntime Runtime { get; }
    public IReadOnlyDictionary<SlotKey, IIntSlotPort> Ports { get; }
    public RuleTable RuleTable { get; } = new();
    public EffectTable EffectTable { get; } = new();
    public SpawnTable SpawnTable { get; } = GameplaySpawnTable.Create();

    public CoreTestFixture(IReadOnlyDictionary<SlotKey, IIntSlotPort>? ports = null)
        : this(new MiniArchRuntime(), ports)
    {
    }

    public CoreTestFixture(MiniArchRuntime runtime, IReadOnlyDictionary<SlotKey, IIntSlotPort>? ports = null)
    {
        Runtime = runtime;
        Ports = ports ?? CharacterSlotPorts.Create();
    }

    public void AddValidationSystem()
        => Runtime.AddSystem(new ValidationSystem());

    public void AddRuleDispatchSystem()
        => Runtime.AddSystem(new RuleDispatchSystem(RuleTable));

    public void AddEffectDispatchSystem()
        => Runtime.AddSystem(new EffectDispatchSystem(EffectTable));

    public void AddModifierApplySystem()
        => Runtime.AddSystem(new ModifierApplySystem(Ports));

    public void AddSpawnSystem()
        => Runtime.AddSystem(new SpawnSystem(SpawnTable));

    /// <summary>
    /// Adds the standard core pipeline: validation → rule → effect → modifier.
    /// </summary>
    public void AddCoreSystems()
    {
        AddValidationSystem();
        AddRuleDispatchSystem();
        AddEffectDispatchSystem();
        AddModifierApplySystem();
    }

    /// <summary>
    /// Runs ticks until the pipeline reports stable or max ticks is reached.
    /// </summary>
    public void StepUntilStable(int maxTicks = 20)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            Runtime.Tick();
            if (Runtime.IsStable)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Pipeline did not stabilize within {maxTicks} ticks.");
    }
}
