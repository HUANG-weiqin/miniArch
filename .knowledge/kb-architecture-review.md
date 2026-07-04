---
title: Architecture Mechanistic Review
module: MiniArch.Core
description: Mechanistic insight of the entire miniArch ECS library — one-line truths, subsystem breakdown, data flows, known issues, and design tensions. Links to per-subsystem kb pages for depth.
updated: 2026-07-03 (GetFirst<T> → GetSingleton<T>；修正 StructuralChange 陈旧的 upsert 描述)
---
# Architecture Mechanistic Review

> 这个页面只做**整体审视**：每个子系统一句话真相 + 真实问题 + 设计张力。
> 深度实现细节在各 `kb-*.md` 子模块页里，**不在这里重复**。

## 这个模块是干什么的

- 整体架构审视记录：把 miniArch 拆成"哪些子系统、各自一句话在做什么、彼此怎么连接"
- 标注当前已知问题和可疑取舍，作为改动决策的入口
- 不替代各 `kb-*.md` 页的详细实现记录——单一事实来源在那里

## 全局一句话

miniArch = 一张 entity→location 表 + 按组件集合分组的**可增长**密集 byte 存储 + 一套延迟命令录制器。
所有功能都是这三个原语的组合。

## 全局依赖图

```
ComponentRegistry.Shared (进程内全局 Type↔id)
  ↓
ComponentType (internal int wrapper) → Signature (排序 ComponentType[] + ComponentMask 512-bit)
  ↓
Archetype (单块 byte[] 或多 Segment，按列排布所有组件数据)
  ├── 内联 edge cache: _addDestinationCache / _removeDestinationCache (Archetype?[] 直索引)
  └── ChunkView (public readonly struct 视图，屏蔽单块/多 Segment 差异)
  ↓
QueryCache (archetype/chunk 快照 + 两段式失效: archetypeCount + per-archetype segment count)
  ↓
World (拆分为 7 个 partial 文件，编排一切)
  ↓
CommandStream (typed store 录制器) → FrameDelta (packed byte[] + varint op 流) → World.Replay
HierarchyTable (side-table, SoA 邻接表)
WorldSnapshot / WorldClone / WorldStateSnapshot (持久化 + 内存快照)
```

## 核心子系统

### 1. 实体身份 (Entity / Version / FreeList)
- `Entity = (id, version)` 二元组；World 维护 `_records: EntityRecord[]` 单表 + `_freeIds: RecycledEntity[]` 栈式 free-list
- 分配：free-list pop 或 `_entitySlotCount++`；销毁：`record.Version++` + 清空 location + free-list push
- 两套 id 分配 API：`AcquireEntityIdUnsafe`（lock-free，主线程路径）vs `ReserveDeferredEntity`（加锁，CommandStream / 异步 snapshot 路径）
- 代码位置：`World.EntityLifecycle.cs`

### 2. 存储 (Archetype)
- 按列排布所有组件数据，swap-remove 删除行
- 读取/写入：`Unsafe.As<byte, T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), columnOffset + row * elementSize))`
- `_componentIdToColumnIndex: int[]`：component id → 列索引 direct map
- **双存储模式**（详见 `kb-chunk-storage.md`）：
  - 非 chunked：单 `_data: byte[]` + `_entities: Entity[]`，doubling 直到 `_capacity * 2 > _segmentEntityCapacity`
  - chunked：`Segment[] _segments`，每 segment 固定容量（目标 ~2 MB/segment），单向晋升不回退
- `ChunkView`（public readonly struct）对用户屏蔽底层模式差异，`ForEachChunkParallel` 也透明
- 代码位置：`Archetype.cs`（字段/metadata）+ `Archetype.Storage.cs`（存储操作）+ `Archetype.TestHooks.cs`（`*ForTesting` 内部方法，与生产职责分离）

### 3. Archetype lookup（两层）
| Lookup | 触发场景 | 复杂度 |
|---|---|---|
| `_archetypes: Dict<Signature, Archetype>` | 创建 archetype 主路径 | O(1) 哈希 |
| `_archetypeByMask: Dict<ComponentMask, Archetype>` | Replay 路径零分配查找（仅 cache canonical mask） | O(1) 哈希 |

`_archetypeByMask` 只 cache "canonical" mask（popcount == component count，即所有 id < 512）。高 id archetype 故意不进 mask cache，避免不同 signature 在 mask 上碰撞。`IsMaskCanonical`（`World.cs:406`）做这个不变式检查。

> **历史**：曾存在第三层 `TryGetArchetype(types)` 线性扫描 fallback，用于兼容 CommandStream 早期版本未排序的 signature。CommandStream materialize 改为显式排序后该 fallback 变死代码，已于 2026-06-29 删除。

