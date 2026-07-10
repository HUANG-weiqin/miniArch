// ManagedEntityMap ValueLab — M2: Benchmark 5 Entity -> managed object sidecar implementations.
// Uses real MiniArch.World and Entity. Runs in Release config only (per AGENTS.md).
//
// Run:
//   dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab
//
// All measurements share the same entity arrays, mapped set, miss set, and destroy set
// so comparisons across implementations are fair.

using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MiniArch;

#nullable enable

// ────────────────────────────────────────────────────────────────
// Parameters
var entityCount = 100000;
var repetitions = 3;
var warmup = 1;
double mappingRatio = 1.0;
double destroyRatio = 0.1;
string operationMix = "all";
bool correctnessOnly = false;

// Parse args
for (int i = 0; i < args.Length; i++)
{
    switch (args[i].ToLowerInvariant())
    {
        case "--entity-count" when i + 1 < args.Length: entityCount = int.Parse(args[++i]); break;
        case "--repetitions" when i + 1 < args.Length: repetitions = int.Parse(args[++i]); break;
        case "--warmup" when i + 1 < args.Length: warmup = int.Parse(args[++i]); break;
        case "--mapping-ratio" when i + 1 < args.Length: mappingRatio = double.Parse(args[++i]); break;
        case "--destroy-ratio" when i + 1 < args.Length: destroyRatio = double.Parse(args[++i]); break;
        case "--operation-mix" when i + 1 < args.Length: operationMix = args[++i]; break;
        case "--correctness-only": correctnessOnly = true; break;
        case "--help":
        case "-h":
            PrintUsage();
            return;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            PrintUsage();
            Environment.ExitCode = 2;
            return;
    }
}

if (correctnessOnly)
{
    Console.WriteLine("Correctness scenarios are implemented in M3.");
    return;
}

if (!string.Equals(operationMix, "all", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"--operation-mix '{operationMix}' is not supported in M2. Only 'all' is supported. M3+ will add selective mixes.");
    Environment.ExitCode = 2;
    return;
}

if (entityCount <= 0) { Console.Error.WriteLine("--entity-count must be > 0."); Environment.ExitCode = 2; return; }
if (repetitions <= 0) { Console.Error.WriteLine("--repetitions must be > 0."); Environment.ExitCode = 2; return; }
if (warmup < 0) { Console.Error.WriteLine("--warmup must be >= 0."); Environment.ExitCode = 2; return; }
if (mappingRatio <= 0 || mappingRatio > 1) { Console.Error.WriteLine("--mapping-ratio must be in (0, 1]."); Environment.ExitCode = 2; return; }
if (destroyRatio < 0 || destroyRatio >= 1) { Console.Error.WriteLine("--destroy-ratio must be in [0, 1)."); Environment.ExitCode = 2; return; }

Console.WriteLine("=== ManagedEntityMap ValueLab M2 ===");
Console.WriteLine($"entity-count:  {entityCount}");
Console.WriteLine($"repetitions:   {repetitions}");
Console.WriteLine($"warmup:        {warmup}");
Console.WriteLine($"mapping-ratio: {mappingRatio}");
Console.WriteLine($"destroy-ratio: {destroyRatio}");
Console.WriteLine($"operation-mix: {operationMix}");
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// Shared constants (computed once)
using var world = new World();

int mappedCount = (int)(entityCount * mappingRatio);
mappedCount = Math.Max(1, Math.Min(mappedCount, entityCount));
// Miss entities: stale version handles for TryGet-miss measurement.
// Always >= 1, up to 100, taken from the mapped set (version+1).
int missCount = Math.Clamp(mappedCount / 100, 1, 100);
int destroyCount = (int)(mappedCount * destroyRatio);

// Pre-allocated payloads — avoid allocation inside Set measurement
var payloads = new Payload[mappedCount];
for (int i = 0; i < mappedCount; i++)
    payloads[i] = new Payload { Value = i };

Console.WriteLine($"Mapped count:  {mappedCount}");
Console.WriteLine($"Miss count:    {missCount}");
Console.WriteLine($"Destroy count: {destroyCount}");
Console.WriteLine();

