using System.Diagnostics;

using MiniArch;
using MiniArch.Diagnostics;

internal static class Program
{
    private const int FullClearEntities = 50_000;
    private const int QueryEntities = 40_000;
    private const int CascadeRoots = 15_000;
    private const int PartialCascadeRoots = 20_000;
    private const int Iterations = 8;

    private static readonly QueryDescription PositionQuery = new QueryDescription().With<Position>();

    private static int Main()
    {
        Console.WriteLine("DestroyMany.Perf — Release-only destroy phase comparison");
        Console.WriteLine("Setup is outside timed region. Baseline is guarded for Destroy.");
        Console.WriteLine();

        var scenarios = new[]
        {
            new Scenario(
                "DestroyMany full dense archetype",
                FullClearEntities,
                Iterations,
                BuildFullClearWorld,
                DestroyWithGuardedLoop,
                DestroyWithDestroyMany,
                MinSpeedup: 1.20),
            new Scenario(
                "Destroy(query) full Position query",
                QueryEntities,
                Iterations,
                BuildQueryWorld,
                DestroyPositionQueryWithMaterializedLoop,
                DestroyPositionQuery,
                MinSpeedup: 1.20),
            new Scenario(
                "DestroyMany cascade forest",
                CascadeRoots,
                Iterations,
                BuildCascadeForestWorld,
                DestroyWithGuardedLoop,
                DestroyWithDestroyMany,
                MinSpeedup: 1.20),
            new Scenario(
                "DestroyMany partial cascade forest",
                PartialCascadeRoots / 2,
                Iterations,
                BuildPartialCascadeForestWorld,
                DestroyWithGuardedLoop,
                DestroyWithDestroyMany,
                MinSpeedup: 1.05),
        };

        var allPassed = true;
        foreach (var scenario in scenarios)
        {
            VerifyEquivalent(scenario);
            Warmup(scenario);

            var baseline = Measure(scenario, scenario.Baseline);
            var batch = Measure(scenario, scenario.Batch);
            var speedup = baseline.Elapsed.TotalMilliseconds / batch.Elapsed.TotalMilliseconds;

            Console.WriteLine($"{scenario.Name}");
            Console.WriteLine($"  entities      : {scenario.EntityCount:N0}");
            Console.WriteLine($"  for Destroy   : {baseline.Elapsed.TotalMilliseconds,9:F3} ms total | {baseline.MicrosecondsPerIteration,9:F3} us/iter | alloc {baseline.AllocatedBytesPerIteration,8:F0} B/iter");
            Console.WriteLine($"  batch API     : {batch.Elapsed.TotalMilliseconds,9:F3} ms total | {batch.MicrosecondsPerIteration,9:F3} us/iter | alloc {batch.AllocatedBytesPerIteration,8:F0} B/iter");
            Console.WriteLine($"  speedup       : {speedup,9:F2}x");

            if (speedup < scenario.MinSpeedup)
            {
                allPassed = false;
                Console.WriteLine($"  FAIL          : expected >= {scenario.MinSpeedup:F2}x");
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
            {
                throw new InvalidOperationException($"{scenario.Name}: invalid world after destroy.");
            }

            if (!expected.World.CanonicalChecksum().SequenceEqual(actual.World.CanonicalChecksum()))
            {
                throw new InvalidOperationException($"{scenario.Name}: canonical checksum mismatch.");
            }

            var diff = WorldDiff.Compare(expected.World, actual.World);
            if (!diff.AreIdentical)
            {
                throw new InvalidOperationException(
                    $"{scenario.Name}: diff mismatch. EntityDiffs={diff.EntityDiffs.Count}, FreeListDiff={diff.FreeListDiff is not null}");
            }
        }
        finally
        {
            expected.World.Dispose();
            actual.World.Dispose();
        }
    }

    private static void Warmup(Scenario scenario)
    {
        for (var i = 0; i < 2; i++)
        {
            var baseline = scenario.Setup();
            scenario.Baseline(baseline.World, baseline.Targets);
            baseline.World.Dispose();

            var batch = scenario.Setup();
            scenario.Batch(batch.World, batch.Targets);
            batch.World.Dispose();
        }
    }

    private static Measurement Measure(Scenario scenario, DestroyAction action)
    {
        var stopwatch = new Stopwatch();
        long allocatedBytes = 0;

        for (var i = 0; i < scenario.Iterations; i++)
        {
            var setup = scenario.Setup();

            var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
            stopwatch.Start();
            action(setup.World, setup.Targets);
            stopwatch.Stop();
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;

            setup.World.Dispose();
        }

        return new Measurement(stopwatch.Elapsed, allocatedBytes, scenario.Iterations);
    }

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

    private static WorldSetup BuildCascadeForestWorld()
    {
        var world = new World(entityCapacity: CascadeRoots * 3);
        var roots = new Entity[CascadeRoots];

        for (var i = 0; i < roots.Length; i++)
        {
            var root = world.Create(
                new Position(i, i + 1),
                new Velocity(i + 2, i + 3),
                new A(i),
                new B(i),
                new C(i),
                new D(i));
            var child = world.Create(
                new Position(i + 4, i + 5),
                new A(i + 6),
                new B(i + 7),
                new C(i + 8),
                new D(i + 9));
            var grandChild = world.Create(
                new Velocity(i + 10, i + 11),
                new A(i + 12),
                new B(i + 13),
                new C(i + 14),
                new D(i + 15));
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
                new Position(i, i + 1),
                new Velocity(i + 2, i + 3),
                new A(i),
                new B(i),
                new C(i),
                new D(i));
            var child = world.Create(
                new Position(i + 4, i + 5),
                new A(i + 6),
                new B(i + 7),
                new C(i + 8),
                new D(i + 9));
            var grandChild = world.Create(
                new Velocity(i + 10, i + 11),
                new A(i + 12),
                new B(i + 13),
                new C(i + 14),
                new D(i + 15));
            world.AddChild(root, child);
            world.AddChild(child, grandChild);

            if ((i & 1) == 0)
                roots[i / 2] = root;
        }

        return new WorldSetup(world, roots);
    }

