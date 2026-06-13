# MiniArch vs 其他 ECS 库

与其他 C# ECS 库（[Arch](https://github.com/genaray/Arch)、[Friflo](https://github.com/friflo/Friflo.Engine.ECS)、[DefaultEcs](https://github.com/Doraku/DefaultEcs)）的对比。

## 功能矩阵

| 功能 | MiniArch | Arch | Friflo | DefaultEcs |
|---|---|---|---|---|
| Archetype ECS | ✅ | ✅ | ✅ | ✅ (混合) |
| Chunk 级迭代 | ✅ | ✅ | ✅ | ❌ |
| Query With/Without | ✅ | ✅ | ✅ | ✅ |
| Query WithAny | ✅ | ❌ | ✅ | ❌ |
| CommandBuffer | ✅ | ✅ | ✅ | ❌ |
| Ref 返回组件访问 | ✅ | ✅ | ❌（值拷贝） | ❌ |
| Entity 层次（Link/Unlink） | ✅ | ❌ | ✅ | ❌ |
| **帧同步（Lockstep）专属** | | | | |
| **FrameDelta（帧增量）** | ✅ **独有** | ❌ | ❌ | ❌ |
| **Snapshot() 提取可序列化增量** | ✅ **独有** | ❌ | ❌ | ❌ |
| **World.Replay(FrameDelta)** | ✅ **独有** | ❌ | ❌ | ❌ |
| **Replay 时 ID 一致性校验** | ✅ **独有** | ❌ | ❌ | ❌ |
| **FrameDelta.Merge()（增量合并）** | ✅ **独有** | ❌ | ❌ | ❌ |
| **World.Clone() 回滚点** | ✅ **独有** | ❌ | ❌ | ❌ |
| **WorldSnapshot（二进制序列化）** | ✅ | ❌ | ❌ | ❌ |
| **SubmitAndSnapshotAsync（流水线）** | ✅ **独有** | ❌ | ❌ | ❌ |
| **多帧跨 World 模糊重放测试** | ✅ **独有** | ❌ | ❌ | ❌ |
| **性能** | | | | |
| **CommandStream**（字节流录制） | ✅ **独有** | ❌ | ❌ | ❌ |

## 基准测试（公平对比）

以下数据来自仓库 `perf/` 下的同场景 benchmark，所有引擎执行相同的工作量，使用 Release 编译。

### 1. 12 游戏场景全面对比（MiniArch vs Friflo vs Arch）

12 个高压力游戏场景，涵盖纯迭代、多 Archetype 查询、创建/销毁、组件增删、宽组件读取、带过滤查询、Archetype 切换等维度。

| 场景 | 类型 | MiniArch | Friflo | Arch |
|---|---|---|---|---|
| S1-BulletHell | 纯迭代吞吐 | **14,416 ops/s** | 14,058 | 13,057 |
| S2-MMOZone | 多 Archetype 混合 | **48,326** | 47,902 | 46,023 |
| S3-WaveSpawner | 创建/销毁压力 | 6,273 | 5,720 | **8,241** |
| S4-BuffSystem | 组件增删 | **7,594** | 6,425 | 4,771 |
| S5-FullGameLoop | 多系统 pipeline | **21,470** | 19,885 | 17,244 |
| S6-RPGStats | 5 组件宽查询 | 20,817 | 18,283 | **24,554** |
| S7-ConditionalEffects | 带 Without 过滤 | **60,236** | 55,369 | 54,410 |
| S8-AIStateMachine | Archetype 切换 | 1,327 | **1,395** | 896 |
| S9-TeamAlternation | 交替查询 | **52,814** | 45,357 | 48,040 |
| S10-MixedLoad | 创建+查询+销毁 | **29,309** | 24,253 | 23,896 |
| S11-RandomEntityAccess | 随机实体 Get/Set | **14,825** | 10,043 | 11,936 |
| S12-FollowTheLeader | 跨实体查找 | 12,700 | **12,854** | 8,392 |