// ────────────────────────────────────────────────────────────────
// All implementations
var maps = new (string name, IManagedEntityMap<Payload> map)[]
{
    ("NaiveDict",       new NaiveDictionaryMap<Payload>()),
    ("CompDict",        new CompetentDictionaryMap<Payload>(world)),
    ("RawDenseUnsafe",  new RawDenseUnsafeMap<Payload>()),
    ("CompDenseUser",   new CompetentDenseUserMap<Payload>(world)),
    ("ProtoMap",        new ManagedEntityMapPrototype<Payload>(world)),
};

// ────────────────────────────────────────────────────────────────
// Benchmark
var results = new List<BenchResult>();

foreach (var (mapName, map) in maps)
{
    Console.WriteLine($"--- {mapName} ---");

    // Create fresh entities for this map iteration
    // (each map gets its own generation to avoid stale-handle cross-contamination)
    var allEntities = new Entity[entityCount];
    for (int i = 0; i < entityCount; i++)
        allEntities[i] = world.Create();

    // Mapped entities: first mappedCount of allEntities (always alive)
    var mappedEntities = allEntities.AsSpan(0, mappedCount).ToArray();

    // Miss entities: stale handles (version+1) from the last missCount mapped entities
    var missEntities = new Entity[missCount];
    for (int i = 0; i < missCount; i++)
    {
        var src = mappedEntities[mappedCount - 1 - i];
        missEntities[i] = new Entity(src.Id, src.Version + 1);
    }

    // Set
    Measure("Set", map,
        prep: () => map.Clear(),
        op: () => { for (int i = 0; i < mappedCount; i++) map.Set(mappedEntities[i], payloads[i]); },
        ops: mappedCount);

    // Get hit
    Measure("Get hit", map,
        prep: () => { for (int i = 0; i < mappedCount; i++) map.Set(mappedEntities[i], payloads[i]); },
        op: () => { for (int i = 0; i < mappedCount; i++) _ = map.Get(mappedEntities[i]); },
        ops: mappedCount);

    // TryGet hit
    Measure("TryGet hit", map,
        prep: () => { for (int i = 0; i < mappedCount; i++) map.Set(mappedEntities[i], payloads[i]); },
        op: () => { for (int i = 0; i < mappedCount; i++) map.TryGet(mappedEntities[i], out _); },
        ops: mappedCount);

    // TryGet miss
    Measure("TryGet miss", map,
        prep: () => { for (int i = 0; i < mappedCount; i++) map.Set(mappedEntities[i], payloads[i]); },
        op: () => { for (int i = 0; i < missCount; i++) map.TryGet(missEntities[i], out _); },
        ops: missCount);

    // Remove
    Measure("Remove", map,
        prep: () => { for (int i = 0; i < mappedCount; i++) map.Set(mappedEntities[i], payloads[i]); },
        op: () => { for (int i = 0; i < mappedCount; i++) map.Remove(mappedEntities[i]); },
        ops: mappedCount);

    // Align: create zombies by destroying entities, then measure Align()
    // Uses cleanup callback to destroy entities between runs so each prep
    // starts from a clean world slate.
    Entity[]? alignBatch = null;
    Measure("Align", map,
        prep: () =>
        {
            map.Clear(); // Start with clear map
            // Create mappedCount fresh entities, map all of them, then destroy
            // the first destroyCount mapped entities so Align observes zombies.
            var batch = new Entity[mappedCount];
            for (int i = 0; i < batch.Length; i++)
                batch[i] = world.Create();
            for (int i = 0; i < mappedCount; i++)
                map.Set(batch[i], payloads[i]);
            for (int i = 0; i < destroyCount; i++)
                world.Destroy(batch[i]);
            alignBatch = batch;
        },
        op: () => map.Align(),
        ops: Math.Max(1, mappedCount),
        cleanup: () =>
        {
            // Destroy all entities from the batch that are still alive
            if (alignBatch is not null)
            {
                for (int i = 0; i < alignBatch.Length; i++)
                {
                    if (world.IsAlive(alignBatch[i]))
                        world.Destroy(alignBatch[i]);
                }
                alignBatch = null;
            }
        });

    // Clear
    Measure("Clear", map,
        prep: () => { for (int i = 0; i < mappedCount; i++) map.Set(mappedEntities[i], payloads[i]); },
        op: () => map.Clear(),
        ops: 1); // single operation per Clear call

    // TrimExcess
    Measure("TrimExcess", map,
        prep: () => { for (int i = 0; i < mappedCount; i++) map.Set(mappedEntities[i], payloads[i]); },
        op: () => map.TrimExcess(),
        ops: 1); // single operation per TrimExcess call

    // Cleanup between maps: destroy all entities for this generation
    for (int i = 0; i < entityCount; i++)
        world.Destroy(allEntities[i]);

    Console.WriteLine();
}