### 4. Signature & ComponentMask
- Signature：不可变排序 `ComponentType[]` + 缓存的 hash + 缓存的 512-bit mask
- `CreateNormalized` escape hatch：跳过排序/拷贝，直接持有调用方传入的数组——前置条件严苛，文档明示
- ComponentMask：8 × ulong（id 0..511），手写 8-way 分支 JIT 友好；`MaskBuilder` 是 mutable counterpart，`BitsSet` 跟踪 canonical 性
- Edge Cache：内联在 Archetype 上的 `Archetype?[] _addDestinationCache` / `_removeDestinationCache`，按 componentId 直索引

### 4b. 回滚快照池（2026-06-30 新增）
- `World._stateSnapshotPool: Stack<WorldStateSnapshot>`（替换原单 spare slot）
- `CaptureState()`：池非空时 Pop，否则 `new`；填充数据后 `_isRecycled = false` 返回给调用者
- `RestoreState(snap)`：校验 `snap._isRecycled == false`（已 recycled 则 `InvalidOperationException` fail-fast）；恢复后 `_isRecycled = true`、`Clear()`、Push 回池
- 池容量自我稳定在峰值并发使用量 → GGPO 多帧窗口（N 帧预测+乱序 restore）稳态零 GC
- 历史问题：原单 spare 设计下，连着两次 `CaptureState` 不 restore 时第二次必分配；重复 restore 同一 snapshot 静默污染 world 状态。两个问题都被池 + IsRecycled 修复
- 代码位置：`World.cs:902-1015` + `WorldStateSnapshot.cs:34-99`

### 5. 组件类型系统
- `ComponentType` = `internal readonly record struct` 包 int；用户侧只见 `<T>` 泛型
- `ComponentRegistry.Shared` 全局 copy-on-write
- `Component<T>.ComponentType` 静态字段消除热路径查找
- **跨进程约束**：`ComponentType.Value` 是进程内 int，FrameDelta wire 直接 varint 编码——跨进程使用必须双方 `ComponentRegistry` 注册顺序一致（见 `FrameDelta.AsSpan` XML doc）

### 6. Query 系统
- `QueryDescription`（`Type` 集合）→ `QueryFilter`（`ComponentType` 集合）→ `QueryCache`（archetype + chunk view 快照，`internal sealed class`）
- **两段式失效**（`Core/Query.cs:104-128`）：
  - 快路径：`World.ArchetypeCount == _lastArchetypeCount` → 不扫
  - 慢路径 A：archetype 数量变 → `Refresh`（append-only 扫新 archetype）
  - 慢路径 B：matched archetype segment count 变（chunked 增长）→ `RefreshViewsOnly`（不重做 match，只重建 ChunkView）
- `Matches`：mask 预过滤 + `Signature.Contains` fallback for id ≥ 512
- 用户层 `MiniArch.Query` 是 struct facade：`GetChunks()` 零拷贝、`ForEachChunk` / `ForEachChunkParallel`、`OrderByEntityId` + `OrderByComponent<T>` 走 `ArrayPool`
- 两类 chunk 迭代入口：
  - `ForEachChunk(ChunkAction)` / `ForEachChunkParallel(ChunkAction)`：基于 delegate，缓存 delegate 时零分配
  - `ForEachChunk<TForEach>(ref TForEach)` / `ForEachChunkParallel<TForEach>(TForEach)`：基于 `IChunkForEach` struct 接口（`src/MiniArch/Query.cs:196`），JIT 特化去虚化、零分配。`ref` 路径支持 stateful accumulator；by-value 路径供并行 worker 拷贝（不能用 `in` 因 Parallel.For lambda 不允许捕获 ref-like 参数）
- `ForEachChunkParallel` 在 chunks < threads 时自动按 entity 子区间拆分，`[ThreadStatic] t_partitions` 避免每次分配
- 代码位置：`Core/Query.cs`（`QueryCache` internal class）+ `Query.cs`（`MiniArch.Query` facade + `IChunkForEach`）+ `World.QueryCache.cs`

### 7. Hierarchy
- side-table SoA 邻接表：`_parentByChild[id]` 直索引 + `_firstChild[id]` → `_childNext[slot]` → -1 链表 + slot free list
- 子节点用 slot 而非 id 索引（一个 parent 可有多个 child）
- `AddChild` vs `AddChildRestored` 区分：前者 `removeFirst=true`，后者跳过（snapshot 恢复时旧 parent 已清）
- Destroy 子树：DFS 后序遍历（generation-counter `_destroyVisitedGen` 避免 O(n) 清零），逐个 `DestroySingle`
- 同时提供 `GetChildren`（List alloc）和 `EnumerateChildren`（零分配 struct enumerator）
- 代码位置：`HierarchyTable.cs`

