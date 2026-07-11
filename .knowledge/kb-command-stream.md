---
title: Command Stream Runtime
module: MiniArch.Core CommandStream
description: CommandStream/ParallelCommandStream typed-store append-only recorder, compatible with FrameDelta. The per-entity deduplicating CommandBuffer was removed (YAGNI) — CommandStream is now the sole recorder.
updated: 2026-07-11
---
# Command Stream Runtime

## 这个模块是干什么的

- 提供录制层，把结构变化和 hierarchy 变化先记成延迟命令
- **两个公开类型**：
  - `CommandStream`（单线程默认）：所有 mutator 直接调用，无锁、无非虚拟化开销
  - `ParallelCommandStream`：所有 mutator 在 `_storeCreateLock` 内，可多线程并发录制
- 共享层是 `public abstract class CommandStreamCore`，承载所有 emit/submit/snapshot/replay/async/ComponentStore/FrozenState 等共享逻辑
- append-only：`Add/Set/Remove` 按组件类型分片记录 typed value，不做录制期去重；同帧冲突命令的净效果由调用方负责
- `Submit()` 消费当前批次后自动清空，允许下一帧复用同一实例
- **帧同步端到端指南** → 见 `kb-lockstep-playbook.md`
- **DeferredEntities flag（placeholder vs real-id 模式）** → 见 `kb-deferred-create-design.md`
- 历史：曾并存 `CommandBuffer`（per-entity 录制期去重的安全默认）。2026-06-26 按 YAGNI 移除。2026-07-05 进一步把单线程/并行两套路径从同一 sealed class + `_parallelMode` flag 拆分为两个独立 sealed 类型。
- **M2 pre-validation 优化（2026-07-09）**：在 `Submit()` 的 materialize 前预验证 pending slot reserved 状态。增加 epoch guard 优化：`World.ReservedReleaseEpoch`（每次 release 递增）+ `CommandStreamCore._submitEpoch`（记录上次同步时的 epoch），`PreValidatePendingSlots()` 快速路径在 epoch 相等时直接返回，避免 O(N) pending-batch 扫描。Epoch 在 CreateImpl、CancelPendingEntity、Clear、Submit/SubmitFromFrozen 操作后同步。HeroComing.Perf: Movement 1902.1, Attack 1125.6，内存稳定。详见 `kb-code-review-findings.md` #1。

- **`Submit()` 增加 pre-validation 检查 pending slot reserved 状态**（2026-07-09，M2）：在 `Submit()` 和 `SubmitFromFrozen()` 的 materialize 前增加 `PreValidatePendingSlots()`，扫描所有非 cancelled pending batch 的 slot 是否仍为 reserved 状态（`!record.IsOccupied && record.Version == entity.Version`）。若 slot 已不再是 reserved，则立即抛 `InvalidOperationException`，防止部分 materialize 后因 slot 状态不一致导致无事务回滚。新增内部 helper `World.IsSlotReserved(Entity)`。零分配、零 public API 变更。

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandStreamCore.cs`：`public abstract` base，所有共享字段、emit/submit/snapshot/replay 逻辑、ComponentStore/FrozenState/HierarchyIntent 等嵌套类型
  - `src/MiniArch/Core/CommandStream.cs`：`public sealed`，单线程默认。9 个 mutator 是子类自己的 `public` 非虚拟方法
  - `src/MiniArch/Core/ParallelCommandStream.cs`：`public sealed`，并行实现。9 个 mutator 是子类自己的 `public` 非虚拟方法，按需用 lock 包裹共享 helper
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR，可保留并重放到同步 world
  - `src/MiniArch/Core/World.cs`（+ partial 文件）：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`
