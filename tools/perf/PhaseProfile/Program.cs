using System.Diagnostics;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using MiniArch.Core;
using MiniArchBenchmarks;
// Both aliases map to the same type (MiniArch.Core.CommandStream).
// "CB" and "CS" labels in output distinguish usage patterns, not implementations.
using MiniCommandBuffer = MiniArch.Core.CommandStream;
using MiniCommandStream = MiniArch.Core.CommandStream;
using FrifloEntityStore = Friflo.Engine.ECS.EntityStore;
using FrifloCommandBuffer = Friflo.Engine.ECS.CommandBuffer;

const int N = 10_000;
const int warmup = 500;
const int measure = 5000;

var scenario = CommandBufferBenchmarkScenario.DenseExisting;

Console.WriteLine("=== Phase timing: record vs submit (DenseExisting, 10k ents) ===");
Console.WriteLine($"Warmup: {warmup}, Measure: {measure} iter");
Console.WriteLine();
Console.WriteLine($"{"Engine",-16} | {"record(us)",10} | {"submit(us)",10} | {"total(us)",10}");
Console.WriteLine(new string('-', 54));

// ── MiniArch CB ──
{
    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, N);
    var cb = new MiniCommandBuffer(state.World);
    for (var i = 0; i < warmup; i++) { RecordMini(cb, state); cb.Submit(); }

    long recordT = 0, submitT = 0;
    for (var i = 0; i < measure; i++)
    {
        var t0 = Stopwatch.GetTimestamp();
        RecordMini(cb, state);
        var t1 = Stopwatch.GetTimestamp();
        cb.Submit();
        var t2 = Stopwatch.GetTimestamp();
        recordT += t1 - t0;
        submitT += t2 - t1;
    }
    var us = 1_000_000.0 / (Stopwatch.Frequency * measure);
    Console.WriteLine($"{"MiniArch CB",-16} | {recordT * us,10:F1} | {submitT * us,10:F1} | {(recordT + submitT) * us,10:F1}");
}

// ── MiniArch CS ──
{
    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, N);
    var cs = new MiniCommandStream(state.World);
    for (var i = 0; i < warmup; i++) { RecordMiniCS(cs, state); cs.Submit(); }

    long recordT = 0, submitT = 0;
    for (var i = 0; i < measure; i++)
    {
        var t0 = Stopwatch.GetTimestamp();
        RecordMiniCS(cs, state);
        var t1 = Stopwatch.GetTimestamp();
        cs.Submit();
        var t2 = Stopwatch.GetTimestamp();
        recordT += t1 - t0;
        submitT += t2 - t1;
    }
    var us2 = 1_000_000.0 / (Stopwatch.Frequency * measure);
    Console.WriteLine($"{"MiniArch CS",-16} | {recordT * us2,10:F1} | {submitT * us2,10:F1} | {(recordT + submitT) * us2,10:F1}");
}

// ── Friflo ──
{
    var store = new FrifloEntityStore();
    store.EnsureCapacity(N * 2);
    var ids = new int[N];
    for (var i = 0; i < N; i++)
        ids[i] = store.CreateEntity(new FP(i, i + 1), new FV(i + 2, i + 3), new FH(100 + i)).Id;
    var buffer = store.GetCommandBuffer();
    buffer.ReuseBuffer = true;

    for (var i = 0; i < warmup; i++) { RecordFriflo(buffer, ids); buffer.Playback(); }

    long recordT = 0, submitT = 0;
    for (var i = 0; i < measure; i++)
    {
        var t0 = Stopwatch.GetTimestamp();
        RecordFriflo(buffer, ids);
        var t1 = Stopwatch.GetTimestamp();
        buffer.Playback();
        var t2 = Stopwatch.GetTimestamp();
        recordT += t1 - t0;
        submitT += t2 - t1;
    }
    var us = 1_000_000.0 / (Stopwatch.Frequency * measure);
    Console.WriteLine($"{"Friflo",-16} | {recordT * us,10:F1} | {submitT * us,10:F1} | {(recordT + submitT) * us,10:F1}");
}

