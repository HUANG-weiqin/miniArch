---
title: Command Buffer Runtime
module: MiniArch.Core CommandBuffer
description: CommandBuffer safety-first per-entity deduplicating recorder plus CommandStream typed-store expert mode, both compatible with FrameDelta
updated: 2026-06-14 (FrameDelta 紧凑字节流重构落地：Merge 简化、零拷贝序列化、开箱即用 Send/Recv)
---
# Command Buffer Runtime

## 这个模块是干什么的

- 提供 `CommandBuffer` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
- 录制时按实体+组件类型去重（而非追加日志），消除编译步骤
- `Submit()` 消费当前批次后自动清空，允许下一帧复用同一实例
- 约束并发只覆盖 recording，不把 `World` 变成并发写安全对象
- 提供 `CommandStream` 专家模式：typed component stores + lightweight structural log，降低 record 成本，同时能生成 `FrameDelta`

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandBuffer.cs`：public recording API + `Submit()` + `Snapshot()` + `Clone()`
  - `src/MiniArch/Core/CommandStream.cs`：append-only expert-mode API + `Submit()` + `Snapshot()`
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR，可保留并重放到同步 world
  - `src/MiniArch/Core/InlineMap.cs`：4 inline slot + overflow 的 per-entity map
  - `src/MiniArch/Core/OverflowPool.cs`：三个并行数组（keys, values, next）backed by `ArrayPool<T>` 的单链表节点池
  - `src/MiniArch/Core/World.cs`（+ partial 文件）：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`
- 数据流 / 控制流：
  - 工作线程通过 `CommandBuffer` 只记录命令；`Create()` 立刻从 world 预留真实 `Entity`
  - recording 完成后可选：`Submit()`（直接执行到 world）→ `Snapshot()`（生成自包含 `FrameDelta`）→ `SubmitAndSnapshotAsync()`（并行执行 Submit + BuildDelta）
  - `Clone()`：深拷贝整个 CommandBuffer 状态（含所有 slab 数据），用于 snapshot/replay 场景
  - `CommandStream`：`Add/Set` 进入按组件类型分组的 typed store；`Remove/Destroy` 进入 structural log；created entity 的组件录制期分流到 created side table，提交时一次性 materialize；`Snapshot()` 从 typed stores + side table/log 生成 `FrameDelta`

## CommandStream 专家模式

- 适用场景：调用方能保证命令质量，优先需要低 record overhead 的高频游戏帧。
- API 首版：`Create()`、`Add<T>()`、`Set<T>()`、`Remove<T>()`、`Destroy()`、`Submit()`、`Snapshot()`。
- 关键约束：
  - 不替代默认 `CommandBuffer`；默认 API 继续保留录制期去重与安全归约语义。
  - `Create()` 仍使用 `World.ReserveDeferredEntity()`，所以生成的 `FrameDelta` 可以保留确定的 entity id/version。
  - 组件数据按 typed value 记录，`Submit()` 直接写 typed value，`Snapshot()` 再转成 `FrameDelta` 所需 raw bytes。
  - component `Add/Set` 按类型批处理，不承诺与 `Remove/Destroy` 的严格全局追加顺序；同帧冲突命令的净效果由调用方负责。
  - created entity 组件在 record 时分流，避免提交时 O(created × commands) 扫描整条日志。
  - created materialize 使用小型 archetype cache，避免每个 spawn 反复分配 signature/type array。

## 录制时去重模型

- 每个实体维护 O(1) 的 `ExistingEntityOps`（4 inline slot + overflow linked-list node）
- 同实体同组件类型的后续操作替换而非追加
- `CreatedState` 同样用 4 inline 槽位 + overflow pool
- Arena slab allocator：`CopyData<T>` 写入 `ArrayPool<byte>.Shared.Rent` 租来的 slab

## 决策

