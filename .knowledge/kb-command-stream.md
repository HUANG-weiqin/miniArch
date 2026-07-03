---
title: Command Stream Runtime
module: MiniArch.Core CommandStream
description: CommandStream typed-store append-only recorder, compatible with FrameDelta. The per-entity deduplicating CommandBuffer was removed (YAGNI) — CommandStream is now the sole recorder.
updated: 2026-07-03 (补 trace 优化：Create 单线程无锁化、Destroy 按需 hierarchy unavailable lookup、并发 reserve 契约明确; FrameDelta.Merge → Concat 重命名; MaterializeReservedEntity IReadOnlyList 链替换为 MaterializeEmptyReservedEntity)
---
# Command Stream Runtime

## 这个模块是干什么的

- 提供 `CommandStream` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
- append-only：`Add/Set/Remove` 按组件类型分片记录 typed value，不做录制期去重；同帧冲突命令的净效果由调用方负责
- `Submit()` 消费当前批次后自动清空，允许下一帧复用同一实例
- 约束并发只覆盖 recording，不把 `World` 变成并发写安全对象
- **帧同步端到端指南** → 见 `kb-lockstep-playbook.md`
- **FrameDelta.Concat** → 见本章 "Concat + id 回收" 段
- **DeferredEntities flag（placeholder vs real-id 模式）** → 见 `kb-deferred-create-design.md`
- 历史：曾并存 `CommandBuffer`（per-entity 录制期去重的安全默认）。2026-06-26 按 YAGNI 移除——实测无真实消费者依赖去重语义，CommandStream 在所有工作负载上更快（Movement +37%、Attack +33%），双实现是冗余维护包袱。

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandStream.cs`：append-only recording API + `Submit()` + `Snapshot()` + `Clone()`
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR，可保留并重放到同步 world
  - `src/MiniArch/Core/World.cs`（+ partial 文件）：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`
- 数据流 / 控制流：
  - 工作线程通过 `CommandStream` 只记录命令；`Create()` 在 `DeferredEntities=false`（默认）时立刻从 world 预留真实 `Entity`，`DeferredEntities=true` 时返回 placeholder `Entity(-1, seq)`
  - recording 完成后可选：`Submit()`（直接执行到 world）→ `Snapshot()`（生成自包含 `FrameDelta`）→ `SubmitAndSnapshotAsync()`（并行执行 Submit + BuildDelta）
  - `Clone()`：深拷贝源实体及子树到 pending batch，用于 snapshot/replay 场景
  - `Add/Set` 进入按组件类型分组的 typed store；`Remove/Destroy` 进入 structural log；created entity 的组件录制期分流到 created side table（per-batch 单链表），提交时一次性 materialize；`Snapshot()` 从 typed stores + side table/log 生成 `FrameDelta`

## 关键约束

- `Create()` 在 `DeferredEntities=false` 时使用 `World.ReserveDeferredEntity()` 分配 real id；`DeferredEntities=true` 时返回 placeholder，不碰 World id allocator。
- 组件数据按 typed value 记录，`Submit()` 直接写 typed value，`Snapshot()` 再转成 `FrameDelta` 所需 raw bytes。
- component `Add/Set` 按类型批处理，不承诺与 `Remove/Destroy` 的严格全局追加顺序；同帧冲突命令的净效果由调用方负责。
- created entity 组件在 record 时分流，避免提交时 O(created × commands) 扫描整条日志。
- created materialize 使用小型 archetype cache，避免每个 spawn 反复分配 signature/type array。
- `SubmitAndSnapshotAsync` 使用 FrozenState 双缓冲池（`_spareFrozen`/`_pendingFrozen`），稳态零分配。

## 数据模型

- existing entity 的 `Add/Set/Remove` 进入 per-component-type 的 typed store（`ComponentStore<T>`，flat `T[]` + `Entity[]` + `byte[] kinds`），append-only 不去重
- created entity 的组件写入共享 `byte[] _batchBuf`，通过 per-batch 单链表（`_batchHeads[]` + `BatchedComponent.Next`）归属；materialize 时稳定排序+去重达到 last-wins
- Arena slab allocator：组件值写入 `ArrayPool<byte>` 租来的 `_batchBuf`

## 决策

