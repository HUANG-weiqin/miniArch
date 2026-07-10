// ComponentBucketQuery benchmarking: ScanOneKey vs ManualExpanded vs BucketQuery.
//
// Matrix:
//   P = 4 / 16 / 64   (distinct TComponent values = partition count)
//   N = 100000
//   Workloads: CountOnly, Read2, Write3, AutoFreshness
//
// Usage:
//   dotnet run -c Release --project tools/perf/ComponentBucketIndex.Perf (project folder name unchanged)

using System.Diagnostics;
using MiniArch;

// ════════════════════════════════════════════════════════════════
// Top-level
// ════════════════════════════════════════════════════════════════

const int N = 100_000;       // total entities
const int Warmup = 8;        // warmup rounds before measuring
const int MeasureMs = 3000;  // measurement window per case
const int MoveDiv = 10;      // AutoFreshness: modify 1/10th per round

int[] partitionSizes = [4, 16, 64];

Console.WriteLine("=== ComponentBucketQuery Benchmark ===");
Console.WriteLine($"Entities: {N:N0}  |  Warmup: {Warmup} rounds  |  Measure: {MeasureMs}ms  |  MoveDiv: {MoveDiv}");
Console.WriteLine();

// Header: throughput row
Console.WriteLine("  {0,-16}  {1,14}  {2,14}  {3,14}", "Workload", "ScanOneKey", "ManualExpanded", "BucketQuery");
Console.WriteLine($"  {new string('-', 62)}");
// Sub-header: what the first data row is
Console.WriteLine("  {0,-16}  {1,14}  {2,14}  {3,14}", "thru", "rnd/s", "rnd/s", "rnd/s");

foreach (var P in partitionSizes)
{
    Console.WriteLine();
    Console.WriteLine($"P = {P}");

    PerfResult r0, r1, r2;

    r0 = MeasureScanOneKey(P, CountOnly_ScanOneKey);
    r1 = MeasureManualExpanded(P, CountOnly_ManualExpanded);
    r2 = MeasureBucketQuery(P, CountOnly_Bucket);
    PrintResults("CountOnly", r0, r1, r2);

    r0 = MeasureScanOneKey(P, Read2_ScanOneKey);
    r1 = MeasureManualExpanded(P, Read2_ManualExpanded);
    r2 = MeasureBucketQuery(P, Read2_Bucket);
    PrintResults("Read2", r0, r1, r2);

    r0 = MeasureScanOneKey(P, Write3_ScanOneKey);
    r1 = MeasureManualExpanded(P, Write3_ManualExpanded);
    r2 = MeasureBucketQuery(P, Write3_Bucket);
    PrintResults("Write3", r0, r1, r2);

    r0 = MeasureScanOneKey(P, AutoFreshness_ScanOneKey);
    r1 = MeasureManualExpanded(P, AutoFreshness_ManualExpanded);
    r2 = MeasureBucketQuery(P, AutoFreshness_Bucket);
    PrintResults("AutoFreshness", r0, r1, r2);
}

Console.WriteLine();
// Note about AutoFreshness allocations
Console.WriteLine("NOTE: AutoFreshness workloads allocate a List<(Entity, CardZone)> each round,");
Console.WriteLine("      so *all three* variants show allocation there. BucketQuery's own overhead");
Console.WriteLine("      is the difference vs ScanOneKey/ManualExpanded.");

// ════════════════════════════════════════════════════════════════
// Printing
// ════════════════════════════════════════════════════════════════

void PrintResults(string name, PerfResult c0, PerfResult c1, PerfResult c2)
{
    // Throughput row
    Console.WriteLine($"  {name,-16}  {c0.RoundsPerSecond,14:F0}  {c1.RoundsPerSecond,14:F0}  {c2.RoundsPerSecond,14:F0}");
    // Allocation row
    void AllocCell(PerfResult r) => Console.Write(r.Gen0 == 0 && r.Gen1 == 0 && r.Gen2 == 0
        ? $"{r.BytesPerRound,14:F1}"
        : $"{r.BytesPerRound,14:F1} g{r.Gen0},{r.Gen1},{r.Gen2}");
    Console.Write($"  {"alloc B/r",-16}  ");
    AllocCell(c0);
    Console.Write("  ");
    AllocCell(c1);
    Console.Write("  ");
    AllocCell(c2);
    Console.WriteLine();
}

// ════════════════════════════════════════════════════════════════
// Entity creation
// ════════════════════════════════════════════════════════════════

