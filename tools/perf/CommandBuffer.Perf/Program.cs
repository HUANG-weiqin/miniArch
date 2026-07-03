using System.Diagnostics;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using MiniArch.Core;
using MiniArchBenchmarks;
using MiniCommandBuffer = MiniArch.Core.CommandStream;
using MiniCommandStream = MiniArch.Core.CommandStream;
using MiniEntity = MiniArch.Entity;
using FrifloEntityStore = Friflo.Engine.ECS.EntityStore;
using FrifloCommandBuffer = Friflo.Engine.ECS.CommandBuffer;
using DefaultEntity = DefaultEcs.Entity;
using DefaultWorld = DefaultEcs.World;

const int N = 10_000;
const int warmupIters = 500;
const int measureSeconds = 10;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== CB steady-state: record+submit, reuse World+CB, no Destroy ===");
Console.WriteLine($"Entities: {N:N0}, Warmup: {warmupIters} iters, Measure: {measureSeconds}s");
Console.WriteLine();
Console.WriteLine($"{"Scenario",-20} | {"Engine",-12} | {"Ops/s",12} | {"GC Gen0/1/2",12}");
Console.WriteLine(new string('-', 75));

foreach (var scenario in Enum.GetValues<CommandBufferBenchmarkScenario>())
{
    if (!IsSteadyStateFriendly(scenario)) continue;
    // ── MiniArch CB ──
    Run($"MiniArch CB", scenario, () =>
    {
        var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, N);
        var cb = new MiniCommandBuffer(state.World);
        if (scenario == CommandBufferBenchmarkScenario.MixedScript)
        {
            var created = new MiniEntity[state.EntityCount / 2 + 1];
            return (warmup: () => RecordMixedSteady(cb, state, created),
                    run:    () => RecordMixedSteady(cb, state, created));
        }
        return (warmup: () => { Record(cb, state, scenario); cb.Submit(); },
                run:    () => { Record(cb, state, scenario); cb.Submit(); });
    });

    // ── MiniArch CS ──
    Run($"MiniArch CS", scenario, () =>
    {
        var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, N);
        var cs = new MiniCommandStream(state.World);
        if (scenario == CommandBufferBenchmarkScenario.MixedScript)
        {
            var created = new MiniEntity[state.EntityCount / 2 + 1];
            return (warmup: () => RecordMixedSteady(cs, state, created),
                    run:    () => RecordMixedSteady(cs, state, created));
        }
        return (warmup: () => { Record(cs, state, scenario); cs.Submit(); },
                run:    () => { Record(cs, state, scenario); cs.Submit(); });
    });

    // ── Friflo ──
    Run($"Friflo", scenario, () =>
    {
        var friflo = new FrifloBench(N);
        return (warmup: () => friflo.Run(scenario),
                run:    () => friflo.Run(scenario));
    });

    // ── DefaultEcs ──
    Run($"DefaultEcs", scenario, () =>
    {
        var def = new DefaultEcsBench(N);
        return (warmup: () => def.Run(scenario),
                run:    () => def.Run(scenario));
    });

    Console.WriteLine();
}

Console.WriteLine("=== Done ===");

static void Run(string engine, CommandBufferBenchmarkScenario scenario, Func<(Action warmup, Action run)> setup)
{
    var (warmup, run) = setup();
    for (var i = 0; i < warmupIters; i++) warmup();

    GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
    long iters = 0;
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < measureSeconds) { run(); iters++; }
    int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
    Console.WriteLine($"{scenario,-20} | {engine,-12} | {iters / sw.Elapsed.TotalSeconds,12:F1} | {$"{d0}/{d1}/{d2}",12}");
}

// ════════════════════════════════════════════════════════════════
//  Unified record helpers (no Destroy �?fair comparison)
// ════════════════════════════════════════════════════════════════

static void Record(CommandStream cb, MiniSharedCommandBufferState state, CommandBufferBenchmarkScenario scenario)
{
    switch (scenario)
    {
        case CommandBufferBenchmarkScenario.DenseExisting: RecordDense(cb, state); break;
        case CommandBufferBenchmarkScenario.CreateHeavy:   RecordCreate(cb, state); break;
        case CommandBufferBenchmarkScenario.MixedScript:   RecordMixed(cb, state); break;
    }
}

static bool IsSteadyStateFriendly(CommandBufferBenchmarkScenario scenario) =>
    scenario is CommandBufferBenchmarkScenario.DenseExisting or CommandBufferBenchmarkScenario.MixedScript;

static void RecordDense(CommandStream cb, MiniSharedCommandBufferState state)
{
    // Pure Set on existing entities — no structural changes for steady-state safety.
    // Structural ops (Add/Remove) are covered by MixedScript and CreateHeavy scenarios.
    for (var i = 0; i < state.ExistingEntities.Length; i++)
    {
        var e = state.ExistingEntities[i];
        cb.Set(e, new BenchmarkPosition(i + 1, i + 2));
        cb.Set(e, new BenchmarkVelocity(i + 3, i + 4));
        cb.Set(e, new BenchmarkHealth(200 + i));
    }
}