- `Snapshot()` 用于跨 world 同步或延迟回放；无此需求时优先用 `Submit()`
- `CommandStream.Snapshot()` 首版同步生成 `FrameDelta`；后续可在同一日志形状上增加 async/compile 变体。
- `SubmitAndSnapshotAsync()`：换出 buffer 状态后，主线程 Submit 与后台线程 BuildDelta 并行执行
- `DeferredEntities=false` 时记录期返回真实 `Entity`（reserved handle，`world.IsAlive(entity)` 仍为 false）；`DeferredEntities=true` 时返回 placeholder `Entity(-1, seq)`（单帧有效，不跨帧）
- query layout generation 在 replay 期间被抑制，整批结束后只递增一次
- ~~`ICommandRecorder`~~ — 已删除。CommandStream 直接提供 Record API（YAGNI）
- `Clone()` 新增：完整深拷贝，用于需要保留录制状态的场景
- **2026-06-28 API 统一**：删除 `SetConcurrent`/`AddConcurrent`/`RemoveConcurrent` 专用并行方法。用户在 `ParallelRecording=true` 时直接用 `Set`/`Add`/`Remove`/`Create`/`Destroy`/`AddChild`/`RemoveChild` 即可，热路径自动切换到并发实现。单线程模式零额外成本（一次可预测的 `_parallelMode` 分支）。
- **2026-07-03 并发 reserve 契约明确**：同一 `World` 不支持多个 `CommandStream` 实例并发录制。`ParallelRecording=true` 的并发只能在一个 stream 内。单线程路径 `Create`/`Destroy` 跳过不必要的记录期状态：
  - `Destroy` 单线程只写 `DestroyEntities[]` / pending cancel；hierarchy 过滤所需的 unavailable lookup 在消费前由复用的 `HashSet<Entity>` 从 `DestroyEntities[] + BatchCanceled[]` 重建，**不产生新的 GC 容器**
  - `Destroy` parallel 路径仍保留 `MarkUnavailableConcurrent`，因为 `CanRecordParallelComponentCommand` 是记录期消费者；进入 Submit/Snapshot 前会清空并重建同一个复用 HashSet
  - `Create` 单线程走 `World.ReserveDeferredEntityUnsafe`（原 `ReserveDeferredEntityBatch` 改名，无锁，调用方保证无并发）
  - `World.ReserveDeferredEntity` 保留锁供外部或并行路径安全使用

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
2. **Cache line 利用率提升**：struct 缩小 30-50%，同样 cache line（64B）装更多条目，遍历时的 cache miss 大幅减少。
3. **内存带宽减少**：高频复制（List.Add、DeepCopyOwnedData）的 per-copy 数据量减少。

**教训：在热路径 struct 中避免持有引用类型字段（即使只是缓存），代价远比看起来大。**

## 认知模型

- `CommandStream`：对 world 的"低成本 typed component stores + created side table + 可编译 FrameDelta"的延迟命令录制器。

## 入口

- 第一次读：`src/MiniArch/Core/CommandStream.cs` → `src/MiniArch/Core/FrameDelta.cs`
- 修 bug：`tests/MiniArch.Tests/Core/CommandStreamTests.cs`

## 坑点

