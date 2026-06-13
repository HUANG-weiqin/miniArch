# MiniArch

A minimal, high-performance archetype ECS runtime for C#, with built-in support for frame-synchronized multiplayer.

## Features

- **Archetype ECS** — Simple `World` / `Entity` / `QueryDescription` API with chunk-level iteration
- **CommandBuffer & CommandStream** — Deferred command recording with per-entity deduplication; CommandStream uses flat byte-stream encoding and is 20–48% faster
- **FrameDelta + Replay** — Record a frame's changes as a self-contained `FrameDelta`, then replay it on any other `World` to produce identical state. Deterministic entity IDs ensure consistency across machines.
- **Pipelined Snapshot** — `SubmitAndSnapshotAsync()` runs submit and delta-building in parallel
- **Clone & Snapshot** — Deep-copy any world with `Clone()` for rollback checkpoints; serialize/deserialize with `WorldSnapshot.Save/Load`
- **Query filtering** — `With<T>`, `Without<T>`, `WithAny<T>`, `Or<T>` composition
- **Entity hierarchy** — `Link` / `Unlink` with cascade destroy
- **GC-friendly** — Zero GC collections during steady-state simulation in all game scenarios

## Quick Start

```shell
dotnet new console
dotnet add package MiniArch
```

```csharp
using MiniArch;

// Define components (plain structs)
readonly record struct Position(int X, int Y);
readonly record struct Velocity(int X, int Y);

// Create a world
var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

// Query and iterate
var desc = new QueryDescription()
    .With<Position>()
    .With<Velocity>();

foreach (var entity in world.Query(in desc))
{
    if (world.TryGet(entity, out Position pos) &&
        world.TryGet(entity, out Velocity vel))
    {
        world.Set(entity, new Position(pos.X + vel.X, pos.Y + vel.Y));
    }
}
```

## Frame-Synchronized Multiplayer

MiniArch is the only C# ECS with built-in lockstep support:

```csharp
// Server: record frame delta
var buffer = new CommandBuffer(world);
// ... record all changes ...
var delta = buffer.Snapshot();   // self-contained delta
buffer.Submit();                  // apply locally

// Send delta over network...

// Client: replay to get identical state
replicaWorld.Replay(delta);       // deterministic ID validation
```

```csharp
// Rollback support
var checkpoint = world.Clone();  // deep copy
// ... predict frames ...
// Correction received: revert and re-apply
world = checkpoint.Clone();
```

## Performance

MiniArch consistently outperforms or matches other C# ECS libraries across a wide range of game scenarios. See [docs/comparison.md](docs/comparison.md) for detailed benchmarks against Arch, Friflo, and DefaultEcs.

| Scenario | MiniArch (Stream) | Friflo | Arch |
|---|---|---|---|
| HeroLight (1K chars) | 299,946 ticks/s | 281,817 | — |
| SteadyCombat (20K actors) | 3,129 ticks/s | 2,762 | — |
| BulletHell (100K entities) | 14,416 ops/s | 14,058 | 13,057 |
| MixedLoad (create+query+destroy) | 29,309 ops/s | 24,253 | 23,896 |

## API Layers

- **`MiniArch`** — Minimal user entry: `World`, `Entity`, `QueryDescription`
- **`MiniArch.Core`** — Advanced types: `Query`, `Chunk`, `CommandBuffer`, `CommandStream`, `FrameDelta`, `WorldSnapshot`

See [docs/README.md](docs/README.md) for full API documentation.

## Project Structure

```
src/MiniArch/       # Library source
tests/              # Tests, benchmarks, shared infrastructure
tools/perf/         # Performance regression & comparison tests
docs/               # Documentation
```

## License

[MIT](LICENSE)