- `Snapshot()` 用于跨 world 同步或延迟回放；无此需求时优先用 `Submit()`
- `CommandStream.Snapshot()` 首版同步生成 `FrameDelta`；后续可在同一日志形状上增加 async/compile 变体。
- `SubmitAndSnapshotAsync()`：换出 buffer 状态后，主线程 Submit 与后台线程 BuildDelta 并行执行
- 记录期返回真实 `Entity`，但只是 reserved handle（`world.IsAlive(entity)` 仍为 false）
- query layout generation 在 replay 期间被抑制，整批结束后只递增一次
- `ICommandRecorder` 接口存在但仅用于测试抽象层，CommandBuffer 和 CommandStream 都实现它
- `Clone()` 新增：完整深拷贝，用于需要保留录制状态的场景

## 性能数据（Release, 全帧 record+submit, 3s×1）

| Case | Mini Submit | Mini Async+Snp | Arch | Async vs Submit |
|---|---|---|---|---:|
| 1000/CreateHeavy | 3217 | 2847 | 1343 | -11.5% |
| 10000/CreateHeavy | 331 | 277 | 154 | -16.3% |
| 10000/DenseExisting | 285 | 284 | 189 | -0.4% |
| 10000/MixedScript | 357 | 282 | 182 | -21.0% |

### Struct 缩小带来显著性能提升（2026-06-08）

从 FrameDelta 热路径 struct 中删除 `Type RuntimeType`（8B 引用指针）、冗余 `int ComponentTypeId`、`Signature` 字段后：

| 结构体 | 改前 | 改后 | 缩小 |
|---|---|---|---|
| `AddSetEntry` | 24B | 16B | -33% |
| `CreatedComponent` | 24B | 16B | -33% |
| `TypeInfoCache` tuple | 16B | 8B | -50% |
| `RawComponentValue` | 32B | 20B | -38% |
| `RawComponentCommand` | 40B | 28B | -30% |
| `RawRemoveCommand` | 24B | 12B | -50% |

HeroPipeline 回归测试涨幅：

| 链路 | 改前 | 改后 | 涨幅 |
|---|---|---|---|
| Movement | 858.7 rounds/s | 1284.3 rounds/s | **+49.6%** |
| Attack | 626.7 rounds/s | 809.2 rounds/s | **+29.1%** |

**根因分析（三个因素叠加）：**

1. **GC write barrier 消除**（最大因素）：`Type` 是引用类型，每次构造包含它的 struct 时 JIT 必须插入 write barrier 更新 GC card table。删除后热路径零 write barrier。
2. **Cache line 利用率提升**：struct 缩小 30-50%，同样 cache line（64B）装更多条目，InlineMap 遍历时的 cache miss 大幅减少。
3. **内存带宽减少**：高频复制（List.Add、InlineMap.Set、DeepCopyOwnedData）的 per-copy 数据量减少。

**教训：在热路径 struct 中避免持有引用类型字段（即使只是缓存），代价远比看起来大。**

## 认知模型

- `CommandBuffer`：对 world 的"延迟结构变更 + 录制时去重 + 直接批量提交器"。
- `CommandStream`：对 world 的"低成本 typed component stores + created side table + 可编译 FrameDelta"。

## 入口

- 第一次读：`src/MiniArch/Core/CommandBuffer.cs` / `src/MiniArch/Core/CommandStream.cs` → `src/MiniArch/Core/FrameDelta.cs`
- 修 bug：`tests/MiniArch.Tests/Core/CommandBufferTests.cs`、`tests/MiniArch.Tests/Core/CommandStreamTests.cs`

## 坑点

