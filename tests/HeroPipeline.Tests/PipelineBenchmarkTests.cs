using System.Diagnostics;
using System.Collections.Generic;
using Hero.Ecs;
using Hero.GameplayEcs.Bootstrap;
using Hero.GameplayEcs.Cards;
using Hero.GameplayEcs.Characters.Actions;
using Hero.GameplayEcs.Characters.Attack;
using Hero.GameplayEcs.Characters.Components;
using Hero.GameplayEcs.Characters.Defense;
using Hero.GameplayEcs.Characters.Movement;
using Hero.GameplayEcs.Characters.Slots;
using Hero.GameplayEcs.Characters.Spawn;
using Hero.GameplayEcs.Trigger;
using Hero.GameplayEcs.TurnSerial;
using Hero.Tests.Fixtures;

namespace Hero.Tests;

/// <summary>
/// End-to-end benchmarks measuring pipeline throughput.
/// </summary>
public sealed class PipelineBenchmarkTests
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_Movement_20Seconds()
    {
        // Movement: create request → move effect → position modifier
        CoreTestFixture core = new();
        _ = new CharacterTestFixture(core);

        core.AddCoreSystems();
        core.AddSpawnSystem();

        MiniArchRuntime runtime = core.Runtime;

        MiniArch.Entity player = CharacterSpawnBootstrap.CreatePlayerAt(runtime, 0, 0);
        core.StepUntilStable();

        // Warmup
        ExecuteMoveCycle(runtime, core, player);

        long ticks = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            ExecuteMoveCycle(runtime, core, player);
            ticks++;
        }
        sw.Stop();

        Report("Movement (position change)", sw, ticks);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_SimpleAttack_20Seconds()
    {
        // Simple attack: create request → damage → modifier (no trigger)
        CoreTestFixture core = new();
        _ = new CharacterTestFixture(core);

        core.AddCoreSystems();
        core.AddSpawnSystem();

        MiniArchRuntime runtime = core.Runtime;

        MiniArch.Entity player = CharacterSpawnBootstrap.CreatePlayerAt(runtime, 5, 8);
        core.StepUntilStable();

        MiniArch.Entity enemy = TestEntities.Create(runtime.World,
            new PositionQValue(7), new PositionRValue(8), new CurrentHpValue(10));
        core.StepUntilStable();

        FrameView frame = runtime.CurrentFrame;
        MiniArch.Entity action = FindActionChild(runtime, frame, player, CharacterActionKinds.Attack);

        // Warmup
        ExecuteSimpleAttackCycle(runtime, core, action, enemy);

        long ticks = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            ExecuteSimpleAttackCycle(runtime, core, action, enemy);
            ticks++;
        }
        sw.Stop();

        Report("Simple Attack (damage only)", sw, ticks);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_AttackWithTrigger_20Seconds()
    {
        // Full pipeline: attack → damage → trigger → armor gain
        // Uses a card with built-in trigger markers (no manual observer needed)
        CoreTestFixture core = new();
        CharacterTestFixture charFixture = new(core);
        TriggerTestFixture triggerFixture = new(core);

        core.AddCoreSystems();
        core.AddSpawnSystem();
        triggerFixture.AddTriggerSystem();

        MiniArchRuntime runtime = core.Runtime;

        MiniArch.Entity player = CharacterSpawnBootstrap.CreatePlayerAt(runtime, 5, 8);
        core.StepUntilStable();

        // Create enemy
        MiniArch.Entity enemy = TestEntities.Create(runtime.World,
            new PositionQValue(7), new PositionRValue(8), new CurrentHpValue(10));
        core.StepUntilStable();

        // Create a special attack card with built-in damage-to-armor trigger
        MiniArch.Entity triggerCard = TestEntities.Create(runtime.World,
            new ActionEntity(),
            CardActionKinds.Attack,
            new ActionRuleId(CharacterAttackIds.AttackRule),
            new ActionPointCostValue(1),
            new CardZone(CardZoneKind.Hand),
            new CardOrderValue(0),
            new TriggerCondition(TriggerIds.DamageDealtBySelf),
            new TriggerAction(TriggerIds.GainArmorFromDamage));
        runtime.World.Link(player, triggerCard);

        FrameView frame = runtime.CurrentFrame;
        MiniArch.Entity action = FindActionChild(runtime, frame, player, CharacterActionKinds.Attack);

        // Warmup
        ExecuteAttackWithTriggerCycle(runtime, core, action, enemy, player);

        long ticks = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            ExecuteAttackWithTriggerCycle(runtime, core, action, enemy, player);
            ticks++;
        }
        sw.Stop();

        Report("Attack + Trigger (damage + armor)", sw, ticks);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_FullCardPlayWithCollision_20Seconds()
    {
        // Full pipeline with collision detection:
        // play card → action dispatch → attack → collision scan → damage → trigger → armor
        CoreTestFixture core = new(CreateMergedSlotPorts());
        CharacterTestFixture charFixture = new(core);
        CardTestFixture cardFixture = new(core);
        TriggerTestFixture triggerFixture = new(core);

        core.AddCoreSystems();
        core.AddSpawnSystem();
        triggerFixture.AddTriggerSystem();

        MiniArchRuntime runtime = core.Runtime;

        MiniArch.Entity player = CharacterSpawnBootstrap.CreatePlayerAt(runtime, 5, 8);
        core.StepUntilStable();

        // Create enemy at attack target position
        MiniArch.Entity enemy = TestEntities.Create(runtime.World,
            new PositionQValue(7), new PositionRValue(8), new CurrentHpValue(10));
        core.StepUntilStable();

        // Create 10 additional enemies scattered elsewhere to test collision scan overhead
        for (int i = 0; i < 10; i++)
        {
            TestEntities.Create(runtime.World,
                new PositionQValue(i * 10 + 100),
                new PositionRValue(i * 10 + 100),
                new CurrentHpValue(10));
        }
        core.StepUntilStable();

        // Create a special attack card with built-in damage-to-armor trigger
        MiniArch.Entity triggerCard = TestEntities.Create(runtime.World,
            new ActionEntity(),
            CardActionKinds.Attack,
            new ActionRuleId(CharacterAttackIds.AttackRule),
            new ActionPointCostValue(1),
            new CardZone(CardZoneKind.Hand),
            new CardOrderValue(0),
            new TriggerCondition(TriggerIds.DamageDealtBySelf),
            new TriggerAction(TriggerIds.GainArmorFromDamage));
        runtime.World.Link(player, triggerCard);
        MiniArch.Entity context = TurnSerialBootstrap.CreateContext(runtime);
        TurnSerialBootstrap.Activate(runtime, context, player);

        // Warmup
        ExecuteFullPlayCardWithCollisionCycle(runtime, core, triggerCard, enemy, player);

        long ticks = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            ExecuteFullPlayCardWithCollisionCycle(runtime, core, triggerCard, enemy, player);
            ticks++;
        }
        sw.Stop();

        Report("Full Card Play with Collision (play card → attack → collision → damage → trigger → armor)", sw, ticks);
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_FullCardPlayToArmor_20Seconds()
    {
        // Most complex: play trigger card → action dispatch → attack → damage → trigger → armor
        CoreTestFixture core = new(CreateMergedSlotPorts());
        CharacterTestFixture charFixture = new(core);
        CardTestFixture cardFixture = new(core);
        TriggerTestFixture triggerFixture = new(core);

        core.AddCoreSystems();
        core.AddSpawnSystem();
        triggerFixture.AddTriggerSystem();

        MiniArchRuntime runtime = core.Runtime;

        MiniArch.Entity player = CharacterSpawnBootstrap.CreatePlayerAt(runtime, 5, 8);
        core.StepUntilStable();

        // Create enemy at attack target position
        MiniArch.Entity enemy = TestEntities.Create(runtime.World,
            new PositionQValue(7), new PositionRValue(8), new CurrentHpValue(10));
        core.StepUntilStable();

        // Create a special attack card with built-in damage-to-armor trigger
        MiniArch.Entity triggerCard = TestEntities.Create(runtime.World,
            new ActionEntity(),
            CardActionKinds.Attack,
            new ActionRuleId(CharacterAttackIds.AttackRule),
            new ActionPointCostValue(1),
            new CardZone(CardZoneKind.Hand),
            new CardOrderValue(0),
            new TriggerCondition(TriggerIds.DamageDealtBySelf),
            new TriggerAction(TriggerIds.GainArmorFromDamage));
        runtime.World.Link(player, triggerCard);
        MiniArch.Entity context = TurnSerialBootstrap.CreateContext(runtime);
        TurnSerialBootstrap.Activate(runtime, context, player);

        // Warmup
        ExecuteFullPlayCardWithCollisionCycle(runtime, core, triggerCard, enemy, player);

        long ticks = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            ExecuteFullPlayCardWithCollisionCycle(runtime, core, triggerCard, enemy, player);
            ticks++;
        }
        sw.Stop();

        Report("Full Card Play (play card → attack → trigger → armor)", sw, ticks);
    }

    private static void ExecuteMoveCycle(
        MiniArchRuntime runtime, CoreTestFixture core,
        MiniArch.Entity player)
    {
        // Reset position
        runtime.Recorder.Set(player, new PositionQValue(0));
        runtime.Recorder.Set(player, new PositionRValue(0));

        // Create move request: move +1, +2
        CharacterMovementBootstrap.CreateMoveRequest(runtime, player, 1, 2);
        core.StepUntilStable();
    }

    private static void ExecuteSimpleAttackCycle(
        MiniArchRuntime runtime, CoreTestFixture core,
        MiniArch.Entity action, MiniArch.Entity enemy)
    {
        runtime.Recorder.Set(enemy, new CurrentHpValue(10));
        FrameView frame = runtime.CurrentFrame;
        CharacterAttackBootstrap.CreateAttackRequest(runtime, action,
            frame.Get<PositionQValue>(enemy).Value,
            frame.Get<PositionRValue>(enemy).Value);
        core.StepUntilStable();
    }

    private static void ExecuteAttackWithTriggerCycle(
        MiniArchRuntime runtime, CoreTestFixture core,
        MiniArch.Entity action, MiniArch.Entity enemy, MiniArch.Entity player)
    {
        runtime.Recorder.Set(enemy, new CurrentHpValue(10));
        // Reset armor to 0 (do not remove component; ModifierApplySystem needs current value)
        runtime.Recorder.Set(player, new CurrentArmorValue(0));

        FrameView frame = runtime.CurrentFrame;
        CharacterAttackBootstrap.CreateAttackRequest(runtime, action,
            frame.Get<PositionQValue>(enemy).Value,
            frame.Get<PositionRValue>(enemy).Value);
        core.StepUntilStable();
    }

    private static void ExecuteFullPlayCardWithCollisionCycle(
        MiniArchRuntime runtime, CoreTestFixture core,
        MiniArch.Entity attackCard, MiniArch.Entity enemy, MiniArch.Entity player)
    {
        runtime.Recorder.Set(enemy, new CurrentHpValue(10));
        runtime.Recorder.Set(player, new CurrentArmorValue(0));

        FrameView frame = runtime.CurrentFrame;

        // Reset card back to hand
        runtime.Recorder.Set(player, new CurrentApValue(3));
        runtime.Recorder.Set(attackCard, new CardZone(CardZoneKind.Hand));
        runtime.Recorder.Set(attackCard, new CardOrderValue(0));
        int targetQ = frame.Get<PositionQValue>(enemy).Value;
        int targetR = frame.Get<PositionRValue>(enemy).Value;

        _ = TurnSerialBootstrap.SubmitAttackCardPlay(runtime, attackCard, targetQ, targetR);

        core.StepUntilStable();
    }

    private static void Report(string name, Stopwatch sw, long iterations)
    {
        Console.WriteLine($"=== {name} ===");
        Console.WriteLine($"  Duration: {sw.Elapsed.TotalMilliseconds:F2} ms");
        Console.WriteLine($"  Iterations: {iterations:N0}");
        Console.WriteLine($"  Avg per cycle: {sw.Elapsed.TotalMilliseconds / iterations:F4} ms");
        Console.WriteLine($"  Cycles/sec: {iterations / sw.Elapsed.TotalSeconds:F0}");
        Console.WriteLine();
    }

    private static IReadOnlyDictionary<SlotKey, IIntSlotPort> CreateMergedSlotPorts()
    {
        Dictionary<SlotKey, IIntSlotPort> ports = new(CharacterSlotPorts.Create());
        foreach (KeyValuePair<SlotKey, IIntSlotPort> pair in TurnSerialSlotPorts.Create())
        {
            ports[pair.Key] = pair.Value;
        }
        return ports;
    }

    private static MiniArch.Entity FindActionChild(
        MiniArchRuntime runtime, FrameView frame, MiniArch.Entity parent, ActionKind kind)
    {
        foreach (MiniArch.Entity entity in frame.Each(
            new MiniArch.QueryDescription().With<ActionEntity>().With<ActionKind>()))
        {
            if (runtime.World.TryGetParent(entity, out MiniArch.Entity p) && p == parent
                && frame.Get<ActionKind>(entity).Value == kind.Value)
            {
                return entity;
            }
        }
        throw new InvalidOperationException($"No action child of kind {kind.Value} found.");
    }

}