// ────────────────────────────────────────────────────────────────
// Output
Console.WriteLine("## Benchmark Results");
Console.WriteLine();
Console.WriteLine("| Map | Operation | Ops/s | ns/op | Alloc/op | Gen0 | Gen1 | Gen2 | Ret KB |");
Console.WriteLine("|---|---|---|---|---|---|---|---|---|");

foreach (var r in results)
{
    string allocStr = r.AllocBytesPerOp switch
    {
        < 1000 => $"{r.AllocBytesPerOp,8:F1}",
        _ => $"{r.AllocBytesPerOp,8:F0}"
    };
    Console.WriteLine(
        $"| {r.MapName,-15} | {r.OpName,-13} | {r.OpsPerSec,10:F1} | {r.NsPerOp,8:F1} | {allocStr} | {r.Gen0,4} | {r.Gen1,4} | {r.Gen2,4} | {r.RetainedKB,7:F2} |");
}

Console.WriteLine();
Console.WriteLine("## Notes");
Console.WriteLine();
Console.WriteLine("- **RawDenseUnsafeMap is a performance upper bound / incorrect example.**");
Console.WriteLine("  It does NOT call `world.IsAlive(entity)` for liveness checks.");
Console.WriteLine("  It can return stale data for destroyed entities whose slot was reused.");
Console.WriteLine("  It serves as an unattainable performance ceiling for correct implementations.");
Console.WriteLine();
Console.WriteLine("- **Managed sidecar is host-local, non-deterministic, and non-thread-safe.**");
Console.WriteLine("  It does not participate in lockstep, FrameDelta, Snapshot, Checksum, or Replay.");
Console.WriteLine("  `world.IsAlive(entity)` is the only liveness oracle; sidecar versions only represent");
Console.WriteLine("  slot binding state.");
Console.WriteLine();
Console.WriteLine("- Clear and TrimExcess are measured as single operations (1 op per call).");
Console.WriteLine("  All other operations count per-entity invocations as ops.");
Console.WriteLine("- Alloc/op may be inflated for Clear/TrimExcess since they represent batch-level");
Console.WriteLine("  allocation for the single call, not divided by entity count.");
Console.WriteLine("- Ret KB = retained memory after measurement runs (GC.GetTotalMemory delta from baseline).");

// ────────────────────────────────────────────────────────────────
void Measure(string opName, IManagedEntityMap<Payload> map, Action prep, Action op, int ops, Action? cleanup = null)
{
    // Warmup
    for (int w = 0; w < warmup; w++)
    {
        prep();
        op();
        cleanup?.Invoke();
    }

    // Force GC before measurement
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();

    long baselineMemory = GC.GetTotalMemory(true);

    long totalTicks = 0;
    long totalAlloc = 0;
    int totalGen0 = 0, totalGen1 = 0, totalGen2 = 0;

    for (int r = 0; r < repetitions; r++)
    {
        // Clean up from previous run, then prepare this run's state
        cleanup?.Invoke();
        prep();

        long allocBefore = GC.GetTotalAllocatedBytes();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        op();
        sw.Stop();

        long allocAfter = GC.GetTotalAllocatedBytes();
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);

        totalTicks += sw.ElapsedTicks;
        totalAlloc += (allocAfter - allocBefore);
        totalGen0 += (gen0After - gen0Before);
        totalGen1 += (gen1After - gen1Before);
        totalGen2 += (gen2After - gen2Before);
    }
    // Final cleanup
    cleanup?.Invoke();

    // Compute per-op metrics
    double totalSec = (double)totalTicks / Stopwatch.Frequency;
    double opsPerSec = (ops * repetitions) / totalSec;
    double nsPerOp = (totalSec * 1_000_000_000.0) / (ops * repetitions);
    double allocPerOp = (double)totalAlloc / (ops * repetitions);
    double avgGen0 = (double)totalGen0 / repetitions;
    double avgGen1 = (double)totalGen1 / repetitions;
    double avgGen2 = (double)totalGen2 / repetitions;

    // Retained memory: collect after all runs and compare to baseline
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    long retainedMemory = GC.GetTotalMemory(true) - baselineMemory;
    double retainedKB = retainedMemory / 1024.0;

    string mapName = map.GetType().Name.Replace("`1", "");
    results.Add(new BenchResult(mapName, opName, opsPerSec, nsPerOp, allocPerOp,
        (int)Math.Round(avgGen0), (int)Math.Round(avgGen1), (int)Math.Round(avgGen2), retainedKB));

    // Inline progress line
    Console.WriteLine($"  {mapName,-15} {opName,-13} {opsPerSec,10:F1} ops/s  {nsPerOp,8:F1} ns/op  alloc={allocPerOp,9:F1}B  GC({avgGen0:F1},{avgGen1:F1},{avgGen2:F1})  ret={retainedKB,7:F2}KB");
}

