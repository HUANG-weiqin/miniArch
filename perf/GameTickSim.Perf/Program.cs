using System.Diagnostics;
using MiniArchBenchmarks.GameTick;
using GameTickSim;

if (args.Length > 0 && args[0] == "--scenarios")
{
    ScenarioBenchmark.RunAll(args.Length > 1 ? args[1] : null);
    return;
}

Console.WriteLine("=== GameTickSim ECS Performance Test ===");
Console.WriteLine($"Entities: {GameTickData.TotalEntityCount}");
Console.WriteLine($"Duration: {GameTickData.DurationSeconds}s");
Console.WriteLine($"Spawn/tick: {GameTickData.SpawnPerTick}, Destroy/tick: {GameTickData.DestroyPerTick}, Debuff/tick: {GameTickData.DebuffPerTick}");
Console.WriteLine();

var results = new List<(string name, double ticksPerSec, double avgMs, int totalTicks, double heapDeltaKB, bool memoryStable)>();

using (var miniWorld = MiniGameTickWorldFactory.CreateWorld())
using (var defaultWorld = DefaultGameTickWorldFactory.CreateWorld())
using (var archWorld = ArchGameTickWorldFactory.CreateWorld())
{
    var frifloWorld = FrifloGameTickWorldFactory.CreateWorld();

    Console.WriteLine($"MiniArch  world created: {miniWorld.EntityCount} entities");
    Console.WriteLine($"DefaultEcs world created: {defaultWorld} entities");
    Console.WriteLine($"Arch      world created: {archWorld} entities");
    Console.WriteLine($"Friflo    world created: {frifloWorld.Count} entities");
    Console.WriteLine();

    MiniGameTickRunner.Initialize(miniWorld);
    DefaultGameTickRunner.Initialize(defaultWorld);
    ArchGameTickRunner.Initialize(archWorld);
    FrifloGameTickRunner.Initialize(frifloWorld);

    results.Add(Run("MiniArch", () => MiniGameTickRunner.ExecuteTick(miniWorld)));
    results.Add(Run("DefaultEcs", () => DefaultGameTickRunner.ExecuteTick(defaultWorld)));
    results.Add(Run("Arch", () => ArchGameTickRunner.ExecuteTick(archWorld)));
    results.Add(Run("Friflo", () => FrifloGameTickRunner.ExecuteTick(frifloWorld)));
}

Console.WriteLine();
Console.WriteLine("=== GameTickSim Final Summary ===");
Console.WriteLine();
Console.WriteLine($"{"Engine",12} | {"Ticks/s",10} | {"ms/tick",10} | {"Ticks",8} | {"Heap Δ KB",10} | {"Memory",8}");
Console.WriteLine(new string('-', 70));
foreach (var (name, ticksPerSec, avgMs, totalTicks, heapDeltaKB, memoryStable) in results)
{
    Console.WriteLine($"{name,12} | {ticksPerSec,10:F1} | {avgMs,10:F3} | {totalTicks,8} | {heapDeltaKB,10:F1} | {(memoryStable ? "OK" : "WARN"),8}");
}
Console.WriteLine();

if (results.Count >= 2)
{
    var first = results[0];
    for (var i = 1; i < results.Count; i++)
    {
        var other = results[i];
        var diff = ((first.ticksPerSec - other.ticksPerSec) / other.ticksPerSec) * 100;
        Console.WriteLine($"{first.name} vs {other.name}: {diff:+0.00;-0.00}% ({first.ticksPerSec:F1} / {other.ticksPerSec:F1} ticks/s)");
    }
}

(string name, double ticksPerSec, double avgMs, int totalTicks, double heapDeltaKB, bool memoryStable) Run(string label, Func<int> tickFunc)
{
    Console.WriteLine($"--- {label} ---");

    var sw = Stopwatch.StartNew();

    for (var i = 0; i < GameTickData.WarmupTicks; i++)
        tickFunc();

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    var baselineHeap = GC.GetTotalMemory(true);

    var gen0Base = GC.CollectionCount(0);
    var gen1Base = GC.CollectionCount(1);
    var gen2Base = GC.CollectionCount(2);

    Console.WriteLine($"{"Tick",7} | {"Ticks/s",10} | {"Heap MB",9} | {"dHeap KB",10} | {"WS MB",8} | {"Gen0",5} | {"Gen1",5} | {"Gen2",5}");
    Console.WriteLine(new string('-', 75));

    sw.Restart();
    var totalTicks = 0;
    var lastReportTime = sw.ElapsedMilliseconds;
    var lastReportTicks = 0L;

    while (sw.ElapsedMilliseconds < GameTickData.DurationSeconds * 1000L)
    {
        tickFunc();
        totalTicks++;

        if (totalTicks % GameTickData.ReportInterval == 0)
        {
            var now = sw.ElapsedMilliseconds;
            var elapsed = (now - lastReportTime) / 1000.0;
            var ticksPerSec = (totalTicks - lastReportTicks) / elapsed;

            var currentHeap = GC.GetTotalMemory(false);
            var heapMB = currentHeap / (1024.0 * 1024.0);
            var deltaHeapKB = (currentHeap - baselineHeap) / 1024.0;

            var process = Process.GetCurrentProcess();
            var wsMB = process.WorkingSet64 / (1024.0 * 1024.0);

            var gen0 = GC.CollectionCount(0) - gen0Base;
            var gen1 = GC.CollectionCount(1) - gen1Base;
            var gen2 = GC.CollectionCount(2) - gen2Base;

            Console.WriteLine($"{totalTicks,7} | {ticksPerSec,10:F1} | {heapMB,9:F2} | {deltaHeapKB,10:F1} | {wsMB,8:F1} | {gen0,5} | {gen1,5} | {gen2,5}");

            lastReportTime = now;
            lastReportTicks = totalTicks;
        }
    }

    sw.Stop();
    var totalElapsed = sw.ElapsedMilliseconds / 1000.0;
    var avgThroughput = totalTicks / totalElapsed;
    var avgTimePerTick = totalElapsed / totalTicks * 1000.0;

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    var finalHeap = GC.GetTotalMemory(true);
    var heapDeltaKB = (finalHeap - baselineHeap) / 1024.0;
    var memoryStable = heapDeltaKB < 1024;

    var gen0Total = GC.CollectionCount(0) - gen0Base;
    var gen1Total = GC.CollectionCount(1) - gen1Base;
    var gen2Total = GC.CollectionCount(2) - gen2Base;

    Console.WriteLine();
    Console.WriteLine($"  Total ticks:       {totalTicks}");
    Console.WriteLine($"  Avg throughput:    {avgThroughput:F1} ticks/s");
    Console.WriteLine($"  Avg time/tick:     {avgTimePerTick:F3}ms");
    Console.WriteLine($"  Heap delta:        {heapDeltaKB:F1} KB  ({(memoryStable ? "stable" : "WARN - growing")})");
    Console.WriteLine($"  GC Gen0/1/2:       {gen0Total}/{gen1Total}/{gen2Total}");
    Console.WriteLine();

    return (label, avgThroughput, avgTimePerTick, totalTicks, heapDeltaKB, memoryStable);
}
