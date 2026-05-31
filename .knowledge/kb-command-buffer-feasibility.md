---
title: Command Buffer Runtime
module: MiniArch.Core CommandBuffer
description: Single-threaded per-entity-deduplicating command buffer with arena slab allocator, inline CreatedState/ExistingEntityOps, direct Submit() path, Snapshot() for cross-world replay, FrameDelta merge, SubmitAndSnapshotAsync() for parallel submit+snapshot, and Clone support
updated: 2026-05-31
---

# Command Buffer Runtime

## 这个模块是干什么的

- 提供 `CommandBuffer` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
- `CommandBuffer` 本体可以长期持有；`Submit()` 消费当前批次后自动清空记录，允许下一帧复用同一个实例，避免每帧新建 buffer
- 录制时按实体+组件类型去重（而非追加日志），消除编译步骤
- 说明 `Submit()` 的固定执行顺序：`create -> add/set/remove -> link/unlink -> destroy`
- 说明 `World.Replay(FrameDelta)` 的 batch mutation 与 query 可见性边界
- 约束并发只覆盖 recording，不把 `World` 变成并发写安全对象
- 这个模块不负责：
  - 直接把 `World` 改成并发写安全
  - 替代现有立即生效 `World.Create/Add/Set/Remove/Destroy` API
  - 在 replay 期间支持多线程 world mutation

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandBuffer.cs`：public recording API，录制时去重，`Submit()` 直接 replay，`Snapshot()` 返回 self-contained `FrameDelta`
  - `src/MiniArch/Core/ICommandRecorder.cs`：只录接口，供系统代码多态使用
  - `src/MiniArch/Core/CommandBufferEntityAllocator.cs`：线程安全地从 `World` 预留真实 entity handle
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR，可保留并重放到同步 world
  - `src/MiniArch/Core/ComponentWriterCache.cs`：per-type typed column writer/reader delegate 缓存，公开可用
  - `src/MiniArch/Core/World.cs`：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`、structural mutation
- 数据流 / 控制流：
  - 工作线程通过 `CommandBuffer` 只记录命令；`Create()` 会立刻从 world 预留真实 `Entity`
  - `Clone(source)` 在录制时校验 source 存活并立即 DFS 遍历 source subtree：为 root/children 分配 deferred entity、把组件数据快照到 CreatedState slab、把 subtree 内部 hierarchy link 写入 `_hierarchyByChild`；clone root 不继承 source parent
  - recording 完成后可选三条路径：
    - `Submit()`：直接执行到 world，清空录制缓冲，适合"录完就生效"
    - `Snapshot()`：生成自包含 `FrameDelta` 但不影响 world，可保留、merge、或跨 world replay
    - `SubmitAndSnapshotAsync()`：换出内部状态后主线程 Submit 与后台线程 BuildDelta 并行执行；适合"需要同时 submit + snapshot"的帧同步场景
- 与其他模块的交互方式：
  - 依赖 `World` 的 entity version / free-list / archetype mutation 基础设施
  - hierarchy 仍由 `HierarchyTable` side-table 持有，command buffer 只决定 replay 顺序
  - query 仍依赖 `World.QueryLayoutGeneration`，只是 replay 期间改为 batch publish