Console.WriteLine();

// ── Per-op breakdown for MiniArch CB: remove some ops to isolate ──
Console.WriteLine("=== MiniArch CB per-op breakdown ===");
Console.WriteLine($"{"Variant",-20} | {"avg(us)",10} | {"ops",10}");
Console.WriteLine(new string('-', 46));

// All 4 ops
{
    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, N);
    var cb = new MiniCommandBuffer(state.World);
    for (var i = 0; i < warmup; i++) { RecordMini(cb, state); cb.Submit(); }
    var t0 = Stopwatch.GetTimestamp();
    for (var i = 0; i < measure; i++) { RecordMini(cb, state); cb.Submit(); }
    var t1 = Stopwatch.GetTimestamp();
    var us = (t1 - t0) * 1_000_000.0 / (Stopwatch.Frequency * measure);
    Console.WriteLine($"{"4 ops/entity",-20} | {us,10:F1} | {"4x10000=40000",10}");
}

// 3 ops only (no arm/rm)
{
    var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, N);
    var cb = new MiniCommandBuffer(state.World);
    for (var i = 0; i < warmup; i++) { RecordMini3(cb, state); cb.Submit(); }
    var t0 = Stopwatch.GetTimestamp();
    for (var i = 0; i < measure; i++) { RecordMini3(cb, state); cb.Submit(); }
    var t1 = Stopwatch.GetTimestamp();
    var us = (t1 - t0) * 1_000_000.0 / (Stopwatch.Frequency * measure);
    Console.WriteLine($"{"3 ops/entity",-20} | {us,10:F1} | {"3x10000=30000",10}");
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

[MethodImpl(MethodImplOptions.NoInlining)]
static void RecordMini(MiniCommandBuffer cb, MiniSharedCommandBufferState state)
{
    for (var i = 0; i < state.ExistingEntities.Length; i++)
    {
        var e = state.ExistingEntities[i];
        cb.Set(e, new BenchmarkPosition(i + 1, i + 2));
        cb.Set(e, new BenchmarkVelocity(i + 3, i + 4));
        cb.Set(e, new BenchmarkHealth(200 + i));
        if ((i & 1) == 0) cb.Remove<BenchmarkHealth>(e);
        else               cb.Add(e, new BenchmarkArmor(300 + i));
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void RecordMiniCS(MiniCommandStream cb, MiniSharedCommandBufferState state)
{
    for (var i = 0; i < state.ExistingEntities.Length; i++)
    {
        var e = state.ExistingEntities[i];
        cb.Set(e, new BenchmarkPosition(i + 1, i + 2));
        cb.Set(e, new BenchmarkVelocity(i + 3, i + 4));
        cb.Set(e, new BenchmarkHealth(200 + i));
        if ((i & 1) == 0) cb.Remove<BenchmarkHealth>(e);
        else               cb.Add(e, new BenchmarkArmor(300 + i));
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void RecordMini3(MiniCommandBuffer cb, MiniSharedCommandBufferState state)
{
    for (var i = 0; i < state.ExistingEntities.Length; i++)
    {
        var e = state.ExistingEntities[i];
        cb.Set(e, new BenchmarkPosition(i + 1, i + 2));
        cb.Set(e, new BenchmarkVelocity(i + 3, i + 4));
        cb.Set(e, new BenchmarkHealth(200 + i));
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void RecordFriflo(FrifloCommandBuffer buffer, int[] ids)
{
    for (var i = 0; i < ids.Length; i++)
    {
        var id = ids[i];
        buffer.AddComponent(id, new FP(i + 1, i + 2));
        buffer.AddComponent(id, new FV(i + 3, i + 4));
        buffer.AddComponent(id, new FH(200 + i));
        if ((i & 1) == 0) buffer.RemoveComponent<FH>(id);
        else               buffer.AddComponent(id, new FA(300 + i));
    }
}

public readonly record struct FP(int X, int Y) : IComponent;
public readonly record struct FV(int X, int Y) : IComponent;
public readonly record struct FH(int Value) : IComponent;
public readonly record struct FA(int Value) : IComponent;
