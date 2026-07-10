using System.Diagnostics;

using MiniArch;
using MiniArch.Diagnostics;

internal static class Program
{
    private const int FullClearEntities = 50_000;
    private const int QueryEntities = 40_000;
    private const int MultiArchCount = 16;
    private const int MultiArchEntitiesPer = 2_500;
    private const int MultiArchTotal = MultiArchCount * MultiArchEntitiesPer;
    private const int CascadeRoots = 15_000;
    private const int PartialCascadeRoots = 20_000;
    private const int SparseTotal = 50_000;
    private const int SparseKill = 4;

    private const int WarmupSeconds = 2;
    private const int MeasureSeconds = 3;

    private static readonly QueryDescription PositionQuery = new QueryDescription().With<Position>();

    private static int Main()
    {
        Console.WriteLine("DestroyMany.Perf — steady-state throughput (warmup {0}s + measure {1}s)",
            WarmupSeconds, MeasureSeconds);
        Console.WriteLine("Only the destroy phase is timed; setup/dispose is outside.");
        Console.WriteLine();

        var scenarios = new[]
        {
            new Scenario(
                "full dense archetype",
                FullClearEntities,
                BuildFullClearWorld,
                DestroyWithGuardedLoop,
                DestroyWithBatchDestroy,
                MinSpeedup: 1.20),
            new Scenario(
                "Destroy(query) Position [1 archetype]",
                QueryEntities,
                BuildQueryWorld,
                DestroyPositionQueryWithMaterializedLoop,
                DestroyPositionQuery,
                MinSpeedup: 1.20),
            new Scenario(
                $"Destroy(query) Position [{MultiArchCount} archetypes]",
                MultiArchTotal,
                BuildMultiArchetypeWorld,
                DestroyPositionQueryWithMaterializedLoop,
                DestroyPositionQuery,
                MinSpeedup: 1.20),
            new Scenario(
                "full cascade forest",
                CascadeRoots,
                BuildCascadeForestWorld,
                DestroyWithGuardedLoop,
                DestroyWithBatchDestroy,
                MinSpeedup: 1.20),
            new Scenario(
                "partial cascade forest",
                PartialCascadeRoots / 2,
                BuildPartialCascadeForestWorld,
                DestroyWithGuardedLoop,
                DestroyWithBatchDestroy,
                MinSpeedup: 1.05),
            new Scenario(
                "sparse: 4 of 50000",
                SparseKill,
                BuildSparseWorld,
                DestroyWithGuardedLoop,
                DestroyWithBatchDestroy,
                MinSpeedup: 0.0),
        };

        var allPassed = true;
        foreach (var scenario in scenarios)
        {
            VerifyEquivalent(scenario);

            Console.Write("  warmup... ");
            RunForDuration(scenario, scenario.Baseline, WarmupSeconds);
            RunForDuration(scenario, scenario.Batch, WarmupSeconds);
            Console.WriteLine("done");

            var baseline = RunForDuration(scenario, scenario.Baseline, MeasureSeconds);
            var batch = RunForDuration(scenario, scenario.Batch, MeasureSeconds);
            var speedup = batch.OpsPerSecond / baseline.OpsPerSecond;

            Console.WriteLine($"{scenario.Name}");
            Console.WriteLine($"  entities   : {scenario.EntityCount:N0}");
            Console.WriteLine($"  for Destroy : {baseline.OpsPerSecond,9:F1} ops/s | {baseline.UsPerOp,8:F1} us/op | alloc {baseline.BytesPerOp,6:F1} B/op");
            Console.WriteLine($"  batch API   : {batch.OpsPerSecond,9:F1} ops/s | {batch.UsPerOp,8:F1} us/op | alloc {batch.BytesPerOp,6:F1} B/op");
            Console.WriteLine($"  speedup     : {speedup,9:F2}x");

            if (speedup < scenario.MinSpeedup)
            {
                allPassed = false;
                Console.WriteLine($"  FAIL        : expected >= {scenario.MinSpeedup:F2}x");
            }

            Console.WriteLine();
        }

        PrintSteadyStateAlloc();
        PrintClearPerf(ref allPassed);
        PrintThresholdSweep(ref allPassed);

        return allPassed ? 0 : 1;
    }

