using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MiniArch;

#if DEBUG
Console.Error.WriteLine("Run this experiment with -c Release.");
return 2;
#else
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Any(a => a != "--correctness-only"))
{
    Console.Error.WriteLine("Usage: dotnet run -c Release --project tools/perf/QueryPlan.ValueLab [--correctness-only]");
    return 2;
}

Console.WriteLine("QueryPlan.ValueLab: handwritten physical strategies, NOT a language implementation");
Console.WriteLine($"Runtime={Environment.Version}; OS={Environment.OSVersion}; CPU threads={Environment.ProcessorCount}");
Correctness.Run();
Console.WriteLine("Correctness: PASS (exact result comparisons against independent fixture-data oracles)");
if (args.Contains("--correctness-only")) return 0;

Console.WriteLine("World setup, initial buffer allocation, and JIT warmup excluded; per-operation index rebuild, scratch reset, and typed output writes INCLUDED.");
Console.WriteLine("Three rotating-order samples, median us/op; reusable buffers; no parser/compiler cost is measured.");
Console.WriteLine("dataset,workload,variant,median_us,min_us,max_us,bytes_per_op");
foreach (var settings in new[] { (N: 10_000, Targets: 2, Hot: false), (N: 100_000, Targets: 64, Hot: false), (N: 100_000, Targets: 64, Hot: true) })
{
    using var f = new Fixture(settings.N, settings.Targets, settings.Hot);
    string dataset = $"n{settings.N}-targets{settings.Targets}-{(settings.Hot ? "hot" : "uniform")}";
    Measure.Group(dataset, "projection",
        new("entity-list-then-get", f.MaterializedProjection),
        new("entity-fused", f.EntityProjection),
        new("chunk-fused-manual-equals-candidate", f.ChunkProjection));
    foreach (int q in new[] { 1, 64, 512 })
    {
        f.SetKeys(q);
        Measure.Group(dataset, $"group-q{q}",
            new("repeated-chunk-scan", f.RepeatedGroupScan),
            new("reused-hash-aggregate", f.HashGroup),
            new("bounded-dense-aggregate", f.DenseGroup));
    }
    foreach (int k in new[] { 10, 1000, Math.Min(50_000, settings.N) })
    {
        f.SetK(k);
        Measure.Group(dataset, $"top-k{k}",
            new("chunk-full-sort", f.FullSort),
            new("bounded-heap", f.TopK));
    }
    Measure.Group(dataset, "logical-key-join",
        new("per-item-query-get", f.EntityNestedJoin),
        new("cached-chunk-nested-loop", f.ChunkNestedJoin),
        new("reused-projected-hash-index", f.HashJoin),
        new("bounded-projected-dense-index", f.DenseJoin));
}
return 0;
#endif

internal readonly record struct Zone(int Value);
internal readonly record struct TargetKey(int Value);
internal readonly record struct Score(int Value);
internal readonly record struct Weight(int Value);
internal readonly record struct Active(int Value);
internal readonly record struct Cold(long A, long B, long C, long D);
internal readonly record struct TargetData(int Key, int X);
internal readonly record struct SourceItem(Entity Entity, int Zone, int Target, int Score, int Weight, int Active);
internal readonly record struct ResultRow(Entity Entity, int Score, int Weight);
internal readonly record struct JoinedRow(Entity Entity, int Score, int TargetX);
internal record struct Summary(int Count, long Weight);
internal sealed record Variant(string Name, Func<long> Execute);

internal sealed class RankOrder : IComparer<ResultRow>
{
    public static readonly RankOrder BestFirst = new(false);
    public static readonly RankOrder WorstFirst = new(true);
    private readonly bool _reverse;
    private RankOrder(bool reverse) => _reverse = reverse;
    public int Compare(ResultRow a, ResultRow b)
    {
        int c = b.Score.CompareTo(a.Score);
        if (c == 0) c = a.Entity.Id.CompareTo(b.Entity.Id);
        if (c == 0) c = a.Entity.Version.CompareTo(b.Entity.Version);
        return _reverse ? -c : c;
    }
}