- 数据流 / 控制流：
  - 工作线程通过具体子类只记录命令；`Create()` 在 `DeferredEntities=false`（默认）时立刻从 world 预留真实 `Entity`，`DeferredEntities=true` 时返回 placeholder `Entity(-1, seq)`
  - recording 完成后可选：`Submit()`（直接执行到 world）→ `Snapshot()`（生成自包含 `FrameDelta`）→ `SubmitAndSnapshotAsync()`（并行执行 Submit + BuildDelta）
   - `Clone()`：深拷贝 source 实体及子树到 pending batch，record-time 快照语义。支持 materialized source（archetype 组件 + component store overlay）和 pending source（batch 链表虚拟状态合并）。用于 snapshot/replay 场景
  - `Add/Set` 进入按组件类型分组的 typed store；`Remove/Destroy` 进入 structural log；created entity 的组件录制期分流到 created side table（per-batch 单链表），提交时一次性 materialize；`Snapshot()` 从 typed stores + side table/log 生成 `FrameDelta`

## 关键约束

- **9 个 mutator（Create/Track/Add/Set/Remove/Destroy/AddChild/RemoveChild/Clone）不在 `CommandStreamCore` 上公开**。base 只暴露消费/编译 API（`Submit`/`Snapshot`/`Replay` 等）和 `protected` helper（`CreateCore`/`AddChildCore` 等）。
- **调用方契约**：录制期必须持有 `CommandStream` 或 `ParallelCommandStream`（sealed 子类）引用；如果只持有 `CommandStreamCore`，编译期就看不到 mutator。
- `CommandStreamCore` 内部递归路径（如 `CloneChildrenRecursive`）必须直接调 `*Core` helper（`CreateCore`/`AddChildCore`），不能假设存在 base public mutator。
- `Create()` 在 `DeferredEntities=false` 时使用 `World.ReserveDeferredEntityUnsafe()` 分配 real id；`DeferredEntities=true` 时返回 placeholder，不碰 World id allocator。
- 组件数据按 typed value 记录，`Submit()` 直接写 typed value，`Snapshot()` 再转成 `FrameDelta` 所需 raw bytes。
- component `Add/Set` 按类型批处理，不承诺与 `Remove/Destroy` 的严格全局追加顺序；同帧冲突命令的净效果由调用方负责。
- **Pending entity 最终状态契约**：通过 `Create()`/`Clone()` 创建的 pending entity，其 batch 内所有 `Add`/`Set`/`Remove` 操作在 `Submit()`/`Snapshot()`/`Replay()` 前**折叠为最终 materialized state**。这意味着中间操作不会产生独立的 `ChangeWatch.Diff` value change 或 `TransitionWatch.Diff` membership transition——实体被直接以最终组件签名的形态创建。具体表现：
  - `ChangeWatch.Diff` 在 Submit/Snapshot/Replay 后不包含 pending entity 的中间 Set 快照（Watch 不注册 world；但 `Diff` 扫描时当前值是其最终 materialized 状态，与 baseline 对比的差异只反映最终值与 snapshot 的差异）。
  - `TransitionWatch.Diff` 只反映实体的最终 filter 匹配状态，不反映 batch 内的中间结构变化（如 Add 后马上 Remove → 实体从未进入 filter）。
  - 此行为适用于所有消费路径：`Submit()`（单机）、`Snapshot()` + `Replay()`（跨 world）、`SubmitAndSnapshotAsync()`。
  - 对 existing entity 的 `Add`/`Set`/`Remove`（通过 component store 路径）不受此影响——existing entity 的最终当前值会被 `ChangeWatch.Diff` 在扫描时与 baseline 对比，结构成员变化会进入 `TransitionWatch.Diff`。