void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --entity-count <int>     Number of entities to create (default: 100000)");
    Console.WriteLine("  --repetitions <int>      Number of measurement repetitions (default: 3)");
    Console.WriteLine("  --warmup <int>           Warmup runs before measurement (default: 1)");
    Console.WriteLine("  --mapping-ratio <double> Fraction of entities to map (default: 1.0)");
    Console.WriteLine("  --destroy-ratio <double> Fraction of mapped to destroy for Align (default: 0.1)");
    Console.WriteLine("  --operation-mix <string> Operation mix (default: all; only 'all' supported in M2)");
    Console.WriteLine("  --correctness-only       Stub: prints 'M3' message and exits 0");
    Console.WriteLine("  --help                   Show this help");
}

// ────────────────────────────────────────────────────────────────
// Benchmark result record
record struct BenchResult(
    string MapName, string OpName,
    double OpsPerSec, double NsPerOp,
    double AllocBytesPerOp,
    int Gen0, int Gen1, int Gen2,
    double RetainedKB);

// ────────────────────────────────────────────────────────────────
// Sealed managed payload
sealed class Payload
{
    public int Value;
}

// ────────────────────────────────────────────────────────────────
// Common interface for all 5 map implementations
interface IManagedEntityMap<T>
{
    void Set(Entity entity, T value);
    T Get(Entity entity);
    bool TryGet(Entity entity, out T value);
    bool Remove(Entity entity);
    void Align();
    void Clear();
    void TrimExcess();
    int Count { get; }
}

// ────────────────────────────────────────────────────────────────
// 1) NaiveDictionaryMap<T> — strawman: Dictionary<Entity, T>
sealed class NaiveDictionaryMap<T> : IManagedEntityMap<T>
{
    private readonly Dictionary<Entity, T> _dict = new();

    public void Set(Entity entity, T value) => _dict[entity] = value;
    public T Get(Entity entity) => _dict[entity];
    public bool TryGet(Entity entity, out T value) => _dict.TryGetValue(entity, out value!);
    public bool Remove(Entity entity) => _dict.Remove(entity);
    public void Align() { } // No-op: no world reference, can't detect zombies
    public void Clear() => _dict.Clear();
    public void TrimExcess() => _dict.TrimExcess();
    public int Count => _dict.Count;
}

// ────────────────────────────────────────────────────────────────
// 2) CompetentDictionaryMap<T> — Dictionary<int, Entry> with version + IsAlive
sealed class CompetentDictionaryMap<T> : IManagedEntityMap<T> where T : class
{
    private readonly struct Entry(T value, int version)
    {
        public readonly T Value = value;
        public readonly int Version = version;
    }

    private readonly World _world;
    private readonly Dictionary<int, Entry> _dict = new();

    public CompetentDictionaryMap(World world) => _world = world;

