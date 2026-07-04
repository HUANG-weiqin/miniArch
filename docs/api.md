# MiniArch API Reference

---

## World

[`MiniArch`] Primary entry point. Manages entities and components.

| Method | Description |
|---|---|
| `Create()` | Create empty entity |
| `Create<T1..T16>(...)` | Create entity with 1–16 components |
| `CreateMany(Span<Entity>)` | Batch create empty entities |
| `Destroy(Entity)` | Remove entity (cascades through hierarchy) |
| `IsAlive(Entity)` | Validate entity handle |
| `EnsureCapacity(int)` | Pre-allocate entity slots |
| `Add<T>(Entity, T)` | Add a component |
| `Set<T>(Entity, T)` | Set a component value (throws if missing) |
| `Remove<T>(Entity)` | Remove a component |
| `Get<T>(Entity)` | Get component by value |
| `GetRef<T>(Entity)` | Get component by ref (zero-copy in-place mutation) |
| `TryGet<T>(Entity, out T)` | Try-get a component |
| `Has<T>(Entity)` | Check component existence |
| `GetSingleton<T>()` | Get single entity with component (throws if zero or >1) |
| `Access(Entity)` | Returns `EntityAccessor` for batched operations |
| `AddChild(Entity, Entity)` | Add child entity (parent-child hierarchy) |
| `RemoveChild(Entity)` | Remove from hierarchy |
| `TryGetParent(Entity, out Entity)` | Get parent |
| `EnumerateChildren(Entity)` | Enumerate children (zero-alloc) |
| `HasChildren(Entity)` | O(1) check for children |
| `Query(in QueryDescription)` | Create a query |
| `Clone(Entity)` | Deep-copy an entity (including child subtree) |
| `Clone()` | Materialize a new independent world (branching / checkpoint) |
| `CaptureState()` | Save mutable state into an opaque handle for in-place rollback |
| `RestoreState(WorldStateSnapshot)` | Revert world to a previously captured state |
| `GetStats()` | Returns `WorldStats` |
| `GetArchetypeStats()` | Returns per-archetype statistics |
| `Dispose()` | Release resources |

---

## Entity

[`MiniArch`] Entity handle.

```csharp
public readonly record struct Entity(int Id, int Version)
```

- `Id` — unique entity ID within the world
- `Version` — incremented on destroy/recycle for stale handle detection
- `IsValid` — `true` when `Id >= 0 && Version > 0`

`default(Entity)` is invalid; real entities start at `Version = 1`.

---

## QueryDescription

[`MiniArch`] Describes which entities a query matches.

| Method | Description |
|---|---|
| `With<T>()` | Include required component |
| `Without<T>()` | Exclude entities with this component |
| `WithAny<T>()` | OR-match: include entities that have this component |

---

## Query

[`MiniArch`] An enumerable result set from `World.Query()`.

| Member | Description |
|---|---|
| `GetEnumerator()` | `foreach` support over matching entities |
| `GetChunks()` | Returns `ReadOnlySpan<ChunkView>` for chunk-level iteration |
| `ForEachChunk(ChunkAction)` | Sequential chunk iteration |
| `ForEachChunk<TForEach>(ref TForEach)` | Struct-generic sequential iteration (zero-alloc, JIT-devirtualised) |
| `ForEachChunkParallel(ChunkAction)` | Multi-threaded chunk iteration |
| `ForEachChunkParallel<TForEach>(TForEach)` | Multi-threaded via struct job |
| `OrderByEntityId()` | Sorted ascending by entity ID |
| `OrderByEntityIdDescending()` | Sorted descending by entity ID |
| `OrderByComponent<T>(Comparison<T>)` | Sorted by component value (batch-linear scan) |
| `OrderByComponentDescending<T>(Comparison<T>)` | Descending variant |
| `RefreshCount` | Times the cached result has been invalidated |