internal sealed class Fixture : IDisposable
{
    internal const int ZoneCount = 4096;
    private static readonly QueryDescription ProjectionScope = new QueryDescription().With<Score>().With<Weight>().With<Active>();
    private static readonly QueryDescription GroupScope = new QueryDescription().With<Zone>().With<Weight>();
    private static readonly QueryDescription JoinScope = new QueryDescription().With<TargetKey>().With<Score>();
    private static readonly QueryDescription TargetScope = new QueryDescription().With<TargetData>();
    private readonly World _world;
    internal readonly SourceItem[] Items;
    internal readonly Dictionary<int, int> TargetOracle = new();
    internal readonly ResultRow[] Rows;
    internal readonly JoinedRow[] Joined;
    internal readonly Summary[] Summaries = new Summary[512];
    internal int ResultCount;
    internal int[] Keys = [];
    internal int K;
    private readonly List<Entity> _matches;
    private readonly ResultRow[] _sortScratch;
    private readonly Summary[] _denseSummary = new Summary[ZoneCount];
    private readonly Dictionary<int, Summary> _hashSummary = new(ZoneCount);
    private readonly Dictionary<int, int> _targetIndex;
    private readonly bool[] _targetPresent;
    private readonly int[] _targetX;
    private PriorityQueue<ResultRow, ResultRow> _heap = new(RankOrder.WorstFirst);

    internal Fixture(int n, int targetCount, bool hot)
    {
        _world = new World(entityCapacity: n + targetCount);
        Items = new SourceItem[n];
        Rows = new ResultRow[n];
        Joined = new JoinedRow[n];
        _matches = new List<Entity>(n);
        _sortScratch = new ResultRow[n];
        _targetIndex = new Dictionary<int, int>(targetCount);
        _targetPresent = new bool[targetCount];
        _targetX = new int[targetCount];
        // Logical target keys are unique and bounded in this experiment.
        for (int i = 0; i < targetCount; i++)
        {
            int x = i * 19 - 31;
            _world.Create(new TargetData(i, x));
            TargetOracle.Add(i, x);
        }
        var random = new Random(73421);
        for (int i = 0; i < n; i++)
        {
            int zone = hot && i % 5 != 0 ? 0 : i % ZoneCount;
            int target = i % targetCount;
            if (hot && i % 5 != 0) target = 0;
            if (i % 17 == 0) target = -1;
            int score = random.Next(4096);
            int weight = random.Next(1, 101);
            int active = i % 4 != 0 ? 1 : 0;
            Entity e = i % 4 == 0
                ? _world.Create(new Zone(zone), new TargetKey(target), new Score(score), new Weight(weight), new Active(active), new Cold(1, 2, 3, 4))
                : _world.Create(new Zone(zone), new TargetKey(target), new Score(score), new Weight(weight), new Active(active));
            Items[i] = new SourceItem(e, zone, target, score, weight, active);
        }
        _ = _world.Query(ProjectionScope).GetChunks();
        _ = _world.Query(GroupScope).GetChunks();
        _ = _world.Query(JoinScope).GetChunks();
        _ = _world.Query(TargetScope).GetChunks();
        SetKeys(1);
        SetK(Math.Min(10, n));
    }

    internal void SetKeys(int q) => Keys = Enumerable.Range(0, q).Select(i => i * 37 % ZoneCount).ToArray();
    internal void SetK(int k)
    {
        K = k;
        _heap = new PriorityQueue<ResultRow, ResultRow>(Math.Min(k, Items.Length), RankOrder.WorstFirst);
    }
    private static bool Matches(int score, int active) => score > 1024 && active != 0;
    private long FinishRows(int count)
    {
        ResultCount = count;
        return count == 0 ? 0 : count + Rows[0].Score + Rows[count - 1].Entity.Id;
    }
    private long FinishJoined(int count)
    {
        ResultCount = count;
        return count == 0 ? 0 : count + Joined[0].TargetX + Joined[count - 1].Entity.Id;
    }
    private long FinishSummaries()
    {
        ResultCount = Keys.Length;
        long sum = 0;
        for (int i = 0; i < Keys.Length; i++) sum += Summaries[i].Count + Summaries[i].Weight;
        return sum;
    }