    public void Set(Entity entity, T value)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException($"Entity {entity} is not alive.");
        _dict[entity.Id] = new Entry(value, entity.Version);
    }

    public T Get(Entity entity)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException($"Entity {entity} is not alive.");
        if (_dict.TryGetValue(entity.Id, out var entry) && entry.Version == entity.Version)
            return entry.Value;
        throw new KeyNotFoundException($"Entity {entity} not found in map.");
    }

    public bool TryGet(Entity entity, out T value)
    {
        value = null!;
        if (!_world.IsAlive(entity))
            return false;
        if (_dict.TryGetValue(entity.Id, out var entry) && entry.Version == entity.Version)
        {
            value = entry.Value;
            return true;
        }
        return false;
    }

    public bool Remove(Entity entity)
    {
        if (!_world.IsAlive(entity))
            return false;
        if (!_dict.TryGetValue(entity.Id, out var entry) || entry.Version != entity.Version)
            return false;
        return _dict.Remove(entity.Id);
    }

    public void Align()
    {
        // Collect zombie IDs (entries whose entity is no longer alive)
        List<int>? zombieIds = null;
        foreach (var kvp in _dict)
        {
            var e = new Entity(kvp.Key, kvp.Value.Version);
            if (!_world.IsAlive(e))
            {
                (zombieIds ??= new List<int>()).Add(kvp.Key);
            }
        }
        if (zombieIds is not null)
        {
            foreach (var id in zombieIds)
                _dict.Remove(id);
        }
    }

    public void Clear() => _dict.Clear();
    public void TrimExcess() => _dict.TrimExcess();
    public int Count => _dict.Count;
}

// ────────────────────────────────────────────────────────────────
// 3) RawDenseUnsafeMap<T> — T?[] + int[] versions, NO IsAlive (performance upper bound / wrong example)
sealed class RawDenseUnsafeMap<T> : IManagedEntityMap<T> where T : class
{
    private T?[] _values = [];
    private int[] _versions = [];

    private void EnsureCapacity(int id)
    {
        if (id < _values.Length) return;
        int newSize = Math.Max(id + 1, Math.Max(_values.Length * 2, 16));
        Array.Resize(ref _values, newSize);
        Array.Resize(ref _versions, newSize);
    }

    public void Set(Entity entity, T value)
    {
        EnsureCapacity(entity.Id);
        _values[entity.Id] = value;
        _versions[entity.Id] = entity.Version;
    }

    public T Get(Entity entity)
    {
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            throw new KeyNotFoundException($"Entity {entity} not found in map.");
        return _values[id]!;
    }

    public bool TryGet(Entity entity, out T value)
    {
        value = null!;
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            return false;
        value = _values[id]!;
        return true;
    }

    public bool Remove(Entity entity)
    {
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            return false;
        _values[id] = null;
        _versions[id] = 0; // 0 = empty
        return true;
    }

    public void Align()
    {
        // No World reference — no zombie detection. This is the "unsafe/incorrect" example.
        // Intentional no-op (same as naive dict).
    }

    public void Clear()
    {
        Array.Clear(_values, 0, _values.Length);
        Array.Clear(_versions, 0, _versions.Length);
    }

    public void TrimExcess()
    {
        // No resizing: this is an upper-bound example, no trimming needed.
    }

    public int Count
    {
        get
        {
            int c = 0;
            for (int i = 0; i < _versions.Length; i++)
                if (_versions[i] != 0) c++;
            return c;
        }
    }
}

// ────────────────────────────────────────────────────────────────
// 4) CompetentDenseUserMap<T> — dense arrays + IsAlive + version + Align/Remove
sealed class CompetentDenseUserMap<T> : IManagedEntityMap<T> where T : class
{
    private readonly World _world;
    private T?[] _values = [];
    private int[] _versions = [];

    public CompetentDenseUserMap(World world) => _world = world;

    private void EnsureCapacity(int id)
    {
        if (id < _values.Length) return;
        int newSize = Math.Max(id + 1, Math.Max(_values.Length * 2, 16));
        Array.Resize(ref _values, newSize);
        Array.Resize(ref _versions, newSize);
    }

