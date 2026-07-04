# MiniArch vs 其他 ECS 库

与其他 C# ECS 库（[Arch](https://github.com/genaray/Arch)、[Friflo](https://github.com/friflo/Friflo.Engine.ECS)、[DefaultEcs](https://github.com/Doraku/DefaultEcs)）的对比。

## 功能矩阵

| 功能 | MiniArch | Arch | Friflo | DefaultEcs |
|---|---|---|---|---|
| Archetype ECS | ✅ | ✅ | ✅ | ✅ (混合) |
| Chunk 级迭代 | ✅ | ✅ | ✅ | ❌ |
| Query With/Without | ✅ | ✅ | ✅ | ✅ |
| Query WithAny | ✅ | ❌ | ✅ | ❌ |
| CommandBuffer / CommandStream | ✅ | ✅ | ✅ | ❌ |
| Ref 返回组件访问 | ✅ | ✅ | ❌（值拷贝） | ❌ |
| Entity 层次（AddChild/RemoveChild） | ✅ | ❌ | ✅ | ❌ |
| **帧同步（Lockstep）专属** | | | | |
| **FrameDelta（帧增量）** | ✅ **独有** | ❌ | ❌ | ❌ |
| **Snapshot() 提取可序列化增量** | ✅ **独有** | ❌ | ❌ | ❌ |
| **CommandStream.Replay(FrameDelta)** | ✅ **独有** | ❌ | ❌ | ❌ |
| **Replay 时 ID 一致性校验** | ✅ **独有** | ❌ | ❌ | ❌ |
| **World.Clone() 分支/独立副本** | ✅ **独有** | ❌ | ❌ | ❌ |
| **CaptureState/RestoreState（原地零分配回滚，多帧窗口）** | ✅ **独有** | ❌ | ❌ | ❌ |
| **WorldSnapshot（二进制序列化）** | ✅ | ❌ | ❌ | ❌ |
| **SubmitAndSnapshotAsync（流水线）** | ✅ **独有** | ❌ | ❌ | ❌ |
| **多帧跨 World 模糊重放测试** | ✅ **独有** | ❌ | ❌ | ❌ |
| **性能** | | | | |
| **CommandStream**（字节流录制） | ✅ **独有** | ❌ | ❌ | ❌ |

## 基准测试（公平对比）

以下数据来自仓库 `tools/perf/` 下的同场景 benchmark，所有引擎执行相同的工作量，使用 Release 编译。

> **测试环境：** 11th Gen Intel Core i5-1135G7 @ 2.40GHz, 4 cores / 8 threads, Windows 11, .NET 8.0

### 1. CommandStream 游戏稳态场景（MiniArch vs Friflo）

三种拟真游戏负载：

| 场景 | **MiniArch CommandStream** | Friflo | Stream vs Friflo |
|---|---|---|---|
| SteadyCombat（20K 角色 + 8K 弹幕） | **3,486** t/s | 3,254 | **+7%** |
| ParticleStorm（4K/帧 粒子创建销毁） | **1,502** t/s | 1,517 | -1%（持平） |
| HeroLight（1K 角色，轻量请求） | **343,918** t/s | 342,476 | **+0.4%** |

MiniArch CommandStream 在所有场景中持平或超过 Friflo。

> 历史数据：早期并存 per-entity 去重的 `CommandBuffer` 实现，Stream 相比它提升 12%~38%。2026-06-26 按 YAGNI 移除 CommandBuffer，CommandStream 成为唯一录制器。

### 2. CommandStream 延迟路径分段对比（PhaseProfile）

10K 实体 DenseExisting 场景，每 tick 40K 操作。分段计时：

| 引擎 | Record (μs) | Submit (μs) | Total (μs) |
|---|---|---|---|
| **MiniArch CS** | **229** | 520 | **750** |
| Friflo | 163 | 428 | 591 |

- **Friflo Record 最快**（163 μs）—— 纯 typed `T[]` 追加，无分支判断
- **CS Submit 比 Friflo 慢**（520 vs 428 μs）—— 三趟扫描（BuildMasks + Migrate + WriteValues）比 Friflo 的 ApplyToWorld 一趟多付了扫描税

### 3. 关键场景：SetHeavy — MiniArch 的架构优势

**核心洞察：** Friflo 的 `Compile()` 对**所有操作**（包括纯值修改 Set）都执行两趟扫描（`PrepareComponentCommands` → `ExecuteComponentCommands`），而 MiniArch CommandStream 对现有实体只有一趟扫描（`ApplyToWorld`）。

我们构造了一个以 Set 为主的真实游戏场景来验证：

| 场景规模 | 条件 | MiniArch CS | Friflo | 优势 |
|---|---|---|---|---|
| 500 实体 | 94% Set，15 spawn/destroy | **87,540 t/s** | 53,194 | **+65%** |
| 10K 实体 | 94% Set，300 spawn/destroy | **3,489 t/s** | 2,738 | **+27%** |

**为什么差距在小规模更大？** Friflo 的 Compile 有固定开销（字典分配、EntityChange 数组管理），小规模下这部分占比更高。MiniArch 的轻量路径（batch buffer + 单趟 ApplyToWorld）不付这个固定税。

