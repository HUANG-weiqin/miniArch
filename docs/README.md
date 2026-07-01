# MiniArch API Guide

MiniArch exposes two API layers:

- **`MiniArch`** — Default user entry: `World`, `Entity`, `QueryDescription`
- **`MiniArch.Core`** — Advanced types: `CommandStream`, `FrameDelta`, `WorldSnapshot`, `EntityAccessor`, `IChunkForEach`

## Default Layer: `MiniArch`

```csharp
using MiniArch;

readonly record struct Position(int X, int Y);
readonly record struct Velocity(int X, int Y);

var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

var description = new QueryDescription()
    .With<Position>()
    .With<Velocity>();

foreach (var entity in world.Query(in description))
{
    if (world.TryGet(entity, out Position position) &&
        world.TryGet(entity, out Velocity velocity))
    {
        world.Set(entity, new Position(position.X + velocity.X, position.Y + velocity.Y));
    }
}
```

### `Entity`

```csharp
public readonly record struct Entity(int Id, int Version)
```

- `Id` — unique entity ID within the world
- `Version` — incremented on destroy/recycle for stale handle detection
- `IsValid` — `true` when `Id >= 0 && Version > 0`

`default(Entity)` is invalid; real entities start at `Version = 1`.

### `Query`

- `GetEnumerator()` — `foreach` support
- `OrderBy(IComparer<Entity>)` / `OrderBy(Comparison<Entity>)` — sorted enumeration
- `GetChunks()` — returns `ReadOnlySpan<ChunkView>` for batch/chunk-level access
- `ForEachChunk(ChunkAction)` — sequential chunk iteration (zero-alloc when delegate is cached)
- `ForEachChunk<TForEach>(ref TForEach)` — sequential chunk iteration via a struct `IChunkForEach` implementation (zero-alloc + JIT-devirtualised; supports stateful jobs via `ref`)
- `ForEachChunkParallel(ChunkAction)` — parallel chunk iteration; safe for component value reads/writes via `chunk.GetSpan<T>()`. Structural changes must be deferred to `CommandStream` after the call returns
- `ForEachChunkParallel<TForEach>(TForEach)` — parallel chunk iteration via a struct `IChunkForEach` (job struct copied per worker; use `[ThreadStatic]` fields for per-worker accumulation)
- `RefreshCount` — number of times the cached result has been invalidated

### `QueryDescription`

- `With<T>()` — include required component
- `Without<T>()` — exclude entities with this component
- `WithAny<T>()` — OR-match: include entities that have this component (in addition to required types)

## Advanced Layer: `MiniArch.Core`

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

var description = new QueryDescription().With<Position>().With<Velocity>();
var query = world.Query(in description);

