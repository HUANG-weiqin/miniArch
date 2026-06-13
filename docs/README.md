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

Queries use `QueryDescription` exclusively — no generic `Query<T1, T2>` syntax.

## Advanced Layer: `MiniArch.Core`

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

var description = new QueryDescription().With<Position>().With<Velocity>();
var query = world.Query(in description);

// Chunk-level iteration with direct span access
foreach (var chunk in query.Advanced.GetChunks())
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
| `FrameDelta` | Self-contained delta — `DeltaCount`, `HasEntity`, `Merge`, `IsEmpty` |
| `WorldSnapshot` | Binary serialization: `Save(Stream, World)`, `Load(Stream)` → World |
| `ICommandRecorder` | Shared interface for `CommandBuffer` / `CommandStream` |
| `EntityInfo` | Entity metadata (archetype, row index) |
| `EntityAccessor` | Ref struct for direct component access by pointer |

### CommandBuffer

- `Create` / `Add<T>` / `Set<T>` / `Remove<T>` — record commands
- `Destroy` / `Link` / `Unlink` — structural changes
- `Submit()` — apply to world synchronously
- `Snapshot()` — produce a `FrameDelta` without applying
- `SubmitAndSnapshotAsync()` — pipelined: main thread submits while background builds delta

### FrameDelta

- `DeltaCount` — total number of delta entries
- `HasEntity(Entity)` — check if an entity is referenced
- `Merge(a, b)` — combine two deltas into one
- `IsEmpty` — whether the delta has no entries

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
