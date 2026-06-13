# MiniArch API Guide

MiniArch exposes two API layers:

- **`MiniArch`** — Default user entry: `World`, `Entity`, `QueryDescription`
- **`MiniArch.Core`** — Advanced types: `CommandBuffer`, `CommandStream`, `FrameDelta`, `WorldSnapshot`, `EntityInfo`, `EntityAccessor`, `ICommandRecorder`

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

### Query

Entity iteration via `QueryDescription`:
- `GetEnumerator()` — `foreach` support
- `OrderBy(IComparer<Entity>)` / `OrderBy(Comparison<Entity>)` — sorted iteration
- `GetChunks()` — returns `ReadOnlySpan<ChunkView>` for batch/chunk-level access

### QueryDescription

- `With<T>()` / `Without<T>()` / `WithAny<T>()` — include, exclude, or-match filters

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
| `CommandBuffer` | Deferred command recording with per-entity deduplication |
| `CommandStream` | Byte-stream command recording (20–48% faster than CommandBuffer) |
| `FrameDelta` | Self-contained delta for cross-world replay |
| `WorldSnapshot` | Binary world serialization |
| `ICommandRecorder` | Shared interface: `Create`, `Add<T>`, `Set<T>`, `Remove<T>`, `Destroy`, `Link`, `Submit` |
| `EntityAccessor` | Ref struct for direct component access, obtained via `World.Access(entity)` |
| `EntityInfo` | Entity metadata (archetype, row index) |

## Common Types

### `World`

| Method | Description |
|---|---|
| `Create<T1..T16>(...)` | Create entity with 1–16 components |
| `CreateMany(Span<Entity>)` | Batch create empty entities |
| `Destroy(Entity)` | Remove entity (cascades through hierarchy) |
| `Add<T>(Entity, T)` | Add component |
| `Set<T>(Entity, T)` | Set component (adds if missing) |
| `Remove<T>(Entity)` | Remove component |
| `Get<T>(Entity)` | Get component by value |
| `GetRef<T>(Entity)` | Get component by ref (for in-place mutation) |
| `TryGet<T>(Entity, out T)` | Try-get component |
| `Has<T>(Entity)` | Check component existence |
| `Access(Entity)` | Returns `EntityAccessor` for batched access |
| `Link(Entity, Entity)` | Parent-child hierarchy |
| `Unlink(Entity)` | Remove from hierarchy |
| `TryGetParent(Entity, out Entity)` | Get parent |
| `GetChildren(Entity)` | Get children list |
| `IsAlive(Entity)` | Validate entity handle |
| `Query(in QueryDescription)` | Create a query |
| `Clone(Entity)` | Deep-copy an entity |
| `Clone()` | Deep-copy the entire world |
| `Replay(FrameDelta)` | Apply delta to produce identical state |
| `Dispose()` | Release resources |

### `CommandBuffer`

- `Create` / `Add<T>` / `Set<T>` / `Remove<T>` — record commands
- `Destroy` / `Link` / `Unlink` — structural changes
- `Submit()` — apply to world synchronously
- `Snapshot()` — produce a `FrameDelta` without applying
- `SubmitAndSnapshotAsync()` — pipelined: main thread submits while background builds delta

### `CommandStream`

Same API as `CommandBuffer` but records to a flat byte stream instead of per-entity accumulation. 20–48% faster in game scenarios.

### `FrameDelta`

- `DeltaCount` — total number of delta entries
- `HasEntity(Entity)` — check if an entity is referenced
- `Merge(FrameDelta, FrameDelta)` — combine two deltas into one
- `IsEmpty` — whether the delta has no entries

### `WorldSnapshot`

- `Save(Stream, World)` — serialize world to binary stream
- `Load(Stream)` → deserialize and return a new `World`

### `EntityAccessor`

Obtained via `World.Access(entity)`. Allows multiple component operations without repeated entity lookups:

```csharp
var accessor = world.Access(entity);
ref var pos = ref accessor.Get<Position>();
ref var vel = ref accessor.Get<Velocity>();
accessor.Set(new Health(100));
bool hasArmor = accessor.Has<Armor>();
```

## Constraints

- `default(Entity)` is invalid; real entities start at `Version = 1`
- `World.IsAlive(entity)` is the authoritative validity check
- `Destroy()` cascades through the hierarchy subtree
- `Set<T>()` on a missing component adds it (implicit migration)
- `CommandBuffer` supports concurrent recording; `Submit()` is single-threaded
- `WorldSnapshot` only supports unmanaged component types

## Concurrency

| API | Semantics | Notes |
|---|---|---|
| `Query` / `OrderedQuery` | MT-Read | World must not mutate concurrently |
| `CommandBuffer` recording | MT-Record | Each thread records to its own buffer |
| `CommandBuffer.Submit()` | Exclusive | Single-threaded |
| `SubmitAndSnapshotAsync()` | Pipelined | Main thread: Submit; background: BuildDelta |
| `World` mutation API | Not concurrent | No concurrent writes |