    internal long MaterializedProjection()
    {
        _matches.Clear();
        foreach (var e in _world.Query(ProjectionScope))
            if (Matches(_world.Get<Score>(e).Value, _world.Get<Active>(e).Value)) _matches.Add(e);
        int count = 0;
        foreach (var e in _matches)
            Rows[count++] = new ResultRow(e, _world.Get<Score>(e).Value, _world.Get<Weight>(e).Value);
        return FinishRows(count);
    }
    internal long EntityProjection()
    {
        int count = 0;
        foreach (var e in _world.Query(ProjectionScope))
        {
            int score = _world.Get<Score>(e).Value;
            if (Matches(score, _world.Get<Active>(e).Value))
                Rows[count++] = new ResultRow(e, score, _world.Get<Weight>(e).Value);
        }
        return FinishRows(count);
    }
    internal long ChunkProjection()
    {
        int count = 0;
        foreach (var chunk in _world.Query(ProjectionScope).GetChunks())
        {
            var scores = chunk.GetSpan<Score>();
            var weights = chunk.GetSpan<Weight>();
            var active = chunk.GetSpan<Active>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
                if (Matches(scores[i].Value, active[i].Value))
                    Rows[count++] = new ResultRow(entities[i], scores[i].Value, weights[i].Value);
        }
        return FinishRows(count);
    }

    internal long RepeatedGroupScan()
    {
        var chunks = _world.Query(GroupScope).GetChunks();
        for (int q = 0; q < Keys.Length; q++)
        {
            Summary sum = default;
            foreach (var chunk in chunks)
            {
                var zones = chunk.GetSpan<Zone>();
                var weights = chunk.GetSpan<Weight>();
                for (int i = 0; i < chunk.Count; i++)
                    if (zones[i].Value == Keys[q]) { sum.Count++; sum.Weight += weights[i].Value; }
            }
            Summaries[q] = sum;
        }
        return FinishSummaries();
    }
    internal long HashGroup()
    {
        _hashSummary.Clear();
        foreach (var chunk in _world.Query(GroupScope).GetChunks())
        {
            var zones = chunk.GetSpan<Zone>();
            var weights = chunk.GetSpan<Weight>();
            for (int i = 0; i < chunk.Count; i++)
            {
                ref Summary sum = ref CollectionsMarshal.GetValueRefOrAddDefault(_hashSummary, zones[i].Value, out _);
                sum.Count++;
                sum.Weight += weights[i].Value;
            }
        }
        for (int q = 0; q < Keys.Length; q++)
            Summaries[q] = _hashSummary.TryGetValue(Keys[q], out var sum) ? sum : default;
        return FinishSummaries();
    }
    internal long DenseGroup()
    {
        Array.Clear(_denseSummary);
        foreach (var chunk in _world.Query(GroupScope).GetChunks())
        {
            var zones = chunk.GetSpan<Zone>();
            var weights = chunk.GetSpan<Weight>();
            for (int i = 0; i < chunk.Count; i++)
            {
                ref Summary sum = ref _denseSummary[zones[i].Value];
                sum.Count++;
                sum.Weight += weights[i].Value;
            }
        }
        for (int q = 0; q < Keys.Length; q++) Summaries[q] = _denseSummary[Keys[q]];
        return FinishSummaries();
    }