Entity[] CreateEntities(World world, int P)
{
    var entities = new Entity[N];
    for (int i = 0; i < N; i++)
    {
        var zone = new CardZone(i % P);
        entities[i] = world.Create(
            zone,
            new Position(1, i),
            new Velocity(1, 2),
            new Health(100),
            new Mana(50));
    }
    return entities;
}

// ════════════════════════════════════════════════════════════════
// Helper: build full Dictionary<CardZone, List<Entity>> (used by ManualExpanded)
// ════════════════════════════════════════════════════════════════

Dictionary<CardZone, List<Entity>> BuildFullBucketing(World world)
{
    var scope = new QueryDescription().With<CardZone>();
    var query = world.Query(in scope);
    var dict = new Dictionary<CardZone, List<Entity>>();

    foreach (var chunk in query.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var entities = chunk.GetEntities();
        var c = chunk.Count;
        for (int i = 0; i < c; i++)
        {
            var key = zones[i];
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<Entity>();
                dict[key] = list;
            }
            list.Add(entities[i]);
        }
    }

    return dict;
}

// ════════════════════════════════════════════════════════════════
// ScanOneKey: raw scan filtering for a single key (theoretical lower bound)
// ════════════════════════════════════════════════════════════════

PerfResult MeasureScanOneKey(int P, Func<World, int, long> workload)
{
    using var world = new World();
    var entities = CreateEntities(world, P);

    for (int w = 0; w < Warmup; w++)
        workload(world, P);

    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();

    long startAlloc = GC.GetAllocatedBytesForCurrentThread();
    int startGen0 = GC.CollectionCount(0);
    int startGen1 = GC.CollectionCount(1);
    int startGen2 = GC.CollectionCount(2);

    var sw = Stopwatch.StartNew();
    long sink = 0;
    long rounds = 0;
    while (sw.ElapsedMilliseconds < MeasureMs)
    {
        sink += workload(world, P);
        rounds++;
    }
    sw.Stop();
    GC.KeepAlive(sink);

    long endAlloc = GC.GetAllocatedBytesForCurrentThread();
    int endGen0 = GC.CollectionCount(0);
    int endGen1 = GC.CollectionCount(1);
    int endGen2 = GC.CollectionCount(2);

    double bytesPerRound = (endAlloc - startAlloc) / (double)rounds;
    return new PerfResult(rounds / sw.Elapsed.TotalSeconds, bytesPerRound,
        endGen0 - startGen0, endGen1 - startGen1, endGen2 - startGen2);
}

long CountOnly_ScanOneKey(World world, int P)
{
    var scope = new QueryDescription().With<CardZone>();
    var query = world.Query(in scope);
    long count = 0;

    foreach (var chunk in query.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var c = chunk.Count;
        for (int i = 0; i < c; i++)
        {
            if (zones[i].Value == 0)
                count++;
        }
    }

    return count;
}

long Read2_ScanOneKey(World world, int P)
{
    var scope = new QueryDescription().With<CardZone>().With<Position>().With<Velocity>();
    var query = world.Query(in scope);
    long sum = 0;

    foreach (var chunk in query.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var positions = chunk.GetSpan<Position>();
        var velocities = chunk.GetSpan<Velocity>();
        var c = chunk.Count;
        for (int i = 0; i < c; i++)
        {
            if (zones[i].Value == 0)
                sum += (long)(positions[i].X + velocities[i].Y);
        }
    }

    return sum;
}

long Write3_ScanOneKey(World world, int P)
{
    var scope = new QueryDescription().With<CardZone>().With<Position>().With<Health>().With<Mana>();
    var query = world.Query(in scope);
    long sum = 0;

    foreach (var chunk in query.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var positions = chunk.GetSpan<Position>();
        var healths = chunk.GetSpan<Health>();
        var manas = chunk.GetSpan<Mana>();
        var c = chunk.Count;
        for (int i = 0; i < c; i++)
        {
            if (zones[i].Value == 0)
            {
                positions[i] = new Position(positions[i].X + 1, positions[i].Y);
                healths[i] = new Health(healths[i].Value - 1);
                manas[i] = new Mana(manas[i].Value + 1);
                sum += (long)(positions[i].X + healths[i].Value + manas[i].Value);
            }
        }
    }

    return sum;
}