- 验证依赖 `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- 与 `Arch` 的对比 benchmark 依赖 `CommandBufferSharedScenarios` 先验证共享结构命令场景的 parity，再跑 `record + submit`

## 录制时去重模型

- 每个实体维护 O(1) 的 `ExistingEntityOps`（4 个 inline slot + overflow dict）
- 同实体同组件类型的后续操作替换而非追加：`Set` 覆盖前面的 `Set` 或 `Add`，`Remove` 覆盖前面的操作
- `CreatedState` 同样用 4 个 inline 槽位 + overflow，对 2-4 组件的典型 entity 零字典分配
- Arena slab allocator：`CopyData<T>` 写入 `ArrayPool<byte>.Shared.Rent` 租来的 slab，消除 per-command `new byte[]`

## 决策

- 旧 `CommandBuffer`（多线程 shard + Compile + FrameDelta）已移除，`FastCommandBuffer` 重命名为 `CommandBuffer` 成为唯一实现。
- 当不需要保留 frame 或跨 world replay 时，应优先用 `Submit()`。
- `Snapshot()` 用于需要跨 world 同步或延迟回放的场景。
- `SubmitAndSnapshotAsync()` 用于同时需要 submit 和 snapshot 的帧同步场景：换出 buffer 状态后，主线程 SubmitFromFrozen 与后台线程 BuildFromFrozen + DeepCopy 并行执行。返回 `Task<FrameDelta>`，调用返回时 Submit 已完成，await 只等 delta。
- `FrameDelta` 是可保留的 frame 数据；只要目标 world 和源 world 保持同步初始态与同步回放顺序，就可以顺序 replay 到另一个 world。
- 跨 world replay 时 `ResolveCompiledComponentType` 会验证 component type 对目标 world 的 registry 有效，保证跨实例安全。
- 记录期直接返回真实 `Entity`，但它只是 reserved handle：
  - fresh id 会扩展 `_versions/_locations`
  - recycled id 会先从 free-list 取出
  - 直到 `Submit()` 或 `Replay()` 前，`world.IsAlive(entity)` 仍为 `false`
- query layout generation 在 replay 期间被抑制，整批 replay 结束后只递增一次。
- 多线程 recording：每个线程有独立 `CommandBuffer` 实例，`ReserveDeferredEntity` 通过 `World._entityIdLock` 串行化（无竞争时 ~10ns 开销）。
- existing entity 的 `Destroy(Entity)` 必须保留录制时完整 `(Id, Version)`；`Submit()`、`Snapshot()`、`SubmitAndSnapshotAsync()` 都不能按 id 重新读取 world 当前 version，否则 stale handle 会误杀 recycled entity。
- `CommandBuffer.Clone(source)` 读取 source 的录制时 world 状态，不观察同一个 buffer 里之前/之后尚未 Submit 的 existing entity `Add/Set/Remove`，也不观察 Clone 调用后到 Submit 前的 world 外部变化；这样 clone 返回的 deferred entity 从录制完成后就与 `Create(...)` 一样可被 `Set/Remove/Destroy` 正常归约。
- clone 录制遍历 source children 时先走 `HierarchyTable.HasChildren(...)` leaf 快速路径；有 children 时再走 `HierarchyTable.EnumerateChildren(...)` 的 internal struct enumerator，不走公开 `GetChildren()` 的 list 快照；DFS traversal stack 使用 `ArrayPool`，支持超过常见 8-child 的 subtree 且稳态无 GC。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 对 world 的"延迟结构变更 + 录制时去重 + 直接批量提交器"，不是线程安全 world
- 这个模块里最重要的抽象是：
  - `reserved entity handle`
  - `per-entity deduplicated ops`
  - `FrameDelta`
- 常见误解：
  - 以为 `command buffer` 等于 world 从此支持多线程写
  - 以为 `Submit()` 之前 world 已经被改了；实际上真正 mutation 发生在 `Submit()` 时
  - 以为 `CommandBuffer` 只能消费一次；当前实现是"消费当前批次后清空，随后可继续复用同一个实例"
  - 以为 `Set<T>` 永远只是值更新；当前实现里组件不存在时它仍会退化成结构变更

## 入口

- 如果是第一次读这个模块，先看：
  - `src/MiniArch/Core/CommandBuffer.cs`：recording API、`Submit()`、`Snapshot()`
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR 与 `Merge` 静态方法
  - `src/MiniArch/Core/World.cs`：`ReserveDeferredEntity` / `ReleaseReservedEntity` / `Replay(FrameDelta)`
- 如果是修 bug，先看：
  - `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：submit/snapshot/merge 契约
  - `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`：existing entity 的 structural semantics 是否仍与立即生效 API 对齐
  - `tests/MiniArch.Tests/Core/QueryTests.cs`：batch replay 后 query 可见性和快照失效
- 如果是加功能，先看：
  - `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`：entity id/version/free-list/hierarchy 的底层契约
  - `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`：`record + submit` 性能入口

## 坑点

- 历史上容易出问题的地方：
  - 把 `add/set/remove` 当成完全可交换操作；existing entity 和 created entity 的归约规则并不相同
  - 忘记释放同帧 `create + destroy` 的 reserved entity，导致 id 被永远占用
  - replay 期间逐条触发 query layout 失效，导致额外开销和观察窗口不稳定
- 容易误判的地方：
  - 以为 `destroy` 放最后就足够，实际上 created entity 的录时去重同样关键
  - 以为 recording 并发问题只在字典去重；实际上 entity reservation 也需要保护
  - 以为 existing destroy 只需要存 entity id；`Entity` 身份包含 version，hierarchy skip 和 delta 输出也必须按完整 `Entity` 比较
  - 以为 `CommandBuffer.Clone(source)` 会读取 Submit 时 source 的最终状态；当前语义是录制时快照，pending source ops 不参与 clone
  - 以为 destroy cloned subtree 需要 visited 防环；hierarchy 契约是单父无环，内部只需要 traversal stack
- 改这里时要特别小心：
  - 当前 `World.Set<T>` 在组件不存在时会走"补组件 + 迁移"路径，command buffer 的 `Set` 语义必须与它兼容
  - `ReleaseReservedEntity` 会提升 version 并归还 free-list；如果漏掉 version 递增，stale handle 会重新变活
  - 跨 world replay 现在依赖"目标 world 与源 world 从同一初始态开始，并按相同 frame 顺序推进"；如果这个前提破坏，replay 会在 reservation 对齐时失败

## 优化历史

### Arena slab + 录时去重（2026-05-26）

- **Arena slab allocator**：对 MixedScript 有显著帮助（+200%），对 DenseExisting 无效
  - 原因：DenseExisting 是 World 操作为主（~90%），Record+去重优化被吞没
  - MixedScript 去重直接消灭了 5000 个 CreatedEntityState + 5000 个内部字典 + 4375 个 ToCompiledEntity 的旧 compile 开销