**为什么大场景也赢？** Friflo 的两趟扫描成本随操作数线性增长。2 次 × 20K Set = 40K 数组访问 vs MiniArch 的 1 次 × 20K = 20K。差一个常数因子 2x，乘以操作数量。

### 4. 12 游戏场景全面对比（MiniArch vs Friflo vs Arch）

即时 API 调用（非延迟路径），12 个高压力游戏场景：

| 场景 | 类型 | MiniArch | Friflo | Arch |
|---|---|---|---|---|
| S1-BulletHell | 纯迭代吞吐 | **14,793** | 13,899 | 14,134 |
| S2-MMOZone | 多 Archetype 混合 | **53,273** | 50,310 | 49,016 |
| S3-WaveSpawner | 创建/销毁压力 | **10,077** | 8,650 | — |
| S4-BuffSystem | 组件增删 | **7,594** | 6,425 | 4,771 |
| S5-FullGameLoop | 多系统 pipeline | **21,470** | 19,885 | 17,244 |
| S6-RPGStats | 5 组件宽查询 | 20,817 | 18,283 | **24,554** |
| S7-ConditionalEffects | 带 Without 过滤 | **60,236** | 55,369 | 54,410 |
| S8-AIStateMachine | Archetype 切换 | 1,327 | **1,395** | 896 |
| S9-TeamAlternation | 交替查询 | **52,814** | 45,357 | 48,040 |
| S10-MixedLoad | 创建+查询+销毁 | **29,309** | 24,253 | 23,896 |
| S11-RandomEntityAccess | 随机实体 Get/Set | **14,825** | 10,043 | 11,936 |
| S12-FollowTheLeader | 跨实体查找 | 12,700 | **12,854** | 8,392 |

**MiniArch 赢得 9/12 场景**（裸引擎即时 API）。

### 5. 内存稳定性（Hero 管道回归测试）

| 场景 | 吞吐量 | 内存变化 | GC |
|---|---|---|---|
| Movement-Buffer | 805 rounds/s | -502 KB（稳定） | 0/0/0 |
| Movement-Stream | **1,019 rounds/s** | -152 KB（稳定） | 0/0/0 |
| Attack-Buffer | 554 rounds/s | -411 KB（稳定） | 0/0/0 |
| Attack-Stream | **803 rounds/s** | -60 KB（稳定） | 0/0/0 |

长时间运行 **GC 完全静默**，无内存泄漏。

### 6. BDN 统计基准：MixedLoad Archetype Scaling 矩阵

使用 BenchmarkDotNet `[Params]` 机制，单次运行覆盖 1/4/8/16 archetype × 5 引擎变体（20 个 benchmark），自动输出 Mean / Error (99.9% CI) / StdDev / Allocated。

> **测试环境：** AMD Ryzen 7 5700X3D (8C/16T), Windows 11, .NET 8.0.28, BenchmarkDotNet v0.14.0
>
> **场景设计：** 15K 实体，每帧 create 200 + structural change 20（Burning Add/Remove toggle）+ 2 个 chunk 级查询 + destroy 200。所有引擎使用各自的标准 chunk 遍历 API（公平对比，非不对称最快路径）。SIMD 变体使用 `Vector256<int>` + `MemoryMarshal.Cast` 手写向量化。源码：`tools/perf/S10Simd.Perf/`。

| 引擎 | 1 arch | 4 arch | 8 arch | 16 arch | 退化 (1→16) |
|---|---:|---:|---:|---:|---:|
| **MiniArch SIMD** | **35.94** | **39.99** | **42.20** | **44.67** | +24% |
| MiniArch Scalar | 52.38 | 52.73 | 54.49 | 64.63 | +23% |
| Friflo SIMD | 41.31 | 42.95 | 48.50 | 54.17 | +31% |
| Friflo Scalar | 51.52 | 54.80 | 61.90 | 67.45 | +31% |
| Arch Scalar | 70.08 | 72.91 | 74.07 | 79.00 | +13% |

（μs/帧，Error ±0.06~1.58 μs，全零 GC 分配）

**关键发现：**

1. **MiniArch SIMD 全程最快**——16 archetype 时 44.67 μs vs Friflo SIMD 54.17 μs（**快 18%**，99.9% CI 不重叠）。

2. **1 archetype scalar 持平**（MiniArch 52.38 ≈ Friflo 51.52，CI 重叠）→ 单 archetype 测试会掩盖 MiniArch 的真实优势。真实游戏 16+ archetype 是常态，多 archetype 数据才是有意义的对比。

3. **MiniArch archetype scaling 更优**（退化 +23% vs Friflo +31%）。1→4 几乎零退化（+1%），8→16 才明显（+19%）。

4. **BDN 统计严谨性**：99.9% CI + 自动离群值剔除 + 字节级分配追踪，结论可证伪。