- existing entity 和 created entity 的归约规则不同
- 同帧 `create + destroy` 必须释放 reserved entity，否则 id 被永久占用
- replay 期间逐条触发 query layout 失效会增加额外开销
- existing destroy 必须存完整 `Entity`（含 version），不能只存 id
- **`ResolveTypeInfo` 缓存哨兵值不能用 `IsValid`**（已修复 2026-06-08）：删除 `RuntimeType` 后缓存元组变为 `(ComponentType, int)`，用 `cached.Size > 0` 判断命中。**教训：缓存哨兵不能依赖值域内的合法值。**
- `Clone()` 是完整深拷贝，包含所有 slab 数据，大 buffer 克隆成本高
- `CommandStream` 不做 per-entity/per-component 去重；重复命令的净效果由调用方负责。
- ~~**混合 World.Create（排序）与 CommandStream（未排序）可能产生重复 archetype**~~ **（已修复，保留作历史）**：早期 CommandStream 用 `Signature.CreateNormalized` 按 ADD 顺序存储不排序，与 `World.Create<T>` 的排序构造函数产生不同 Signature key。**当前 CommandStream materialize 两条路径都已排序**：`hasLargeIds` 分支显式 `SortTypesAndOffsets` + `DeduplicateSortedSpans`（`CommandStream.cs:755-756`）；mask 分支通过 `MaskToTypes` 按位序升序枚举（`CommandStream.cs:1113`）。两者产出的 Signature 与 `World.Create<T>` 完全一致，不再有 archetype 重复。曾经的 fallback `World.TryGetArchetype` 已于 2026-06-29 作为死代码删除。
- **CommandStream 支持对 pending entity 调用 Remove**（2026-06-10 修复）：同批次内通过 `Create()` 或 `Clone()` 创建的实体处于 pending 状态。对其调用 `Remove<T>()` 现在通过 `BatchedComponent.Removed` 标记正确移除组件。`CommandStreamTests.Remove_on_pending_clone_removes_component` 验证。
- **CommandStream 支持创建零组件的空实体**（2026-06-10 修复，2026-07-03 重构为 `MaterializeEmptyReservedEntity`）：`Create()` 后未调用任何 `Add<T>()` 的实体通过 `World.MaterializeEmptyReservedEntity(entity)` 正确 materialize 为无组件实体。`CommandStreamTests.CrossWorld_replay_create_empty_entity` 验证。
- **CommandStream 同批次中 Destroy 后不再影响其他操作**（2026-06-10 修复）：`ApplyAllEntries` 改为两遍处理（pass 1: Create/Add/Set/Remove，pass 2: Destroy），"先 ApplyOps 再 Destroy"顺序。同批次内先 Destroy 后 Add/Set 同一实体现在安全（Add/Set 先执行，然后 Destroy 销毁实体）。对 pending entity，`CancelPendingEntity` 后的条目在 materialize 时通过 `_pendingBatch[id] < 0` 检测跳过。`CommandStreamTests.Fuzz_200_frames_submit_and_verify_entity_count_stability` 不再需要 `destroyedThisFrame` 过滤。
- **CommandStream pending batch 组件归属使用 per-batch 链表**（2026-06-11 修复）：旧实现用 `_batchCompCounts` 前缀和 + 扁平 `_batchComps` 数组，隐式假设每个 batch 的组件在数组中连续。但录制 API 不保证此顺序（例如 `Create(A), Create(B), Add(B,V), Add(A,P)` 时组件按 B→A 顺序写入数组，前缀扫描会将 V 误归给 A）。修复为 per-batch 单链表（`_batchHeads[]` 指向每个 batch 的链表头，`BatchedComponent.Next` 链接节点），彻底消除组件归属错误。`CommitBatchComponent` 不做 last-wins 遍历（O(1) prepend），materialize 时稳定排序+去重达到 last-wins 语义。详见测试 `Interleaved_pending_creates_get_correct_components`、`Remove_pending_component_then_create_another_entity` 等。

- **Submit vs Replay 命令顺序对齐**：`World.ReplayCore`（`World.cs:481`）是 `while (decoder.MoveNext()) switch`，**按字节流时序处理**——所谓"canonical 顺序（Reserve→Release→Create→AddChild→RemoveChild→Add→Set→Remove→Destroy）"实际是 `BuildDelta` 分段写入 buffer 的产物，不是 ReplayCore 硬编码。Submit 与 BuildDelta 顺序**完全对齐**（Create→Hierarchy→Ops→Destroy），所有命令组合（含 AddChild+Set、AddChild+Destroy、RemoveChild+Set 等同帧混合）下 Submit 与 Replay 都收敛：
  - CommandStream 的 `ComponentStore<T>.ApplyToWorld` 与 `.EmitToDelta` 都按相同 `_kinds` 数组顺序遍历，Submit 与 Replay 行为一致。
  - 验证：`Submit_on_source_equals_Replay_on_replica_for_safe_patterns` (`FrameDeltaDeterminismTests.cs:55`) 用 `BuildComplexScenario` 覆盖多样命令组合；`Submit_link_and_set_on_same_child_same_frame_converges_with_replay` / `Submit_link_parent_then_destroy_parent_same_frame_converges_with_replay` / `Submit_unlink_then_set_same_frame_converges_with_replay` 等针对性测试（`FrameDeltaDeterminismTests.cs:592` 起）覆盖所有同帧组合。