    public void Set(Entity entity, T value)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException($"Entity {entity} is not alive.");
        EnsureCapacity(entity.Id);
        _values[entity.Id] = value;
        _versions[entity.Id] = entity.Version;
    }

    public T Get(Entity entity)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException($"Entity {entity} is not alive.");
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            throw new KeyNotFoundException($"Entity {entity} not found in map.");
        return _values[id]!;
    }

    public bool TryGet(Entity entity, out T value)
    {
        value = null!;
        if (!_world.IsAlive(entity))
            return false;
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            return false;
        value = _values[id]!;
        return true;
    }

    public bool Remove(Entity entity)
    {
        if (!_world.IsAlive(entity))
            return false;
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            return false;
        _values[id] = null;
        _versions[id] = 0;
        return true;
    }

    public void Align()
    {
        for (int i = 0; i < _versions.Length; i++)
        {
            if (_versions[i] != 0)
            {
                var e = new Entity(i, _versions[i]);
                if (!_world.IsAlive(e))
                {
                    _values[i] = null;
                    _versions[i] = 0;
                }
            }
        }
    }

    public void Clear()
    {
        Array.Clear(_values, 0, _values.Length);
        Array.Clear(_versions, 0, _versions.Length);
    }

    public void TrimExcess()
    {
        // Find the highest occupied slot and trim array to that + 1
        int maxId = -1;
        for (int i = 0; i < _versions.Length; i++)
        {
            if (_versions[i] != 0) maxId = i;
        }
        int targetSize = maxId + 1;
        if (targetSize < _values.Length && targetSize > 0)
        {
            Array.Resize(ref _values, targetSize);
            Array.Resize(ref _versions, targetSize);
        }
    }

    public int Count
    {
        get
        {
            int c = 0;
            for (int i = 0; i < _versions.Length; i++)
                if (_versions[i] != 0) c++;
            return c;
        }
    }
}

// ────────────────────────────────────────────────────────────────
// 5) ManagedEntityMapPrototype<T> — candidate library prototype
//   API: Set/Get/TryGet/Remove/Align/Clear/TrimExcess/Count
//   Set(null) throws.
//   world.IsAlive is the only liveness oracle.
//   Align only cleans zombies.
sealed class ManagedEntityMapPrototype<T> : IManagedEntityMap<T> where T : class
{
    private readonly World _world;
    private T?[] _values = [];
    private int[] _versions = [];
    private int _count;

    public ManagedEntityMapPrototype(World world, int initialCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _world = world;
        if (initialCapacity > 0)
        {
            _values = new T?[initialCapacity];
            _versions = new int[initialCapacity];
        }
    }

    private void EnsureCapacity(int id)
    {
        if (id < _values.Length) return;
        int newSize = Math.Max(id + 1, Math.Max(_values.Length * 2, 16));
        Array.Resize(ref _values, newSize);
        Array.Resize(ref _versions, newSize);
    }

    public void Set(Entity entity, T value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value), "ManagedEntityMapPrototype does not allow null values.");
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException($"Entity {entity} is not alive.");

        EnsureCapacity(entity.Id);
        ref int ver = ref _versions[entity.Id];
        bool wasEmpty = ver == 0;
        _values[entity.Id] = value;
        ver = entity.Version;
        if (wasEmpty) _count++;
    }

    public T Get(Entity entity)
    {
        if (!_world.IsAlive(entity))
            throw new InvalidOperationException($"Entity {entity} is not alive.");
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            throw new KeyNotFoundException($"Entity {entity} not found in map.");
        return _values[id]!;
    }

    public bool TryGet(Entity entity, out T value)
    {
        value = null!;
        if (!_world.IsAlive(entity))
            return false;
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            return false;
        value = _values[id]!;
        return true;
    }

    public bool Remove(Entity entity)
    {
        if (!_world.IsAlive(entity))
            return false;
        var id = entity.Id;
        if ((uint)id >= (uint)_values.Length || _versions[id] != entity.Version)
            return false;

        _values[id] = null;
        _versions[id] = 0;
        _count--;
        return true;
    }

    public void Align()
    {
        for (int i = 0; i < _versions.Length; i++)
        {
            if (_versions[i] != 0)
            {
                var e = new Entity(i, _versions[i]);
                if (!_world.IsAlive(e))
                {
                    _values[i] = null;
                    _versions[i] = 0;
                    _count--;
                }
            }
        }
    }

    public void Clear()
    {
        Array.Clear(_values, 0, _values.Length);
        Array.Clear(_versions, 0, _versions.Length);
        _count = 0;
    }

    public void TrimExcess()
    {
        int maxId = -1;
        for (int i = 0; i < _versions.Length; i++)
        {
            if (_versions[i] != 0) maxId = i;
        }
        int targetSize = maxId + 1;
        if (targetSize < _values.Length && targetSize > 0)
        {
            Array.Resize(ref _values, targetSize);
            Array.Resize(ref _versions, targetSize);
        }
    }

    public int Count => _count;
}
