// FrameReadModel benchmarks — real-world build/consume timings for all layouts.
// No LINQ on hot paths. Stopwatch-based timing. GC tracking per variant.
//
// Layout variants:
//   RawRepeatedScan   — naive per-key chunk scan (N*Q <= 500M only)
//   ComponentBucket   — ComponentBucketQuery<Cell> per-key scan baseline
//   DictionaryList    — Dictionary<int, List<Entity>>
//   RawSameCompact    — direct CompactRowLookup.BuildAutoGrow (no DSL overhead)
//   EntityArrayDsl    — EntityArrayLookup via Rows<Cell,Position,Health>.KeyBy().Into()
//   LinkedRowDsl      — LinkedRowLookup via Rows DSL
//   CompactRowDsl     — CompactRowLookup via Rows DSL
//   CompactRowDirectForEach — CompactRowLookup direct key consume (no CopyRowRefs)
//   DenseIntDsl       — DenseIntCompactLookup via Rows DSL

using System.Diagnostics;
using System.Runtime.CompilerServices;
using MiniArch;

namespace FrameReadModels.ValueLab;

// ────────────────────────────────────────────────────────────────
//  Scenario configuration
// ────────────────────────────────────────────────────────────────

internal enum Distribution { Uniform, Hot }

internal readonly struct Scenario
{
    public readonly string Name;
    public readonly int N;
    public readonly int Q;
    public readonly int Distinct;
    public readonly Distribution Distribution;

    public Scenario(string name, int n, int q, int distinct, Distribution dist)
    {
        Name = name; N = n; Q = q; Distinct = distinct; Distribution = dist;
    }

    public string DistLabel => Distribution == Distribution.Hot ? "hot" : "uniform";
}

// ────────────────────────────────────────────────────────────────
//  Benchmark result
// ────────────────────────────────────────────────────────────────

internal readonly struct BenchResult
{
    public readonly double BuildMs;
    public readonly double EntityMs;
    public readonly double RowComponentMs;
    public readonly double EntityComponentMs;
    public readonly long AllocatedBytes;
    public readonly int Gen0Count;
    public readonly int StoredRows;
    public readonly int DistinctKeys;
    public readonly int MaxBucket;
    public readonly bool Resized;
    public readonly long Checksum;
    public readonly string Notes;

    public double TotalRowMs => EntityMs + RowComponentMs;
    public double TotalEntityComponentMs => EntityMs + EntityComponentMs;

    public BenchResult(
        double buildMs, double entityMs, double rowComponentMs, double entityComponentMs,
        long allocBytes, int gen0,
        int storedRows, int distinctKeys, int maxBucket, bool resized,
        long checksum, string notes)
    {
        BuildMs = buildMs;
        EntityMs = entityMs;
        RowComponentMs = rowComponentMs;
        EntityComponentMs = entityComponentMs;
        AllocatedBytes = allocBytes;
        Gen0Count = gen0;
        StoredRows = storedRows;
        DistinctKeys = distinctKeys;
        MaxBucket = maxBucket;
        Resized = resized;
        Checksum = checksum;
        Notes = notes;
    }
}

// ────────────────────────────────────────────────────────────────
//  Main benchmark runner
// ────────────────────────────────────────────────────────────────

internal static class FrameReadModelBenchmarks
{
    private static readonly QueryDescription QueryDesc =
        new QueryDescription().With<Cell>().With<Position>().With<Health>();

    private static readonly PassAll<Cell, Position, Health> PredAll = default;
    private static readonly CellKeySelector3 KeySel = default;

    // ── Entry points ──

    public static void RunQuick()
    {
        var scenarios = new[]
        {
            new Scenario("small-q",   50_000,   8,     4096, Distribution.Uniform),
            new Scenario("realistic", 50_000,   10_000, 4096, Distribution.Uniform),
            new Scenario("hot",       100_000,  10_000, 64,   Distribution.Hot),
        };
        RunScenarios(scenarios);
    }

    public static void RunFull()
    {
        var scenarios = new[]
        {
            new Scenario("small-q",   50_000,     8,      4096, Distribution.Uniform),
            new Scenario("realistic", 50_000,     10_000, 4096, Distribution.Uniform),
            new Scenario("hot",       100_000,    10_000, 64,   Distribution.Hot),
            new Scenario("full-1m",   1_000_000,  50_000, 4096, Distribution.Uniform),
        };
        RunScenarios(scenarios);
    }