- created entity 组件在 record 时分流，避免提交时 O(created × commands) 扫描整条日志。
- created materialize 使用小型 archetype cache，避免每个 spawn 反复分配 signature/type array。
- `SubmitAndSnapshotAsync` 使用 FrozenState 双缓冲池（`_spareFrozen`/`_pendingFrozen`），稳态零分配。
- **CreateMany v2 bulk materialize（2026-07-11）**：`CreateManyCore` 录制完 per-entity pending batch 后，追加一个 `CreateManyGroup`（StartBatch/Count/ComponentCount + C1..C8 inline ComponentType）到 `FrozenState.CreateManyGroups`。Submit 的 `MaterializePendingBatches` 共享 helper 识别 group：precondition 满足时（chain 逆序匹配 group 类型、无 Removed、组件类型唯一且 id < 512）批量 `AllocateRows(liveCount)` + 预算 column index + pin BatchBuf 紧凑写入；否则 fallback 到 per-entity `MaterializePending`。Cancelled batch 跳过。Snapshot/Replay 不变（仍从 pending batch emit）。World 新增 `MaterializeReservedEntityAt(entity, archetype, row)` 处理已分配 row 的 entity 放置。Short benchmark: 30k×8c Submit `CreateMany` 9.41 ms / 17.68 MB vs per-entity 26.94 ms / 20.02 MB（约 2.9x）。重复组件类型必须 fallback，否则会破坏 pending batch 的 last-wins 语义；回归见 `BUG_CreateMany_duplicate_component_types_use_last_write`。

### Clone 虚拟状态约束

- **Destroy 边界**：同批次 Destroy(source) 后 Clone(source) 抛 `InvalidOperationException`（source 在 `DestroyEntities[]` 或 pending batch 已 cancel）；Clone(source) 后 Destroy(source) 不影响已产生的 clone（clone 已作为标准 pending 命令写入 batch）
- **ParallelCommandStream 限制**：不支持 pending source 的 Clone（抛 `NotSupportedException`）；materialized 路径只读 archetype storage（不扫 component store overlay），children 从 world hierarchy 读取（无虚拟视图）。虚拟状态语义只在单线程 `CommandStream` 生效

## 决策

### 为什么用 `new` 而不是 `override`（关键性能决策）

**最初实现**用 `public abstract` + `public override`，结果在 HeroComing.Perf 上 regression ~10%（Movement 1917→1737，Attack 1205→1063）。

**根因**：.NET 8 JIT **不**对 generic virtual 方法（`Add<T>`/`Set<T>`/`Remove<T>`）做可靠的 devirtualize，即使 receiver 静态类型是 sealed 子类。每次调用都付虚表查证 + 阻止 inline。详见 `kb-code-review-findings.md` 的 CS9。

**修复**：base class 不再公开这些 mutator，只保留 `protected *Core` helper；两个 sealed 子类直接公开自己的非虚拟 mutator 并调用 helper。JIT 视为 direct call，可以 inline。修复后性能恢复或小幅超过原版（同机器 baseline 1917/1205，修复后 2065/1300；后续多次测量有 ±2% 波动，故不承诺固定增益）。

**副作用**：调用方如果把录制器类型擦成 `CommandStreamCore`，编译期就无法调用 `Add<T>`/`Create` 等 mutator。这是有意的 API 边界，而不是缺失重载。

### 为什么两个 sealed 子类而不是 `_parallelMode` flag

原版单 sealed class + `_parallelMode` volatile bool：每个 mutator 都 `if (_parallelMode)` 分支判断，CPU 分支预测器要 track，且语义"录制中途切模式"使代码路径复杂。

拆分后：
- 单线程路径无锁、无分支判断、可 inline
- 并行路径无 `if` 判断、直接进 lock
- 调用方在构造时选类型，模式不可运行时切换（YAGNI——实际无消费者需要中途切换）

### 并行 placeholder 录制仍是双锁（待优化）

`ParallelCommandStream.Add<T>(placeholder, ...)` 当前路径：
1. `CanRecordParallelComponentCommand(entity)`：进锁检查 `TryGetPendingBatch`
2. `GetOrCreateStoreParallel<T>().AppendConcurrent(...)`：内部 ThreadLocal 写

placeholder 每次 Add 双锁，是已知优化点。**未做**——`seq` 线程唯一的属性可让 pending-batch 用 Interlocked 实现真正无锁写入。详见本页"待优化"段。

## 待优化