**MiniArch 赢得 7/12 场景**，在混合负载（+21%）、随机访问（+24% vs Friflo）、组件增删（+18%）等典型游戏场景中领先显著。

### 2. CommandBuffer 游戏稳态场景（MiniArch vs Friflo）

三种拟真游戏负载，对比 MiniArch 的两种录制模式（CommandBuffer / CommandStream）与 Friflo：

| 场景 | MiniArch CB | **MiniArch Stream** | Friflo | Stream 领先幅度 |
|---|---|---|---|---|
| SteadyCombat（20K 角色 + 8K 弹幕） | 2,119 ticks/s | **3,129** | 2,762 | +13% vs Friflo |
| ParticleStorm（4K/帧 粒子创建销毁） | 1,143 ticks/s | **1,369** | 1,328 | +3% vs Friflo |
| HeroLight（1K 角色，轻量请求） | 255,158 ticks/s | **299,946** | 281,817 | +6% vs Friflo |

MiniArch 的 **CommandStream** 模式在所有场景中均超过 Friflo，相比传统 CommandBuffer 提升 20%~48%。

### 3. 纯 record+submit（MiniArch vs Arch vs DefaultEcs）

| 场景 | MiniArch | Arch | DefaultEcs |
|---|---|---|---|
| DenseExisting（更新 10K 已有实体） | **411 ops/s** | 122 | 359 |
| CreateHeavy（创建 10K 新实体） | 647 ops/s | 107 | **719** |
| MixedScript（混合操作） | 614 ops/s | 121 | **642** |

- MiniArch 在更新已有实体场景大幅领先（3.4x vs Arch，15% vs DefaultEcs）
- 纯创建场景 DefaultEcs 略高（immediate 模式无 CB 开销）

### 4. 内存稳定性（Hero 管道回归测试）

| 场景 | 吞吐量 | 内存变化 | GC |
|---|---|---|---|
| Movement-Buffer | 805 rounds/s | -502 KB（稳定） | 0/0/0 |
| Movement-Stream | **1,019 rounds/s** | -152 KB（稳定） | 0/0/0 |
| Attack-Buffer | 554 rounds/s | -411 KB（稳定） | 0/0/0 |
| Attack-Stream | **803 rounds/s** | -60 KB（稳定） | 0/0/0 |

长时间运行 **GC 完全静默**，无内存泄漏。

## MiniArch 的独特优势

### 1. 帧同步（Lockstep）原生支持

帧同步的核心需求：服务端录制一帧的变更 → 序列化发送客户端 → 客户端回放产生完全一致的状态。

#### Arch 的方式

Arch 的 `CommandBuffer.Create()` 源码：
```csharp
// Arch: Create 时返回负 ID 占位符，Playback 时才分配真 ID
public Entity Create(ComponentType[] types) {
    var entity = new Entity(-(Size + 1), -1); // ← 临时占位符
    return entity;
}
// Playback 时：
var entity = world.Create(cmd.Types);  // ← 真 ID 取决于 world 当前状态
```

Arch 的实体 ID **在回放时才确定**，序列化 buffer 发给客户端后无法确保两端 ID 一致。

#### Friflo 的方式

Friflo 的 `CommandBuffer.CreateEntity()` 源码：
```csharp
// Friflo: Create 时直接从 store 预分配 ID
public int CreateEntity() {
    int id;
    lock (intern.componentCommandTypes) {
        id = intern.store.NewId(); // ← 预分配
    }
    return id;
}
// Playback 时用预分配的 ID
store.CreateEntity(entityId);
```

Friflo 的实体 ID **在 `CreateEntity()` 调用时已确定**，如果两端 world 状态一致，同一批 CommandBuffer 能产生相同的 ID。

#### MiniArch 的方式

```csharp
// 录制帧增量
var buffer = new CommandBuffer(world);
var delta = buffer.Snapshot();   // 生成自包含 FrameDelta
buffer.Submit();                  // 应用本地

// 网络发送 delta 到其他客户端

// 对端回放
replicaWorld.Replay(delta);      // 自动校验 ID 一致性
```

