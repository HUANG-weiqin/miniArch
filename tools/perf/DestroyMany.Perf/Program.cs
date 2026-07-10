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
                DestroyWithDestroyMany,
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
                DestroyWithDestroyMany,
                MinSpeedup: 1.20),
            new Scenario(
                "partial cascade forest",
                PartialCascadeRoots / 2,
                BuildPartialCascadeForestWorld,
                DestroyWithGuardedLoop,
                DestroyWithDestroyMany,
                MinSpeedup: 1.05),
            new Scenario(
                "sparse: 4 of 50000",
                SparseKill,
                BuildSparseWorld,
                DestroyWithGuardedLoop,
                DestroyWithDestroyMany,
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

        return allPassed ? 0 : 1;
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

    private static void DestroyWithDestroyMany(World world, Entity[] targets)
    {
        world.DestroyMany(targets);
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
