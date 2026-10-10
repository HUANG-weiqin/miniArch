# MiniArch API Reference

---

## World

[`MiniArch`] Primary entry point. Manages entities and components.

| Method | Description |
|---|---|
| `CreateEmpty()` | Create entity with no components |
| `Create<T1..T16>(...)` | Create entity with 1–16 components |
| `Destroy(Entity)` | Remove only the specified entity |
| `Destroy(ReadOnlySpan<Entity>)` | Remove the specified entities in a batch |
| `Destroy(in QueryDescription)` | Remove entities matching the query |
| `Clear(in QueryDescription)` | Fast bulk removal of matching entities |
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
| `Query(in QueryDescription)` | Create a query |
| `Clone(Entity)` | Copy only the specified entity's components into a new entity |
| `Clone()` | Materialize a new independent world (branching / checkpoint) |
| `Watch<TComponent,THandler>(QueryDescription?)` | Create a value-change watch for component `TComponent`; returns `ChangeWatch<TComponent, THandler>` |
| `Watch<TComponent,TValue,THandler>(QueryDescription?)` | Create a projected value-change watch; returns `ChangeWatch<TComponent, TValue, THandler>` |
| `Watch<THandler>(QueryDescription)` | Create a structural-transition watch; returns `TransitionWatch<THandler>` |
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
| `Exact()` | Match only archetypes whose component set is exactly the required set |

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
public Span<T> UnsafeGetComponentSpanAt<T>(int columnIndex) where T : unmanaged
```

`UnsafeGetComponentSpanAt<T>` is an opt-in hot-path API. The index must come from
`TryGetComponentIndex<T>` on the same chunk/archetype and is invalid after any
structural change. Debug builds reject a mismatched index; Release builds leave it
unchecked. Prefer `GetSpan<T>` unless profiling proves the cached-index path matters.

---

## ChangeWatch\<TComponent, THandler\>

[`MiniArch`] A pull-based watch that tracks value changes for component type `TComponent` by comparing the current world state against the last `Snapshot`. Obtained via `World.Watch<TComponent, THandler>()`.

```csharp
public sealed class ChangeWatch<TComponent, THandler>
    where TComponent : unmanaged, IEquatable<TComponent>
    where THandler : struct, IChangeHandler<TComponent>
{
    public ref THandler Handler { get; }
    public void Snapshot(World world);
    public void Diff(World world);
}
```

| Member | Description |
|---|---|
| `Handler` | Gets/sets the handler struct. Mutations affect subsequent `Diff` calls. |
| `Snapshot(World)` | Records a baseline of all matching entities' component values. Must be called before `Diff`. |
| `Diff(World)` | Scans the world; for each entity whose value differs from baseline, collects the diff into an internal buffer, then dispatches `IChangeHandler.OnChange` callbacks (two-phase safety). |

A baseline belongs to the full `(Entity.Id, Entity.Version)` identity. A replacement entity that reuses an id receives `default` as its old value.

---

## ChangeWatch\<TComponent, TValue, THandler\> (Projected)

[`MiniArch`] A pull-based watch that tracks *projected* value changes — the handler both projects component data into a tracked value and receives change callbacks. Obtained via `World.Watch<TComponent, TValue, THandler>()`.

```csharp
public sealed class ChangeWatch<TComponent, TValue, THandler>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where THandler : struct, IChangeHandler<TComponent, TValue>
{
    public ref THandler Handler { get; }
    public void Snapshot(World world);
    public void Diff(World world);
}
```

Same Snapshot/Diff lifecycle as the non-projected variant, but comparison is done on the projected `TValue` rather than the raw `TComponent`.

---

## TransitionWatch\<THandler\>

[`MiniArch`] A pull-based watch that tracks structural transitions (entities entering/exiting a query filter). Obtained via `World.Watch<THandler>(QueryDescription)`.

```csharp
public sealed class TransitionWatch<THandler>
    where THandler : struct, ITransitionHandler
{
    public ref THandler Handler { get; }
    public void Snapshot(World world);
    public void Diff(World world);
}
```

| Member | Description |
|---|---|
| `Handler` | Gets/sets the handler struct. |
| `Snapshot(World)` | Records which entities currently match the filter. |
| `Diff(World)` | Compares current entities against snapshot; dispatches `ITransitionHandler.OnChange` for each entity that entered or exited. |

Membership uses the full `(Entity.Id, Entity.Version)` identity. Reusing an id reports an exit for the old generation and an entry for the new one.

---

## IChangeHandler\<TComponent\>

[`MiniArch`] Handler interface for `ChangeWatch<TComponent, THandler>`.

```csharp
public interface IChangeHandler<TComponent>
    where TComponent : unmanaged, IEquatable<TComponent>
{
    void OnChange(World world, Entity entity, in TComponent oldValue, in TComponent newValue);
}
```

---

## IChangeHandler\<TComponent, TValue\>

[`MiniArch`] Handler interface for projected `ChangeWatch<TComponent, TValue, THandler>`.

```csharp
public interface IChangeHandler<TComponent, TValue>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
{
    TValue Project(in TComponent component);
    void OnChange(World world, Entity entity, TValue oldValue, TValue newValue);
}
```

- `Project` — transforms a component into the tracked value (e.g. extract a single field)
- `OnChange` — called during `Diff` for each entity whose projected value changed

---

## ITransitionHandler

[`MiniArch`] Handler interface for `TransitionWatch<THandler>`.

```csharp
public interface ITransitionHandler
{
    void OnChange(World world, Entity entity, TransitionKind kind);
}
```

Where `TransitionKind` is:

```csharp
public enum TransitionKind { Entered, Exited }
```

---

## Snapshot/Diff Pull-Event Semantics

All Watch types follow the same pull-event model:

1. **Snapshot** records a baseline of the current world state for the tracked component or query filter.
2. **Diff** rescans the world, compares against the baseline, and dispatches callbacks for changes.

Key properties:

| Property | Description |
|---|---|
| **No per-write cost** | Watch types do not intercept `Set`/`Add`/`Remove`. Zero cost when not used. |
| **Two-phase callback safety** | `Diff` collects all changes into a buffer before dispatching any callbacks. A handler that mutates the world during `OnChange` (e.g. spawning a health bar entity) does not corrupt the diff iteration. |
| **Stale slot semantics** | An entity slot that was never populated at `Snapshot` time reports `default` as the old value. Entities removed/destroyed after `Snapshot` are not reported (the current scan cannot find them). |
| **No automatic baseline advance** | Multiple `Diff` calls against the same `Snapshot` repeat the same callbacks. Call `Snapshot` again to advance the baseline. |
| **Handler mutation via `ref`** | Access `ref watch.Handler` to mutate the handler struct between `Snapshot`/`Diff` calls (e.g. toggle an accumulation flag). |
| **Allocation** | Internal arrays grow as needed; after warm-up, steady-state `Snapshot`+`Diff` cycles allocate zero heap memory (no per-call GC allocations). |
| **Dense epoch membership** | `TransitionWatch` uses dense epoch marks (`int[]` indexed by entity.Id) for O(1) membership checks, avoiding per-Diff clearing. |
| **Filter flexibility** | `ChangeWatch` accepts an optional `QueryDescription` for multi-component filtering. `TransitionWatch` requires a non-empty `QueryDescription` filter (throws `ArgumentException` for empty). |

---

## CommandStream

[`MiniArch.Core`] Single-threaded deferred mutation recorder. Component mutations are
appended to typed stores and consumed as one deterministic batch.

| Member | Description |
|---|---|
| `Create()` | Record entity creation |
| `Track(Entity)` | Returns `EntitySlot` that auto-resolves placeholder→real on Submit/Replay |
| `Add<T>(Entity, T)` | Record component addition; existing-entity liveness is decided when the stream is consumed |
| `Set<T>(Entity, T)` | Record component value change; existing-entity liveness is decided when the stream is consumed |
| `Remove<T>(Entity)` | Record component removal; existing-entity liveness is decided when the stream is consumed |
| `Destroy(Entity)` | Record entity destruction |
| `Clone(Entity)` | Record a single-entity component copy |
| `Validate()` | Check placeholder/layout and component-presence contracts without mutating the `World` |
| `Submit()` | Apply recorded changes synchronously; apply-time contract failures may leave earlier commands applied |
| `Snapshot()` | Produce a budget-valid `FrameDelta` without applying; semantic validation is opt-in |
| `SnapshotInto(FrameDelta)` | Produce into a reusable target; mandatory guard/budget failure leaves the target unchanged |
| `Replay(FrameDelta, Boolean)` | Apply a delta to produce identical state; `true` resolves tracked `EntitySlot`s |
| `SubmitAndSnapshotAsync()` | Submit synchronously and build a real-id delta in a worker; mandatory guards/budget run before handoff |
| `SubmitAndSnapshotIntoAsync(FrameDelta)` | Reusable-target variant; after worker handoff, target content is undefined on failure |
| `Clear()` | Discard recorded commands without applying |
| `DeferredEntities` | Enable placeholder entity IDs for lockstep mode |

**Rollback contract.** `CaptureState()` rejects outstanding real-id reservations, not every
recorded frame. A successful `RestoreState()` invalidates any still-recorded frame from the old
timeline; consumption and further reservation/Clone/pending cancellation reject it until
`Clear()` discards it. The same stream can then record a new frame. See [Rollback contract](#contract-commandstream-frames-across-capturestate-and-restorestate).

`Submit`, `Snapshot`, and the async consume APIs do **not** implicitly run
component-presence validation. Call `Validate()` explicitly before
consuming when fail-before-mutation semantics are required for that contract.
`Validate()` does not cover CreateMany group consistency, pending-slot invariants, or
FrameDelta budgets; those remain consume-time checks. Modified CreateMany groups and duplicate
writer component types are checked by every consume path before materialization, component
application, deferred-id resolution or reusable-target clearing; fully cancelled groups need
no live archetype. Failed Submit/async calls still discard the frame and can release real-id
reservations made during recording; this is not a promise of allocator rollback. The resulting typed apply and wire emit
share the same prepared Create → Component Ops → Destroy ordering. Mandatory
explicit/embedded Entity-reference, layout, and frame-budget guards still run before
deferred-id resolution or async worker handoff. These checks do not make Submit transactional:
without explicit presence validation, later apply-time failures may leave earlier commands
applied, and an async reusable target is undefined once its worker has started.

### Embedded Entity component contract

Unmanaged components may contain `Entity` references at arbitrary depth through sequential or explicit structs, record private backing fields, generic wrappers, and `InlineArray` elements. Nested structs are not intrinsically invalid. Submit, Snapshot, `FrameDelta.Validate()`, Replay, and Clone use the same complete cached offset set; source and replay must therefore share the same offset discovery contract or they can diverge.

Entity-bearing `LayoutKind.Auto` graphs remain unsupported and fail fast. Exact overlapping explicit Entity ranges are deduplicated; partially overlapping Entity ranges are rejected rather than resolved ambiguously.

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

Each tracked placeholder owns a slot registration. `EntitySlot` is an external handle;
it cannot be stored as an ECS component.

---

## FrameDelta

[`MiniArch.Core`] Self-contained delta of world mutations.

| Member | Description |
|---|---|
| `MaxFrameBytes` | Maximum produced or accepted wire size (16 MiB) |
| `MaxOpsPerFrame` | Maximum produced or accepted operation count (1,000,000) |
| `DeltaCount` | Total number of delta entries |
| `HasEntity(Entity)` | Check if an entity is referenced |
| `IsEmpty` | Whether the delta has no entries |
| `AsSpan()` | Expose the packed wire bytes without copying |
| `Deserialize(ReadOnlySpan<byte>)` | Validate and copy wire bytes into a reusable delta |
| `FromWire(ReadOnlySpan<byte>)` | Allocate a delta from wire bytes |
| `Validate()` | Validate an untrusted delta's internal state machine and payloads |

---

## Rollback (CaptureState / RestoreState)

[`MiniArch`] In-place world rollback with zero-alloc steady-state.

```csharp
var snapshot = world.CaptureState();  // save current state
// ... mutate world ...
world.RestoreState(snapshot);         // revert, payload returned to pool
```

- **Zero-allocation** after warm-up (generation-stamped value leases over pooled payloads)
- **Create caches survive a restore** — archetypes created after the capture are still in the
  world, so re-creating the same component combinations after `RestoreState` stays
  allocation-free in steady state. The internal whole-world reset path still invalidates
  the cache, because it drops the archetypes themselves.
- **`CanonicalChecksum()` after `RestoreState()` matches a world that was never rolled back**,
  because the canonical payload ignores the empty archetypes the restore leaves behind.
- **Multiple snapshots** may be live simultaneously (GGPO-style multi-frame rollback window)
- **`WorldStateSnapshot.Dispose()`** — release an unconsumed checkpoint without restoring it
- **`WorldStateSnapshot.IsRecycled`** — check whether a lease is no longer valid
- `RestoreState` throws `InvalidOperationException` if the lease was already consumed or belongs to another world

### Contract: CommandStream frames across CaptureState and RestoreState

`CaptureState()` rejects a World with outstanding real-id reservations (`InvalidOperationException`)
**before** taking a snapshot lease. It does not require every stream to be empty: a pure
`Set` frame, deferred-only draft, or fully cancelled draft may be captured, but stream buffers
are not part of the checkpoint.

A successful `RestoreState()` advances the World's command timeline. A stream with an active
frame recorded on the old timeline rejects `Validate()`, `Submit()`, `Snapshot()`,
`SnapshotInto()`, both async consume entries, further reservation/Clone, and cancellation
of a pending entity with `InvalidOperationException`, even if the original entity is still alive.
Ordinary `Add`/`Set`/`Remove` on existing handles may append to the old frame, but cannot make
it consumable. An empty stream can continue to be used. `Clear()` safely discards a stale frame
without releasing a reservation that the restored World may have reassigned to another stream;
the **same** stream can then
record and consume a new frame. Do not submit or keep recording the old frame.

```csharp
var checkpoint = world.CaptureState();
stream.Set(entity, new Position(1, 1)); // no real-id reservation
world.RestoreState(checkpoint);
// stream.Submit();                      // throws: old recorded frame
stream.Clear();                          // discard it safely
stream.Set(entity, new Position(2, 2)); // new timeline, if entity is alive
stream.Submit();
```

`SubmitAndSnapshotAsync` applies to the World synchronously before returning its task; its
wire-encoding worker may finish **after** a restore and still produce the earlier frame's
historical delta. Await each returned task before using its delta; completion of one task does
not imply that all earlier workers have finished. Explicit `Replay(delta)` of a saved historical
delta remains permitted; the caller must ensure that the target World's history and allocator
are compatible. `RestoreState()` does not make previously held `Entity` handles, refs, spans,
or `EntitySlot.Value` safe to reuse across the rollback. A `Snapshot()` → `Clear()` → own
`Replay(delta, resolveSlots: true)` flow can still resolve its registered slots after restore.

---

## WorldSnapshot

[`MiniArch.Core`] Binary world serialization.

| Method | Description |
|---|---|
| `Save(Stream, World)` | Write a canonical version-5 snapshot followed by CRC32 |
| `Load(Stream)` | Strictly validate a v5 snapshot and return a new `World` |
| `ComputeChecksum(World)` | Compute the legacy physical-row rollback projection hash, preserving entity-component association |
| `ComputeCanonicalChecksum(World)` | Compute SHA-256 over the exact v5 payload, excluding its CRC trailer; the payload covers non-empty archetypes only |

Version 5 encodes unmanaged component **fields**, not raw CLR struct memory. Primitive
values use fixed-width little-endian encoding; padding, offsets, alignment, declaration
order, and runtime component registration ids are not part of the wire format. Nested
unmanaged structs, fixed buffers, and inline arrays are supported. Pointer/native-sized,
recursive, AutoLayout, managed, and overlapping shapes used by non-empty archetypes are
rejected without a raw fallback. Types found only in empty archetypes are neither serialized
nor schema-validated.

`Load` accepts only v5; v3/v4 files must be regenerated or migrated by the application.
CRC32 detects corruption but is not a schema-migration mechanism. Exact type identity and
canonical field shape must match the local component type.

The canonical payload describes **non-empty archetypes only**: an archetype left empty by
`Destroy` or by `RestoreState` contributes neither an archetype entry nor a schema entry, so
two worlds that differ only by such leftovers produce the same `CanonicalChecksum()` and the
same `Save` bytes. Chunk capacity, every slot version, a hierarchy-count field fixed at zero,
and the ordered free list are still part of the payload. Older v5 files with a zero hierarchy
count remain readable; files with nonzero hierarchy counts are rejected rather than silently
losing relations. Older FrameDelta `0x04`/`0x05` relationship operations are likewise rejected
without changing the other opcode numbers or layouts. For migration, use an older
hierarchy-capable version to export relations first ([details](../.knowledge/kb-hierarchy-runtime.md)).
Snapshots written by older v5 builds that *do* contain
empty archetypes still load; re-saving such a world normalizes those entries away, so the
original bytes are not preserved. `Checksum()` is unchanged — it already ignored empty
archetypes.

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
| `Export()` | Export the registered component schema as a portable type-name blob |
| `Import(byte[])` | Resolve and register an exported schema, returning types in schema order |

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
- `Destroy(Entity)` removes only that entity; bulk `Destroy` and `Clear(query)` remove their explicitly selected entities, not entities reached through component `Entity` references
- `UnsafeGetComponentSpanAt<T>` trusts a same-archetype cached column index in Release and must be locally justified by profiling
- `CommandStream.Add<T>` is strict at consume time: adding a component already present on a surviving entity throws when that operation is applied; earlier commands in the same submit may already have mutated the World (call `Validate()` first for component-presence preflight)
- `Set<T>()` throws if the entity does not have the component; use `Add<T>()` to add a new component
- `WorldSnapshot` only supports unmanaged component types
- `World.RestoreState(snapshot)` throws if `snapshot.IsRecycled` is `true`
- `CaptureState()` requires no outstanding real-id reservations; after `RestoreState()`, discard an old recorded frame with `CommandStream.Clear()` before reusing that stream — see [Rollback contract](#contract-commandstream-frames-across-capturestate-and-restorestate)

---

## Concurrency

| API | Semantics | Notes |
|---|---|---|
| `Query` / ordered queries | MT-Read | World must not mutate concurrently |
| `CommandStream` recording | Single-threaded | No concurrent callers |
| `CommandStream.Submit()` | Exclusive | Single-threaded |
| `SubmitAndSnapshotAsync()` | Pipelined | Submit is synchronous; delta construction completes in the returned task |
| `World` mutation API | Not concurrent | No concurrent writes |