long AutoFreshness_ScanOneKey(World world, int P)
{
    // Phase 1: modify a fraction of entities' CardZone values.
    var scope = new QueryDescription().With<CardZone>();
    var query = world.Query(in scope);
    var toChange = new List<(Entity, CardZone)>();

    foreach (var chunk in query.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var entities = chunk.GetEntities();
        var c = chunk.Count;
        int changeCount = Math.Max(1, c / MoveDiv);
        for (int i = 0; i < changeCount && i < c; i++)
        {
            var newVal = (zones[i].Value + 1) % P;
            toChange.Add((entities[i], new CardZone(newVal)));
        }
    }

    foreach (var (entity, newZone) in toChange)
        world.Set(entity, newZone);

    // Phase 2: scan for key=0.
    long count = 0;
    var q2 = world.Query(in scope);
    foreach (var chunk in q2.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var c = chunk.Count;
        for (int i = 0; i < c; i++)
        {
            if (zones[i].Value == 0)
                count++;
        }
    }

    return count;
}

// ════════════════════════════════════════════════════════════════
// ManualExpanded: scan query, build full Dictionary, then use key=0
// (Semantically equivalent to what ComponentBucketQuery does)
// ════════════════════════════════════════════════════════════════

PerfResult MeasureManualExpanded(int P, Func<World, int, long> workload)
{
    using var world = new World();
    var entities = CreateEntities(world, P);

    for (int w = 0; w < Warmup; w++)
        workload(world, P);

    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();

    long startAlloc = GC.GetAllocatedBytesForCurrentThread();
    int startGen0 = GC.CollectionCount(0);
    int startGen1 = GC.CollectionCount(1);
    int startGen2 = GC.CollectionCount(2);

    var sw = Stopwatch.StartNew();
    long sink = 0;
    long rounds = 0;
    while (sw.ElapsedMilliseconds < MeasureMs)
    {
        sink += workload(world, P);
        rounds++;
    }
    sw.Stop();
    GC.KeepAlive(sink);

    long endAlloc = GC.GetAllocatedBytesForCurrentThread();
    int endGen0 = GC.CollectionCount(0);
    int endGen1 = GC.CollectionCount(1);
    int endGen2 = GC.CollectionCount(2);

    double bytesPerRound = (endAlloc - startAlloc) / (double)rounds;
    return new PerfResult(rounds / sw.Elapsed.TotalSeconds, bytesPerRound,
        endGen0 - startGen0, endGen1 - startGen1, endGen2 - startGen2);
}

long CountOnly_ManualExpanded(World world, int P)
{
    var dict = BuildFullBucketing(world);
    return dict.TryGetValue(new CardZone(0), out var list) ? list.Count : 0;
}

long Read2_ManualExpanded(World world, int P)
{
    var dict = BuildFullBucketing(world);
    long sum = 0;
    if (dict.TryGetValue(new CardZone(0), out var list))
    {
        foreach (var e in list)
            sum += (long)(world.GetRef<Position>(e).X + world.GetRef<Velocity>(e).Y);
    }
    return sum;
}

long Write3_ManualExpanded(World world, int P)
{
    var dict = BuildFullBucketing(world);
    long sum = 0;
    if (dict.TryGetValue(new CardZone(0), out var list))
    {
        foreach (var e in list)
        {
            ref var pos = ref world.GetRef<Position>(e);
            ref var hp = ref world.GetRef<Health>(e);
            ref var mp = ref world.GetRef<Mana>(e);
            pos = new Position(pos.X + 1, pos.Y);
            hp = new Health(hp.Value - 1);
            mp = new Mana(mp.Value + 1);
            sum += (long)(pos.X + hp.Value + mp.Value);
        }
    }
    return sum;
}

long AutoFreshness_ManualExpanded(World world, int P)
{
    // Phase 1: modify a fraction of entities' CardZone values.
    var scope = new QueryDescription().With<CardZone>();
    var query = world.Query(in scope);
    var toChange = new List<(Entity, CardZone)>();

    foreach (var chunk in query.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var entities = chunk.GetEntities();
        var c = chunk.Count;
        int changeCount = Math.Max(1, c / MoveDiv);
        for (int i = 0; i < changeCount && i < c; i++)
        {
            var newVal = (zones[i].Value + 1) % P;
            toChange.Add((entities[i], new CardZone(newVal)));
        }
    }

    foreach (var (entity, newZone) in toChange)
        world.Set(entity, newZone);

    // Phase 2: manual expanded bucketing + read.
    var dict = BuildFullBucketing(world);
    return dict.TryGetValue(new CardZone(0), out var list) ? list.Count : 0;
}