static void RecordCreate(CommandStream cb, MiniSharedCommandBufferState state)
{
    for (var i = 0; i < state.EntityCount; i++)
    {
        var e = cb.Create();
        cb.Add(e, new BenchmarkPosition(i + 1, i + 2));
        cb.Add(e, new BenchmarkVelocity(i + 3, i + 4));
        cb.Add(e, new BenchmarkHealth(200 + i));

        if ((i & 1) == 0) cb.Remove<BenchmarkVelocity>(e);
    }
}

static void RecordMixed(CommandStream cb, MiniSharedCommandBufferState state)
{
    for (var i = 0; i < state.EntityCount; i++)
    {
        if ((i & 1) == 0)
        {
            var e = state.ExistingEntities[i];
            cb.Set(e, new BenchmarkPosition(i + 1, i + 2));
            cb.Set(e, new BenchmarkVelocity(i + 3, i + 4));

            if ((i & 3) == 0) cb.Remove<BenchmarkHealth>(e);
            else               cb.Set(e, new BenchmarkHealth(300 + i));
        }
        else
        {
            var e = cb.Create();
            cb.Add(e, new BenchmarkPosition(i + 11, i + 12));
            cb.Add(e, new BenchmarkVelocity(i + 13, i + 14));
            cb.Add(e, new BenchmarkHealth(400 + i));

            if ((i & 3) == 1) cb.Remove<BenchmarkVelocity>(e);
        }
    }
}

/// <summary>
/// Records MixedScript and cleans up created entities after Submit,
/// matching Friflo's behavior where Mixed() deletes created entities post-Playback.
/// </summary>
static void RecordMixedSteady(CommandStream cb, MiniSharedCommandBufferState state, MiniEntity[] created)
{
    var ci = 0;
    for (var i = 0; i < state.EntityCount; i++)
    {
        if ((i & 1) == 0)
        {
            var e = state.ExistingEntities[i];
            cb.Set(e, new BenchmarkPosition(i + 1, i + 2));
            cb.Set(e, new BenchmarkVelocity(i + 3, i + 4));

            if ((i & 3) == 0) cb.Remove<BenchmarkHealth>(e);
            else               cb.Set(e, new BenchmarkHealth(300 + i));
        }
        else
        {
            var e = cb.Create();
            cb.Add(e, new BenchmarkPosition(i + 11, i + 12));
            cb.Add(e, new BenchmarkVelocity(i + 13, i + 14));
            cb.Add(e, new BenchmarkHealth(400 + i));

            if ((i & 3) == 1) cb.Remove<BenchmarkVelocity>(e);
            created[ci++] = e;
        }
    }

    cb.Submit();
    for (var i = 0; i < ci; i++) state.World.Destroy(created[i]);
}

// ════════════════════════════════════════════════════════════════
//  Friflo
// ════════════════════════════════════════════════════════════════

sealed class FrifloBench
{
    private readonly FrifloEntityStore _store;
    private readonly FrifloCommandBuffer _buffer;
    private readonly int[] _ids;
    private readonly int _count;

    public FrifloBench(int count)
    {
        _store = new FrifloEntityStore();
        _store.EnsureCapacity(count * 2);
        _ids = new int[count];
        _count = count;
        for (var i = 0; i < count; i++)
            _ids[i] = _store.CreateEntity(new FPos(i, i + 1), new FVel(i + 2, i + 3), new FHp(100 + i)).Id;
        _buffer = _store.GetCommandBuffer();
        _buffer.ReuseBuffer = true;
    }

    public void Run(CommandBufferBenchmarkScenario scenario)
    {
        switch (scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting: Dense(); break;
            case CommandBufferBenchmarkScenario.CreateHeavy:   Create(); break;
            case CommandBufferBenchmarkScenario.MixedScript:   Mixed(); break;
        }
    }

    private void Dense()
    {
        for (var i = 0; i < _count; i++)
        {
            var id = _ids[i];
            _buffer.AddComponent(id, new FPos(i + 1, i + 2));
            _buffer.AddComponent(id, new FVel(i + 3, i + 4));
            _buffer.AddComponent(id, new FHp(200 + i));
        }
        _buffer.Playback();
    }