    internal long FullSort()
    {
        int count = 0;
        foreach (var chunk in _world.Query(ProjectionScope).GetChunks())
        {
            var scores = chunk.GetSpan<Score>();
            var weights = chunk.GetSpan<Weight>();
            var active = chunk.GetSpan<Active>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
                if (Matches(scores[i].Value, active[i].Value))
                    _sortScratch[count++] = new ResultRow(entities[i], scores[i].Value, weights[i].Value);
        }
        Array.Sort(_sortScratch, 0, count, RankOrder.BestFirst);
        int take = Math.Min(K, count);
        _sortScratch.AsSpan(0, take).CopyTo(Rows);
        return FinishRows(take);
    }
    internal long TopK()
    {
        _heap.Clear();
        if (K == 0) return FinishRows(0);
        foreach (var chunk in _world.Query(ProjectionScope).GetChunks())
        {
            var scores = chunk.GetSpan<Score>();
            var weights = chunk.GetSpan<Weight>();
            var active = chunk.GetSpan<Active>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (!Matches(scores[i].Value, active[i].Value)) continue;
                var candidate = new ResultRow(entities[i], scores[i].Value, 0);
                if (_heap.Count < K)
                {
                    candidate = candidate with { Weight = weights[i].Value };
                    _heap.Enqueue(candidate, candidate);
                }
                else if (RankOrder.BestFirst.Compare(candidate, _heap.Peek()) < 0)
                {
                    candidate = candidate with { Weight = weights[i].Value };
                    _heap.DequeueEnqueue(candidate, candidate);
                }
            }
        }
        int count = _heap.Count;
        int destination = count;
        // Worst-first dequeue is already sorted; reverse it into the output.
        while (_heap.TryDequeue(out var row, out _)) Rows[--destination] = row;
        return FinishRows(count);
    }

    internal long EntityNestedJoin()
    {
        int count = 0;
        foreach (var chunk in _world.Query(JoinScope).GetChunks())
        {
            var keys = chunk.GetSpan<TargetKey>();
            var scores = chunk.GetSpan<Score>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                foreach (var target in _world.Query(TargetScope))
                {
                    var data = _world.Get<TargetData>(target);
                    if (data.Key != keys[i].Value) continue;
                    Joined[count++] = new JoinedRow(entities[i], scores[i].Value, data.X);
                    break;
                }
            }
        }
        return FinishJoined(count);
    }
    internal long ChunkNestedJoin()
    {
        int count = 0;
        var targets = _world.Query(TargetScope).GetChunks();
        foreach (var chunk in _world.Query(JoinScope).GetChunks())
        {
            var keys = chunk.GetSpan<TargetKey>();
            var scores = chunk.GetSpan<Score>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                bool found = false;
                foreach (var targetChunk in targets)
                {
                    var data = targetChunk.GetSpan<TargetData>();
                    for (int t = 0; t < targetChunk.Count; t++)
                    {
                        if (data[t].Key != keys[i].Value) continue;
                        Joined[count++] = new JoinedRow(entities[i], scores[i].Value, data[t].X);
                        found = true;
                        break;
                    }
                    if (found) break;
                }
            }
        }
        return FinishJoined(count);
    }
    internal long HashJoin()
    {
        _targetIndex.Clear();
        foreach (var chunk in _world.Query(TargetScope).GetChunks())
        {
            var data = chunk.GetSpan<TargetData>();
            for (int t = 0; t < chunk.Count; t++) _targetIndex.Add(data[t].Key, data[t].X);
        }
        int count = 0;
        foreach (var chunk in _world.Query(JoinScope).GetChunks())
        {
            var keys = chunk.GetSpan<TargetKey>();
            var scores = chunk.GetSpan<Score>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
                if (_targetIndex.TryGetValue(keys[i].Value, out int x))
                    Joined[count++] = new JoinedRow(entities[i], scores[i].Value, x);
        }
        return FinishJoined(count);
    }
    internal long DenseJoin()
    {
        Array.Clear(_targetPresent);
        foreach (var chunk in _world.Query(TargetScope).GetChunks())
        {
            var data = chunk.GetSpan<TargetData>();
            for (int t = 0; t < chunk.Count; t++)
            {
                _targetPresent[data[t].Key] = true;
                _targetX[data[t].Key] = data[t].X;
            }
        }
        int count = 0;
        foreach (var chunk in _world.Query(JoinScope).GetChunks())
        {
            var keys = chunk.GetSpan<TargetKey>();
            var scores = chunk.GetSpan<Score>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int key = keys[i].Value;
                if ((uint)key < (uint)_targetPresent.Length && _targetPresent[key])
                    Joined[count++] = new JoinedRow(entities[i], scores[i].Value, _targetX[key]);
            }
        }
        return FinishJoined(count);
    }
    public void Dispose() => _world.Dispose();
}

