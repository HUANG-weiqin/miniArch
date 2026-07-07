# MiniArch

The C# ECS with **built-in frame-synchronized multiplayer** — and a **Set-dominant advantage over Friflo (+27–65%)** in real game workloads.

> **Constraint:** Components must be `unmanaged` value types (no `string`, no reference-type fields). This is a deliberate trade-off for zero-GC, cache-line-friendly flat storage.

---

## Quick Start

```shell
dotnet new console
dotnet add package MiniArch
```

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var entity = world.Create(new Position(0, 0), new Velocity(1, 2));

// Chunk iteration — the fast path
var query = world.Query(new QueryDescription().With<Position>());
foreach (var chunk in query.GetChunks())
{
    var positions = chunk.GetSpan<Position>();
    for (int i = 0; i < positions.Length; i++)
        positions[i] = new Position(positions[i].X + 1, positions[i].Y + 1);
}

// CommandStream — deferred mutation
var stream = new CommandStream(world);
stream.Set(entity, new Position(10, 20));
stream.Submit();

Console.WriteLine(world.Get<Position>(entity)); // Position(10, 20)

// Type declarations (must appear after top-level statements)
readonly record struct Position(float X, float Y);
readonly record struct Velocity(float X, float Y);
```

---

## Features

- **Archetype ECS** — `World` / `Entity` / `QueryDescription` with chunk-level iteration
- **Change Tracking** — `World.Track<T>()` cursors for reactive systems: modified chunks, membership transitions, old-value capture
- **CommandStream** — deferred mutation recording; single-pass Set (12–48% faster than traditional command buffers)
- **FrameDelta + Replay** — record self-contained deltas, replay on any world with deterministic ID validation
- **CaptureState/RestoreState** — zero-alloc in-place rollback (GGPO-style at 60 fps)
- **WorldSnapshot** — binary world serialize/deserialize for replays and netcode
- **Entity hierarchy** — `AddChild` / `RemoveChild` with cascade destroy
- **Parallel iteration** — `ForEachChunkParallel` with struct-generic `IChunkForEach` (zero-alloc, JIT-devirtualised)
- **World diagnostics** — `WorldDiff.Compare()` for lockstep divergence pinpointing, `WorldValidator.Validate()` for structural integrity, `EntityDump.Describe()` for per-entity state, `WorldDigest.Compute()` for per-domain checksum narrowing (`MiniArch.Diagnostics` namespace)

  ```csharp
  using MiniArch.Diagnostics;

  var diff = WorldDiff.Compare(worldA, worldB);               // two-world comparison
  var report = EntityDump.Describe(world, entity);            // inspect one entity
  var ok = WorldValidator.Validate(world).IsValid;            // check structural integrity
  var digest = WorldDigest.Compute(world);                    // per-domain checksums
  ```
- **Zero GC** — no collections in steady-state simulation across all game scenarios

[Full example catalogue →](docs/examples.md)

---

## Performance

In Set-heavy game scenarios (position, health, velocity updates), MiniArch CommandStream outperforms Friflo's `Compile()` by **+27–65%** because MiniArch scans once per Set operation while Friflo scans twice. [Full benchmark comparison →](docs/comparison.md)

---

## When to Use MiniArch

| Your scenario | Recommendation |
|---|---|
| Lockstep multiplayer game | ✅ Native FrameDelta + Replay |
| State sync + rollback | ✅ CaptureState/RestoreState + World.Clone |
| Per-frame Set-heavy (position, health, velocity) | ✅ CommandStream advantage vs Friflo |
| Zero-GC steady-state | ✅ 0/0/0 collections |
| Archetype-switch-heavy workloads | ⚠️ Friflo slightly ahead (S8 scenario) |
| Debugging state divergence | ✅ WorldDiff, WorldDigest, WorldValidator |

---

## Documentation

| Resource | Link |
|---|---|
| API Reference | [docs/api.md](docs/api.md) |
| Examples & Patterns | [docs/examples.md](docs/examples.md) |
| World Diagnostics | [`.knowledge/kb-ecs-diagnostics.md`](.knowledge/kb-ecs-diagnostics.md) |
| Benchmarks vs Other ECS | [docs/comparison.md](docs/comparison.md) |
| Full Multiplayer Demo | [samples/BulletLockstep.Demo/](samples/BulletLockstep.Demo/) |

---

## License

[MIT](LICENSE)