- existing entity 和 created entity 的归约规则不同
- 同帧 `create + destroy` 必须释放 reserved entity，否则 id 被永久占用
- replay 期间逐条触发 query layout 失效会增加额外开销
- existing destroy 必须存完整 `Entity`（含 version），不能只存 id
- `InlineMap` 的 `OverflowHead` 默认值 0 在 `Clear()` 后可能导致遍历空 pool；新分配 map 必须显式初始化 `OverflowHead = -1`
- **`ResolveTypeInfo` 缓存哨兵值不能用 `IsValid`**（已修复 2026-06-08）：删除 `RuntimeType` 后缓存元组变为 `(ComponentType, int)`，用 `cached.Size > 0` 判断命中。**教训：缓存哨兵不能依赖值域内的合法值。**
- `Clone()` 是完整深拷贝，包含所有 slab 数据，大 buffer 克隆成本高
- `CommandStream` 是专家模式，不做 `CommandBuffer` 那样的 per-entity/per-component 去重；重复命令的净效果由调用方负责。
- **混合 World.Create（排序）与 CommandBuffer/CommandStream（未排序）可能产生重复 archetype**：两者创建组件的顺序不同（`World.Create<T>` 用 `public Signature(params ComponentType[])` 构造函数，自动排序去重；CommandBuffer/CommandStream 用 `Signature.CreateNormalized` 按 ADD 顺序存储，不做排序）。如果同一组件集用两条路径分别创建，`_archetypes` 字典中会出现两个不同键的 Signature，产生两个功能等价但列顺序不同的 archetype。`World.TryGetArchetype` 的集合比较能正确匹配两者，但 `ArchetypeMatchesComponentSpan`（CommandStream 分组用顺序比较）可能因顺序不同而失败。**解决办法：整个项目统一用一种创建路径**。
- **CommandStream 支持对 pending entity 调用 Remove**（2026-06-10 修复）：同批次内通过 `Create()` 或 `Clone()` 创建的实体处于 pending 状态。对其调用 `Remove<T>()` 现在通过 `BatchedComponent.Removed` 标记正确移除组件。与 CommandBuffer 行为一致。`CommandStreamTests.Remove_on_pending_clone_removes_component` 验证。
- **CommandStream 支持创建零组件的空实体**（2026-06-10 修复）：`Create()` 后未调用任何 `Add<T>()` 的实体现在通过 `MaterializeReservedEntity(entity, Array.Empty<>())` 正确 materialize 为无组件实体。与 CommandBuffer 行为一致。`CommandStreamTests.CrossWorld_replay_create_empty_entity` 验证。
- **CommandStream 同批次中 Destroy 后不再影响其他操作**（2026-06-10 修复）：`ApplyAllEntries` 改为两遍处理（pass 1: Create/Add/Set/Remove，pass 2: Destroy），与 CommandBuffer 的"先 ApplyOps 再 Destroy"顺序一致。同批次内先 Destroy 后 Add/Set 同一实体现在安全（Add/Set 先执行，然后 Destroy 销毁实体）。对 pending entity，`CancelPendingEntity` 后的条目在 materialize 时通过 `_pendingBatch[id] < 0` 检测跳过。`CommandStreamTests.Fuzz_200_frames_submit_and_verify_entity_count_stability` 不再需要 `destroyedThisFrame` 过滤。
- **CommandStream pending batch 组件归属使用 per-batch 链表**（2026-06-11 修复）：旧实现用 `_batchCompCounts` 前缀和 + 扁平 `_batchComps` 数组，隐式假设每个 batch 的组件在数组中连续。但录制 API 不保证此顺序（例如 `Create(A), Create(B), Add(B,V), Add(A,P)` 时组件按 B→A 顺序写入数组，前缀扫描会将 V 误归给 A）。修复为 per-batch 单链表（`_batchHeads[]` 指向每个 batch 的链表头，`BatchedComponent.Next` 链接节点），彻底消除组件归属错误。`CommitBatchComponent` 不做 last-wins 遍历（O(1) prepend），materialize 时稳定排序+去重达到 last-wins 语义。详见测试 `Interleaved_pending_creates_get_correct_components`、`Remove_pending_component_then_create_another_entity` 等。
- **CommandBuffer Entity lookup 加 Version 校验**（2026-06-11 修复）：`GetCreatedStateIndex`、`IsFrozenCreatedDestroyed`、`GetOrCreateOpsIndex` 之前只按 `Entity.Id` 查找，不校验 `Entity.Version`。World 回收实体后，同 Id 不同 Version 的 stale handle 会错误匹配到最新实体。修复后三个 lookup 函数都校验 `_entityByPoolIndex[idx].Version == entity.Version`，stale handle 不再别名。同时 `ApplyOpDirect`/`ApplyOpDirectFromFrozen` 加 `IsAlive` 保护，Submit 阶段遇到死实体自动跳过。
- **FrameDelta.Replay 命令处理顺序与原始记录顺序不同**（架构差异，非 bug）：`World.ReplayCore` 按固定顺序处理：Reserved → Released → Created → Link → Unlink → AddCommands → SetCommands → RemoveCommands → Destroy。当同帧对同一组件类型执行 `Remove<Pos>` 后 `Add<Pos>` 时，`Submit` 与 `Snapshot+Replay` 可能产生不同结果。**CommandBuffer 不受此影响：录制时 per-component 去重消除了重复操作。CommandStream 调用方应避免同帧对同组件 Remove+Add 模式，改用 `Set`。**