- **placeholder 并行写入优化**（v2，未实施）：让 `ParallelCommandStream.Add/Set/Remove` 在 placeholder 路径下走 pending-batch（与单线程一致），用 Interlocked 处理全局 counter。预期每 placeholder Add 节省 ~90ns。前提：ECS 天然保证同一 entity 不被多线程并发写。

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
- `Clone()` 虚拟状态快照：materialized source 读取 archetype 组件 + component store overlay（pending Add/Set/Remove 合并，last-wins）；pending source 读取 batch 链表（bit-dedup last-wins）。Clone 后对 source 的任何后续操作不影响已产生的 clone 副本

- **GetVirtualChildren 不检查 intent.Parent 的原因**：`RemoveChildCore` 将 `HierarchyByChild[child]` 设为 `new HierarchyIntent(false, default(Entity))`，即 `IsAdd=false, Parent=default`。由于 Parent 为 `default(Entity)`，对任一 parent 查询时 `intent.Parent == parent` 都不会成立。因此 `GetVirtualChildren` 处理 RemoveChild 时直接遍历 `!IsAdd` 条目并从 children 列表中移除，不检查 Parent——否则 RemoveChild 在虚拟视图里会永远不生效，变成空操作。
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
- `Clone()` 是完整深拷贝，包含所有 slab 数据，大 buffer 克隆成本高。注意虚拟状态快照语义：
  - **组件虚拟状态**：materialized source 从 archetype 读取 base 组件后扫描所有 component store 合并 overlay（`ForEachEntityEntry`，KindRemove 删类型 / KindAdd+KindSet 覆盖或新增）；pending source 直接从 batch 链表提取 effective state（bit-dedup last-wins，跳过 Removed 标记）
  - **虚拟 hierarchy**：`GetVirtualChildren(parent)` = world children + pending AddChild（`intent.IsAdd && intent.Parent == parent`）- pending RemoveChild（`!intent.IsAdd` 不检查 Parent，因为 `RemoveChildCore` 存 `default(Entity)`）
  - **防环检测**：`CloneChildrenRecursive` 遍历时用 visited 集线性扫描，检测到虚拟 hierarchy 环则抛 `InvalidOperationException`
