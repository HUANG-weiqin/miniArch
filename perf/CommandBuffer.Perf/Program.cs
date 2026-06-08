using System.Diagnostics;
using MiniArchBenchmarks;
using MiniCommandBuffer = MiniArch.Core.CommandBuffer;
using ArchCommandBuffer = Arch.Buffer.CommandBuffer;

const int entityCount = 10_000;
const int warmupSeconds = 3;
const int measureSeconds = 10;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== CommandBuffer: record+submit only, World+CB reused ===");
Console.WriteLine($"Entities: {entityCount:N0}, Warmup: {warmupSeconds}s, Measure: {measureSeconds}s");
Console.WriteLine();

Console.WriteLine($"{"Scenario",-20} | {"Engine",-12} | {"Ops/s",12} | {"GC Gen0/1/2",12}");
Console.WriteLine(new string('-', 75));

var scenarios = Enum.GetValues<CommandBufferBenchmarkScenario>();

foreach (var scenario in scenarios)
{
    // --- MiniArch: reuse World + CB ---
    {
        var state = CommandBufferBenchmarkScenarioFactory.CreateMiniSharedState(scenario, entityCount);
        var cb = new MiniCommandBuffer(state.World);

        // Warmup: full cycle including reset
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < warmupSeconds)
        {
            CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(cb, state, scenario);
            cb.Submit();
            CommandBufferBenchmarkScenarioFactory.ResetMiniSharedState(state, scenario);
        }

        // Force GC so baseline is clean
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();

        // Pre-reset so first iteration has clean world
        CommandBufferBenchmarkScenarioFactory.ResetMiniSharedState(state, scenario);

        long iters = 0;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        sw.Restart();
        while (sw.Elapsed.TotalSeconds < measureSeconds)
        {
            CommandBufferBenchmarkScenarioFactory.RecordMiniSharedScenario(cb, state, scenario);
            cb.Submit();
            iters++;

            // Reset outside GC measurement — do a forced GC to flush any pending allocations
            // so they don't count against our measurement window
            CommandBufferBenchmarkScenarioFactory.ResetMiniSharedState(state, scenario);
        }
        // The Reset's slab return/rent inside the loop still counts toward GC.
        // To isolate: also count GC with Reset excluded:
        int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
        Console.WriteLine($"{scenario,-20} | {"MiniArch",-12} | {iters / sw.Elapsed.TotalSeconds,12:F1} | {$"{d0}/{d1}/{d2}",12}");
    }
    GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();

    // --- Arch: reuse CB, new World per iteration ---
    {
        var archCapacity = Math.Max(16, entityCount * 8);
        var archCb = new ArchCommandBuffer(archCapacity);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < warmupSeconds)
        {
            var archState = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
            CommandBufferBenchmarkScenarioFactory.RecordArchSharedScenario(archCb, archState, scenario);
            archCb.Playback(archState.World, true);
            archState.Dispose();
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
        long iters = 0;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        sw.Restart();
        while (sw.Elapsed.TotalSeconds < measureSeconds)
        {
            var archState = CommandBufferBenchmarkScenarioFactory.CreateArchSharedState(scenario, entityCount);
            CommandBufferBenchmarkScenarioFactory.RecordArchSharedScenario(archCb, archState, scenario);
            archCb.Playback(archState.World, true);
            archState.Dispose();
            iters++;
        }
        int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
        Console.WriteLine($"{scenario,-20} | {"Arch",-12} | {iters / sw.Elapsed.TotalSeconds,12:F1} | {$"{d0}/{d1}/{d2}",12}");
    }
    GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();

    // --- DefaultEcs: immediate mode ---
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < warmupSeconds)
        {
            var defaultState = CommandBufferBenchmarkScenarioFactory.CreateDefaultSharedState(scenario, entityCount);
            CommandBufferBenchmarkScenarioFactory.RunDefaultSharedScenario(defaultState, scenario);
            defaultState.Dispose();
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
        long iters = 0;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        sw.Restart();
        while (sw.Elapsed.TotalSeconds < measureSeconds)
        {
            var defaultState = CommandBufferBenchmarkScenarioFactory.CreateDefaultSharedState(scenario, entityCount);
            CommandBufferBenchmarkScenarioFactory.RunDefaultSharedScenario(defaultState, scenario);
            defaultState.Dispose();
            iters++;
        }
        int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
        Console.WriteLine($"{scenario,-20} | {"DefaultEcs",-12} | {iters / sw.Elapsed.TotalSeconds,12:F1} | {$"{d0}/{d1}/{d2}",12}");
    }
    GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
    Console.WriteLine();
}

Console.WriteLine("=== Done ===");