- **Cancelled pending create 的单遍 emit 约束**（2026-06-30 修复）：`EmitPendingEntitiesToDelta` 必须保留每个 batch `Reserve + Release/Create` 的单遍顺序，不能退回“先所有 Reserve、再 Release/Create”。原因是同帧取消的 reserved id 可能被后续 `Create()` 复用，Replay 端必须先看到旧 id 的 `Release` 才能预定复用 id。副作用是 `Release` 会污染 free list，后续 fresh `Reserve(Entity(slotCount), v1)` 不能走普通 `ReserveDeferredEntity()`（它会先 pop free list），而应只在 `id == _entitySlotCount && version == 1` 时直接创建 fresh slot；`id > _entitySlotCount` 仍表示 replay 历史分叉，必须抛错。回归：`Pending_cancel_after_later_create_does_not_diverge_replay_allocator`、短 seed sweep `0..5000/65535/999999/int.MaxValue`、长程 seed `42`。
- **Component command 对 stale entity 必须录制期过滤**（2026-06-30 修复）：`Submit()` 对 stale `Add/Set/Remove` 会在 apply 时因 `TryGetLocation` 失败而跳过；`Snapshot()` 若仍 emit 这些命令，Replay 端会在 `ApplyRawAddOrSet` / `RemoveBoxed` 上抛错并分叉。因此单线程和 `ParallelRecording=true` 路径都要跳过既非 alive、也非本 batch pending 的 entity。并行路径要保留 pending create 的组件命令（pending entity 尚未 alive），同时跳过已 `Destroy()` 标记 unavailable 的 pending entity。回归：`Parallel_recording_skips_stale_existing_entity_component_commands`、`Parallel_recording_keeps_pending_create_component_commands`。

- **`ComponentStore.EmitToDelta` 删除冗余 `_kinds[i]` 二次检查**（2026-07-02）：`switch (_kinds[i]) { case KindAdd: case KindSet: ... if (_kinds[i] == KindAdd) ... }` 中，switch 已匹配 KindAdd/KindSet，内部又读一次 `_kinds[i]` 来区分两者。拆分为独立 `case KindAdd:` / `case KindSet:`，消除一次数组 read + 分支。Attack +0.8%，Movement 噪声。注意：拆分后 `case KindAdd` 和 `case KindSet` 的 `fixed` 块内不能共享 `ptr` 变量，各自独立声明。

- **`Submit()` 成功路径清理不再 `TryReleaseReserved`**（2026-07-02）：`Submit()` 成功后，所有未取消 pending entity 已被 `MaterializeAllPending` 变成 alive，`Clear()` 中的 `TryReleaseReserved(entity)` 必然返回 false，但仍会读 `_records` 并做版本/占用判断。改为 `Clear(releaseReserved: !submitted)`：成功路径只清 `_frozen.PendingBatch[id]`；异常/显式 `Clear()` 路径仍释放 reserved id。语义边界：只有 `ApplyDestroys()` 结束后才设置 `submitted=true`。

- **`MaterializePending` 依赖 caller 已跳过 cancelled batch**（2026-07-02）：两个调用点（`MaterializeAllPending`、`SubmitFromFrozen`）都在调用前判断 `BatchCanceled[i]`，`MaterializePending` 内部二次 cancelled/bounds 检查是冗余热路径分支，已删除。若未来新增调用点，必须保持“只传非 cancelled batch”的前置条件。

- **`MaterializeFromBatchBuffer` 合并 `HasBit` + `SetBit`**（2026-07-02）：小 id 组件去重原先对同一个 component id 先跑一遍 8-lane branch ladder 做 `HasBit`，未命中再跑一遍 ladder 做 `SetBit`。改为 `TrySetBit`，一次定位 lane 完成 test-and-set。对 create-heavy batch materialize 路径最直接。

- **`EnsureCapacity` 在 `Archetype.AddEntity` 中有副作用陷阱**（2026-07-02 发现）：`AddEntity` 非 chunked 路径调用 `EnsureCapacity(_count + 1)`，该方法在 `_capacity * 2 > _segmentCapacity` 时会 `ConvertToChunked()`，将 `_entities = null!` 并设置 `_isChunked = true`。后续如果直接访问 `_entities[row]` 会 NRE。旧代码在 `EnsureCapacity` 之后用 `if (!_isChunked)` 守卫，看似"冗余"的 `else` 分支（`return AddEntityChunked(entity)`）实际是 conversion 后的安全 fallback。**教训：在有副作用的调用之后，不能假设类型状态不变。**