- 同批次 Destroy(source) 后 Clone(source) 抛错；Clone(source) 后 Destroy(source) 不影响 clone
- **pending source Clone 复制 batch buffer 时不能跨 `ReserveBatchBufSpace` 持有旧 `BatchBuf` 引用**（2026-07-11 修复）：`ReserveBatchBufSpace` 可能 `Array.Resize(ref _frozen.BatchBuf, ...)` 替换数组，`CopyComponentsFromBatch` 必须在 reserve 后重新读取 `_frozen.BatchBuf`。回归测试：`BUG_pending_clone_copies_from_resized_batch_buffer`。
- `CommandStream` 不做 per-entity/per-component 去重；重复命令的净效果由调用方负责。
- ~~**混合 World.Create（排序）与 CommandStream（未排序）可能产生重复 archetype**~~ **（已修复，保留作历史）**：早期 CommandStream 用 `Signature.CreateNormalized` 按 ADD 顺序存储不排序，与 `World.Create<T>` 的排序构造函数产生不同 Signature key。**当前 CommandStream materialize 两条路径都已排序**：统一通过 `SortAndDeduplicateComponents`（`CommandStreamCore.cs:1892`）排序去重。两者产出的 Signature 与 `World.Create<T>` 完全一致，不再有 archetype 重复。曾经的 fallback `World.TryGetArchetype` 已于 2026-06-29 作为死代码删除。
- **CommandStream 支持对 pending entity 调用 Remove**（2026-06-10 修复）：同批次内通过 `Create()` 或 `Clone()` 创建的实体处于 pending 状态。对其调用 `Remove<T>()` 现在通过 `BatchedComponent.Removed` 标记正确移除组件。`CommandStreamTests.Remove_on_pending_clone_removes_component` 验证。
- **CommandStream 支持创建零组件的空实体**（2026-06-10 修复，2026-07-03 重构为 `MaterializeEmptyReservedEntity`）：`Create()` 后未调用任何 `Add<T>()` 的实体通过 `World.MaterializeEmptyReservedEntity(entity)` 正确 materialize 为无组件实体。`CommandStreamTests.CrossWorld_replay_create_empty_entity` 验证。
- **CommandStream 同批次中 Destroy 后不再影响其他操作**（2026-06-10 修复）：`ApplyAllEntries` 改为两遍处理（pass 1: Create/Add/Set/Remove，pass 2: Destroy），"先 ApplyOps 再 Destroy"顺序。同批次内先 Destroy 后 Add/Set 同一实体现在安全（Add/Set 先执行，然后 Destroy 销毁实体）。对 pending entity，`CancelPendingEntity` 后的条目在 materialize 时通过 `_pendingBatch[id] < 0` 检测跳过。`CommandStreamTests.Fuzz_200_frames_submit_and_verify_entity_count_stability` 不再需要 `destroyedThisFrame` 过滤。
- **CommandStream pending batch 组件归属使用 per-batch 链表**（2026-06-11 修复）：旧实现用 `_batchCompCounts` 前缀和 + 扁平 `_batchComps` 数组，隐式假设每个 batch 的组件在数组中连续。但录制 API 不保证此顺序（例如 `Create(A), Create(B), Add(B,V), Add(A,P)` 时组件按 B→A 顺序写入数组，前缀扫描会将 V 误归给 A）。修复为 per-batch 单链表（`_batchHeads[]` 指向每个 batch 的链表头，`BatchedComponent.Next` 链接节点），彻底消除组件归属错误。`CommitBatchComponent` 不做 last-wins 遍历（O(1) prepend），materialize 时稳定排序+去重达到 last-wins 语义。详见测试 `Interleaved_pending_creates_get_correct_components`、`Remove_pending_component_then_create_another_entity` 等。

- **Submit vs Replay 命令顺序对齐**：`World.ReplayCore`（`World.cs:696`）是 `while (decoder.MoveNext()) switch`，**按字节流时序处理**——所谓"canonical 顺序（Reserve→Release→Create→AddChild→RemoveChild→Add→Set→Remove→Destroy）"实际是 `BuildDelta` 分段写入 buffer 的产物，不是 ReplayCore 硬编码。Submit 与 BuildDelta 顺序**完全对齐**（Create→Hierarchy→Ops→Destroy），所有命令组合（含 AddChild+Set、AddChild+Destroy、RemoveChild+Set 等同帧混合）下 Submit 与 Replay 都收敛：
  - CommandStream 的 `ComponentStore<T>.ApplyToWorld` 与 `.EmitToDelta` 都按相同 `_kinds` 数组顺序遍历，Submit 与 Replay 行为一致。
  - 验证：`Submit_on_source_equals_Replay_on_replica_for_safe_patterns` (`FrameDeltaDeterminismTests.cs:55`) 用 `BuildComplexScenario` 覆盖多样命令组合；`Submit_link_and_set_on_same_child_same_frame_converges_with_replay` / `Submit_link_parent_then_destroy_parent_same_frame_converges_with_replay` / `Submit_unlink_then_set_same_frame_converges_with_replay` 等针对性测试（`FrameDeltaDeterminismTests.cs:592` 起）覆盖所有同帧组合。