MiniArch 相比 Arch/Friflo 多了什么：

| 差距 | 为什么重要 |
|---|---|
| **`Snapshot()` 提取 delta** | Arch/Friflo 没有此 API，需自行序列化 buffer 内部结构 |
| **`Replay(FrameDelta)`** | MiniArch 原生 API，其他库需手动构造临时 world + 手动 Playback |
| **`EnsureReplayReservation`** | 回放前校验每个预分配 ID 是否匹配，不匹配立刻抛异常（防止静默数据错乱） |
| **`FrameDelta.Merge()`** | 将多帧合并为单个 delta，减少网络包数量 |
| **`World.Clone()`** | 深拷贝整个 world 作为回滚 checkpoint |
| **1000 帧模糊测试** | 已验证随机操作跨 world 回放产生完全一致状态 |

你可以用 Friflo/Arch 手动实现帧同步——自己维护 ID 映射、序列化 buffer、实现 merge、实现回滚。MiniArch 把这些**直接做成了一组原生 API**。

### 2. CommandStream — 比传统 CommandBuffer 更快

CommandStream 使用字节流录制，避免了 CommandBuffer 的 per-entity 哈希表积累，性能提升 20%~48%：

```
记录模式    SteadyCombat    ParticleStorm    HeroLight
CB          2,119           1,143            255,158
Stream      3,129 (+48%)    1,369 (+20%)     299,946 (+18%)
```

### 3. SubmitAndSnapshotAsync — 流水线并行

录制 + 提交 + 快照构建**流水线并行**，主线程不阻塞：

```csharp
// 主线程 Submit 与后台构建 delta 并行执行
Task<FrameDelta> deltaTask = buffer.SubmitAndSnapshotAsync();
// 主线程可以立即开始下一帧的查询或录制
```

### 4. 简洁、完整的 API

MiniArch 提供两层 API：

- **`MiniArch`** — 最小化入口，只有 `World`、`Entity`、`QueryDescription`
- **`MiniArch.Core`** — 底层能力：`Query`、`Chunk`、`CommandBuffer`、`CommandStream`、`FrameDelta`、`WorldSnapshot`

```csharp
// 默认层 — 简单直观
var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

var desc = new QueryDescription().With<Position>().With<Velocity>();
foreach (var entity in world.Query(in desc))
{
    if (world.TryGet(entity, out Position pos) && world.TryGet(entity, out Velocity vel))
        world.Set(entity, new Position(pos.X + vel.X, pos.Y + vel.Y));
}
```

```csharp
// 底层 — 零开销 chunk/span 遍历
var query = Query.Create(world, in desc);
foreach (var chunk in query.GetChunks())
{
    var positions = chunk.GetSpan<Position>();
    var velocities = chunk.GetSpan<Velocity>();
    for (var i = 0; i < positions.Length; i++)
        total += positions[i].X + velocities[i].VY;
}
```

### 5. 确定性与可验证

- **确定性 Entity ID** — 顺序分配 + LIFO 回收 + `EnsureReplayReservation` 校验
- **1000 帧模糊测试** — `CrossWorld_1000_frame_fuzz_replay_produces_identical_world` 已验证
- **WorldSnapshot** — 完整二进制存档，支持存档、断线重连、状态同步

## 适用场景

| 场景 | 推荐引擎 |
|---|---|
| 单机游戏 ECS | MiniArch / Arch / Friflo 均可 |
| **帧同步联机游戏** | **MiniArch（唯一内置支持）** |
| **状态同步 + 回滚** | **MiniArch（Clone + Replay）** |
| 纯 CommandBuffer 吞吐 | **MiniArch**（3.4x Arch） |
| 纯创建/销毁密集型 | DefaultEcs / Arch |
| 需要宽 archetype 查询 | Arch（+18% on S6） |
| 简单 ECS 项目 | DefaultEcs（最轻量） |