    // Compares three approaches for query-based bulk entity removal:
    //   1. for-loop: materialize query → Destroy each
    //   2. Destroy(query): safe batch (collect → group → compact → kill)
    //   3. Clear(query): archetype-level reset, no collection
    private static void PrintClearPerf(ref bool allPassed)
    {
        const int Seconds = 3;

        Console.WriteLine("Clear(query) — archetype-level bulk entity removal (no hierarchy)");
        Console.WriteLine();

        // Correctness: Clear must produce same surviving-entity set
        // as Destroy(query) when there is no hierarchy.
        VerifyClear(BuildQueryWorld, QueryEntities, "1 archetype");
        VerifyClear(BuildMultiArchetypeWorld, MultiArchTotal, $"{MultiArchCount} archetypes");

        // 1 archetype
        BenchmarkDestroy("Clear [1 archetype]",
            QueryEntities, BuildQueryWorld,
            world => DestroyPositionQueryWithMaterializedLoop(world, []),
            world => world.Destroy(in PositionQuery),
            world => world.Clear(in PositionQuery),
            Seconds, ref allPassed);

        // 16 archetypes
        BenchmarkDestroy($"Clear [{MultiArchCount} archetypes]",
            MultiArchTotal, BuildMultiArchetypeWorld,
            world => DestroyPositionQueryWithMaterializedLoop(world, []),
            world => world.Destroy(in PositionQuery),
            world => world.Clear(in PositionQuery),
            Seconds, ref allPassed);

        Console.WriteLine();
    }

    private static void VerifyClear(Func<WorldSetup> setup, int expectedEntities, string label)
    {
        var safe = setup();
        var clearWorld = setup();

        try
        {
            safe.World.Destroy(in PositionQuery);
            clearWorld.World.Clear(in PositionQuery);

            var safeValidation = WorldValidator.Validate(safe.World);
            var clearValidation = WorldValidator.Validate(clearWorld.World);
            if (!safeValidation.IsValid || !clearValidation.IsValid)
                throw new InvalidOperationException($"Clear [{label}]: invalid world.");

            // Surviving entities (without Position) must be identical.
            var safeSurvivors = safe.World.CanonicalChecksum();
            var clearSurvivors = clearWorld.World.CanonicalChecksum();
            if (!safeSurvivors.SequenceEqual(clearSurvivors))
                throw new InvalidOperationException($"Clear [{label}]: checksum mismatch.");

            // All cleared entities must be dead.
            foreach (var entity in safe.World.Query(in PositionQuery))
                throw new InvalidOperationException($"Clear [{label}]: entity still alive after clear.");
        }
        finally
        {
            safe.World.Dispose();
            clearWorld.World.Dispose();
        }
    }

    private static void BenchmarkDestroy(
        string label, int entityCount, Func<WorldSetup> setup,
        Action<World> forLoop, Action<World> safeBatch, Action<World> unsafeBatch,
        int seconds, ref bool allPassed)
    {
        // Warmup
        WarmupAction(setup, forLoop, 1);
        WarmupAction(setup, safeBatch, 1);
        WarmupAction(setup, unsafeBatch, 1);

        var forResult = MeasureAction(setup, forLoop, seconds);
        var safeResult = MeasureAction(setup, safeBatch, seconds);
        var unsafeResult = MeasureAction(setup, unsafeBatch, seconds);

        Console.WriteLine($"  {label} ({entityCount:N0} entities)");
        Console.WriteLine($"    for-loop           : {forResult.UsPerOp,8:F1} us/op | alloc {forResult.BytesPerOp,8:F1} B/op");
        Console.WriteLine($"    Destroy(query)     : {safeResult.UsPerOp,8:F1} us/op | {forResult.UsPerOp / safeResult.UsPerOp,5:F2}x vs for");
        Console.WriteLine($"    Clear(query)       : {unsafeResult.UsPerOp,8:F1} us/op | {forResult.UsPerOp / unsafeResult.UsPerOp,5:F2}x vs for | {safeResult.UsPerOp / unsafeResult.UsPerOp,5:F2}x vs safe");
    }

