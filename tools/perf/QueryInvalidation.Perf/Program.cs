using System;
using System.Collections.Generic;
using System.Diagnostics;
using MiniWorld = MiniArch.World;
using MiniEntity = MiniArch.Entity;
using MiniQuery = MiniArch.Core.QueryCache;
using MiniQueryDescription = MiniArch.QueryDescription;
using ArchWorld = Arch.Core.World;
using ArchEntity = Arch.Core.Entity;
using ArchQuery = Arch.Core.Query;
using ArchQueryDescription = Arch.Core.QueryDescription;

namespace QueryInvalidation.Perf;

struct C0 { public int V; public C0(int v) { V = v; } }
struct C1 { public int V; public C1(int v) { V = v; } }
struct C2 { public int V; public C2(int v) { V = v; } }
struct C3 { public int V; public C3(int v) { V = v; } }
struct C4 { public int V; public C4(int v) { V = v; } }
struct C5 { public int V; public C5(int v) { V = v; } }
struct C6 { public int V; public C6(int v) { V = v; } }
struct C7 { public int V; public C7(int v) { V = v; } }
struct C8 { public int V; public C8(int v) { V = v; } }
struct C9 { public int V; public C9(int v) { V = v; } }

static class Program
{
    const int ArchetypeCount = 10;
    const int EntitiesPerArchetype = 100;
    const int DurationSeconds = 10;
    const int WarmupIterations = 1000;
    const int StructuralChangesPerIteration = 10;

    static void Main()
    {
        Console.WriteLine("=== Query Invalidation Throughput: MiniArch vs Arch ===");
        Console.WriteLine($"Archetypes:        {ArchetypeCount}");
        Console.WriteLine($"Entities/archetype: {EntitiesPerArchetype}");
        Console.WriteLine($"Duration:          {DurationSeconds}s per engine");
        Console.WriteLine($"Structural changes/iter: {StructuralChangesPerIteration}");
        Console.WriteLine($"Iteration:         entity-by-entity via cached query");
        Console.WriteLine();

        var miniResult = RunMiniArch();
        var archResult = RunArch();

        Console.WriteLine();
        Console.WriteLine("=== Comparison ===");
        Console.WriteLine($"{"Engine",-10} | {"Iter/s",12} | {"Total iter",12} | {"Heap Δ KB",10}");
        Console.WriteLine(new string('-', 55));
        Console.WriteLine($"{"MiniArch",-10} | {miniResult.IterPerSec,12:F0} | {miniResult.TotalIterations,12} | {miniResult.HeapDeltaKB,10:F1}");
        Console.WriteLine($"{"Arch",-10} | {archResult.IterPerSec,12:F0} | {archResult.TotalIterations,12} | {archResult.HeapDeltaKB,10:F1}");
        Console.WriteLine();

        double diff = (miniResult.IterPerSec - archResult.IterPerSec) / archResult.IterPerSec * 100;
        Console.WriteLine($"MiniArch vs Arch: {diff:+0.0;-0.0}% throughput");
    }

