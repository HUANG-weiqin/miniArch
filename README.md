# MiniArch

The only C# ECS with **built-in frame-synchronized multiplayer** — and a **Set-dominant advantage over Friflo (+27–65%)** in real game workloads.

📊 [See full benchmarks vs Arch, Friflo, and DefaultEcs →](docs/comparison.md)

> **Constraint:** Components must be `unmanaged` value types (no `string`, no reference-type fields). This trades generality for zero-GC, cache-line-friendly flat storage. If you need managed components, use Arch or Friflo.

---

## Benchmarked Against

MiniArch was tested against **industry-leading C# ECS libraries**:

- **Friflo** — Top performer in ECS.CSharp.Benchmark (2.55x faster than Arch on average), used in Steam-shipping titles (Vanguard Tides, Horse Runner DX). Fully managed C#, no unsafe code.
- **Arch** — 2.5k+ GitHub stars, used by Space Station 14 (thousands of concurrent players), benchmark-topping performance. Widely considered the C# ECS performance baseline.
- **DefaultEcs** — 1.2k+ stars, mature and stable, reference implementation for many C# ECS projects.

These are **not toy projects**. They are production-grade, battle-tested libraries running in shipped games. MiniArch outperforms them where it matters for real games while adding capabilities neither provides.

## Why MiniArch?

### MiniArch wins because Friflo scans twice for every op — even pure value updates

Friflo's `Playback()` does two passes over every operation: `PrepareComponentCommands` (build bitmasks) → `ExecuteComponentCommands` (write values). Every `Set` — even when it causes zero structural change — gets scanned twice.

MiniArch CommandStream does **one pass** for Set on existing entities. The difference is visible in any game with significant per-frame value updates (position, health, velocity, timers):

| Scale | Set ratio | MiniArch CS | Friflo | |
|---|---|---|---|---|
| 500 entities | 94% Set | **87,540 t/s** | 53,194 | **+65%** |
| 10K entities | 94% Set | **3,489 t/s** | 2,738 | **+27%** |

This advantage holds across all of MiniArch's benchmarked game scenarios:

| Scenario | MiniArch CS | Friflo | |
|---|---|---|---|
| Game (20K actors, spawn/destroy + mutations) | **3,486 t/s** | 3,254 | +7% |
| ParticleStorm (4K creates/tick) | **1,502 t/s** | 1,517 | -1% (tied) |
| HeroLight (1K chars, light mutations) | **343,918 t/s** | 342,476 | +0.4% |

### The extra capabilities Friflo and Arch lack

| Other ECS libraries | MiniArch |
|---|---|
| ❌ No frame sync support | ✅ **FrameDelta + Replay** — record changes as a self-contained delta, replay on any machine to produce identical state |
| ❌ No rollback support | ✅ **World.Clone()** — deep copy for rollback checkpoints |
| ❌ No CommandStream | ✅ **CommandStream** — 12–48% faster than traditional command buffers |
| ❌ No binary serialization | ✅ **WorldSnapshot** — full state save/load for replays and netcode |
| ❌ No delta merging | ✅ **FrameDelta.Merge()** — squash multiple frames into one for network optimization |

---

## Quick Start

```shell
dotnet new console
dotnet add package MiniArch
```

```csharp
using MiniArch;

// Components must be unmanaged value types (struct without reference-type fields)
readonly record struct Position(int X, int Y);
readonly record struct Velocity(int X, int Y);

var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

var desc = new QueryDescription()
    .With<Position>()
    .With<Velocity>();

foreach (var entity in world.Query(in desc))
{
    ref var pos = ref world.GetRef<Position>(entity);
    ref var vel = ref world.GetRef<Velocity>(entity);
    pos = new Position(pos.X + vel.X, pos.Y + vel.Y);
}
```

---

## Frame-Synchronized Multiplayer — Built-in

Other ECS libraries have `CommandBuffer`, but frame sync needs more than that:

| Gap | Arch | Friflo | MiniArch |
|---|---|---|---|
| `Create()` ID 分配时机 | 回放时（负 ID 占位符） | 调用时（预分配） | 调用时（预分配） |
| **`Snapshot()` → 可序列化 delta** | ❌ | ❌ | ✅ |
| **`Replay(FrameDelta)`** | ❌ | ❌ | ✅ |
| **Replay 时 ID 一致性校验** | ❌ | ❌ | ✅ |
| **多帧合并 `Merge()`** | ❌ | ❌ | ✅ |
| **`World.Clone()` 回滚点** | ❌ | ❌ | ✅ |
| **跨 World 重放测试** | ❌ | ❌ | ✅ 1000帧模糊测试 |