---

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
```

Arch 的实体 ID **在回放时才确定**，序列化 buffer 发给客户端后无法确保两端 ID 一致。

#### Friflo 的方式

Friflo 的 `CommandBuffer.CreateEntity()` 预分配 ID，但缺少 `Snapshot()`、`Replay()` 等帧同步所需的 API。你需要自己实现 delta 提取、跨 World 回放、ID 校验。

#### MiniArch 的方式

```csharp
var authority = new CommandStream(world);
var delta = authority.Snapshot();   // 生成自包含 FrameDelta
authority.Submit();                  // 应用本地
// 网络发送 delta 到其他客户端
var replica = new CommandStream(replicaWorld);
replica.Replay(delta);              // 自动校验 ID 一致性
```

| 差距 | MiniArch | Friflo |
|---|---|---|
| **`Snapshot()` 提取 delta** | ✅ | ❌ |
| **`Replay(FrameDelta)`** | ✅ | ❌ |
| **Replay 时的 ID 一致性校验** | ✅ | ❌ |
| **`CaptureState/RestoreState`（原地回滚，多帧窗口）** | ✅ | ❌ |
| **`World.Clone()`（独立副本/分支）** | ✅ | ❌ |

### 2. CommandStream — Set 密集型场景比 Friflo 快 27%~65%

**根本原因：** Friflo 的 `Compile()` 对所有操作（包括纯 Set）都执行两趟扫描。MiniArch CommandStream 对现有实体的 Set 只一趟扫描。

数量级对比：

| 差距 | Friflo | MiniArch | 影响 |
|---|---|---|---|
| Set 扫描次数 | **2 趟**（PrepareComponentCommands + ExecuteComponentCommands） | **1 趟**（ApplyToWorld） | Set 越密集差距越大 |
| Pending entity 提交 | CreateEntity + MoveEntityTo（两步骤） | batch buffer 一次 MaterializeReservedEntityRaw | 创建密集型 CS 更优 |
| Record 开销 | typed `T[]` 追加 | 纯 append store | Friflo Record 略快（163 vs 229 μs per 40K ops） |

**适用场景规律：**

| 你的游戏特征 | CS vs Friflo |
|---|---|
| **Set 为主**（每帧大量位置/血量更新） | **CS 大优**（+65% at 500 实体） |
| 频繁结构变更（Add/Remove 组件） | 持平或近 |
| 纯创建/销毁 | CS 优（batch buffer 一次到位） |

### 3. SubmitAndSnapshotAsync — 流水线并行

录制 + 提交 + 快照构建**流水线并行**，主线程不阻塞：

```csharp
var stream = new CommandStream(world);
Task<FrameDelta> deltaTask = stream.SubmitAndSnapshotAsync();
// 主线程可以立即开始下一帧的查询或录制
```

### 4. 简洁、完整的 API

两层 API：

```csharp
// 默认层 — 简单直观
var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));
foreach (var entity in world.Query(new QueryDescription().With<Position>().With<Velocity>()))
    world.Set(entity, new Position(...));

// 底层 — 零开销 chunk/span 遍历
var query = world.Query(new QueryDescription().With<Position>().With<Velocity>());
foreach (var chunk in query.GetChunks())
{
    var positions = chunk.GetSpan<Position>();
    var velocities = chunk.GetSpan<Velocity>();
    for (int i = 0; i < positions.Length; i++)
        total += positions[i].X + velocities[i].Y;
}
```

### 5. 确定性与可验证

- **确定性 Entity ID** — 顺序分配 + LIFO 回收 + Replay 时 ID 分配器一致性校验
- **1000 帧模糊测试** — 已验证跨 World 回放一致性
- **WorldSnapshot** — 完整二进制存档，支持存档、断线重连、状态同步

---

## Friflo 的优势

公平起见，Friflo 在以下方面领先：

| Friflo 优势 | 原因 |
|---|---|
| **裸 Record 最快**（163 μs vs CS 229 μs） | typed `T[]` 组件数组直追加，无分支判断 |
| **Submit 单操作 Scan 更快**（10.7 ns/op vs CS 13.0） | BitSet SIMD 比较、heapMap[structIndex] 零边界检查 |
| **Archetype 切换**（S8）略快 | BitSet 单条 SIMD 相等判断 |
| **不需要 World 引用即可访问组件** | Entity 携带 Store 引用（16 bytes vs MiniArch 8 bytes） |
| **更成熟的生态** | NuGet 包、文档、社区 |

这些优势来自 **引擎底层常数**（typed `T[]` vs 统一 `byte[]`、BitSet SIMD vs ComponentMask 标量），不是架构差异。MiniArch 的架构优势（帧同步原生支持 + Set 密集型单趟扫描）在对应场景中可以获得更大的整体性能优势。

---

## 适用场景

| 场景 | 推荐引擎 |
|---|---|
| **帧同步联机游戏** | **MiniArch（唯一内置支持）** |
| **状态同步 + 回滚** | **MiniArch（Clone + Replay）** |
| **Set 密集型游戏**（大量位置/血量更新） | **MiniArch CS（+27~65% vs Friflo）** |
| 纯 command buffer 吞吐 | MiniArch（CS 比 Friflo 快 7% in Game） |
| 纯创建/销毁密集型 | MiniArch / Friflo |
| Archetype 切换密集型 | Friflo（略优 on S8） |
| 简单 ECS 项目 | DefaultEcs（最轻量） |