    // ────────────────────────────────────────────────────────────
    //  Orchestrator
    // ────────────────────────────────────────────────────────────

    private static void RunScenarios(Scenario[] scenarios)
    {
        PrintHeader();

        var allResults = new List<(Scenario Sc, string Variant, BenchResult Result)>();

        foreach (var sc in scenarios)
        {
            Console.WriteLine();
            Console.WriteLine($"── Scenario: {sc.Name}  N={sc.N}  Q={sc.Q}  distinct={sc.Distinct}  dist={sc.DistLabel} ──");

            World? world = null;
            ReadOnlySpan<ChunkView> chunks;
            int[] queryKeys;

            try
            {
                world = CreateWorld(sc);
                chunks = world.Query(QueryDesc).GetChunks();
                _ = world.Query(QueryDesc).GetChunks(); // warm query cache
                queryKeys = SelectQueryKeys(sc.Q, sc.Distinct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR creating world/keys: {ex.Message}");
                world?.Dispose();
                continue;
            }

            // Estimate max bucket for buffer sizing
            var maxBucketEst = sc.Distribution == Distribution.Hot
                ? (int)(sc.N * 0.8) + (sc.N / Math.Max(sc.Distinct, 1))
                : Math.Min(sc.N, Math.Max(1024, sc.N / Math.Max(sc.Distinct, 1) * 4 + 128));

            var entityBuf = new Entity[maxBucketEst];
            var rowRefBuf = new RowRef[maxBucketEst];

            // ── 1. RawRepeatedScan ──
            if ((long)sc.N * sc.Q <= 500_000_000L)
            {
                var rrs = MeasureRawRepeatedScan(world, chunks, queryKeys, sc);
                PrintRow(sc, "RawRepeatedScan", rrs);
                allResults.Add((sc, "RawRepeatedScan", rrs));
            }
            else
            {
                PrintSkipped(sc, "RawRepeatedScan", "N*Q>500M");
            }

            // ── 1b. ComponentBucketQuery<Cell> ──
            if ((long)sc.N * sc.Q <= 500_000_000L)
            {
                var bucket = MeasureComponentBucket(world, queryKeys, entityBuf, sc);
                PrintRow(sc, "ComponentBucket", bucket);
                allResults.Add((sc, "ComponentBucket", bucket));
            }
            else
            {
                PrintSkipped(sc, "ComponentBucket", "N*Q>500M");
            }

            // ── 2. DictionaryList ──
            var dict = MeasureDictionaryList(world, chunks, queryKeys, entityBuf, sc);
            PrintRow(sc, "DictionaryList", dict);
            allResults.Add((sc, "DictionaryList", dict));

            // ── 3. RawSameCompact ──
            var rawCompact = MeasureRawSameCompact(world, chunks, queryKeys, entityBuf, rowRefBuf, sc);
            PrintRow(sc, "RawSameCompact", rawCompact);
            allResults.Add((sc, "RawSameCompact", rawCompact));

            // ── 4-7. DSL variants ──
            var ea = MeasureLookup<EntityArrayLookup<int>>(
                world, chunks, queryKeys, entityBuf, rowRefBuf, sc,
                EntityArrayLookup<int>.Create(), "entity-only-layout");
            PrintRow(sc, "EntityArrayDsl", ea);
            allResults.Add((sc, "EntityArrayDsl", ea));

            var lr = MeasureLookup<LinkedRowLookup<int>>(
                world, chunks, queryKeys, entityBuf, rowRefBuf, sc,
                LinkedRowLookup<int>.Create(), "");
            PrintRow(sc, "LinkedRowDsl", lr);
            allResults.Add((sc, "LinkedRowDsl", lr));

            var cr = MeasureLookup<CompactRowLookup<int>>(
                world, chunks, queryKeys, entityBuf, rowRefBuf, sc,
                CompactRowLookup<int>.Create(), "");
            PrintRow(sc, "CompactRowDsl", cr);
            allResults.Add((sc, "CompactRowDsl", cr));

            var crDirect = MeasureCompactDirectForEach(
                world, chunks, queryKeys, entityBuf, sc);
            PrintRow(sc, "CompactRowDirectForEach", crDirect);
            allResults.Add((sc, "CompactRowDirectForEach", crDirect));

            var di = MeasureLookup<DenseIntCompactLookup>(
                world, chunks, queryKeys, entityBuf, rowRefBuf, sc,
                DenseIntCompactLookup.Create(sc.Distinct), "");
            PrintRow(sc, "DenseIntDsl", di);
            allResults.Add((sc, "DenseIntDsl", di));

            world.Dispose();
        }

        PrintSummary(scenarios, allResults);
    }

    // ────────────────────────────────────────────────────────────
    //  World creation
    // ────────────────────────────────────────────────────────────

    private static World CreateWorld(Scenario sc)
    {
        var world = new World();
        var rng = new Random(42 + sc.Name.GetHashCode());

        for (var i = 0; i < sc.N; i++)
        {
            int cellVal;
            if (sc.Distribution == Distribution.Hot)
                cellVal = rng.NextDouble() < 0.8 ? 0 : rng.Next(sc.Distinct - 1) + 1;
            else
                cellVal = rng.Next(sc.Distinct);

            world.Create(
                new Cell(cellVal),
                new Position(rng.Next(1000000), rng.Next(1000000)),
                new Health(rng.Next(1, 10000)));
        }

        return world;
    }

    // ────────────────────────────────────────────────────────────
    //  Key selection
    // ────────────────────────────────────────────────────────────

    private static int[] SelectQueryKeys(int q, int distinct)
    {
            var result = new int[q];
            for (var i = 0; i < q; i++)
                result[i] = i % distinct;
            return result;
        }

    // ────────────────────────────────────────────────────────────
    //  Variant: RawRepeatedScan
    // ────────────────────────────────────────────────────────────

    private static BenchResult MeasureRawRepeatedScan(
        World world, ReadOnlySpan<ChunkView> chunks, int[] queryKeys, Scenario sc)
    {
        const double buildMs = 0.0;

        // Entity path: scan all chunks for each key, sum Entity.Id
        var entityStart = Stopwatch.GetTimestamp();
        var entitySum = 0L;
        foreach (var key in queryKeys)
        {
            foreach (ref readonly var chunk in chunks)
            {
                var entities = chunk.GetEntities();
                var cells = chunk.GetSpan<Cell>();
                for (var ri = 0; ri < chunk.Count; ri++)
                {
                    if (cells[ri].Value == key)
                        entitySum += entities[ri].Id;
                }
            }
        }
        var entityMs = ElapsedMs(entityStart);

        // Row component path: scan all chunks, read Health via chunk span
        var rowStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            foreach (ref readonly var chunk in chunks)
            {
                var cells = chunk.GetSpan<Cell>();
                var healths = chunk.GetSpan<Health>();
                for (var ri = 0; ri < chunk.Count; ri++)
                {
                    if (cells[ri].Value == key)
                        _ = healths[ri].Value; // consume via row path
                }
            }
        }
        var rowMs = ElapsedMs(rowStart);

        // Entity component path: scan all chunks, world.Get<Health>
        var ecStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            foreach (ref readonly var chunk in chunks)
            {
                var entities = chunk.GetEntities();
                var cells = chunk.GetSpan<Cell>();
                for (var ri = 0; ri < chunk.Count; ri++)
                {
                    if (cells[ri].Value == key)
                        _ = world.Get<Health>(entities[ri]).Value;
                }
            }
        }
        var ecMs = ElapsedMs(ecStart);