    private static void DestroyWithGuardedLoop(World world, Entity[] targets)
    {
        for (var i = 0; i < targets.Length; i++)
        {
            var entity = targets[i];
            if (world.IsAlive(entity))
                world.Destroy(entity);
        }
    }

    private static void DestroyWithDestroyMany(World world, Entity[] targets)
    {
        world.DestroyMany(targets);
    }

    private static void DestroyPositionQueryWithMaterializedLoop(World world, Entity[] _)
    {
        var targets = new List<Entity>(QueryEntities);
        foreach (var entity in world.Query(in PositionQuery))
            targets.Add(entity);

        for (var i = 0; i < targets.Count; i++)
        {
            var entity = targets[i];
            if (world.IsAlive(entity))
                world.Destroy(entity);
        }
    }

    private static void DestroyPositionQuery(World world, Entity[] _)
    {
        world.Destroy(in PositionQuery);
    }

    private delegate void DestroyAction(World world, Entity[] targets);

    private readonly record struct Scenario(
        string Name,
        int EntityCount,
        int Iterations,
        Func<WorldSetup> Setup,
        DestroyAction Baseline,
        DestroyAction Batch,
        double MinSpeedup);

    private readonly record struct WorldSetup(World World, Entity[] Targets);

    private readonly record struct Measurement(TimeSpan Elapsed, long AllocatedBytes, int Iterations)
    {
        public double MicrosecondsPerIteration => Elapsed.TotalMicroseconds / Iterations;
        public double AllocatedBytesPerIteration => (double)AllocatedBytes / Iterations;
    }

    private readonly record struct Position(int X, int Y);
    private readonly record struct Velocity(int X, int Y);
    private readonly record struct A(int Value);
    private readonly record struct B(int Value);
    private readonly record struct C(int Value);
    private readonly record struct D(int Value);
}