```csharp
// Record a frame's changes as a self-contained delta
var buffer = new CommandStream(world);
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

---

## Performance at a Glance

### CommandStream (deferred path) — real game workloads

| Scenario | MiniArch CS | Friflo | Notes |
|---|---|---|---|
| Game (20K actors, mixed ops) | **3,486 t/s** | 3,254 | +7%, Set-heavy advantage |
| ParticleStorm (4K creates) | **1,502 t/s** | 1,517 | Tied |
| HeroLight (1K chars) | **343,918 t/s** | 342,476 | +0.4% |
| SetHeavy 500 entities | **87,540 t/s** | 53,194 | +65%, maximum advantage |
| SetHeavy 10K entities | **3,489 t/s** | 2,738 | +27% |

### Immediate API — 12 game scenarios (MiniArch vs Friflo vs Arch)

| Scenario | MiniArch | Friflo | Arch |
|---|---|---|---|
| BulletHell (100K entity query) | **14,793** | 13,899 | 14,134 |
| MMOZone (30K, 8 archetypes) | **53,273** | 50,310 | 49,016 |
| WaveSpawner (spawn/despawn) | **10,077** | 8,650 | — |
| MixedLoad (create+query+destroy) | **29,309** | 24,253 | 23,896 |
| RandomEntityAccess | **14,825** | 10,043 | 11,936 |
| S8 AIStateMachine | 1,327 | **1,395** | 896 |
| S12 FollowTheLeader | 12,700 | **12,854** | 8,392 |

**MiniArch wins 9/12 scenarios** on immediate API. [Full table →](docs/comparison.md)

All benchmarks: **GC 0/0/0** across all game scenarios, zero heap pressure.

---

## Design: Why It's Fast

Most C# ECS libraries (Arch included) split each archetype into **multiple fixed-size chunks** (e.g. 16KB). Entity data for the same component set is scattered across chunks. Iteration means jumping between chunks — cache misses everywhere.

MiniArch does the opposite: **one archetype = one contiguous `byte[]` that grows by doubling.** All entities with the same components sit in a single flat block of memory. Query iteration is pure sequential memory scan — zero cross-chunk jumps, hardware prefetcher runs at full speed.

This single-block design cascades into simpler code everywhere: no chunk list management, no chunk boundary checks, no chunk iteration overhead. The entire storage is just a base pointer + column offsets + element sizes. Row access is one multiply-add. Query iteration is one pointer bump per entity.

Around this core, there's a collection of targeted optimizations: 512-bit archetype matching (8 × ulong, O(1) per mask), 16-byte entity records (4 fit in one cache line), inline small-size maps in CommandBuffer (4 entries zero-alloc inline), slab bump-allocator for component data (bounds check + pointer write), size-specialized memcpy for 1/2/4/8/12/16 byte components, and an 8-slot direct-mapped archetype cache in CommandStream.

**Why CommandStream beats Friflo:** Friflo's `Playback()` scans every operation twice (once to build bitmasks, once to write values). MiniArch CommandStream scans **once** for Set operations on existing entities — because structural changes are the exception, not the norm in real games. The more your game does "update health by -5" vs "add Burning debuff," the more MiniArch pulls ahead.

**Capacity:** Single `byte[]` per archetype supports up to ~67M entities (2 GB .NET array limit, ~32 bytes/component). Doubling growth means 19 reallocations from 128 to 67M — amortized O(1) per create. The bottleneck is physical RAM, not the data structure.

---

## Friflo's Strengths (fair comparison)

| Friflo advantage | Details |
|---|---|
| **Raw Record speed** | Typed `T[]` component arrays — 163 μs vs MiniArch CS 229 μs per 40K ops |
| **BitSet SIMD** | `Vector256<long>` single-instruction equality vs 8-scalar compares |
| **Entity self-resolution** | `entity.Get<T>()` without World reference (16-byte Entity carries Store ref) |
| **Archetype switch benchmark** | S8-AIStateMachine: Friflo 1,395 vs MiniArch 1,327 |
| **Ecosystem** | NuGet package, docs, community |

Friflo's advantages are **engine-level constant factors** (typing + SIMD). MiniArch's advantages are **architectural** (frame sync + single-pass Set). For games — where frame sync matters and Set dominates — MiniArch's design wins.

---

## Features

- **Archetype ECS** — `World` / `Entity` / `QueryDescription` with chunk-level iteration
- **CommandStream** — deferred command recording; 12–48% faster than traditional command buffers
- **FrameDelta + Replay** — record and replay frame deltas across worlds with deterministic ID validation; zero-allocation replay path (mask cache + pre-scan)
- **World.Clone()** — deep copy for rollback
- **WorldSnapshot** — binary serialize/deserialize entire world state
- **SubmitAndSnapshotAsync()** — pipelined submit + delta building
- **Query filtering** — `With<T>`, `Without<T>`, `WithAny<T>`
- **Parallel iteration** — `ForEachChunkParallel` for multi-threaded batch processing (auto fast-path for single-chunk queries)
- **Entity accessor** — `Access()` for cached multi-component read/write on a single entity
- **Ref-return access** — `GetRef<T>()` for zero-copy in-place component mutation
- **Batch creation** — `CreateMany()` for bulk entity spawning
- **Entity hierarchy** — `Link` / `Unlink` with cascade destroy
- **GC-friendly** — zero GC collections in steady-state simulation

---

## When to Use MiniArch

| 你的场景 | 推荐 |
|---|---|
| 要做**帧同步联机游戏**（Lockstep） | ✅ **MiniArch 原生支持** — 游戏逻辑天然可同步 |
| 要做**状态同步 + 回滚** | ✅ **World.Clone() + Replay** 开箱即用 |
| 游戏有大量**每帧 Set 操作**（位置/血量/速度更新） | ✅ **CommandStream +27~65% vs Friflo** |
| 追求**零 GC** 稳定运行 | ✅ 所有游戏场景 GC = 0/0/0 |
| 单机游戏、随便玩玩 | ✅ API 简洁不折腾 |
| 纯 Archetype 切换密集型 | ⚠️ Friflo 略优（S8） |

---

## Quality

- **Source:** ~9,700 lines
- **Tests:** ~14,400 lines（467 tests，测试:代码 ≈ 1.5:1）
- **GC:** 0/0/0 across all game scenarios
- **Fuzz:** 1,000-frame cross-world replay verified

---

## Learn More

| Resource | Link |
|---|---|
| API Guide | [docs/README.md](docs/README.md) |
| Benchmarks vs Other ECS | [docs/comparison.md](docs/comparison.md) |

---

## License

[MIT](LICENSE)