## CommandStream vs Friflo: Record 阶段瓶颈分析（2026-06-13，历史——移除 CommandBuffer 的依据）

### 分阶段计时数据

**基准测试 A：DenseExisting（纯 mutation，10k 实体，40k ops/iter）**

| Engine | record(us) | submit(us) | total(us) |
|---|---|---|---|
| MiniArch CS | 263 | 703 | 965 |
| Friflo | 169 | 414 | 583 |

- **CS record 是 Friflo 的 1.6x**：flat append，剩 typed store 管理 + entry 流开销
- **CS submit 比 Friflo 慢 1.7x**：遍历 entry stream → switch → 虚方法 `WriteToWorld`

> 历史 CB 数据（已删除的实现）：record 540us（Friflo 3.2x）、submit 293us（Friflo 0.7x）。CB 因 InlineMap 4-slot linear scan + 每条 op 重复 version check 而 record 固定慢 2.5-3.2x。这正是 CommandStream 取代 CommandBuffer 的核心依据。

**基准测试 B：CommandStreamGame.Perf（真实游戏循环，含 query）**

| 场景 | Engine | Ticks/s | record% | apply% |
|---|---|---|---|---|
| Combat | CS | 3,274 | 25.4% | 59.5% |
| Combat | Friflo | 3,384 | 16.6% | 67.5% |
| ParticleStorm | CS | 1,691 | 46.3% | 53.7% |
| ParticleStorm | Friflo | 1,655 | 24.8% | 75.2% |
| HeroLight | CS | 341k | 51.0% | 48.5% |
| HeroLight | Friflo | 354k | 34.1% | 65.4% |

### 关键发现

1. **CS record 固定慢 1.5-1.8x**：差距来自 typed store 管理（`GetOrCreateStore<T>` + resize check）和 entry 流 append。这个差距在 Create-heavy 场景能被 submit 的 batch materialization 收益弥补甚至反超。
2. **CS 在 Create-heavy 场景反超 Friflo**：ParticleStorm（4000 Create/tick）中 CS 的 apply 阶段通过 batch materialization 省了 137us/tick，超过 record 阶段亏的 124us，总时间 591us < Friflo 604us，达到 +2%。

### 优化方向

- **CS record**：合并 `GetOrCreateStore<T>` 和 `AppendEntry` 为一步，预分配 store 数组避免 resize。差距已经较小（1.5x），ROI 有限。

## CommandStream 专用 Profile Runner（2026-07-02）

`tools/perf/CommandStream.Profile` 是独立于三方对比的 CommandStream 专剖工具。

**用途：**
- 只测 MiniArch CommandStream，排除 query / 游戏逻辑 / 空间 hash 噪声
- 分阶段计时：record % / submit % / snapshot % / clear %
- 输出 heap delta、GC count，配合 `dotnet-trace` 做 CPU sampling

**6 个 workload：**

| workload | 目标 | 典型 record% | 典型 submit% |
|---|---|---|---|
| existing-set | `ComponentStore.ApplyToWorld`（Set 热路径） | ~67% | ~33% |
| existing-add-remove | 结构 Add/Remove Apply | ~54% | ~46% |
| create-small4 | pending materialize mask 路径；持续创建，会增长 world/heap，长 trace 会混入扩容成本 | ~64-71% | ~29-36% |
| create-duplicates | per-batch last-wins 去重 | ~64% | ~36% |
| create-destroy | reserve/release/cancel + destroy submit；固定 2,000 live entity 的稳态替换 | ~65% | ~35% |
| snapshot-only | `EmitToDelta` / FrameDelta append（~37% snapshot） | ~63% | — |

**快速上手：**
```bash
# 列出 workload
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --list

# 跑单个 workload（warmup 3s, measure 10s）
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4

# 配合 dotnet-trace 采样（trace 20s）
tools/scripts/profile-commandstream.ps1 -Scenario create-small4 -TraceSeconds 20

# 从采样结果看热点
dotnet-trace report profiles/commandstream-*.nettrace topN -n 50 --inclusive
# 当前 dotnet-trace 版本没有 --exclusive；不加 --inclusive 即 exclusive topN
dotnet-trace report profiles/commandstream-*.nettrace topN -n 50
```

