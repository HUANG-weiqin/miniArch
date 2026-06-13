# MiniArch

The only C# ECS with **built-in frame-synchronized multiplayer** — and **7/12 game scenarios faster than Arch and Friflo.**

📊 [See full benchmarks vs Arch, Friflo, and DefaultEcs →](docs/comparison.md)

---

## Benchmarked Against

MiniArch was tested against **industry-leading C# ECS libraries**:

- **Arch** — 2.5k+ GitHub stars, used by Space Station 14 (thousands of concurrent players), benchmark-topping performance. Widely considered the C# ECS performance baseline.
- **Friflo** — Top performer in ECS.CSharp.Benchmark (2.55x faster than Arch on average), used in Steam-shipping titles (Vanguard Tides, Horse Runner DX). Fully managed C#, no unsafe code.
- **DefaultEcs** — 1.2k+ stars, mature and stable, reference implementation for many C# ECS projects.

These are **not toy projects**. They are production-grade, battle-tested libraries running in shipped games. MiniArch outperforms them in 7/12 scenarios while adding capabilities neither provides.

## Why MiniArch?

| Other ECS libraries | MiniArch |
|---|---|
| ❌ No frame sync support | ✅ **FrameDelta + Replay** — record changes as a self-contained delta, replay on any machine to produce identical state |
| ❌ No rollback support | ✅ **World.Clone()** — deep copy for rollback checkpoints |
| ❌ No CommandStream | ✅ **CommandStream** — 20–48% faster than traditional CommandBuffer |
| ❌ No binary serialization | ✅ **WorldSnapshot** — full state save/load for replays and netcode |
| ❌ No delta merging | ✅ **FrameDelta.Merge()** — squash multiple frames into one for network optimization |

## Quick Start

```shell
dotnet new console
dotnet add package MiniArch
```

```csharp
using MiniArch;

readonly record struct Position(int X, int Y);
readonly record struct Velocity(int X, int Y);

var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

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

## Frame-Synchronized Multiplayer — Built-in

Other ECS libraries have `CommandBuffer`, but frame sync needs more than that:

| Gap | Arch | Friflo | MiniArch |
|---|---|---|---|
| `Create()` ID 分配时机 | **回放时**（负 ID 占位符） | 调用时（预分配） | 调用时（预分配） |
| **`Snapshot()` → 可序列化 delta** | ❌ | ❌ | ✅ |
| **`Replay(FrameDelta)`** | ❌ | ❌ | ✅ |
| **Replay 时 ID 一致性校验** | ❌ | ❌ | ✅ |
| **多帧合并 `Merge()`** | ❌ | ❌ | ✅ |
| **`World.Clone()` 回滚点** | ❌ | ❌ | ✅ |
| **跨 World 重放测试** | ❌ | ❌ | ✅ 1000帧模糊测试 |

```csharp
// Record a frame's changes as a self-contained delta
var buffer = new CommandBuffer(world);
// ... record all mutations ...
var delta = buffer.Snapshot();    // produce delta without applying
buffer.Submit();                   // apply locally

// Send delta over network...

// Any client replays to produce identical state
replicaWorld.Replay(delta);       // ensure replay reservation + ID validation

// Rollback: save checkpoint, predict, revert on correction
var checkpoint = world.Clone();   // deep copy with hierarchy
// ... predict frames ...
world = checkpoint.Clone();       // revert and re-apply
```

## Performance at a Glance

| Scenario | MiniArch (Stream) | Friflo | Arch |
|---|---|---|---|
| HeroLight (1K chars) | **299,946** ticks/s | 281,817 | — |
| SteadyCombat (20K actors) | **3,129** ticks/s | 2,762 | — |
| BulletHell (100K entities) | **14,416** ops/s | 14,058 | 13,057 |
| MixedLoad (create+query+destroy) | **29,309** ops/s | 24,253 | 23,896 |
| RandomEntityAccess | **14,825** ops/s | 10,043 | 11,936 |
| BuffSystem (add/remove stress) | **7,594** ops/s | 6,425 | 4,771 |

All benchmarks run on identical workloads with the same measurement methodology. GC collections: **0/0/0** during measurement across all game scenarios.

## Features

- **Archetype ECS** — `World` / `Entity` / `QueryDescription` with chunk-level iteration
- **CommandBuffer & CommandStream** — deferred command recording; CommandStream is 20–48% faster
- **FrameDelta + Replay** — record and replay frame deltas across worlds with deterministic ID validation
- **World.Clone()** — deep copy for rollback
- **WorldSnapshot** — binary serialize/deserialize entire world state
- **SubmitAndSnapshotAsync()** — pipelined submit + delta building
- **Query filtering** — `With<T>`, `Without<T>`, `WithAny<T>`
- **Entity hierarchy** — `Link` / `Unlink` with cascade destroy
- **GC-friendly** — zero GC collections in steady-state simulation

## When to Use MiniArch

| 你的场景 | 推荐 |
|---|---|
| 要做**帧同步联机游戏**（Lockstep） | ✅ **MiniArch 原生支持** — 游戏逻辑天然可同步，不需要额外适配 |
| 要做**状态同步 + 回滚** | ✅ **World.Clone() + Replay** 开箱即用 |
| 追求极致 ECS 性能 | ✅ 7/12 场景领先，CommandStream 比 Friflo 快 13%~48% |
| 需要**零 GC** 稳定运行 | ✅ 所有游戏场景 GC Collections = 0/0/0 |
| 单机游戏、随便玩玩 | ✅ 也行，API 简洁不折腾 |
| 需要纯创建/销毁密集型 | ⚠️ 这个场景 DefaultEcs 更快（无 CB 开销） |

## Quality

- **Core:** ~7,750 lines
- **Tests:** ~15,400 lines（396 tests，测试:代码 ≈ 2:1）
- **GC:** 0/0/0 across all game scenarios
- **Fuzz:** 1,000-frame cross-world replay verified

## Learn More

| Resource | Link |
|---|---|
| API Guide | [docs/README.md](docs/README.md) |
| Benchmarks vs Other ECS | [docs/comparison.md](docs/comparison.md) |

## License

[MIT](LICENSE)

---

*Built with GPT 5.5 · DeepSeek V4 · GLM5 · Kimi 2.6 · Mimo · Hermes Agent*