### 8. CommandStream（recorder 层）
- per-component-type typed store（`ComponentStore<T>`：flat `T[]` + `Entity[]` + `byte[] kinds`）append-only 录制
- created entity 走 pending batch（per-batch 单链表 `_batchHeads` → `BatchedComponent.Next`），materialize 时稳定排序+去重达到 last-wins
- `Submit()` 直接写 typed value 到 World（零序列化）；`Snapshot()` 编译成 `FrameDelta`；`SubmitAndSnapshotAsync()` 双 buffer 池（`_spareFrozen` ↔ `_pendingFrozen`）稳态零分配
- `DeferredEntities` flag：`false`（默认）`Create()` 立即分配 real id；`true` 返回 placeholder（多 host lockstep）
- 历史曾并存 per-entity 去重的 `CommandBuffer`，2026-06-26 按 YAGNI 移除——详见 `kb-command-stream.md`

### 9. FrameDelta（wire format 层）
- 单一 `byte[] _buffer` + 时序排列的 op：`[1B tag][varint entityId][varint version][payload...]`
- 9 种 op kind（`DeltaOpKind` 枚举 0x01-0x09），unknown byte 立即 throw 提示 version mismatch（lockstep fail-fast）
- Varint codec显式拒绝 5 字节 / 32 位溢出
- `AsSpan()` 直接 `new(_buffer, 0, _length)` —— 零拷贝网络发送
- `Deserialize` 只是 `wire.ToArray()` + 一次走读计数
- 两种 entity-id 模式（placeholder vs real），wire format 相同，由 `CommandStream.DeferredEntities` flag 控制 producer 行为

### 10. World（编排者）
- 拆分为 7 个 partial 文件：
  - `World.cs`：字段 + TryGet/Get/Has + Clone + Replay + archetype lookup
  - `World.EntityLifecycle.cs`：Create/Destroy + free list + 版本管理
  - `World.SnapshotBridge.cs`：snapshot/clone 用的 internal backdoor（`Reset`、`AddChildFromSnapshot`、`SetSnapshot*`、`WriteFreeList`/`ReadFreeList`/`CopyFreeIdsFrom`、`FreeList`、`ValidateSnapshotEntitySlot`）
  - `World.Create.Generated.cs`：泛型重载 + `GetSingleton<T>`
  - `World.QueryCache.cs`：Query 缓存管理
  - `World.StructuralChange.cs`：Add/Set/Remove（strict 语义：`Add` 组件已存在时抛异常，`Set` 组件不存在时抛异常；CommandStream/Replay 同样 strict——详见 `kb-design-rationale.md` §2.9）
  - `World.Checksum.cs`：`Checksum()` / `CanonicalChecksum()` 双模式（详见 `kb-snapshot-persistence.md` Checksum 段）
- 结构变更核心：查 `EntityRecord` → 算目标签名 → edge cache → `MoveEntityCore`（带 catch rollback）+ `FinishMoveEntity` 分离，便于批量 materialize 复用

### 已删除的子系统
- **CommandBuffer**（2026-06-26）：per-entity 去重的录制器，被 CommandStream 取代。详见 `kb-command-stream.md` 的历史段落
- **DebugMetrics**（2026-06-08）：整个子系统 YAGNI 删除。删除内容：`WorldDebugMetrics` / `CommandBufferDebugMetrics` struct、`#if DEBUG` 计数器累加、`GetDebugMetrics()`/`GetDebugReport()` API、`DebugMetricsTests.cs`。替代方案：`dotnet-trace` / `EventSource` 外部采样，或 benchmark 中 `PERF_DIAG` 条件编译临时埋点

## 已知问题

> **已评估的"问题"入口**：大部分条目已被 `kb-design-rationale.md` 详细评估过，此处只保留结论和交叉引用。

### P2. Edge Cache 直索引稀疏膨胀
- `_addDestinationCache` / `_removeDestinationCache` 用 `Archetype?[]` 按 componentId 直索引
- component id 稀疏（如 max=10000 但只有 2 个组件）时数组浪费内存
- 当前游戏场景下 component id 密集，trade-off 合理

### P4. Hierarchy 作为 side-table 的表达力缺失
- 无法在 query 中按层级关系过滤
- 已评估 Parent 作为组件的方案——代价（级联销毁从 O(subtree) → O(N)、每次 AddChild 触发 archetype 迁移）远超收益
- 详见 `kb-design-rationale.md` §2.4 及 §3.4

### --- 以下为已评估并解决或拒绝的条目 ---

### P1. Query 失效粒度 — 已评估，不值得改
- 稳态 = 一次 `cmp`（`archetypeCount == _lastCount`），已是最优
- 非稳态 refresh 成本（~250ns）比 archetype 创建（~5000ns）小两个数量级
- Per-filter 跟踪用代码复杂度换噪音级 CPU 时间
- 详见 `kb-design-rationale.md` §2.8 及 §3.1