**2026-07-03 trace 结论：**
- `existing-set`：record 循环被 JIT inline 到 `ExistingSetScenario.RunTick()`，exclusive ~47%；submit 侧 `ComponentStore<Position>.ApplyToWorld` exclusive ~47%。方法级 topN 已确认 Set submit 热点，但 record 内部需要额外 no-inline/条件计数才能继续拆。
- `existing-add-remove`：submit 已转入 `World`/`Archetype` 结构变更：`ComponentStore<Velocity>.ApplyToWorld`、`World.ApplyTypedAdd`、`MoveEntityCore`、`CopySharedComponentsFrom`、`RemoveAt`。CommandStream 记录层不是主要瓶颈。
- `create-destroy`（修复为稳态后）：最热是录制/提交辅助结构，`CommandStream.MarkUnavailable` exclusive ~24%、`World.ReserveDeferredEntity` exclusive ~16%、`MaterializeFromBatchBuffer` exclusive ~10%。**已实施优化**：`Create` 单线程绕开 `World` allocator lock（`ReserveDeferredEntityUnsafe`）；`Destroy` 单线程不再维护 record-time `UnavailableEntities`，改为消费前重建**复用** lookup（无新增 GC 容器）。优化后 create-destroy 从 ~18.7k → ~24.4k ticks/s (+30%)，测量期 GC=0。
- `create-small4` 是有意持续创建的压力场景，30s trace 会导致上亿实体与十 GB 级 heap，适合短跑或配合 growth 分析，不应用来代表稳态 create 成本。

**判停规则：** 如果 `CommandStream.*` 在 sampling 中总占比低于 ~10-15%，或热点已转移到 `World` / `Archetype` / query / GC 层，则停止 CommandStream 内部微优化。

## FrameDelta 两种 entity-id 模式

`FrameDelta` 的 wire format 对两种模式是相同的（signed LEB128 varint 编码 entity id + version），区别只在 **id 是 placeholder 还是 real**。模式由 `CommandStream.DeferredEntities` flag 控制。

| 模式 | flag | 生产者 | id 格式 | 消费者要求 | 场景 |
|---|---|---|---|---|---|
| **Placeholder delta** | `true` | `Snapshot()` | `Entity(-1, seq)` — 未分配 host id | replay 端各自建 `seq→local real` 映射，自行分配 | 多 host lockstep |
| **Real-id delta** | `false`（默认） | `Snapshot()` 或 `SubmitAndSnapshotAsync()` | `Entity(realId, version)` — 已分配 host id | replay 端 id allocator 必须与源同步（`EnsureReplayReservation` 校验） | 单机跨 world 同步 / 权威服务器 + 镜像客户端 |

**注意**：`SubmitAndSnapshotAsync()` 始终输出 real-id delta，忽略 `DeferredEntities` flag。

**关键约束：**
- `DeferredEntities=true` 时 `Snapshot()` 路径**禁止出现 immediate entity**（Id >= 0 的 batch entity）。检测到即抛 `InvalidOperationException`。
- `Submit()` 单机路径两种模式都能用——它不走 delta 序列化，`ResolveDeferredCreates` 在本地兑成 real id。
- wire format 本身是**模式无关**的：producer 写什么 id，consumer 就读到什么 id。

### 选择指南

```
用户场景：多个独立 world 各自创建实体，需要互相同步？
  ├── 是 → Snapshot() 走 placeholder delta，每个 host Replay 自己分配 id
  └── 否 → 单机用 Submit()，服务器镜像用 SubmitAndSnapshotAsync()
```

### 已验证的决定性属性

`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs` 通过 `WorldFingerprint`（投影 stats / archetype 列布局 / per-slot version / 组件值 / hierarchy 到字符串）验证以下性质：

1. **同一 delta 序列重放到 N 个全新 world → 状态完全一致**（含 entity id、version、archetype signature、组件字节、父子关系）。
2. **Submit(源) == Replay(副本)** 在避开 Remove+Add 同组件同帧模式时成立（见前文坑点）。这保证 host 用 Submit、client 用 Replay 的混合模式可收敛。
3. **ID 回收、hierarchy 演化、深克隆、批量 spawn + 剪枝**等场景下决定性稳定。
4. **Safety net**：target world 的 entity id allocator 状态与 source 分叉时，`EnsureReplayReservation` 抛 `InvalidOperationException("out of sync")`，不会静默别名。