> **See also:** [IChunkForEach](#ichunkforeach-zero-alloc-chunk-iteration) for the struct-generic pattern.

---

## ChunkView

[`MiniArch`] A contiguous slice of entities and their component arrays.

```csharp
public int Count { get; }
public ReadOnlySpan<Entity> GetEntities()
public Span<T> GetSpan<T>() where T : unmanaged
public bool TryGetComponentIndex<T>(out int columnIndex) where T : unmanaged
public Span<T> GetComponentSpanAt<T>(int columnIndex) where T : unmanaged
```

---

## CommandStream

[`MiniArch.Core`] The sole deferred mutation recorder. Flat byte stream, single-pass Set.

| Member | Description |
|---|---|
| `Create()` | Record entity creation |
| `Track(Entity)` | Returns `EntitySlot` that auto-resolves placeholder→real on Submit/Replay |
| `Add<T>(Entity, T)` | Record component addition |
| `Set<T>(Entity, T)` | Record component value change |
| `Remove<T>(Entity)` | Record component removal |
| `Destroy(Entity)` | Record entity destruction |
| `AddChild(Entity, Entity)` | Record hierarchy addition |
| `RemoveChild(Entity)` | Record hierarchy removal |
| `Clone(Entity)` | Record entity deep-copy |
| `Submit()` | Apply all recorded changes synchronously |
| `Snapshot()` | Produce a `FrameDelta` without applying |
| `Replay(FrameDelta)` | Apply a delta to produce identical state |
| `SubmitAndSnapshotAsync()` | Pipelined: main thread submits, background builds delta |
| `Clear()` | Discard recorded commands without applying |
| `ParallelRecording` | Enable multi-threaded recording on this stream |
| `DeferredEntities` | Enable placeholder entity IDs for lockstep mode |

---

## EntitySlot

[`MiniArch.Core`] A tracked entity handle that auto-updates when a deferred placeholder is resolved.

```csharp
public readonly struct EntitySlot
{
    public Entity Value { get; }
    public bool HasValue { get; }
    public static implicit operator Entity(EntitySlot slot) => slot.Value;
}
```

Obtained via `CommandStream.Track(Entity)`. Before Submit, `Value` returns the placeholder;
after Submit or Replay, `Value` returns the resolved real entity.

---

## FrameDelta

[`MiniArch.Core`] Self-contained delta of world mutations.

| Member | Description |
|---|---|
| `DeltaCount` | Total number of delta entries |
| `HasEntity(Entity)` | Check if an entity is referenced |
| `IsEmpty` | Whether the delta has no entries |
| `Concat(FrameDelta, FrameDelta)` | Concatenate two deltas in temporal order |

---

## Rollback (CaptureState / RestoreState)

[`MiniArch`] In-place world rollback with zero-alloc steady-state.

```csharp
var handle = world.CaptureState();    // save current state
// ... mutate world ...
world.RestoreState(handle);           // revert, handle auto-recycled to pool
```

- **Zero-allocation** after warm-up (handles recycled from pool)
- **Multiple handles** may be live simultaneously (GGPO-style multi-frame rollback window)
- **`WorldStateSnapshot.IsRecycled`** — check if a handle has already been restored
- `RestoreState` throws `InvalidOperationException` if the handle was already restored

---

## WorldSnapshot

[`MiniArch.Core`] Binary world serialization.

| Method | Description |
|---|---|
| `Save(Stream, World)` | Serialize world to binary stream |
| `Load(Stream)` | Deserialize and return a new `World` |

Only supports unmanaged component types.

---

## EntityAccessor

[`MiniArch.Core`] Ref struct for batched multi-component access on a single entity. Obtained via `World.Access(entity)`.

```csharp
var accessor = world.Access(entity);
ref var pos = ref accessor.Get<Position>();
ref var vel = ref accessor.Get<Velocity>();
accessor.Set(new Health(100));
bool hasArmor = accessor.Has<Armor>();
```

---

## ComponentSchema

[`MiniArch`] Development/debugging utility for cross-version compatibility checks.

| Method | Description |
|---|---|
| `Fingerprint()` | Returns `byte[]` — 32-byte SHA-256 of the global `ComponentRegistry` (id→type mapping) |

```csharp
var fp = ComponentSchema.Fingerprint();
if (!fp.AsSpan().SequenceEqual(peerFp))
    Console.WriteLine("Registry divergence — different builds?");
```

---

## IChunkForEach (zero-alloc chunk iteration)

[`MiniArch`] Implement `OnChunk(ChunkView)` on a `struct`; the JIT specialises the call site, removing delegate allocations.

```csharp
var job = new MoveJob();
query.ForEachChunk(ref job);         // sequential; zero alloc
query.ForEachChunkParallel(job);     // parallel; struct captured per worker

// Type declaration (after top-level statements)
readonly struct MoveJob : IChunkForEach
{
    public void OnChunk(ChunkView chunk)
    {
        var pos = chunk.GetSpan<Position>();
        var vel = chunk.GetSpan<Velocity>();
        for (int i = 0; i < pos.Length; i++)
            pos[i] = new Position(pos[i].X + vel[i].X, pos[i].Y + vel[i].Y);
    }
}
```

Stateful accumulation supported on the sequential path (fields visible across chunks via `ref`):

```csharp
var sum = new SumJob();
query.ForEachChunk(ref sum);
Console.WriteLine(sum.Total);

// Stateful job struct
struct SumJob : IChunkForEach
{
    public float Total;
    public void OnChunk(ChunkView chunk) { /* accumulate */ }
}
```

---

## Constraints

- `default(Entity)` is invalid; real entities start at `Version = 1`
- `World.IsAlive(entity)` is the authoritative validity check
- `Destroy()` cascades through the hierarchy subtree
- `Set<T>()` throws if the entity does not have the component; use `Add<T>()` to add a new component
- `WorldSnapshot` only supports unmanaged component types
- `World.RestoreState(snapshot)` throws if `snapshot.IsRecycled` is `true`

---

## Concurrency

| API | Semantics | Notes |
|---|---|---|
| `Query` / ordered queries | MT-Read | World must not mutate concurrently |
| `CommandStream` recording (non-parallel) | Single-threaded | Default mode (`ParallelRecording = false`) |
| `CommandStream` recording (parallel) | MT-Record | Enable `ParallelRecording = true`; Submit still exclusive |
| `CommandStream.Submit()` | Exclusive | Single-threaded |
| `SubmitAndSnapshotAsync()` | Pipelined | Main: Submit; Background: BuildDelta |
| `World` mutation API | Not concurrent | No concurrent writes |