## CB/CS vs Friflo: Record 阶段瓶颈分析（2026-06-13）

### 分阶段计时数据

**基准测试 A：DenseExisting（纯 mutation，10k 实体，40k ops/iter）**

| Engine | record(us) | submit(us) | total(us) |
|---|---|---|---|
| MiniArch CB | 540 | 293 | 833 |
| MiniArch CS | 263 | 703 | 965 |
| Friflo | 169 | 414 | 583 |

- **CB record 是 Friflo 的 3.2x**：InlineMap 4-slot linear scan + 每个 entity 重复 version check
- **CS record 是 Friflo 的 1.6x**：flat append 不需要 InlineMap/version check，只剩 store 管理 + entry 流开销
- **CB submit 比 Friflo 快 1.4x**：直接 switch + `ApplyOpDirect`，无虚方法
- **CS submit 比 Friflo 慢 1.7x**：遍历 entry stream → switch → 虚方法 `WriteToWorld`

**基准测试 B：CommandBufferGame.Perf（真实游戏循环，含 query）**

| 场景 | Engine | Ticks/s | record% | apply% | CB/Friflo record 倍率 |
|---|---|---|---|---|---|
| Combat | CB | 2,531 | 34.4% | 53.7% | 2.8x |
| Combat | CS | 3,274 | 25.4% | 59.5% | 1.6x |
| Combat | Friflo | 3,384 | 16.6% | 67.5% | 1.0x |
| ParticleStorm | CB | 1,332 | 37.1% | 62.9% | 2.5x |
| ParticleStorm | CS | 1,691 | 46.3% | 53.7% | 1.8x |
| ParticleStorm | Friflo | 1,655 | 24.8% | 75.2% | 1.0x |
| HeroLight | CB | 303k | 44.6% | 54.8% | 2.5x |
| HeroLight | CS | 341k | 51.0% | 48.5% | 1.5x |
| HeroLight | Friflo | 354k | 34.1% | 65.4% | 1.0x |

### 关键发现

1. **CB record 固定慢 2.5-3.2x**：根因是 `Set<T>` 每条 op 走 `GetCreatedStateIndex`（version check）+ `GetOrCreateOpsIndex`（bounds + version check）+ `InlineMap.Set`（4-slot linear scan 去重）。每个 entity 做 4 次 op = 4 次重复 version check。Friflo 用裸 int id 无版本校验，直接数组索引。

2. **CS record 固定慢 1.5-1.8x**：把 CB 的 InlineMap + version check 砍掉后，剩余差距来自 typed store 管理（`GetOrCreateStore<T>` + resize check）和 entry 流 append。这个差距在 Create-heavy 场景能被 submit 的 batch materialization 收益弥补甚至反超。

3. **CS 在 Create-heavy 场景反超 Friflo**：ParticleStorm（4000 Create/tick）中 CS 的 apply 阶段通过 batch materialization 省了 137us/tick，超过 record 阶段亏的 124us，总时间 591us < Friflo 604us，达到 +2%。

### 优化方向

- **CB record**：把 `InlineMap` 改成按 componentId 直索引的数组（去掉 linear scan）；把 entity version check 提升到外围、每个 entity 只做一次而非每条 op 一次。预期能把 3x 差距砍到 1.5x。
- **CS record**：合并 `GetOrCreateStore<T>` 和 `AppendEntry` 为一步，预分配 store 数组避免 resize。差距已经较小（1.5x），ROI 有限。
- **当前定位**：CB 适合 mutation-heavy（版本安全），CS 适合 create-heavy（batch 合并收益）。两者互补而非替代。

## FrameDelta 决定性与帧同步（2026-06-14）

### 已验证的决定性属性

`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs` 通过 `WorldFingerprint`（投影 stats / archetype 列布局 / per-slot version / 组件值 / hierarchy 到字符串）验证以下性质：