### P3. Add/Set 语义 — 已改（strict 语义）
- 原 upsert 语义（`Add`/`Set` 同义别名）已改为 strict：`Add<T>` 抛异常若组件已存在，`Set<T>` 抛异常若组件不存在
- 性能影响为零——`TryGetComponentIndex` 在旧 upsert 代码中同样需要
- 详见 `kb-core-ecs.md` 决策段 + `kb-design-rationale.md` §2.9

### P3b. CaptureState/RestoreState — 已修复
- 2026-06-30 替换为 `Stack<WorldStateSnapshot>` 池 + `IsRecycled` 标志

### P5. EnsureReplayReservation O(n) free list 扫描 — 已评估，不构成热点
- Reserve 路径在正常 replay 中不构成热点——每个实体只 Reserve 一次
- 反向 HashSet 无实测收益
- 详见 `kb-design-rationale.md` §3.6

## 可优化点

**O1-O5 已全部处理**：
- O1（Query 增量失效）、O3（swap-remove 大组件）、O5（free-list 反向索引）→ 已评估，不值得做（见 `kb-design-rationale.md` §3）
- O2（Signature 分配）→ 已有 `CachedCreateArchetype` + edge cache 解决（见 `kb-design-rationale.md` §3.7）
- O4（FrozenState 字段对调 struct 化）→ `d70c0c3` 已完成，`FrozenState` 现为 struct，`SwapOutState` 一次整体赋值

无剩余可优化点。库处于维护期。

## 设计张力

| 张力 | 选择 | 原因 |
|---|---|---|
| 全局 Registry vs World 隔离 | 全局 | 真实游戏场景正确；省一层间接 |
| 单线程写入 vs 并行读取 | 单线程写 | archetype ECS 经典约束 |
| Hierarchy 一等公民 vs 组件 | side-table | 正确性 > 表达力 |
| 大文件 vs partial 拆分 | partial 7 文件 | 按职责分组，保持编译单元聚焦 |
| 直索引 edge cache vs bounded cache | 直索引 `Archetype?[]` | O(1) 查找，简单可靠；稀疏 id 场景的代价已被承认 |
| CommandBuffer per-entity 去重 vs CommandStream typed store | CommandStream（独存） | 实测更快，去重语义无真实消费者，YAGNI |
| FrameDelta IR 折叠 vs 简单拼接 | 简单 byte 拼接 | 折叠状态机的复杂度转嫁到跨帧 id 回收 bug 上得不偿失；Array.Copy 简洁正确 |
| Submit 直接 Apply vs 统一走 Replay | 双路径 | Submit 是本地 canonical 路径无 Replay 不变式；详见 `kb-command-stream.md` |
| `WithTag<T>()` vs `With<T>()` 标签查询 | `With<T>()`（独存） | MiniArch 没有独立标签概念，零大小组件就是标签，`With<T>()` 可查询。`WithTag<T>()` 是纯冗余 API 面，YAGNI 拒绝（详见 `kb-glossary.md` "Tag" 条目） |

## 做得好的地方

- **两层 archetype lookup**：Signature dict（权威）+ Mask dict（canonical-only，零分配 Replay 路径），每层职责单一
- **两段式 query 失效**：archetypeCount 粗扫 + per-archetype segment count 细扫
- **CommandStream typed store**：替代旧 entry stream 后反超 Friflo，详见 `kb-command-stream.md` vs Friflo 段
- **FrameDelta packed byte format**：单一 buffer + varint + tag，零拷贝网络发送
- **`MoveEntityCore` + `FinishMoveEntity` 分离**：带 catch rollback，便于批量 materialize 复用
- **`[SkipLocalsInit]` / `AggressiveInlining` / `Unsafe.As<byte,T>`** 系统化性能卫生
- **Add/Set alias 文档化**、`Unsafe`/`Try` 前缀一致——命名诚实贯穿全库

## 入口

- 第一次理解整体：本文档 → 各 `kb-*.md`（按 `INDEX.md` 路标）
- 改存储：`kb-chunk-storage.md` + `Archetype.cs` / `Archetype.Storage.cs`
- 改录制/同步：`kb-command-stream.md` + `CommandStream.cs` / `FrameDelta.cs`
- 改 query：`kb-query-invalidation.md` + `kb-parallel-query.md` + `Core/Query.cs`
- 改 hierarchy：`kb-hierarchy-runtime.md` + `HierarchyTable.cs`
- 改持久化：`kb-snapshot-persistence.md` + `WorldSnapshot.cs`

## 坑点

- 见各子模块 kb 的"坑点"段；本文档不重复
- **整体最大的坑**：曾经存在的 kb 文档落后于代码演进（如本页历史版本描述已删除的 CommandBuffer），改动前如果只读 kb 会被误导——**必要时直接看代码**