    private static void WarmupAction(Func<WorldSetup> setup, Action<World> action, int seconds)
    {
        var deadline = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var s = setup();
            action(s.World);
            s.World.Dispose();
        }
    }

    private static Measurement MeasureAction(Func<WorldSetup> setup, Action<World> action, int seconds)
    {
        var deadline = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        var ops = 0;
        var ticks = 0L;
        var allocBytes = 0L;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            var s = setup();
            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            action(s.World);
            var end = Stopwatch.GetTimestamp();
            ticks += end - start;
            allocBytes += GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            s.World.Dispose();
            ops++;
        }

        var ms = (double)ticks / Stopwatch.Frequency * 1000;
        return new Measurement(ops, ms, allocBytes);
    }

    // Demonstrates that batch allocation drops to zero when the same World is
    // reused (as in a real game loop). The throughput benchmarks above create a
    // fresh World per iteration, which forces QueryCache creation every time.
    private static void PrintSteadyStateAlloc()
    {
        const int iterations = 200;

        Console.WriteLine("steady-state alloc (same World reused, after cache warmup)");
        Console.WriteLine();

        // --- batch: Destroy(query), 1 archetype ---
        SteadyStateRun("batch   Destroy(query) [1 archetype]",
            QueryEntities, PopulateQueryEntities, world => world.Destroy(in PositionQuery),
            iterations);

        // --- batch: Destroy(query), 16 archetypes ---
        SteadyStateRun($"batch   Destroy(query) [{MultiArchCount} archetypes]",
            MultiArchTotal, PopulateMultiArchetypeEntities, world => world.Destroy(in PositionQuery),
            iterations);

        // --- for-loop baseline (always allocates List<Entity>) ---
        SteadyStateRun("forloop Destroy(query) [1 archetype]",
            QueryEntities, PopulateQueryEntities,
            world => DestroyPositionQueryWithMaterializedLoop(world, []),
            iterations);

        Console.WriteLine();
    }

    private static void SteadyStateRun(
        string label, int entityCount,
        Action<World> populate,
        Action<World> destroy,
        int iterations)
    {
        var world = new World(entityCapacity: entityCount + 16);
        populate(world);
        // Add distractors (entities without Position, not matched by query).
        for (var i = 0; i < 16; i++)
            world.Create(new Velocity(-i, i));
        // Prime caches: first destroy creates QueryCache / warms JIT.
        destroy(world);
        // After destroy, all matched entities are in the free-list.
        // Repopulate reuses archetype storage + recycles IDs — no growth alloc.

        var totalAlloc = 0L;
        var totalTicks = 0L;

        for (var i = 0; i < iterations; i++)
        {
            populate(world);

            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            destroy(world);
            totalTicks += Stopwatch.GetTimestamp() - start;
            totalAlloc += GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        }

        var ms = (double)totalTicks / Stopwatch.Frequency * 1000;
        var usPerOp = ms * 1000 / iterations;
        var bPerOp = (double)totalAlloc / iterations;

        Console.WriteLine($"  {label}");
        Console.WriteLine($"    alloc {bPerOp,8:F1} B/op | {usPerOp,8:F1} us/op");
        world.Dispose();
    }

    private static void PopulateQueryEntities(World world)
    {
        for (var i = 0; i < QueryEntities; i++)
        {
            world.Create(
                new Position(i, i + 1),
                new Velocity(i + 2, i + 3),
                new A(i + 4),
                new B(i + 5),
                new C(i + 6),
                new D(i + 7));
        }
    }

    private static void PopulateMultiArchetypeEntities(World world)
    {
        for (var arch = 0; arch < MultiArchCount; arch++)
        {
            for (var i = 0; i < MultiArchEntitiesPer; i++)
            {
                var id = arch * MultiArchEntitiesPer + i;
                var p = new Position(id, id + 1);
                var v = new Velocity(id + 2, id + 3);
                var a = new A(id + 4);
                var b = new B(id + 5);
                var c = new C(id + 6);
                var d = new D(id + 7);

                switch (arch)
                {
                    case 0: world.Create(p); break;
                    case 1: world.Create(p, v); break;
                    case 2: world.Create(p, a); break;
                    case 3: world.Create(p, b); break;
                    case 4: world.Create(p, c); break;
                    case 5: world.Create(p, d); break;
                    case 6: world.Create(p, v, a); break;
                    case 7: world.Create(p, v, b); break;
                    case 8: world.Create(p, v, c); break;
                    case 9: world.Create(p, v, d); break;
                    case 10: world.Create(p, a, b); break;
                    case 11: world.Create(p, a, c); break;
                    case 12: world.Create(p, b, c, d); break;
                    case 13: world.Create(p, v, a, b); break;
                    case 14: world.Create(p, v, a, b, c); break;
                    default: world.Create(p, v, a, b, c, d); break;
                }
            }
        }
    }

    // Sweep R around the SmallDestroyThreshold to prove that above the
    // threshold, batch is always faster than for-loop (speedup > 1.0).
    private static void PrintThresholdSweep(ref bool allPassed)
    {
        const int SweepM = 50_000;
        // Test both sparse and dense regimes.
        int[] sweepRs = [4, 8, 9, 16, 64, 256, 512, 5_000, 10_000, 15_000, 25_000, 40_000];
        const int SweepSeconds = 1;
        var assertCrossover = SweepM / 2; // 50% — batch reliably beats for-loop above this

        Console.WriteLine($"threshold sweep (M={SweepM:N0}, {SweepSeconds}s per measurement)");
        Console.WriteLine($"  R <= 8        → fast path (individual Destroy, same as for-loop)");
        Console.WriteLine($"  R < {SweepM/5,5}     → sparse (per-entity RemoveAt)");
        Console.WriteLine($"  {SweepM/5,5}..{assertCrossover,5}   → transition (break-even ≈ 30%)");
        Console.WriteLine($"  R >= {assertCrossover,5}     → dense (hole-fill compact, batch wins)");
        Console.WriteLine();
        Console.WriteLine($"  {"R",6} {"R/M",6} {"for μs",10} {"batch μs",10} {"speedup",8}  note");
        Console.WriteLine($"  {"─",6} {"─",6} {"─",10} {"─",10} {"─",8}  {"─",20}");

        foreach (var R in sweepRs)
        {
            Func<WorldSetup> setup = () => BuildSweepWorld(SweepM, R);
            var sweepScenario = new Scenario($"sweep R={R}", R, setup,
                DestroyWithGuardedLoop, DestroyWithBatchDestroy, 0);

            // Correctness check.
            VerifyEquivalent(sweepScenario);

            var baseline = RunForDuration(sweepScenario, DestroyWithGuardedLoop, SweepSeconds);
            var batch = RunForDuration(sweepScenario, DestroyWithBatchDestroy, SweepSeconds);
            var speedup = batch.OpsPerSecond / baseline.OpsPerSecond;
            var density = (double)R / SweepM;

            string note;
            if (R <= 8) note = "fast path";
            else if (R < SweepM / 5) note = "sparse";
            else if (R < assertCrossover) note = "transition";
            else note = "dense";

            Console.WriteLine($"  {R,6} {density,5:P0} {baseline.UsPerOp,10:F2} {batch.UsPerOp,10:F2} {speedup,8:F2}  {note}");

            // For dense R (>= 50%), batch must be >= 1.0x.
            if (R >= assertCrossover && speedup < 1.0)
            {
                allPassed = false;
                Console.WriteLine($"         FAIL: R={R} is dense (>= M/2), expected speedup >= 1.0x");
            }
        }

        Console.WriteLine();
    }

    private static WorldSetup BuildSweepWorld(int m, int r)
    {
        var world = new World(entityCapacity: m);
        var all = new Entity[m];
        for (var i = 0; i < m; i++)
        {
            all[i] = world.Create(
                new Position(i, i + 1),
                new Velocity(i + 2, i + 3),
                new A(i + 4),
                new B(i + 5),
                new C(i + 6),
                new D(i + 7));
        }

        var targets = new Entity[r];
        for (var i = 0; i < r; i++)
            targets[i] = all[(int)((long)i * m / r)];
        return new WorldSetup(world, targets);
    }


    private static void VerifyEquivalent(Scenario scenario)
    {
        var expected = scenario.Setup();
        var actual = scenario.Setup();

        try
        {
            scenario.Baseline(expected.World, expected.Targets);
            scenario.Batch(actual.World, actual.Targets);

            var expectedValidation = WorldValidator.Validate(expected.World);
            var actualValidation = WorldValidator.Validate(actual.World);
            if (!expectedValidation.IsValid || !actualValidation.IsValid)
                throw new InvalidOperationException($"{scenario.Name}: invalid world after destroy.");

            if (!expected.World.CanonicalChecksum().SequenceEqual(actual.World.CanonicalChecksum()))
                throw new InvalidOperationException($"{scenario.Name}: canonical checksum mismatch.");

            var diff = WorldDiff.Compare(expected.World, actual.World);
            if (!diff.AreIdentical)
                throw new InvalidOperationException(
                    $"{scenario.Name}: diff mismatch. EntityDiffs={diff.EntityDiffs.Count}, FreeListDiff={diff.FreeListDiff is not null}");
        }
        finally
        {
            expected.World.Dispose();
            actual.World.Dispose();
        }
    }

    private static Measurement RunForDuration(Scenario scenario, DestroyAction action, int seconds)
    {
        var deadline = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        var ops = 0;
        var destroyTicks = 0L;
        var destroyAlloc = 0L;

        // First op to settle JIT — not counted.
        var primed = scenario.Setup();
        action(primed.World, primed.Targets);
        primed.World.Dispose();

        while (Stopwatch.GetTimestamp() < deadline)
        {
            var setup = scenario.Setup();

            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            action(setup.World, setup.Targets);
            var end = Stopwatch.GetTimestamp();
            destroyTicks += end - start;
            destroyAlloc += GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;

            setup.World.Dispose();
            ops++;
        }

        var destroyMs = (double)destroyTicks / Stopwatch.Frequency * 1000;

        return new Measurement(ops, destroyMs, destroyAlloc);
    }

    // ─── World builders ───

    private static WorldSetup BuildFullClearWorld()
    {
        var world = new World(entityCapacity: FullClearEntities);
        var targets = new Entity[FullClearEntities];
        for (var i = 0; i < targets.Length; i++)
        {
            targets[i] = world.Create(
                new Position(i, i + 1),
                new Velocity(i + 2, i + 3),
                new A(i + 4),
                new B(i + 5),
                new C(i + 6),
                new D(i + 7));
        }
        return new WorldSetup(world, targets);
    }

    private static WorldSetup BuildQueryWorld()
    {
        var world = new World(entityCapacity: QueryEntities + 16);
        for (var i = 0; i < QueryEntities; i++)
        {
            world.Create(
                new Position(i, i + 1),
                new Velocity(i + 2, i + 3),
                new A(i + 4),
                new B(i + 5),
                new C(i + 6),
                new D(i + 7));
        }
        for (var i = 0; i < 16; i++)
            world.Create(new Velocity(-i, i));
        return new WorldSetup(world, []);
    }

    private static WorldSetup BuildMultiArchetypeWorld()
    {
        var world = new World(entityCapacity: MultiArchTotal + 16);
        for (var arch = 0; arch < MultiArchCount; arch++)
        {
            for (var i = 0; i < MultiArchEntitiesPer; i++)
            {
                var id = arch * MultiArchEntitiesPer + i;
                var p = new Position(id, id + 1);
                var v = new Velocity(id + 2, id + 3);
                var a = new A(id + 4);
                var b = new B(id + 5);
                var c = new C(id + 6);
                var d = new D(id + 7);

                switch (arch)
                {
                    case 0: world.Create(p); break;
                    case 1: world.Create(p, v); break;
                    case 2: world.Create(p, a); break;
                    case 3: world.Create(p, b); break;
                    case 4: world.Create(p, c); break;
                    case 5: world.Create(p, d); break;
                    case 6: world.Create(p, v, a); break;
                    case 7: world.Create(p, v, b); break;
                    case 8: world.Create(p, v, c); break;
                    case 9: world.Create(p, v, d); break;
                    case 10: world.Create(p, a, b); break;
                    case 11: world.Create(p, a, c); break;
                    case 12: world.Create(p, b, c, d); break;
                    case 13: world.Create(p, v, a, b); break;
                    case 14: world.Create(p, v, a, b, c); break;
                    default: world.Create(p, v, a, b, c, d); break;
                }
            }
        }
        // Distractor entities without Position — not matched by query.
        for (var i = 0; i < 16; i++)
            world.Create(new Velocity(-i, i));
        return new WorldSetup(world, []);
    }

    private static WorldSetup BuildCascadeForestWorld()
    {
        var world = new World(entityCapacity: CascadeRoots * 3);
        var roots = new Entity[CascadeRoots];

        for (var i = 0; i < roots.Length; i++)
        {
            var root = world.Create(
                new Position(i, i + 1), new Velocity(i + 2, i + 3),
                new A(i), new B(i), new C(i), new D(i));
            var child = world.Create(
                new Position(i + 4, i + 5),
                new A(i + 6), new B(i + 7), new C(i + 8), new D(i + 9));
            var grandChild = world.Create(
                new Velocity(i + 10, i + 11),
                new A(i + 12), new B(i + 13), new C(i + 14), new D(i + 15));
            world.AddChild(root, child);
            world.AddChild(child, grandChild);
            roots[i] = root;
        }
        return new WorldSetup(world, roots);
    }

    private static WorldSetup BuildPartialCascadeForestWorld()
    {
        var world = new World(entityCapacity: PartialCascadeRoots * 3);
        var roots = new Entity[PartialCascadeRoots / 2];

        for (var i = 0; i < PartialCascadeRoots; i++)
        {
            var root = world.Create(
                new Position(i, i + 1), new Velocity(i + 2, i + 3),
                new A(i), new B(i), new C(i), new D(i));
            var child = world.Create(
                new Position(i + 4, i + 5),
                new A(i + 6), new B(i + 7), new C(i + 8), new D(i + 9));
            var grandChild = world.Create(
                new Velocity(i + 10, i + 11),
                new A(i + 12), new B(i + 13), new C(i + 14), new D(i + 15));
            world.AddChild(root, child);
            world.AddChild(child, grandChild);

            if ((i & 1) == 0)
                roots[i / 2] = root;
        }
        return new WorldSetup(world, roots);
    }

    private static WorldSetup BuildSparseWorld()
    {
        var world = new World(entityCapacity: SparseTotal);
        var all = new Entity[SparseTotal];
        for (var i = 0; i < SparseTotal; i++)
        {
            all[i] = world.Create(
                new Position(i, i + 1),
                new Velocity(i + 2, i + 3),
                new A(i + 4),
                new B(i + 5),
                new C(i + 6),
                new D(i + 7));
        }
        // Kill 4 spread across the archetype: first, near-middle, near-end, last.
        var targets = new Entity[SparseKill];
        targets[0] = all[0];
        targets[1] = all[SparseTotal / 3];
        targets[2] = all[(SparseTotal * 2) / 3];
        targets[3] = all[SparseTotal - 1];
        return new WorldSetup(world, targets);
    }

    // ─── Destroy actions ───

    private static void DestroyWithGuardedLoop(World world, Entity[] targets)
    {
        for (var i = 0; i < targets.Length; i++)
        {
            if (world.IsAlive(targets[i]))
                world.Destroy(targets[i]);
        }
    }

    private static void DestroyWithBatchDestroy(World world, Entity[] targets)
    {
        world.Destroy(targets);
    }

    private static void DestroyPositionQueryWithMaterializedLoop(World world, Entity[] _)
    {
        var targets = new List<Entity>();
        foreach (var entity in world.Query(in PositionQuery))
            targets.Add(entity);

        for (var i = 0; i < targets.Count; i++)
        {
            if (world.IsAlive(targets[i]))
                world.Destroy(targets[i]);
        }
    }

    private static void DestroyPositionQuery(World world, Entity[] _)
    {
        world.Destroy(in PositionQuery);
    }

    // ─── Types ───

    private delegate void DestroyAction(World world, Entity[] targets);

    private readonly record struct Scenario(
        string Name,
        int EntityCount,
        Func<WorldSetup> Setup,
        DestroyAction Baseline,
        DestroyAction Batch,
        double MinSpeedup);

    private readonly record struct WorldSetup(World World, Entity[] Targets);

    private readonly record struct Measurement(int Ops, double DestroyMs, long AllocatedBytes)
    {
        public double OpsPerSecond => Ops / (DestroyMs / 1000);
        public double UsPerOp => DestroyMs * 1000 / Ops;
        public double BytesPerOp => (double)AllocatedBytes / Ops;
    }

    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct A(int Value);
    private readonly record struct B(int Value);
    private readonly record struct C(int Value);
    private readonly record struct D(int Value);
}