internal static class Correctness
{
    internal static void Run()
    {
        int cases = 0;
        foreach (int n in new[] { 0, 1, 17, 10_000, 100_000 })
        foreach (int targets in new[] { 2, 8, 64 })
        foreach (bool hot in new[] { false, true })
        {
            using var f = new Fixture(n, targets, hot);
            var projection = f.Items.Where(i => i.Score > 1024 && i.Active != 0)
                .Select(i => new ResultRow(i.Entity, i.Score, i.Weight)).OrderBy(r => r.Entity.Id).ToArray();
            foreach (Func<long> run in new Func<long>[] { f.MaterializedProjection, f.EntityProjection, f.ChunkProjection })
            {
                run();
                var actual = f.Rows.AsSpan(0, f.ResultCount).ToArray().OrderBy(r => r.Entity.Id).ToArray();
                Equal(projection, actual, "projection"); cases++;
            }
            foreach (int q in new[] { 1, 64, 512 })
            {
                f.SetKeys(q);
                var expected = f.Keys.Select(k => new Summary(f.Items.Count(i => i.Zone == k), f.Items.Where(i => i.Zone == k).Sum(i => (long)i.Weight))).ToArray();
                foreach (Func<long> run in new Func<long>[] { f.RepeatedGroupScan, f.HashGroup, f.DenseGroup })
                {
                    run(); Equal(expected, f.Summaries.AsSpan(0, f.ResultCount).ToArray(), "group"); cases++;
                }
            }
            foreach (int k in new[] { 0, 1, 10, 1000, n + 1 })
            {
                f.SetK(k);
                var expected = projection.OrderBy(r => r, RankOrder.BestFirst).Take(k).ToArray();
                foreach (Func<long> run in new Func<long>[] { f.FullSort, f.TopK })
                {
                    run(); Equal(expected, f.Rows.AsSpan(0, f.ResultCount).ToArray(), "top-k"); cases++;
                }
            }
            var joined = f.Items.Where(i => f.TargetOracle.ContainsKey(i.Target))
                .Select(i => new JoinedRow(i.Entity, i.Score, f.TargetOracle[i.Target])).OrderBy(r => r.Entity.Id).ToArray();
            foreach (Func<long> run in new Func<long>[] { f.EntityNestedJoin, f.ChunkNestedJoin, f.HashJoin, f.DenseJoin })
            {
                run();
                Equal(joined, f.Joined.AsSpan(0, f.ResultCount).ToArray().OrderBy(r => r.Entity.Id).ToArray(), "join"); cases++;
            }
        }
        Console.WriteLine($"Exact correctness comparisons: {cases}; covers empty results, absent keys, missing targets, score ties, K=0/K>N, mixed archetypes, uniform/hot distributions.");
    }
    private static void Equal<T>(T[] expected, T[] actual, string scenario)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"{scenario} mismatch: expected={expected.Length}, actual={actual.Length}");
    }
}

internal static class Measure
{
    private const int WarmupMs = 300;
    private const int SampleMs = 350;
    private static long _sink;
    internal static void Group(string dataset, string workload, params Variant[] variants)
    {
        var times = variants.Select(_ => new double[3]).ToArray();
        var allocations = variants.Select(_ => new double[3]).ToArray();
        foreach (var v in variants) Timed(v.Execute, WarmupMs);
        for (int sample = 0; sample < 3; sample++)
        for (int offset = 0; offset < variants.Length; offset++)
        {
            int index = (sample + offset) % variants.Length;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            var (elapsed, iterations) = Timed(variants[index].Execute, SampleMs);
            allocations[index][sample] = (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)iterations;
            times[index][sample] = elapsed * 1000 / iterations;
        }
        for (int i = 0; i < variants.Length; i++)
        {
            Array.Sort(times[i]); Array.Sort(allocations[i]);
            Console.WriteLine($"{dataset},{workload},{variants[i].Name},{times[i][1]:F3},{times[i][0]:F3},{times[i][2]:F3},{allocations[i][1]:F1}");
        }
    }
    private static (double Ms, long Iterations) Timed(Func<long> execute, int milliseconds)
    {
        long start = Stopwatch.GetTimestamp();
        long deadline = start + (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
        long iterations = 0, sink = 0, end;
        do
        {
            sink = unchecked(sink + execute());
            iterations++;
            end = Stopwatch.GetTimestamp();
        } while (end < deadline);
        Volatile.Write(ref _sink, sink);
        return ((end - start) * 1000.0 / Stopwatch.Frequency, iterations);
    }
}