        return new BenchResult(
            buildMs, entityMs, rowMs, ecMs,
            0, 0,
            sc.N, sc.Distinct, sc.N, false,
            entitySum, "");
    }

    // ────────────────────────────────────────────────────────────
    //  Variant: ComponentBucketQuery<Cell>
    // ────────────────────────────────────────────────────────────

    private static BenchResult MeasureComponentBucket(
        World world, int[] queryKeys, Entity[] entityBuf, Scenario sc)
    {
        var bucket = new ComponentBucketQuery<Cell>(world, QueryDesc);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);

        var entitySum = 0L;
        var entityStart = Stopwatch.GetTimestamp();
        for (var qi = 0; qi < queryKeys.Length; qi++)
        {
            var count = bucket.Get(new Cell(queryKeys[qi]), entityBuf);
            var written = Math.Min(count, entityBuf.Length);
            for (var i = 0; i < written; i++)
                entitySum += entityBuf[i].Id;
        }

        var entityMs = ElapsedMs(entityStart);

        var componentStart = Stopwatch.GetTimestamp();
        for (var qi = 0; qi < queryKeys.Length; qi++)
        {
            var count = bucket.Get(new Cell(queryKeys[qi]), entityBuf);
            var written = Math.Min(count, entityBuf.Length);
            for (var i = 0; i < written; i++)
                _ = world.Get<Health>(entityBuf[i]).Value;
        }

        var entityComponentMs = ElapsedMs(componentStart);
        var allocAfter = GC.GetAllocatedBytesForCurrentThread();
        var gen0After = GC.CollectionCount(0);

        return new BenchResult(
            buildMs: 0,
            entityMs: entityMs,
            rowComponentMs: 0,
            entityComponentMs: entityComponentMs,
            allocBytes: allocAfter - allocBefore,
            gen0: gen0After - gen0Before,
            storedRows: sc.N,
            distinctKeys: sc.Distinct,
            maxBucket: 0,
            resized: false,
            checksum: entitySum,
            notes: "per-key-scan; entity-only-layout");
    }

    // ────────────────────────────────────────────────────────────
    //  Variant: DictionaryList
    // ────────────────────────────────────────────────────────────

    private static BenchResult MeasureDictionaryList(
        World world, ReadOnlySpan<ChunkView> chunks, int[] queryKeys,
        Entity[] entityBuf, Scenario sc)
    {
        // ── Warm build ──
        var dict = new Dictionary<int, List<Entity>>(sc.Distinct);
        BuildDict(dict, chunks);
        dict.Clear();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // ── Measured build ──
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);
        var buildStart = Stopwatch.GetTimestamp();

        BuildDict(dict, chunks);

        var buildMs = ElapsedMs(buildStart);
        var allocAfterBuild = GC.GetAllocatedBytesForCurrentThread();
        var gen0AfterBuild = GC.CollectionCount(0);

        // Stats
        var storedRows = 0;
        var distinctKeys = dict.Count;
        var maxBucket = 0;
        foreach (var kv in dict)
        {
            storedRows += kv.Value.Count;
            if (kv.Value.Count > maxBucket) maxBucket = kv.Value.Count;
        }

        // ── Entity path ──
        long entitySum;
        var entityMs = MeasureDictEntityPath(dict, queryKeys, out entitySum);

        // ── Row component path (not supported) ──
        const double rowMs = 0.0;

        // ── Entity component path ──
        var ecStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            if (dict.TryGetValue(key, out var list))
            {
                foreach (var e in list)
                    _ = world.Get<Health>(e).Value;
            }
        }
        var ecMs = ElapsedMs(ecStart);

        return new BenchResult(
            buildMs, entityMs, rowMs, ecMs,
            allocAfterBuild - allocBefore, gen0AfterBuild - gen0Before,
            storedRows, distinctKeys, maxBucket, false,
            entitySum, "entity-only-layout");
    }

    private static void BuildDict(Dictionary<int, List<Entity>> dict, ReadOnlySpan<ChunkView> chunks)
    {
        foreach (ref readonly var chunk in chunks)
        {
            var entities = chunk.GetEntities();
            var cells = chunk.GetSpan<Cell>();
            for (var ri = 0; ri < chunk.Count; ri++)
            {
                var key = cells[ri].Value;
                if (!dict.TryGetValue(key, out var list))
                {
                    list = new List<Entity>();
                    dict[key] = list;
                }
                list.Add(entities[ri]);
            }
        }
    }

    private static double MeasureDictEntityPath(
        Dictionary<int, List<Entity>> dict, int[] queryKeys, out long entitySum)
    {
        entitySum = 0L;
        var eStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            if (dict.TryGetValue(key, out var list))
            {
                foreach (var e in list)
                    entitySum += e.Id;
            }
        }
        return ElapsedMs(eStart);
    }

    // ────────────────────────────────────────────────────────────
    //  Variant: RawSameCompact (direct BuildAutoGrow)
    // ────────────────────────────────────────────────────────────

    private static BenchResult MeasureRawSameCompact(
        World world, ReadOnlySpan<ChunkView> chunks, int[] queryKeys,
        Entity[] entityBuf, RowRef[] rowRefBuf, Scenario sc)
    {
        var compact = CompactRowLookup<int>.Create();
        var p = PredAll;
        var s = KeySel;

        // ── Warm build (triggers growth) ──
        compact.BuildAutoGrow<Cell, Position, Health, PassAll<Cell, Position, Health>, CellKeySelector3>(
            chunks, ref p, ref s);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // ── Measured build ──
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);
        var buildStart = Stopwatch.GetTimestamp();

        compact.Clear();
        compact.BuildAutoGrow<Cell, Position, Health, PassAll<Cell, Position, Health>, CellKeySelector3>(
            chunks, ref p, ref s);

        var buildMs = ElapsedMs(buildStart);
        var allocAfterBuild = GC.GetAllocatedBytesForCurrentThread();
        var gen0AfterBuild = GC.CollectionCount(0);
        var buildResult = compact.LastResult;

        return MeasureConsume(world, chunks, queryKeys, entityBuf, rowRefBuf, compact,
            buildMs, allocAfterBuild - allocBefore, gen0AfterBuild - gen0Before,
            buildResult, "");
    }

    // ────────────────────────────────────────────────────────────
    //  Generic DSL variant measurement
    // ────────────────────────────────────────────────────────────

    private static BenchResult MeasureLookup<TLookup>(
        World world, ReadOnlySpan<ChunkView> chunks, int[] queryKeys,
        Entity[] entityBuf, RowRef[] rowRefBuf, Scenario sc,
        TLookup initialLookup, string notes)
        where TLookup : struct, IFrameLookup<int>
    {
        var lookup = initialLookup;

        // ── Warm build via Rows DSL (triggers capacity growth) ──
        Rows<Cell, Position, Health>.From(world, QueryDesc)
            .KeyBy<int, CellKeySelector3>()
            .Into(ref lookup);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // ── Measured build via Rows DSL ──
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);
        var buildStart = Stopwatch.GetTimestamp();

        Rows<Cell, Position, Health>.From(world, QueryDesc)
            .KeyBy<int, CellKeySelector3>()
            .Into(ref lookup);

        var buildMs = ElapsedMs(buildStart);
        var allocAfterBuild = GC.GetAllocatedBytesForCurrentThread();
        var gen0AfterBuild = GC.CollectionCount(0);
        var buildResult = lookup.LastResult;

        return MeasureConsume(world, chunks, queryKeys, entityBuf, rowRefBuf, lookup,
            buildMs, allocAfterBuild - allocBefore, gen0AfterBuild - gen0Before,
            buildResult, notes);
    }

    // ────────────────────────────────────────────────────────────
    //  Compact direct ForEach variant (publishing-shape probe)
    // ────────────────────────────────────────────────────────────

    private static BenchResult MeasureCompactDirectForEach(
        World world, ReadOnlySpan<ChunkView> chunks, int[] queryKeys,
        Entity[] entityBuf, Scenario sc)
    {
        var lookup = CompactRowLookup<int>.Create();

        // ── Warm build via Rows DSL (same build shape as CompactRowDsl) ──
        Rows<Cell, Position, Health>.From(world, QueryDesc)
            .KeyBy<int, CellKeySelector3>()
            .Into(ref lookup);

        var warmConsumer = new HealthSumConsumer1();
        foreach (var key in queryKeys)
        {
            var warmCount = lookup.CopyEntities(key, entityBuf, chunks);
            lookup.ForEach<Health, HealthSumConsumer1>(key, chunks, ref warmConsumer);
            for (var i = 0; i < warmCount; i++)
                _ = world.Get<Health>(entityBuf[i]).Value;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // ── Measured build via Rows DSL ──
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var gen0Before = GC.CollectionCount(0);
        var buildStart = Stopwatch.GetTimestamp();

        Rows<Cell, Position, Health>.From(world, QueryDesc)
            .KeyBy<int, CellKeySelector3>()
            .Into(ref lookup);

        var buildMs = ElapsedMs(buildStart);
        var allocAfterBuild = GC.GetAllocatedBytesForCurrentThread();
        var gen0AfterBuild = GC.CollectionCount(0);
        var buildResult = lookup.LastResult;

        // ── Entity path: keep the old CopyEntities path for apples-to-apples entity timing ──
        var entitySum = 0L;
        var entityStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            var count = lookup.CopyEntities(key, entityBuf, chunks);
            for (var i = 0; i < count; i++)
                entitySum += entityBuf[i].Id;
        }
        var entityMs = ElapsedMs(entityStart);

        // ── Row component path: Direct ForEach, no CopyRowRefs buffer ──
        var consumer = new HealthSumConsumer1();
        var rowStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            lookup.ForEach<Health, HealthSumConsumer1>(key, chunks, ref consumer);
        }
        var rowMs = ElapsedMs(rowStart);

        // ── Entity component path: CopyEntities + world.Get<Health> ──
        var ecStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            var count = lookup.CopyEntities(key, entityBuf, chunks);
            for (var i = 0; i < count; i++)
                _ = world.Get<Health>(entityBuf[i]).Value;
        }
        var ecMs = ElapsedMs(ecStart);

        var note = "direct-for-each";

        // Verify direct row consume matches entity component consume.
        var ecSumCheck = 0L;
        foreach (var key in queryKeys)
        {
            var count = lookup.CopyEntities(key, entityBuf, chunks);
            for (var i = 0; i < count; i++)
                ecSumCheck += world.Get<Health>(entityBuf[i]).Value;
        }
        if (consumer.Sum != ecSumCheck)
            note += "; CHKSUM-MISMATCH";

        return new BenchResult(
            buildMs, entityMs, rowMs, ecMs,
            allocAfterBuild - allocBefore, gen0AfterBuild - gen0Before,
            buildResult.StoredRows, buildResult.DistinctKeys,
            buildResult.MaxBucketSize, buildResult.Resized,
            entitySum, note);
    }

    // ────────────────────────────────────────────────────────────
    //  Common consume measurement (for IFrameLookup variants)
    // ────────────────────────────────────────────────────────────

    private static BenchResult MeasureConsume<TLookup>(
        World world, ReadOnlySpan<ChunkView> chunks, int[] queryKeys,
        Entity[] entityBuf, RowRef[] rowRefBuf, TLookup lookup,
        double buildMs, long allocBytes, int gen0,
        BuildResult buildResult, string notes)
        where TLookup : struct, IFrameLookup<int>
    {
        // ── Entity path: CopyEntities + sum Entity.Id ──
        var entitySum = 0L;
        var entityStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            var count = lookup.CopyEntities(key, entityBuf, chunks);
            for (var i = 0; i < count; i++)
                entitySum += entityBuf[i].Id;
        }
        var entityMs = ElapsedMs(entityStart);

        // ── Row component path: CopyRowRefs + chunk.GetSpan<Health> ──
        var rowSum = 0L;
        var rowStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            var count = lookup.CopyRowRefs(key, rowRefBuf);
            for (var i = 0; i < count; i++)
            {
                ref readonly var rr = ref rowRefBuf[i];
                rowSum += chunks[rr.ChunkIndex].GetSpan<Health>()[rr.RowIndex].Value;
            }
        }
        var rowMs = ElapsedMs(rowStart);

        // ── Entity component path: CopyEntities + world.Get<Health> ──
        var ecStart = Stopwatch.GetTimestamp();
        foreach (var key in queryKeys)
        {
            var count = lookup.CopyEntities(key, entityBuf, chunks);
            for (var i = 0; i < count; i++)
                _ = world.Get<Health>(entityBuf[i]).Value;
        }
        var ecMs = ElapsedMs(ecStart);

        // Detect entity-only layout
        var probeKey = queryKeys.Length > 0 ? queryKeys[0] : 0;
        var isEntityOnly = lookup.CopyRowRefs(probeKey, rowRefBuf) == 0;
        var note = isEntityOnly && string.IsNullOrEmpty(notes)
            ? "entity-only-layout"
            : notes;

        // Verify row and entity component sums match (same Health values) — skip for entity-only layouts
        if (!isEntityOnly)
        {
            var ecSumCheck = 0L;
            foreach (var key in queryKeys)
            {
                var count = lookup.CopyEntities(key, entityBuf, chunks);
                for (var i = 0; i < count; i++)
                    ecSumCheck += world.Get<Health>(entityBuf[i]).Value;
            }
            if (rowSum != ecSumCheck)
            {
                note = (string.IsNullOrEmpty(note) ? "" : note + "; ") + "CHKSUM-MISMATCH";
            }
        }

        return new BenchResult(
            buildMs, entityMs, rowMs, ecMs,
            allocBytes, gen0,
            buildResult.StoredRows, buildResult.DistinctKeys,
            buildResult.MaxBucketSize, buildResult.Resized,
            entitySum, note);
    }

    // ────────────────────────────────────────────────────────────
    //  Utilities
    // ────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ElapsedMs(long startTicks)
    {
        return Stopwatch.GetElapsedTime(startTicks, Stopwatch.GetTimestamp()).TotalMilliseconds;
    }

    // ────────────────────────────────────────────────────────────
    //  Table output
    // ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine(
            "scenario         variant              N        Q        distinct  dist       buildMs entityMs rowComp entityComp totalRow totalEntC allocBytes gen0 storedRows  distKeys  maxBucket resized checksum     notes");
        Console.WriteLine(
            "──────────────── ──────────────────── ──────── ──────── ───────── ────────── ─────── ──────── ─────── ────────── ──────── ───────── ────────── ──── ────────── ───────── ───────── ─────── ──────────── ────────────────────");
    }

    private static void PrintRow(Scenario sc, string variant, BenchResult r)
    {
        Console.WriteLine(
            $"{sc.Name,-16} {variant,-20} {sc.N,8} {sc.Q,8} {sc.Distinct,9} {sc.DistLabel,-10} " +
            $"{r.BuildMs,7:F2} {r.EntityMs,8:F2} {r.RowComponentMs,7:F2} {r.EntityComponentMs,10:F2} " +
            $"{r.TotalRowMs,8:F2} {r.TotalEntityComponentMs,9:F2} " +
            $"{r.AllocatedBytes,10} {r.Gen0Count,4} " +
            $"{r.StoredRows,10} {r.DistinctKeys,9} {r.MaxBucket,8} " +
            $"{(r.Resized ? "yes" : "no"),-7} {r.Checksum,12} {r.Notes,-20}");
    }

    private static void PrintSkipped(Scenario sc, string variant, string reason)
    {
        Console.WriteLine(
            $"{sc.Name,-16} {variant,-20} {sc.N,8} {sc.Q,8} {sc.Distinct,9} {sc.DistLabel,-10} " +
            $"  skipped  ({reason})");
    }

    // ────────────────────────────────────────────────────────────
    //  Scoped result collector for summary aggregation
    // ────────────────────────────────────────────────────────────

    private sealed class ScenarioResults
    {
        public readonly Scenario Scenario;
        public readonly Dictionary<string, BenchResult> Variants = new();

        public ScenarioResults(Scenario sc) { Scenario = sc; }

        public void Add(string variant, BenchResult r) => Variants[variant] = r;

        public BenchResult? Get(string variant) =>
            Variants.TryGetValue(variant, out var r) ? r : null;
    }

    // ────────────────────────────────────────────────────────────
    //  Summary
    // ────────────────────────────────────────────────────────────

    private static void PrintSummary(Scenario[] scenarios, List<(Scenario Sc, string Variant, BenchResult Result)> allResults)
    {
        Console.WriteLine();
        Console.WriteLine("═══ Per-Scenario Highlights ═══");
        Console.WriteLine();

        foreach (var sc in scenarios)
        {
            var rawCompact = default(BenchResult);
            var compactDsl = default(BenchResult);
            var compactDirect = default(BenchResult);
            var hasRawCompact = false;
            var hasCompactDsl = false;
            var hasCompactDirect = false;

            var bestVariant = "";
            var secondVariant = "";
            var bestTotal = double.MaxValue;
            var secondTotal = double.MaxValue;

            for (var i = 0; i < allResults.Count; i++)
            {
                var item = allResults[i];
                if (!string.Equals(item.Sc.Name, sc.Name, StringComparison.Ordinal))
                    continue;

                if (string.Equals(item.Variant, "RawSameCompact", StringComparison.Ordinal))
                {
                    rawCompact = item.Result;
                    hasRawCompact = true;
                }
                else if (string.Equals(item.Variant, "CompactRowDsl", StringComparison.Ordinal))
                {
                    compactDsl = item.Result;
                    hasCompactDsl = true;
                }
                else if (string.Equals(item.Variant, "CompactRowDirectForEach", StringComparison.Ordinal))
                {
                    compactDirect = item.Result;
                    hasCompactDirect = true;
                }

                if (string.Equals(item.Variant, "RawRepeatedScan", StringComparison.Ordinal) ||
                    string.Equals(item.Variant, "ComponentBucket", StringComparison.Ordinal) ||
                    string.Equals(item.Variant, "DictionaryList", StringComparison.Ordinal) ||
                    string.Equals(item.Variant, "EntityArrayDsl", StringComparison.Ordinal))
                    continue;

                var total = item.Result.TotalRowMs;
                if (total < bestTotal)
                {
                    secondTotal = bestTotal;
                    secondVariant = bestVariant;
                    bestTotal = total;
                    bestVariant = item.Variant;
                }
                else if (total < secondTotal)
                {
                    secondTotal = total;
                    secondVariant = item.Variant;
                }
            }

            var compactBuildMs = compactDsl.BuildMs;
            var rawBuildMs = rawCompact.BuildMs;

            Console.WriteLine($"  Scenario: {sc.Name}  (N={sc.N}, Q={sc.Q}, distinct={sc.Distinct}, {sc.DistLabel})");

            // 1) DSL tax%: (CompactDsl.buildMs - RawSameCompact.buildMs) / RawSameCompact.buildMs * 100
            if (hasRawCompact && hasCompactDsl && rawBuildMs > 0)
            {
                var dslTaxPct = (compactBuildMs - rawBuildMs) / rawBuildMs * 100.0;
                Console.WriteLine($"    Compact vs RawSame DSL tax:  {dslTaxPct,6:F1}%  (CompactDsl build={compactBuildMs,6:F2}ms, RawSameCompact build={rawBuildMs,6:F2}ms)");
            }
            else
            {
                Console.WriteLine($"    Compact vs RawSame DSL tax:  N/A (RawSameCompact build=0ms)");
            }

            // 2) Compare component-read mechanisms only: chunk-row span vs entity random World.Get.
            if (hasCompactDsl && compactDsl.EntityComponentMs > 0)
            {
                var ratio = compactDsl.RowComponentMs / compactDsl.EntityComponentMs;
                Console.WriteLine($"    Compact component row/entity:{ratio,6:F2}x  (rowComp={compactDsl.RowComponentMs,6:F2}ms vs entityComp={compactDsl.EntityComponentMs,6:F2}ms)");
            }
            else
            {
                Console.WriteLine("    Compact component row/entity: N/A (entityComp=0ms)");
            }

            // 2b) Direct ForEach vs CopyRowRefs consume shape.
            if (hasCompactDsl && hasCompactDirect && compactDsl.RowComponentMs > 0)
            {
                var directRatio = compactDirect.RowComponentMs / compactDsl.RowComponentMs;
                Console.WriteLine($"    DirectForEach vs CopyRows: {directRatio,6:F2}x  (direct={compactDirect.RowComponentMs,6:F2}ms vs copyRows={compactDsl.RowComponentMs,6:F2}ms)");
            }
            else
            {
                Console.WriteLine("    DirectForEach vs CopyRows: N/A");
            }

            // 3) Best totalRow among row-capable variants.
            if (bestVariant.Length > 0)
            {
                Console.WriteLine($"    Best totalRow variant:       {bestVariant,-20}  totalRow={bestTotal,6:F2}ms");
                if (secondVariant.Length > 0)
                {
                    Console.WriteLine($"    2nd best:                   {secondVariant,-20}  totalRow={secondTotal,6:F2}ms");
                }
            }

            Console.WriteLine();
        }

        Console.WriteLine("=== FrameReadModel Benchmarks complete ===");
    }
}