    static (double IterPerSec, long TotalIterations, double HeapDeltaKB) RunMiniArch()
    {
        Console.WriteLine("--- MiniArch ---");

        var world = new MiniWorld();
        var queries = new MiniQuery[ArchetypeCount];
        var entities = new List<MiniEntity>[ArchetypeCount];
        for (int i = 0; i < ArchetypeCount; i++)
            entities[i] = new List<MiniEntity>(EntitiesPerArchetype + StructuralChangesPerIteration);

        var descs = CreateMiniDescriptions();
        for (int i = 0; i < ArchetypeCount; i++)
            queries[i] = MiniQuery.Create(world, in descs[i]);

        PopulateMiniArch(world, entities);

        for (int i = 0; i < ArchetypeCount; i++)
            _ = queries[i].GetArchetypeSpan();

        for (int w = 0; w < WarmupIterations; w++)
        {
            MiniStructuralChange(world, entities);
            for (int i = 0; i < ArchetypeCount; i++)
                IterateMiniEntity(queries[i]);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long baselineHeap = GC.GetTotalMemory(true);

        var sw = Stopwatch.StartNew();
        long totalIterations = 0;

        while (sw.ElapsedMilliseconds < DurationSeconds * 1000L)
        {
            MiniStructuralChange(world, entities);
            for (int i = 0; i < ArchetypeCount; i++)
                IterateMiniEntity(queries[i]);
            totalIterations++;
        }

        sw.Stop();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long finalHeap = GC.GetTotalMemory(true);

        double iterPerSec = totalIterations / (sw.ElapsedMilliseconds / 1000.0);
        double heapDeltaKB = (finalHeap - baselineHeap) / 1024.0;

        Console.WriteLine($"  Iter/s: {iterPerSec:F0}, Total: {totalIterations}, Heap Δ: {heapDeltaKB:F1} KB");
        return (iterPerSec, totalIterations, heapDeltaKB);
    }

    static (double IterPerSec, long TotalIterations, double HeapDeltaKB) RunArch()
    {
        Console.WriteLine("--- Arch ---");

        var world = ArchWorld.Create();
        var descriptions = CreateArchDescriptions();
        var queries = new ArchQuery[ArchetypeCount];
        var entities = new List<ArchEntity>[ArchetypeCount];
        for (int i = 0; i < ArchetypeCount; i++)
            entities[i] = new List<ArchEntity>(EntitiesPerArchetype + StructuralChangesPerIteration);

        PopulateArch(world, entities);

        for (int i = 0; i < ArchetypeCount; i++)
            queries[i] = world.Query(in descriptions[i]);

        for (int w = 0; w < WarmupIterations; w++)
        {
            ArchStructuralChange(world, entities);
            for (int i = 0; i < ArchetypeCount; i++)
                IterateArchEntity(queries[i]);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long baselineHeap = GC.GetTotalMemory(true);

        var sw = Stopwatch.StartNew();
        long totalIterations = 0;

        while (sw.ElapsedMilliseconds < DurationSeconds * 1000L)
        {
            ArchStructuralChange(world, entities);
            for (int i = 0; i < ArchetypeCount; i++)
                IterateArchEntity(queries[i]);
            totalIterations++;
        }

        sw.Stop();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long finalHeap = GC.GetTotalMemory(true);

        double iterPerSec = totalIterations / (sw.ElapsedMilliseconds / 1000.0);
        double heapDeltaKB = (finalHeap - baselineHeap) / 1024.0;

        Console.WriteLine($"  Iter/s: {iterPerSec:F0}, Total: {totalIterations}, Heap Δ: {heapDeltaKB:F1} KB");
        world.Dispose();
        return (iterPerSec, totalIterations, heapDeltaKB);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int IterateMiniEntity(MiniQuery query)
    {
        int checksum = 0;
        var archetypes = query.GetArchetypeSpan();
        for (int ai = 0; ai < archetypes.Length; ai++)
        {
            var archetype = archetypes[ai];
            int count = archetype.EntityCount;
            var ents = archetype.GetEntityStorageUnsafe();
            for (int r = 0; r < count; r++)
                checksum += ents[r].Id;
        }
        return checksum;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int IterateArchEntity(ArchQuery query)
    {
        int checksum = 0;
        foreach (var chunk in query)
        {
            int count = chunk.Count;
            for (int r = 0; r < count; r++)
                checksum += chunk.Entity(r).Id;
        }
        return checksum;
    }

    static MiniQueryDescription[] CreateMiniDescriptions()
    {
        return
        [
            new MiniQueryDescription().With<C0>().With<C1>(),
            new MiniQueryDescription().With<C2>().With<C3>(),
            new MiniQueryDescription().With<C4>().With<C5>(),
            new MiniQueryDescription().With<C6>().With<C7>(),
            new MiniQueryDescription().With<C8>().With<C9>(),
            new MiniQueryDescription().With<C0>().With<C2>(),
            new MiniQueryDescription().With<C1>().With<C3>(),
            new MiniQueryDescription().With<C4>().With<C6>(),
            new MiniQueryDescription().With<C5>().With<C7>(),
            new MiniQueryDescription().With<C8>().With<C0>(),
        ];
    }

    static ArchQueryDescription[] CreateArchDescriptions()
    {
        return
        [
            new ArchQueryDescription().WithAll<C0, C1>(),
            new ArchQueryDescription().WithAll<C2, C3>(),
            new ArchQueryDescription().WithAll<C4, C5>(),
            new ArchQueryDescription().WithAll<C6, C7>(),
            new ArchQueryDescription().WithAll<C8, C9>(),
            new ArchQueryDescription().WithAll<C0, C2>(),
            new ArchQueryDescription().WithAll<C1, C3>(),
            new ArchQueryDescription().WithAll<C4, C6>(),
            new ArchQueryDescription().WithAll<C5, C7>(),
            new ArchQueryDescription().WithAll<C8, C0>(),
        ];
    }

    static void PopulateMiniArch(MiniWorld world, List<MiniEntity>[] entities)
    {
        for (int i = 0; i < EntitiesPerArchetype; i++)
        {
            entities[0].Add(world.Create(new C0(i), new C1(i)));
            entities[1].Add(world.Create(new C2(i), new C3(i)));
            entities[2].Add(world.Create(new C4(i), new C5(i)));
            entities[3].Add(world.Create(new C6(i), new C7(i)));
            entities[4].Add(world.Create(new C8(i), new C9(i)));
            entities[5].Add(world.Create(new C0(i), new C2(i)));
            entities[6].Add(world.Create(new C1(i), new C3(i)));
            entities[7].Add(world.Create(new C4(i), new C6(i)));
            entities[8].Add(world.Create(new C5(i), new C7(i)));
            entities[9].Add(world.Create(new C8(i), new C0(i)));
        }
    }

    static void PopulateArch(ArchWorld world, List<ArchEntity>[] entities)
    {
        for (int i = 0; i < EntitiesPerArchetype; i++)
        {
            entities[0].Add(world.Create<C0, C1>(new C0(i), new C1(i)));
            entities[1].Add(world.Create<C2, C3>(new C2(i), new C3(i)));
            entities[2].Add(world.Create<C4, C5>(new C4(i), new C5(i)));
            entities[3].Add(world.Create<C6, C7>(new C6(i), new C7(i)));
            entities[4].Add(world.Create<C8, C9>(new C8(i), new C9(i)));
            entities[5].Add(world.Create<C0, C2>(new C0(i), new C2(i)));
            entities[6].Add(world.Create<C1, C3>(new C1(i), new C3(i)));
            entities[7].Add(world.Create<C4, C6>(new C4(i), new C6(i)));
            entities[8].Add(world.Create<C5, C7>(new C5(i), new C7(i)));
            entities[9].Add(world.Create<C8, C0>(new C8(i), new C0(i)));
        }
    }

    static void MiniStructuralChange(MiniWorld world, List<MiniEntity>[] entities)
    {
        var list = entities[0];
        int n = Math.Min(StructuralChangesPerIteration, list.Count);
        for (int i = 0; i < n; i++)
        {
            int last = list.Count - 1;
            world.Destroy(list[last]);
            list.RemoveAt(last);
        }
        for (int i = 0; i < n; i++)
            list.Add(world.Create(new C0(i), new C1(i)));
    }

    static void ArchStructuralChange(ArchWorld world, List<ArchEntity>[] entities)
    {
        var list = entities[0];
        int n = Math.Min(StructuralChangesPerIteration, list.Count);
        for (int i = 0; i < n; i++)
        {
            int last = list.Count - 1;
            world.Destroy(list[last]);
            list.RemoveAt(last);
        }
        for (int i = 0; i < n; i++)
            list.Add(world.Create<C0, C1>(new C0(i), new C1(i)));
    }
}