    private void Create()
    {
        var cb = _store.GetCommandBuffer();
        cb.ReuseBuffer = false;
        var created = new int[_count];
        for (var i = 0; i < _count; i++)
        {
            var eid = cb.CreateEntity();
            cb.AddComponent(eid, new FPos(i + 1, i + 2));
            cb.AddComponent(eid, new FVel(i + 3, i + 4));
            cb.AddComponent(eid, new FHp(200 + i));
            if ((i & 1) == 0) cb.RemoveComponent<FVel>(eid);
            created[i] = eid;
        }
        cb.Playback();
        // Dispose created entities to keep steady-state
        for (var i = 0; i < _count; i++)
        {
            if ((i & 1) != 0 || (i & 3) != 0) // skip those that were removed in the CB
            {
                var entity = _store.GetEntityById(created[i]);
                if (entity != null) entity.DeleteEntity();
            }
        }
    }

    private void Mixed()
    {
        var cb = _store.GetCommandBuffer();
        cb.ReuseBuffer = false;
        var halfN = (_count + 1) / 2;
        var createdIds = new int[halfN];
        var ci = 0;
        for (var i = 0; i < _count; i++)
        {
            if ((i & 1) == 0)
            {
                var id = _ids[i];
                cb.AddComponent(id, new FPos(i + 1, i + 2));
                cb.AddComponent(id, new FVel(i + 3, i + 4));
                if ((i & 3) == 0) cb.RemoveComponent<FHp>(id);
                else               cb.AddComponent(id, new FHp(300 + i));
            }
            else
            {
                var eid = cb.CreateEntity();
                cb.AddComponent(eid, new FPos(i + 11, i + 12));
                cb.AddComponent(eid, new FVel(i + 13, i + 14));
                cb.AddComponent(eid, new FHp(400 + i));
                if ((i & 3) == 1) cb.RemoveComponent<FVel>(eid);
                createdIds[ci++] = eid;
            }
        }
        cb.Playback();
        for (var i = 0; i < ci; i++)
        {
            var entity = _store.GetEntityById(createdIds[i]);
            if (entity != null) entity.DeleteEntity();
        }
    }
}

// ════════════════════════════════════════════════════════════════
//  DefaultEcs
// ════════════════════════════════════════════════════════════════

sealed class DefaultEcsBench
{
    private readonly DefaultWorld _world;
    private readonly DefaultEntity[] _entities;
    private readonly int _count;

    public DefaultEcsBench(int count)
    {
        _world = new DefaultWorld(count * 2);
        _entities = new DefaultEntity[count];
        _count = count;
        for (var i = 0; i < count; i++)
        {
            var e = _world.CreateEntity();
            e.Set(new DPos(i, i + 1));
            e.Set(new DVel(i + 2, i + 3));
            e.Set(new DHp(100 + i));
            _entities[i] = e;
        }
    }

    public void Run(CommandBufferBenchmarkScenario scenario)
    {
        switch (scenario)
        {
            case CommandBufferBenchmarkScenario.DenseExisting: Dense(); break;
            case CommandBufferBenchmarkScenario.CreateHeavy:   Create(); break;
            case CommandBufferBenchmarkScenario.MixedScript:   Mixed(); break;
        }
    }

    private void Dense()
    {
        for (var i = 0; i < _count; i++)
        {
            var e = _entities[i];
            e.Set(new DPos(i + 1, i + 2));
            e.Set(new DVel(i + 3, i + 4));
            e.Set(new DHp(200 + i));
        }
    }

    private void Create()
    {
        for (var i = 0; i < _count; i++)
        {
            var e = _world.CreateEntity();
            e.Set(new DPos(i + 1, i + 2));
            e.Set(new DVel(i + 3, i + 4));
            e.Set(new DHp(200 + i));
            if ((i & 1) == 0) e.Remove<DVel>();
        }
    }

    private void Mixed()
    {
        var halfN = (_count + 1) / 2;
        var created = new DefaultEntity[halfN];
        var ci = 0;
        for (var i = 0; i < _count; i++)
        {
            if ((i & 1) == 0)
            {
                var e = _entities[i];
                e.Set(new DPos(i + 1, i + 2));
                e.Set(new DVel(i + 3, i + 4));
                if ((i & 3) == 0) e.Remove<DHp>();
                else               e.Set(new DHp(300 + i));
            }
            else
            {
                var e = _world.CreateEntity();
                e.Set(new DPos(i + 11, i + 12));
                e.Set(new DVel(i + 13, i + 14));
                e.Set(new DHp(400 + i));
                if ((i & 3) == 1) e.Remove<DVel>();
                created[ci++] = e;
            }
        }
        for (var i = 0; i < ci; i++) created[i].Dispose();
    }
}

// ── Friflo components ──
public readonly record struct FPos(int X, int Y) : IComponent;
public readonly record struct FVel(int X, int Y) : IComponent;
public readonly record struct FHp(int Value) : IComponent;
public readonly record struct FArmor(int Value) : IComponent;

// ── DefaultEcs components ──
public readonly record struct DPos(int X, int Y);
public readonly record struct DVel(int X, int Y);
public readonly record struct DHp(int Value);
public readonly record struct DArmor(int Value);