1. **同一 delta 序列重放到 N 个全新 world → 状态完全一致**（含 entity id、version、archetype signature、组件字节、父子关系）。
2. **Submit(源) == Replay(副本)** 在避开 Remove+Add 同组件同帧模式时成立（见前文坑点）。这保证 host 用 Submit、client 用 Replay 的混合模式可收敛。
3. **ID 回收、hierarchy 演化、深克隆、批量 spawn + 剪枝**等场景下决定性稳定。
4. **Safety net**：target world 的 entity id allocator 状态与 source 分叉时，`EnsureReplayReservation` 抛 `InvalidOperationException("out of sync")`，不会静默别名。

### 关键约束：必须从 frame 0 完整重放

`EnsureReplayReservation`（World.EntityLifecycle.cs:418）假设 target 的 free-id 栈和 source 同步。**不能从 delta 序列中间开始 replay**——target 的 id allocator 必须被同样的历史驱动到同样位置。否则要么抛 out-of-sync，要么静默分配到错误的 id。

实践含义：
- **存档/回放**：从空 world 开始，按帧顺序 replay 全部 delta。可工作。
- **Lockstep/rollback**：所有 peer 必须从相同的初始快照开始，按相同顺序应用相同 delta。可工作。
- **断点续传 / 增量同步**：当前不支持。需要 id remap 或显式 checkpoint 机制。

### 帧同步尚缺的二块

1. ~~**序列化**~~ → 已完成。`delta.AsSpan()` + `FrameDelta.Deserialize(ReadOnlySpan<byte>)` 开箱即用。
2. **State checksum**：无需新 API。`WorldSnapshot.Save` 输出的字节流可以直接喂给 SHA256/XXHash64 做决定性 hash——前提是两个 world 走过相同的 delta 历史（lockstep 标准用法）。测试中 `HashWorld(World)` 已实现此模式（`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs`）。**注意**：Save 的字节依赖 archetype 创建顺序和 swap-remove 历史，因此只在"同 delta 序列"场景下稳定；不做"逻辑等价但路径不同"的规范化（YAGNI）。
3. **Divergent peer resync**：`EnsureReplayReservation` 抛异常而非尝试对齐。对抗性 netcode 需要 snapshot diff + 重放补救。

### 次要问题

- **Submit 与 Replay 命令顺序不一致**（已记录在坑点）：纯 lockstep 模式（只用 Replay）不受影响，但禁止同帧内某些 peer 用 Submit、另一些用 Replay。
- Replay 期间 query layout generation 抑制是一次性的，超大 delta 或分片重放可能触发额外失效。

### Merge + id 回收导致 allocator 分叉（BUG，未修复）

`FrameDelta.Merge` 把多帧 delta 折叠成一个。当原始帧序列中存在 **跨帧 entity id 回收** 时，合并后的 delta 导致 target replay 时 id allocator 与 source 分叉。

**复现场景：**
- Frame 1：`Create A` → A=(0,v1)
- Frame 2：`Destroy A` → free=[(0,v2)]
- Frame 3：`Create B` → 回收 id=0，B=(0,v2)

逐帧 replay 没问题（`EnsureReplayReservation` 检测到 B 在 free list 中，走 `ReserveDeferredEntity` 正常 pop 回收）。Merge 后：

```
ProcessCommandsInto → A: IsCreated + IsDestroyed
                       B: IsCreated(存活)

RebuildFromState:244 → A 命中 IsCreated && IsDestroyed，
                       折叠为 ReservedEntities+=A, ReleasedEntities+=A
```

合并后 delta 的 `ReplayCore` 处理顺序是 **全部 Reserve → 全部 Release → 全部 Create**：

1. Reserve A=(0,v1)：`ReserveDeferredEntity` 分配 id=0，`_records[0]={v=1, Arch=null}`。
2. Reserve B=(0,v2)：id=0<slotCount、!IsOccupied、Version=1≠2、不在 free list → fall through 重新分配 id=1 → `reserved≠entity` → **抛 out-of-sync 异常**。