// ════════════════════════════════════════════════════════════════
// ComponentBucketQuery
// ════════════════════════════════════════════════════════════════

PerfResult MeasureBucketQuery(int P, Func<World, ComponentBucketQuery<CardZone>, Entity[], int, long> workload)
{
    using var world = new World();
    var entities = CreateEntities(world, P);
    using var query = new ComponentBucketQuery<CardZone>(world);
    var buffer = new Entity[N]; // max possible entities for any single key

    for (int w = 0; w < Warmup; w++)
        workload(world, query, buffer, P);

    GC.Collect(2, GCCollectionMode.Forced, true, true);
    GC.WaitForPendingFinalizers();

    long startAlloc = GC.GetAllocatedBytesForCurrentThread();
    int startGen0 = GC.CollectionCount(0);
    int startGen1 = GC.CollectionCount(1);
    int startGen2 = GC.CollectionCount(2);

    var sw = Stopwatch.StartNew();
    long sink = 0;
    long rounds = 0;
    while (sw.ElapsedMilliseconds < MeasureMs)
    {
        sink += workload(world, query, buffer, P);
        rounds++;
    }
    sw.Stop();
    GC.KeepAlive(sink);

    long endAlloc = GC.GetAllocatedBytesForCurrentThread();
    int endGen0 = GC.CollectionCount(0);
    int endGen1 = GC.CollectionCount(1);
    int endGen2 = GC.CollectionCount(2);

    double bytesPerRound = (endAlloc - startAlloc) / (double)rounds;
    return new PerfResult(rounds / sw.Elapsed.TotalSeconds, bytesPerRound,
        endGen0 - startGen0, endGen1 - startGen1, endGen2 - startGen2);
}

long CountOnly_Bucket(World _, ComponentBucketQuery<CardZone> query, Entity[] _buffer, int P)
{
    return query.Count(new CardZone(0));
}

long Read2_Bucket(World world, ComponentBucketQuery<CardZone> query, Entity[] buffer, int P)
{
    long sum = 0;
    int count = query.Get(new CardZone(0), buffer);
    foreach (var e in buffer.AsSpan(0, count))
    {
        sum += (long)(world.GetRef<Position>(e).X + world.GetRef<Velocity>(e).Y);
    }
    return sum;
}

long Write3_Bucket(World world, ComponentBucketQuery<CardZone> query, Entity[] buffer, int P)
{
    long sum = 0;
    int count = query.Get(new CardZone(0), buffer);
    foreach (var e in buffer.AsSpan(0, count))
    {
        ref var pos = ref world.GetRef<Position>(e);
        ref var hp = ref world.GetRef<Health>(e);
        ref var mp = ref world.GetRef<Mana>(e);
        pos = new Position(pos.X + 1, pos.Y);
        hp = new Health(hp.Value - 1);
        mp = new Mana(mp.Value + 1);
        sum += (long)(pos.X + hp.Value + mp.Value);
    }
    return sum;
}

long AutoFreshness_Bucket(World world, ComponentBucketQuery<CardZone> query, Entity[] buffer, int P)
{
    // Phase 1: modify a fraction of entities' CardZone values.
    var scope = new QueryDescription().With<CardZone>();
    var q = world.Query(in scope);
    var toChange = new List<(Entity, CardZone)>();

    foreach (var chunk in q.GetChunks())
    {
        var zones = chunk.GetSpan<CardZone>();
        var entities = chunk.GetEntities();
        var c = chunk.Count;
        int changeCount = Math.Max(1, c / MoveDiv);
        for (int i = 0; i < changeCount && i < c; i++)
        {
            var newVal = (zones[i].Value + 1) % P;
            toChange.Add((entities[i], new CardZone(newVal)));
        }
    }

    foreach (var (entity, newZone) in toChange)
        world.Set(entity, newZone);

    // Phase 2: read through query (triggers auto-freshness).
    return query.Count(new CardZone(0));
}

// ════════════════════════════════════════════════════════════════
// PerfResult type (must be after top-level statements)
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Result of a single benchmark measurement.
/// </summary>
record struct PerfResult(double RoundsPerSecond, double BytesPerRound, int Gen0, int Gen1, int Gen2);

// ════════════════════════════════════════════════════════════════
// Component types
// ════════════════════════════════════════════════════════════════

file readonly record struct CardZone(int Value);

file record struct Position(float X, float Y);
file record struct Velocity(float X, float Y);
file record struct Health(int Value);
file record struct Mana(int Value);