// Chunk-level iteration with direct span access
foreach (var chunk in query.GetChunks())
{
    var positions = chunk.GetSpan<Position>();
    var velocities = chunk.GetSpan<Velocity>();
    for (var i = 0; i < positions.Length; i++)
    {
        positions[i] = new Position(
            positions[i].X + velocities[i].X,
            positions[i].Y + velocities[i].Y);
    }
}
```

### Available types

| Type | Description |
|---|---|
| `CommandStream` | Byte-stream recorder for deferred world mutations |
| `FrameDelta` | Self-contained delta for cross-world replay |
| `WorldSnapshot` | Binary world serialization |
| `EntityAccessor` | Ref struct for batched component access, via `World.Access(entity)` |
| `IChunkForEach` | Struct-generic chunk iteration interface: implement `OnChunk(ChunkView)` on a struct; pass to `Query.ForEachChunk<TForEach>(ref TForEach)` or `ForEachChunkParallel<TForEach>(TForEach)` for zero-alloc, JIT-devirtualised iteration |
| `ChunkView` | Chunk view returned by `GetChunks()` |

### `ChunkView`

```csharp
public int Count { get; }
public ReadOnlySpan<Entity> GetEntities()
public Span<T> GetSpan<T>() where T : unmanaged
public bool TryGetComponentIndex<T>(out int columnIndex) where T : unmanaged
public Span<T> GetComponentSpanAt<T>(int columnIndex) where T : unmanaged
```

## Common Types

### `World`

| Method | Description |
|---|---|
| `Create()` | Create empty entity |
| `Create<T1..T16>(...)` | Create entity with 1–16 components |
| `CreateMany(Span<Entity>)` | Batch create empty entities |
| `Destroy(Entity)` | Remove entity (cascades through hierarchy) |
| `EnsureCapacity(int)` | Pre-allocate entity slots |
| `Add<T>(Entity, T)` | Add component |
| `Set<T>(Entity, T)` | Set component (adds if missing) |
| `Remove<T>(Entity)` | Remove component |
| `Get<T>(Entity)` | Get component by value |
| `GetRef<T>(Entity)` | Get component by ref (for in-place mutation) |
| `TryGet<T>(Entity, out T)` | Try-get component |
| `Has<T>(Entity)` | Check component existence |
| `Access(Entity)` | Returns `EntityAccessor` for batched operations |
| `TryGetEntityVersion(Entity, out int)` | Check if a handle is alive and get its current version |
| `GetFirst<T>()` | Get first entity with a given component |
| `IsAlive(Entity)` | Validate entity handle |
| `AddChild(Entity, Entity)` | Parent-child hierarchy |
| `RemoveChild(Entity)` | Remove from hierarchy |
| `TryGetParent(Entity, out Entity)` | Get parent |
| `GetChildren(Entity)` | Get children list |
| `Query(in QueryDescription)` | Create a query |
| `Clone(Entity)` | Deep-copy an entity (including child subtree) |
| `Clone()` | Materialize a brand-new independent world (branching / long-lived checkpoint) |
| `CaptureState()` | Save mutable state into an opaque handle for in-place rollback (zero-alloc steady state; multiple handles may be live simultaneously for GGPO-style multi-frame rollback windows) |
| `RestoreState(WorldStateSnapshot)` | Revert this world to a previously captured state; handle is recycled back to the world's pool. Throws `InvalidOperationException` if the handle was already restored (check `WorldStateSnapshot.IsRecycled`) |
| `GetStats()` | Returns `WorldStats` |
| `GetArchetypeStats()` | Returns per-archetype statistics |
| `Replay(FrameDelta)` | Apply delta to produce identical state |
| `Dispose()` | Release resources |

### `CommandBuffer`

- `Create` / `Add<T>` / `Set<T>` / `Remove<T>` — record commands
- `Destroy` / `AddChild` / `RemoveChild` — structural changes
- `Clone(Entity)` — record an entity deep-copy
- `Submit()` — apply to world synchronously
- `Snapshot()` — produce a `FrameDelta` without applying
- `SubmitAndSnapshotAsync()` — pipelined: main thread submits while background builds delta

### `CommandStream`

Same API as `CommandBuffer`. Records to a flat byte stream instead of per-entity accumulation. 20–48% faster in game scenarios.

### `FrameDelta`

- `DeltaCount` — total number of delta entries
- `HasEntity(Entity)` — check if an entity is referenced
- `Merge(FrameDelta, FrameDelta)` — combine two deltas into one
- `IsEmpty` — whether the delta has no entries

### `ComponentSchema`

Development/debugging utility: captures the current component registry state as a SHA-256 fingerprint. Useful for investigating version mismatches between processes. Not a required part of the lockstep protocol.

- `Fingerprint()` → `byte[]` — 32-byte SHA-256 of the global `ComponentRegistry` (id→type mapping, order-dependent). Reflects current state at call time; types registered later are not included.

```csharp
var fp = ComponentSchema.Fingerprint();
// compare with peer's fingerprint during debugging
if (!fp.AsSpan().SequenceEqual(peerFp))
    Console.WriteLine("Registry divergence — different builds?");
```

### `WorldSnapshot`

- `Save(Stream, World)` — serialize world to binary stream
- `Load(Stream)` → deserialize and return a new `World`

### `EntityAccessor`

Obtained via `World.Access(entity)`. Multiple operations without repeated entity lookups:

```csharp
var accessor = world.Access(entity);
ref var pos = ref accessor.Get<Position>();
ref var vel = ref accessor.Get<Velocity>();
accessor.Set(new Health(100));
bool hasArmor = accessor.Has<Armor>();
```

### `IChunkForEach` (zero-alloc chunk iteration)

Implement `OnChunk(ChunkView)` on a `struct`; the JIT specialises the call site for the concrete type, removing the delegate allocation that the `ChunkAction`-based overloads incur when their lambda is not cached.

```csharp
// Stateless job (most common: write component values via spans)
readonly struct MoveJob : IChunkForEach
{
    public void OnChunk(ChunkView chunk)
    {
        var positions = chunk.GetSpan<Position>();
        var velocities = chunk.GetSpan<Velocity>();
        for (var i = 0; i < positions.Length; i++)
            positions[i] = new Position(
                positions[i].X + velocities[i].X,
                positions[i].Y + velocities[i].Y);
    }
}

var job = new MoveJob();
query.ForEachChunk(ref job);          // sequential; zero alloc
query.ForEachChunkParallel(job);      // parallel; struct captured per worker

// Stateful accumulator (sequential path supports `ref`, so struct fields
// are visible across chunks):
var sum = new SumJob();
query.ForEachChunk(ref sum);
return sum.Total;
```

## Constraints

- `default(Entity)` is invalid; real entities start at `Version = 1`
- `World.IsAlive(entity)` is the authoritative validity check
- `Destroy()` cascades through the hierarchy subtree
- `Set<T>()` on a missing component adds it (implicit migration)
- `CommandBuffer` supports concurrent recording; `Submit()` is single-threaded
- `WorldSnapshot` only supports unmanaged component types
- `World.RestoreState(snapshot)` throws if `snapshot.IsRecycled` is `true` — capture a fresh handle before restoring again

## Concurrency

| API | Semantics | Notes |
|---|---|---|
| `Query` / `OrderedQuery` | MT-Read | World must not mutate concurrently |
| `CommandBuffer` recording | MT-Record | Each thread records to its own buffer |
| `CommandBuffer.Submit()` | Exclusive | Single-threaded |
| `SubmitAndSnapshotAsync()` | Pipelined | Main thread: Submit; background: BuildDelta |
| `World` mutation API | Not concurrent | No concurrent writes |