**更阴的变体（纯 reserve+release 被静默丢弃）：**
- Frame 1：`Reserve X` → X=(0,v1)（无 create）
- Frame 2：`Release X`

Merge 命中 `RebuildFromState:251` 的 `if (IsReserved && IsReleased && !Created && !Destroyed) continue` → **X 被整个丢弃**。
Source 走 Submit 后 `free=[(0,v2)]`，target replay 合并 delta 后 `free=[]`。后续帧 create 必然分叉——**不出异常，静默错误**。

**根因：**
`FrameDelta` 的分段结构（同类命令聚成一段）＋ `ReplayCore` 的批处理顺序（全部 Reserve 后才 Release）→ 丢失了 `reserve A → release A → reserve B 回收 A 的 id` 这种**时序依赖**。逐帧 replay 时每个 delta 太小不会触发，Merge 把多帧拍成一个 delta 就暴露了。

**影响：**

| 用法 | 受影响 |
|---|---|
| 逐帧 lockstep（每帧一个 delta） | 安全 |
| 存档/回放（从 frame 0 逐帧 replay） | 安全 |
| **Merge 后网络同步**（带宽优化合并多帧） | **会炸** |
| 单帧内 create+destroy+create 同 id | 也会炸，但 CommandBuffer 录制时语义本就不清 |

**建议措施：**
1. **文档化约束**：Merge 仅用于"无生灭折叠"的 delta 序列（纯 add/set/link）。
2. **运行时检测**：Merge 前扫描 source deltas，若检测到跨帧 id 回收，退化成不合并或抛错。
3. **长期方案**：改 FrameDelta 为时序流（单条命令带 tag），Merge 只 append，Replay 按序处理——但破坏现有分段结构，影响性能。
4. 第 1+2 条优先，等出现真实带宽瓶颈再考虑 3。

### `WorldFingerprint` 模式（已删除）

早期测试用字符串 fingerprint 做状态对比，已替换为 `WorldSnapshot.Save` → SHA256。原因：
- 字符串太长（KB 级），失败时不可读
- 与生产路径脱节——无法复用
- Hash 模式直接复用 Save 序列化路径，零新 API，未来还可以喂给网络同步用

hash 失败时只在 message 里打印 stats（entityCount / archetypeCount / hash 前 16 位）做定位起点，详细差异用调试器查。

## FrameDelta 序列化压缩方案（2026-06-14）

### 目标

把 FrameDelta 序列化字节做到**贴近物理下界**。lockstep 场景对带宽敏感（每帧每个 peer 都要广播），YAGNI 原则下只取收益/复杂度比高的技巧。

### 物理下界估算（基线场景）

典型动作游戏一帧：`spawn 3 个怪(Pos+Vel+Health 共 20B payload)、set 50 个 Pos(8B)、destroy 5 个`。

| 命令 | 不可压缩信息 | 物理下界 |
|---|---|---|
| 3 creates | 3×(20B payload) + 3×~5bit 实体引用 + 9×~5bit 类型引用 | ~90 B |
| 50 sets | 50×(8B payload) + 50×~16bit 实体引用 + 50×~5bit 类型引用 | ~600 B |
| 5 destroys | 5×~16bit 实体引用 | ~12 B |
| **合计** | | **~702 B** |

payload 本身就是大头（>85%），实体/类型引用是零头。

### 当前裸序列化（baseline）

直接 record struct 一个个写：

- 3 creates: 3 × (8 entity + 3×(4 type + payload)) = 120 B
- 50 sets: 50 × (8 + 4 + 8) = 1000 B
- 5 destroys: 5 × 8 = 40 B
- **~1160 B ≈ 1.65× 物理下界**

冗余主要在：8B Entity(4 id + 4 version) 远超必要；4B ComponentType 远超必要；每命令重复存 type 标签。

### Tier 1 技巧（成本极低，直接打平物理极限）

1. **Varint entity id**（LEB128 或 zigzag varint）：entity id 几乎都 < 65536，2-3 字节代替 8 字节。version 在 lockstep 中是冗余的（可从 delta 序列推算），fresh entity 直接省掉。
2. **Per-section packed array**：header 列出每段 count，整段同质命令连续打包。**省掉每命令 1 字节 tag**。
3. **ComponentType → 1 byte varint**：已注册类型通常 < 256，1 字节够。
4. **Delta-local 实体引用**（可选）：本帧 reserved/created 的实体，后续命令引用时用"本帧第 N 个"，1 字节代替完整 entity。