### 关键约束：必须从 frame 0 完整重放

`EnsureReplayReservation`（World.EntityLifecycle.cs:451）假设 target 的 free-id 栈和 source 同步。**不能从 delta 序列中间开始 replay**——target 的 id allocator 必须被同样的历史驱动到同样位置。否则要么抛 out-of-sync，要么静默分配到错误的 id。

实践含义：
- **存档/回放**：从空 world 开始，按帧顺序 replay 全部 delta。可工作。
- **Lockstep/rollback**：所有 peer 必须从相同的初始快照开始，按相同顺序应用相同 delta。可工作。
- **断点续传 / 增量同步**：当前不支持。需要 id remap 或显式 checkpoint 机制。

### 帧同步已实现和尚缺的部分

1. ~~**序列化**~~ → 已完成。`delta.AsSpan()` + `FrameDelta.Deserialize(ReadOnlySpan<byte>)` 开箱即用。
2. **State checksum**：无需新 API。`WorldSnapshot.Save` 输出的字节流可以直接喂给 SHA256/XXHash64 做决定性 hash——前提是两个 world 走过相同的 delta 历史（lockstep 标准用法）。测试中 `HashWorld(World)` 已实现此模式（`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs`）。**注意**：Save 的字节依赖 archetype 创建顺序和 swap-remove 历史，因此只在"同 delta 序列"场景下稳定；不做"逻辑等价但路径不同"的规范化（YAGNI）。
3. **Divergent peer resync**：`EnsureReplayReservation` 抛异常而非尝试对齐。对抗性 netcode 需要 snapshot diff + 重放补救。

### 次要问题

- **Submit 与 Replay 命令顺序不一致**（已记录在坑点）：纯 lockstep 模式（只用 Replay）不受影响，但禁止同帧内某些 peer 用 Submit、另一些用 Replay。
- Replay 期间 query layout generation 抑制是一次性的，超大 delta 或分片重放可能触发额外失效。

### Concat + id 回收 → 已通过重构根除（2026-06-14，commit `b0acf38`）

`FrameDelta.Concat(FrameDelta a, FrameDelta b)` 是 15 行 `Array.Copy` 拼接，不做任何语义折叠：

```csharp
public static FrameDelta Concat(FrameDelta a, FrameDelta b)
{
    var result = new FrameDelta();
    var totalLength = a._length + b._length;
    result._buffer = new byte[totalLength];
    if (a._length > 0) Array.Copy(a._buffer, 0, result._buffer, 0, a._length);
    if (b._length > 0) Array.Copy(b._buffer, 0, result._buffer, a._length, b._length);
    result._length = totalLength;
    result._opCount = a._opCount + b._opCount;
    return result;
}
```

代码位置：`src/MiniArch/Core/FrameDelta.cs`。

**关键约束：** `ArgumentNullException`；不检查版本兼容性；不检查 buffer overflow（帧 delta 远小于 2GB）；结果 buffer 新分配（LOH）。

**历史：** 旧实现（2026-06-14 前）是 200 行折叠状态机，遇到跨帧 entity id 回收时产生 bug——折叠丢失时序依赖。当前 `Concat` 不分析语义，只 `Array.Copy` 两个 buffer——时序完整保留。

**回归测试：** `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs`：`*_concat_delta_round_trip_*` 系列。

**坑点：** Concat 不做去重；不做排序（顺序 = a 的 op 全部在前）；Concat 后的 delta 不能再 `Deserialize`（buffer 本身就是 wire format）。

### `WorldFingerprint` 模式（已删除）

早期测试用字符串 fingerprint 做状态对比，已替换为 `WorldSnapshot.Save` → SHA256（见 `kb-snapshot-persistence.md` Checksum 段）。hash 失败时只在 message 里打印 stats 做定位起点。

## FrameDelta wire format（2026-06-14 已落地）

FrameDelta 内部 `byte[] _buffer` 就是 wire format：每个 op 编码为 `[1 byte tag][varint entityId][varint entityVersion][payload...]`，无需单独序列化步骤。

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