- **Cancelled pending create 的单遍 emit 约束**（2026-06-30 修复）：`EmitPendingEntitiesToDelta` 必须保留每个 batch `Reserve + Release/Create` 的单遍顺序，不能退回“先所有 Reserve、再 Release/Create”。原因是同帧取消的 reserved id 可能被后续 `Create()` 复用，Replay 端必须先看到旧 id 的 `Release` 才能预定复用 id。副作用是 `Release` 会污染 free list，后续 fresh `Reserve(Entity(slotCount), v1)` 不能走普通 `ReserveDeferredEntity()`（它会先 pop free list），而应只在 `id == _entitySlotCount && version == 1` 时直接创建 fresh slot；`id > _entitySlotCount` 仍表示 replay 历史分叉，必须抛错。回归：`Pending_cancel_after_later_create_does_not_diverge_replay_allocator`、短 seed sweep `0..5000/65535/999999/int.MaxValue`、长程 seed `42`。
- **Component command 对 stale entity 必须在“录制期 + 消费前”双层过滤**（2026-06-30 立规矩，2026-07-06 回归修复）：两类 stale 都要防：① 录制当下已 stale 的 existing entity；② 录制时 alive，但在 `Submit`/`Snapshot`/`SubmitAndSnapshotAsync` 前被 direct world 改动变 stale 的 existing entity。若不处理，`Submit()` 会按 `Id` 命中已复用的新实体并误改数据，而 `Replay()` 会在 `RequireLocation` 上按旧 version 抛错，导致消费链分叉。当前规则：record 阶段仅允许 pending entity 或 `world.IsAlive(entity)` 的 existing entity 进入 component store；consume 前再统一 prune 掉“录制后才变 stale”的 existing component commands。回归：`BUG_stale_existing_entity_set_is_skipped_so_submit_matches_replay`、`BUG_existing_entity_that_becomes_stale_before_consume_is_skipped_so_submit_matches_replay`、`Parallel_recording_skips_stale_existing_entity_component_commands`、`Parallel_recording_keeps_pending_create_component_commands`、`SubmitAndSnapshotAsync_skips_existing_entity_commands_that_become_stale_before_consume`。

- **`ComponentStore.EmitToDelta` 删除冗余 `_kinds[i]` 二次检查**（2026-07-02）：`switch (_kinds[i]) { case KindAdd: case KindSet: ... if (_kinds[i] == KindAdd) ... }` 中，switch 已匹配 KindAdd/KindSet，内部又读一次 `_kinds[i]` 来区分两者。拆分为独立 `case KindAdd:` / `case KindSet:`，消除一次数组 read + 分支。Attack +0.8%，Movement 噪声。注意：拆分后 `case KindAdd` 和 `case KindSet` 的 `fixed` 块内不能共享 `ptr` 变量，各自独立声明。

- **`Submit()` 成功路径清理不再 `TryReleaseReserved`**（2026-07-02）：`Submit()` 成功后，所有未取消 pending entity 已被 `MaterializeAllPending` 变成 alive，`Clear()` 中的 `TryReleaseReserved(entity)` 必然返回 false，但仍会读 `_records` 并做版本/占用判断。改为 `Clear(releaseReserved: !submitted)`：成功路径只清 `_frozen.PendingBatch[id]`；异常/显式 `Clear()` 路径仍释放 reserved id。语义边界：只有 `ApplyDestroys()` 结束后才设置 `submitted=true`。

- **`MaterializePending` 依赖 caller 已跳过 cancelled batch**（2026-07-02）：两个调用点（`MaterializeAllPending`、`SubmitFromFrozen`）都在调用前判断 `BatchCanceled[i]`，`MaterializePending` 内部二次 cancelled/bounds 检查是冗余热路径分支，已删除。若未来新增调用点，必须保持“只传非 cancelled batch”的前置条件。

- **`MaterializeFromBatchBuffer` 合并 `HasBit` + `SetBit`**（2026-07-02）：小 id 组件去重原先对同一个 component id 先跑一遍 8-lane branch ladder 做 `HasBit`，未命中再跑一遍 ladder 做 `SetBit`。改为 `TrySetBit`，一次定位 lane 完成 test-and-set。对 create-heavy batch materialize 路径最直接。