应用 1+2+3（都很容易）：

- 3 creates: 3 × (3 entity + 3×1 type + 20 payload) = 78 B
- 50 sets: 50 × (3 entity + 1 type + 8 payload) = 600 B
- 5 destroys: 5 × 3 = 15 B
- section header ~10 B
- **~703 B ≈ 1.00× 物理下界**

### Tier 2 技巧（再省几 B，需要 schema 同步）

- **Archetype-batched create**："spawn N 个 archetype X" + N×payload。省掉每个 create 的类型引用（本例 ~15 B）。需要双方同步 archetype 表。
- **Component payload delta 编码**：只传位置差。适合高频 set，但需要前帧状态，属于 state-sync 层而非命令日志层。

### 不建议

- **Bit packing**（type ref 5 bit、entity ref 不对齐字节）：CPU 处理开销超过节省的字节。
- **通用压缩（zstd/LZ4）**：payload 已经是密度高的二进制，对小 delta 收益小，对 batched 多帧才划算（应在 transport 层做，不在 FrameDelta 层）。

### 推荐落地路径

| 阶段 | 内容 | LOC | 收益 |
|---|---|---|---|
| Tier 1 | varint + per-section packed + 1B type | ~200 | 1.65× → 1.00× 物理下界 |
| Tier 2 | + archetype-batched create | +100 | 把 create 路径也贴死物理下界 |

建议直接做 Tier 1：单次工作量大头，把 65% slack 一次吃光。Tier 2 等 profiling 显示 create 占比高再做。

### 实现注意事项（待定）

- **端序**：小端（与 `WorldSnapshot.Save` 现有约定一致）。
- **Varint 方案**：LEB128（无符号）/ zigzag varint（有符号）。entity id 用 LEB128 即可。
- **Section header 编码**：每段开头 1 byte tag + varint count。tag 值固定（Reserved=0x01, Released=0x02, Created=0x03, Link=0x04, Unlink=0x05, Add=0x06, Set=0x07, Remove=0x08, Destroy=0x09），与 `FrameDelta.ReplayCore` 处理顺序对齐。
- **接口形态**：`IFrameDeltaWriter` / `IFrameDeltaReader` 双向，或静态 `FrameDeltaCodec.Serialize(delta, IBufferWriter<byte>)` / `Deserialize(ReadOnlySequence<byte>)`。倾向后者（无状态、零分配）。
- **Created entity 的 Components 数组**：count + 紧凑排列 (typeId varint + size varint + payload bytes)，无对齐 padding。
- **版本协商**：第一字节留 4 bit version（wire format 版本）+ 4 bit flags（reserved for compression/extension）。

### 实际落地（2026-06-14）

方案已完全实现，且比原计划更简单：

**核心改动：**
- `FrameDelta` 内部改为 `byte[] _buffer` + `int _length` + `int _opCount`
- 每个 op 编码为 `[1 byte tag][varint entityId][varint entityVersion][payload...]`
- buffer 就是 wire format — 无需单独序列化步骤
- `Merge` 从 200 行折叠状态机变为 15 行 `Array.Copy` 拼接

**开箱即用的网络 API：**
```csharp
// 发
var wire = delta.AsSpan();           // ReadOnlySpan<byte>
socket.Send(wire);

// 收
var received = socket.Receive();
var delta = FrameDelta.Deserialize(received);
world.Replay(delta);
```

**约束：**
- lockstep 先决条件（两边 state 一致）满足时可直接收发
- `ComponentType.Value` 是进程内 int，跨进程使用需要两边 `ComponentRegistry` 注册顺序一致
- 跨进程场景可加 type mapping header（约 2-3 字节）

**已删除的死代码：**
- `Clear()`、`DeepCopyOwnedData()` — 零调用
- `BuildFromFrozen` 返回的 `CopiedBytes`
- 所有旧的 `ProcessCommandsInto` / `RebuildFromState` / `FoldComponent` / `SquashEntityState`

