using System.Diagnostics;
using MiniArch;
using MiniArch.Core;

// ── Config ──────────────────────────────────────────────────────────
int entityCount = 100_000;
const int Warmup = 3;
const int MeasureMs = 5000;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("══════════════════════════════════════════════════════════════════════════════════");
Console.WriteLine("  Parallel Recording Benchmark \u2014 MiniArch CommandStream");
Console.WriteLine($"  Entities: {entityCount:N0}  |  Measure: {MeasureMs / 1000.0:F1}s per cell");
Console.WriteLine();
Console.WriteLine("  Records/s = Set<T> calls per second (record phase only)");
Console.WriteLine("  Speedup  = parallel / sequential total (including submit)");
Console.WriteLine("══════════════════════════════════════════════════════════════════════════════════");

// ════════════════════════════════════════════════════════════
// Section 1: Core scenarios (with current ThreadCount = CPU)
// ════════════════════════════════════════════════════════════
int tc = Environment.ProcessorCount;
Console.WriteLine($"\n\u2500\u2500 1. Core scenarios @ {tc} threads \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
RunSuite(tc);

// ════════════════════════════════════════════════════════════
// Section 2: Thread scaling for worst-case (simple Set)
// ════════════════════════════════════════════════════════════
Console.WriteLine($"\n\u2500\u2500 2. Thread scaling: Par-Simple (worst-case, same store contention) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
RunScale(out _);

// ════════════════════════════════════════════════════════════
// Section 3: Partition strategy comparison
// ════════════════════════════════════════════════════════════
Console.WriteLine($"\n\u2500\u2500 3. Partition strategies @ {tc} threads \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
RunPartitionStrategies(tc);

Console.WriteLine("\nDone.");

// ════════════════════════════════════════════════════════════════════
//  Core suite
// ════════════════════════════════════════════════════════════════════

void RunSuite(int threads)
{
    var runner = new Runner(entityCount, Warmup, MeasureMs);

    // ---- Seq-Simple ----
    var r = runner.Measure(multi: false, entityOps: 1,
        (stream, ents, _) =>
        {
            for (var i = 0; i < ents.Length; i++)
                stream.Set(ents[i], new Pos(i, i));
        });
    runner.Print("Seq-Simple", r, null);

    // ---- Par-Simple (same store, range partition) ----
    {
        var r2 = runner.Measure(multi: true, entityOps: 1,
            (stream, ents, _) =>
            {
                Parallel.For(0, threads, ti =>
                {
                    var (s, e) = Range(ents.Length, threads, ti);
                    for (var i = s; i < e; i++)
                        stream.Set(ents[i], new Pos(i, i));
                });
            });
        runner.Print("Par-Simple", r2, r.Total);
    }

    // ---- Seq-ReadCompute (query + compute + record) ----
    var r3 = runner.Measure(multi: false, entityOps: 1,
        (stream, ents, world) =>
        {
            var q = world.Query(new QueryDescription().With<Pos>().With<Vel>());
            foreach (var chunk in q.GetChunks())
            {
                var pos = chunk.GetSpan<Pos>();
                var vel = chunk.GetSpan<Vel>();
                var ids = chunk.GetEntities();
                for (var i = 0; i < chunk.Count; i++)
                    stream.Set(ids[i], new Pos(pos[i].X + vel[i].X, pos[i].Y + vel[i].Y));
            }
        });
    runner.Print("Seq-ReadCompute", r3, null);

    // ---- Par-ReadCompute (parallel chunks + record) ----
    {
        var r4 = runner.Measure(multi: true, entityOps: 1,
            (stream, ents, world) =>
            {
                var q = world.Query(new QueryDescription().With<Pos>().With<Vel>());
                q.ForEachChunkParallel(chunk =>
                {
                    var pos = chunk.GetSpan<Pos>();
                    var vel = chunk.GetSpan<Vel>();
                    var ids = chunk.GetEntities();
                    for (var i = 0; i < chunk.Count; i++)
                        stream.Set(ids[i], new Pos(pos[i].X + vel[i].X, pos[i].Y + vel[i].Y));
                });
            });
        runner.Print("Par-ReadCompute", r4, r3.Total);
    }

    // ---- Seq-Multi3 ----
    var r5 = runner.Measure(multi: false, entityOps: 3,
        (stream, ents, _) =>
        {
            for (var i = 0; i < ents.Length; i++)
            {
                stream.Set(ents[i], new Pos(i, i));
                stream.Set(ents[i], new Vel(i + 1, i + 2));
                stream.Set(ents[i], new Hp(100 + i));
            }
        });
    runner.Print("Seq-Multi3", r5, null);

    // ---- Par-Multi3 (3 stores, range partition) ----
    {
        var r6 = runner.Measure(multi: true, entityOps: 3,
            (stream, ents, _) =>
            {
                Parallel.For(0, threads, ti =>
                {
                    var (s, e) = Range(ents.Length, threads, ti);
                    for (var i = s; i < e; i++)
                    {
                        stream.Set(ents[i], new Pos(i, i));
                        stream.Set(ents[i], new Vel(i + 1, i + 2));
                        stream.Set(ents[i], new Hp(100 + i));
                    }
                });
            });
        runner.Print("Par-Multi3", r6, r5.Total);
    }
}

// ════════════════════════════════════════════════════════════════════
//  Thread scaling
// ════════════════════════════════════════════════════════════════════

void RunScale(out double baselineTotal)
{
    // Measure sequential baseline
    var seqRunner = new Runner(entityCount, Warmup, MeasureMs);
    var seq = seqRunner.Measure(multi: false, entityOps: 1,
        (stream, ents, _) =>
        {
            for (var i = 0; i < ents.Length; i++)
                stream.Set(ents[i], new Pos(i, i));
        });
    baselineTotal = seq.Total;
    Console.WriteLine($"  Seq (baseline):     {seq.Record,12:F0} rec/s  {seq.Total,12:F0} total/s");

    // Parallel with different thread counts
    foreach (var t in new[] { 1, 2, 4, 8, 16, Environment.ProcessorCount })
    {
        if (t > Environment.ProcessorCount * 2) continue;
        var r2 = new Runner(entityCount, Warmup, MeasureMs / 2);
        var par = r2.Measure(multi: true, entityOps: 1,
            (stream, ents, _) =>
            {
                Parallel.For(0, t, ti =>
                {
                    var (s, e) = Range(ents.Length, t, ti);
                    for (var i = s; i < e; i++)
                        stream.Set(ents[i], new Pos(i, i));
                });
            });
        Console.WriteLine($"  Par {t,2} threads:   {par.Record,12:F0} rec/s  {par.Total,12:F0} total/s  ({par.Total / seq.Total,5:F2}x vs seq)");
    }

    // Heavy compute scenario: each entity does 100x the work
    Console.WriteLine();
    var seqH = seqRunner.Measure(multi: false, entityOps: 1,
        (stream, ents, _) =>
        {
            for (var i = 0; i < ents.Length; i++)
            {
                // Simulate heavy work: spin through some iterations
                int sum = 0;
                for (int k = 0; k < 100; k++) sum += k;
                stream.Set(ents[i], new Pos(i, sum));
            }
        });
    Console.WriteLine($"  HeavyCompute Seq:  {seqH.Record,12:F0} rec/s  {seqH.Total,12:F0} total/s");

    var parHeavy = new Runner(entityCount, Warmup, MeasureMs / 2);
    var parH = parHeavy.Measure(multi: true, entityOps: 1,
        (stream, ents, _) =>
        {
            Parallel.For(0, Environment.ProcessorCount, ti =>
            {
                var (s, e) = Range(ents.Length, Environment.ProcessorCount, ti);
                for (var i = s; i < e; i++)
                {
                    int sum = 0;
                    for (int k = 0; k < 100; k++) sum += k;
                    stream.Set(ents[i], new Pos(i, sum));
                }
            });
        });
    Console.WriteLine($"  HeavyCompute Par({Environment.ProcessorCount}): {parH.Record,12:F0} rec/s  {parH.Total,12:F0} total/s  ({parH.Total / seqH.Total,5:F2}x vs seq)");
}

// ════════════════════════════════════════════════════════════════════
//  Partition strategy comparison
// ════════════════════════════════════════════════════════════════════

void RunPartitionStrategies(int threads)
{
    var seqRunner = new Runner(entityCount, Warmup, MeasureMs);
    var seq = seqRunner.Measure(multi: false, entityOps: 3,
        (stream, ents, _) =>
        {
            for (var i = 0; i < ents.Length; i++)
            {
                stream.Set(ents[i], new Pos(i, i));
                stream.Set(ents[i], new Vel(i + 1, i + 2));
                stream.Set(ents[i], new Hp(100 + i));
            }
        });
    seqRunner.Print("  Seq-Multi3 (baseline)", seq, null);

    // Strategy A: Range partition (each thread handles entity range, writes all 3 comps)
    {
        var r = new Runner(entityCount, Warmup, MeasureMs).Measure(multi: true, entityOps: 3,
            (stream, ents, _) =>
            {
                Parallel.For(0, threads, ti =>
                {
                    var (s, e) = Range(ents.Length, threads, ti);
                    for (var i = s; i < e; i++)
                    {
                        stream.Set(ents[i], new Pos(i, i));
                        stream.Set(ents[i], new Vel(i + 1, i + 2));
                        stream.Set(ents[i], new Hp(100 + i));
                    }
                });
            });
        seqRunner.Print("  Par-Range (3 stores contended)", r, seq.Total);
    }

    // Strategy B: By component (each thread writes ONE component across ALL entities)
    // Thread 0: Set<Pos> on all entities
    // Thread 1: Set<Vel> on all entities
    // Thread 2: Set<Hp> on all entities
    {
        var r = new Runner(entityCount, Warmup, MeasureMs).Measure(multi: true, entityOps: 1,
            (stream, ents, _) =>
            {
                Parallel.For(0, 3, ci =>
                {
                    for (var i = 0; i < ents.Length; i++)
                    {
                        if (ci == 0) stream.Set(ents[i], new Pos(i, i));
                        else if (ci == 1) stream.Set(ents[i], new Vel(i + 1, i + 2));
                        else stream.Set(ents[i], new Hp(100 + i));
                    }
                });
            });
        // Ops correction: each thread does 1 op on all entities, total = entityCount * 3
        // But entityOps=1 means ops = entityCount * 1 * iters = entityCount * iters
        // But totalOps should be entityCount * 3 * iters
        // The Measure returns entityCount * entityOps * iters / time = entityCount * 1 * iters / time
        // We need to multiply by 3 for correct comparison
        var corrected = new Result(r.Record * 3, r.Submit * 3, r.Total * 3);
        seqRunner.Print("  Par-ByComponent (no lock contention)", corrected, seq.Total);
    }

    // Strategy C: By component with fewer threads
    // Only 2 components: Pos + Vel (2 threads, no contention)
    {
        var r = new Runner(entityCount, Warmup, MeasureMs).Measure(multi: true, entityOps: 1,
            (stream, ents, _) =>
            {
                Parallel.For(0, 2, ci =>
                {
                    for (var i = 0; i < ents.Length; i++)
                    {
                        if (ci == 0) stream.Set(ents[i], new Pos(i, i));
                        else stream.Set(ents[i], new Vel(i + 1, i + 2));
                    }
                });
            });
        var corrected = new Result(r.Record * 2, r.Submit * 2, r.Total * 2);
        // Compare vs Seq-Multi2 (Seq that does Pos+Vel only)
        var seq2 = seqRunner.Measure(multi: false, entityOps: 2,
            (stream, ents, _) =>
            {
                for (var i = 0; i < ents.Length; i++)
                {
                    stream.Set(ents[i], new Pos(i, i));
                    stream.Set(ents[i], new Vel(i + 1, i + 2));
                }
            });
        seqRunner.Print("  Par-ByComponent-2 (Pos+Vel, no contention)", corrected, seq2.Total);
    }
}

// ════════════════════════════════════════════════════════════════════
//  Helper
// ════════════════════════════════════════════════════════════════════

static (int start, int end) Range(int total, int parts, int idx)
{
    var s = total * idx / parts;
    var e = total * (idx + 1) / parts;
    return (s, e);
}

// ════════════════════════════════════════════════════════════════════
//  Runner
// ════════════════════════════════════════════════════════════════════

sealed class Runner
{
    private readonly int _entityCount;
    private readonly int _warmup;
    private readonly int _measureMs;

    public Runner(int entityCount, int warmup, int measureMs)
    {
        _entityCount = entityCount;
        _warmup = warmup;
        _measureMs = measureMs;
    }

    public Result Measure(bool multi, int entityOps,
        Action<CommandStreamCore, Entity[], World> recordAction)
    {
        // Warmup
        using var ww = CreateWorld();
        for (var wi = 0; wi < _warmup; wi++)
        {
            var ents = SnapshotEntities(ww);
            CommandStreamCore stream = multi ? new ParallelCommandStream(ww) : new CommandStream(ww);
            recordAction(stream, ents, ww);
            stream.Submit();
        }

        // Prepare measure
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        using var w = CreateWorld();
        var entities = SnapshotEntities(w);
        long recTicks = 0, subTicks = 0, iters = 0;
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < _measureMs)
        {
            CommandStreamCore stream = multi ? new ParallelCommandStream(w) : new CommandStream(w);

            var t0 = Stopwatch.GetTimestamp();
            recordAction(stream, entities, w);
            var t1 = Stopwatch.GetTimestamp();

            stream.Submit();
            var t2 = Stopwatch.GetTimestamp();

            recTicks += t1 - t0;
            subTicks += t2 - t1;
            iters++;
        }
        sw.Stop();

        var freq = Stopwatch.Frequency;
        var recOps = _entityCount * entityOps * iters;
        var subOps = _entityCount * iters;
        var totalOps = recOps + subOps;

        return new Result(
            Record: recOps / (recTicks / (double)freq),
            Submit: subOps / (subTicks / (double)freq),
            Total: totalOps / ((recTicks + subTicks) / (double)freq));
    }

    public void Print(string label, Result r, double? baseline)
    {
        var speedup = baseline.HasValue ? r.Total / baseline.Value : 1.0;
        Console.WriteLine(
            $"  {label,-36} {r.Record,12:F0}  {r.Total,12:F0}  {speedup,6:F2}x");
    }

    private World CreateWorld()
    {
        var world = new World();
        for (var i = 0; i < _entityCount; i++)
            world.Create(new Pos(i, 0), new Vel(1, 1), new Hp(100));
        return world;
    }

    private Entity[] SnapshotEntities(World world)
    {
        var q = world.Query(new QueryDescription().With<Pos>());
        var list = new List<Entity>();
        foreach (var chunk in q.GetChunks())
        {
            var ents = chunk.GetEntities();
            for (var i = 0; i < chunk.Count; i++)
                list.Add(ents[i]);
        }
        return [.. list];
    }
}

record struct Result(double Record, double Submit, double Total);

// ════════════════════════════════════════════════════════════════════
//  Components
// ════════════════════════════════════════════════════════════════════

record struct Pos(int X, int Y);
record struct Vel(int X, int Y);
record struct Hp(int Value);