- **`EnsureCapacity` 在 `Archetype.AddEntity` 中的副作用已消除**（2026-07-02 发现，2026-07-05 修复）：旧 `AddEntity` 非 chunked 路径调用 `EnsureCapacity(_count + 1)`，该方法在 `_capacity * 2 > _segmentCapacity` 时会 `ConvertToChunked()`，将 `_entities = null!` 并设置模式切换。旧代码在 `EnsureCapacity` 之后用 `if (!_isChunked)` 守卫，看似"冗余"的 `else` 分支实际是 conversion 后的安全 fallback。**修复**：`AddEntity` 重构为 `AllocateRows(1) + WriteEntityAt`，每个方法各自单次读 `IsChunked`（现为 `_segments is not null` 派生属性），EnsureCapacity 的模式切换副作用被封装在 AllocateRows 内部，调用方不再需要重检。**教训保留：在有副作用的调用之后，不能假设类型状态不变。**

- **Hero perf CommandStream record/submit 微优化（2026-07-05）**：只改 `src/MiniArch/Core/`，未改 HeroPipeline 业务逻辑。保留项：
  - `CommandStream.Set<T>` 单线程路径改为先走 `_world.IsAlive(entity)`，alive existing entity 直接 append；pending/reserved entity 不是 alive，仍落到 pending-batch fallback。收益主要来自 mixed frame 中 existing Set 跳过 `TryGetPendingBatch`。
  - `GetOrCreateStore<T>()` 增加 2-slot LRU cache（按 component type id），命中时跳过 `_frozen.Stores` 数组访问/resize/null 检查；`SwapOutState()` 重置 cache，避免 async frozen state 写错对象。`existing-set` proxy 从约 10.4k ticks/s 提升到约 12.8k ticks/s（单机单轮，噪声存在）。
  - 增加 `_hasStoreCommands` / `_hasParallelStoreWrites` dirty flags：无 component-store 命令时 `Submit()` 不再为 `SealParallelStores()` / `HasAnyCommands()` 扫整张 `Stores`；parallel 写入仍通过 `_hasParallelStoreWrites` 强制 seal。
  - `ComponentStore<T>.ApplyToWorld` 将 `Component<T>.ComponentType` hoist 到循环外，避免依赖 JIT 对 generic static 的 CSE。
  - `World.GetRecordFast(entity)` 用于 `ApplyToWorld`：record 阶段 `Set/Add/Remove` 已做 alive validation，submit 阶段可跳过重复 bounds/version/occupied 检查，只做 direct `_records[entity.Id]` 读取；`record.Archetype is null` 仍作为防御性 skip。`existing-set` proxy 约 12.55k → 13.05k（Hero 噪声内）。
  - `Archetype.SetComponentAtFlat<T>(byteOffset, row, value)` + `GetColumnByteOffset()`：`ApplyToWorld` 在 archetype cache miss 时缓存 column byte offset 和 `IsChunked`，非 chunked 热路径每条 Set 不再重复跑 `IsChunked` 分支、`_columnByteOffsets[columnIndex]`/`_elementSizes[columnIndex]` 数组访问和 runtime `row * elementSize`。`existing-set` proxy 约 13.05k → 15.5k~16.3k。
  - `ComponentStore<T>` 增加 `_allSetKind`，当 store 全是 `KindSet` 时走 Set-only fast path：跳过每条 entry 的 `Kind` 分支和 Add/Remove cache invalidation；与 `SetComponentAtFlat` 累计后 `existing-set` proxy 约 17.6k~18.0k。
  - 最终 fresh 验证：`dotnet test -c Release` 627 + 5 全通过；`HeroComing.Perf --check-baseline` 通过（Movement 2003.5 r/s, Attack 1253.9 r/s, memory OK）。
  - 追加验证（SetComponentAtFlat + Set-only 后）：`dotnet test -c Release` 674 + 5 全通过；`HeroComing.Perf --check-baseline` 单独运行通过（Movement 2104.5 r/s, Attack 1268.1 r/s, memory OK）。
  - 已否定/回退：2-slot cache 的 no-promotion 变体。它对 A/B 交替可能少写 cache 字段，但对 A/B/C 混合局部性不如 LRU；现有数据不能证明收益，保留 LRU。
  - 已否定/回退：last-entity `IsAlive` cache。对严格 `Set<Q>(e); Set<R>(e)` 可能有用，但 `existing-set` proxy 每次不同 entity，额外比较/写 cache 导致约 -17% 回归。
  - 已否定/回退：默认 `Add/Set/Remove` 直接跳过 `IsAlive`。即使 submit 路径可在 `GetRecordFast` 增加 version guard 跳过 stale/recycled entity，`Snapshot()`/`EmitToDelta()` 没有 `World` 参数，无法过滤 stale command；安全折中（record id-range + apply version guard）性能也未优于现状。

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