- **CreatedState 内联 4 字段 + overflow**：消除 10000 个 Dictionary 分配
- **ExistingEntityOps 内联 4 槽 + overflow**：per-entity 操作的字典开销降至零

### 性能对比（Release, 3s×5 repeats）

| Case | Submit (ops/s) | Arch (ops/s) | vs Arch |
|---|---|---|---:|
| 1000/CreateHeavy | 3127 | 1396 | +124% |
| 10000/CreateHeavy | 291 | 155 | +88% |
| 10000/DenseExisting | 183 | 192 | -4.7% |
| 10000/MixedScript | 219 | — | N/A |

### 最终优化组合

- 保留 `_typeInfoCache` 跨帧（不 Clear）
- `_hasCreatedEntities` flag 跳过空字典查找
- Writer 缓存到 `_typeInfoCache` 元组中（避免每次 `ConcurrentDictionary.GetOrAdd`）
- `CreatedState` inline 4 字段 + overflow 字典
- `ExistingEntityOps` inline 4 槽 + overflow 字典
- Arena `ArrayPool<byte>` slab

### SubmitAndSnapshotAsync 并行路径

核心洞察：`BuildDelta` 和 `Submit()` 对 buffer 内部状态都是只读的。换出后，同一份 frozen state 可以被两个线程同时读。

- **换出模型**：`FrozenBufferState`（private nested class）抓走 buffer 所有内部字段的引用，buffer 重置为空状态
- **并行执行**：`Task.Run` 启动后台 BuildFromFrozen；主线程同步执行 SubmitFromFrozen
- **结果所有权**：后台线程完成 `DeepCopyOwnedData` 后 slab 归还 ArrayPool，delta 完全独立
- **换出后 buffer 为空**：可立即开始下一帧录制
- **不换出的字段**：`_world`、`_allocator`（buffer 始终持有）；`_tempComponents`（后台线程自建 scratch list）
- 设计稿：`docs/plans/2026-05-26-async-snapshot-design.md`

### 性能数据（Release, 全帧 record+submit, 3s×5 repeats）

| Case | Mini Submit | Mini Async+Snp | Arch | Async vs Submit |
|---|---|---|---|---|
| 1000/CreateHeavy | 3213 | 2715 | 1381 | -15.5% |
| 10000/CreateHeavy | 308 | 260 | 153 | -15.5% |
| 10000/DenseExisting | 220 | 231 | 193 | +4.8% |
| 10000/MixedScript | 246 | 205 | 183 | -16.6% |

关键结论：Async 路径（含 snapshot）在所有场景下相比纯 Submit（不含 snapshot）开销约 15-16%。DenseExisting 场景下反而略快（swap-out 省了 Clear 开销）。

## FrameDelta

### 自包含所有权模型

`Snapshot()` 每次返回一个**新的、自包含的** `FrameDelta` 实例：

- **byte[] 独立**：`DeepCopyOwnedData()` 将所有组件数据合并到新分配的 owned `byte[]`
- **实例独立**：每次 `Snapshot()` 创建新 `FrameDelta` 对象
- **Merge 安全**：`Merge(a, b)` 的结果引用源 delta 的 owned byte 数组
- 代价：每次 `Snapshot()` 分配一个 byte[] + 一个 FrameDelta（含 9 个 List）

### DeltaCount / HasEntity

- `DeltaCount`（int）：返回所有 command 列表的总条目数（含 ReservedEntities + CreatedEntities 等）
- `HasEntity(Entity)`：检查实体是否出现在任意 command 列表中

### static FrameDelta.Merge(FrameDelta a, FrameDelta b)

纯函数，合并两个 delta 并返回新的 `FrameDelta`，不修改输入：

- 先处理 `a` 的命令，再处理 `b` 的（时序正确）
- 内部对 per-entity per-component-type 做状态机折叠
- **Component 折叠规则**：Add+Set→Add(latest), Add+Remove→cancel, Set+Set→Set(latest), Set+Remove→Remove, Remove+Add→Set, Remove+Set→Set
- **Entity 生命周期折叠**：Create+Destroy→Release, Reserve+Release→cancel
- **Created entity 内联折叠**：后续 Add/Set/Remove 直接折入 `RawCreatedEntity.Components`

验证入口：`CommandBufferTests.cs` 中 `StaticMerge_*` 和 `MergeOfMerge_*` 系列测试。

### 已知限制

- **Link+Unlink 不互消**：同一 child 的 Link+Unlink 不做取消，因为原始 link 状态需要 World 上下文才能确定

## 关联模块

- `kb-core-ecs.md`：当前 world / archetype / chunk / query 的运行时边界
- `kb-test-workflow.md`：后续该怎么补行为测试和 benchmark
- `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：command buffer 专属行为网
- `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`：实体生命周期、id/version、chunk 顺序
- `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`：`Add/Set/Remove` 当前契约
- `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`：record/submit 性能入口
- `benchmarks/MiniArch.Benchmarks/CommandBufferSharedScenarios.cs`：共享结构命令 benchmark 场景与 parity helper