`EnsureReplayReservation`（World.EntityLifecycle.cs:533）假设 target 的 free-id 栈和 source 同步。**不能从 delta 序列中间开始 replay**——target 的 id allocator 必须被同样的历史驱动到同样位置。否则要么抛 out-of-sync，要么静默分配到错误的 id。

实践含义：
- **存档/回放**：从空 world 开始，按帧顺序 replay 全部 delta。可工作。
- **Lockstep/rollback**：所有 peer 必须从相同的初始快照开始，按相同顺序应用相同 delta。可工作。
- **断点续传 / 增量同步**：当前不支持。需要 id remap 或显式 checkpoint 机制。

### 帧同步已实现和尚缺的部分

1. ~~**序列化**~~ → 已完成。`delta.AsSpan()` + `FrameDelta.FromWire(ReadOnlySpan<byte>)` 开箱即用；热路径可用 `reusableDelta.Deserialize(wire)` 实例方法稳态零 GC。
2. **State checksum**：无需新 API。`WorldSnapshot.Save` 输出的字节流可以直接喂给 SHA256/XXHash64 做决定性 hash——前提是两个 world 走过相同的 delta 历史（lockstep 标准用法）。测试中 `HashWorld(World)` 已实现此模式（`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs`）。**注意**：Save 的字节依赖 archetype 创建顺序和 swap-remove 历史，因此只在"同 delta 序列"场景下稳定；不做"逻辑等价但路径不同"的规范化（YAGNI）。
3. **Divergent peer resync**：`EnsureReplayReservation` 抛异常而非尝试对齐。对抗性 netcode 需要 snapshot diff + 重放补救。

### 次要问题

- **Submit 与 Replay 命令顺序不一致**（已记录在坑点）：纯 lockstep 模式（只用 Replay）不受影响，但禁止同帧内某些 peer 用 Submit、另一些用 Replay。
- Replay 期间 query layout generation 抑制是一次性的，超大 delta 或分片重放可能触发额外失效。

### `WorldFingerprint` 模式（已删除）

早期测试用字符串 fingerprint 做状态对比，已替换为 `WorldSnapshot.Save` → SHA256（见 `kb-snapshot-persistence.md` Checksum 段）。hash 失败时只在 message 里打印 stats 做定位起点。

## FrameDelta wire format（2026-06-14 已落地）

FrameDelta 内部 `byte[] _buffer` 就是 wire format：每个 op 编码为 `[1 byte tag][varint entityId][varint entityVersion][payload...]`，无需单独序列化步骤。

**开箱即用的网络 API：**
```csharp
// 发
var wire = delta.AsSpan();           // ReadOnlySpan<byte>
socket.Send(wire);

// 收（一次性分配）
var received = socket.Receive();
var delta = FrameDelta.FromWire(received);
world.Replay(delta);

// 收（热路径，复用实例零 GC）
var reusable = new FrameDelta();
// ... loop on receive ...
reusable.Deserialize(received);
world.Replay(reusable);
```

**约束：**
- lockstep 先决条件（两边 state 一致）满足时可直接收发
- `ComponentType.Value` 是进程内 int，跨进程使用需要两边 `ComponentRegistry` 注册顺序一致
- 跨进程场景可加 type mapping header（约 2-3 字节）

