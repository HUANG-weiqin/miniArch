---
title: Architecture Mechanistic Review
module: MiniArch.Core
description: Mechanistic insight of the entire miniArch ECS library — one-line truths, subsystem breakdown, data flows, known issues, and design tensions. Links to per-subsystem kb pages for depth.
updated: 2026-06-29 (全文重写: 删除已移除的 CommandBuffer 描述, 同步 FrameDelta byte[]+varint 重写, 补充两层 archetype lookup 与两段式 query 失效; 同日删死代码 TryGetArchetype)
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
Query (archetype/chunk 快照 + 两段式失效: archetypeCount + per-archetype segment count)
  ↓
World (拆分为 5 个 partial 文件，编排一切)
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

`_archetypeByMask` 只 cache "canonical" mask（popcount == component count，即所有 id < 512）。高 id archetype 故意不进 mask cache，避免不同 signature 在 mask 上碰撞。`IsMaskCanonical`（`World.cs:413`）做这个不变式检查。

> **历史**：曾存在第三层 `TryGetArchetype(types)` 线性扫描 fallback，用于兼容 CommandStream 早期版本未排序的 signature。CommandStream materialize 改为显式排序后该 fallback 变死代码，已于 2026-06-29 删除。

### 4. Signature & ComponentMask
- Signature：不可变排序 `ComponentType[]` + 缓存的 hash + 缓存的 512-bit mask
- `CreateNormalized` escape hatch：跳过排序/拷贝，直接持有调用方传入的数组——前置条件严苛，文档明示
- ComponentMask：8 × ulong（id 0..511），手写 8-way 分支 JIT 友好；`MaskBuilder` 是 mutable counterpart，`BitsSet` 跟踪 canonical 性
- Edge Cache：内联在 Archetype 上的 `Archetype?[] _addDestinationCache` / `_removeDestinationCache`，按 componentId 直索引

### 5. 组件类型系统
- `ComponentType` = `internal readonly record struct` 包 int；用户侧只见 `<T>` 泛型
- `ComponentRegistry.Shared` 全局 copy-on-write
- `Component<T>.ComponentType` 静态字段消除热路径查找
- **跨进程约束**：`ComponentType.Value` 是进程内 int，FrameDelta wire 直接 varint 编码——跨进程使用必须双方 `ComponentRegistry` 注册顺序一致（见 `FrameDelta.AsSpan` XML doc）

### 6. Query 系统
- `QueryDescription`（`Type` 集合）→ `QueryFilter`（`ComponentType` 集合）→ `Query`（archetype + chunk view 快照）
- **两段式失效**（`Core/Query.cs:104-128`）：
  - 快路径：`World.ArchetypeCount == _lastArchetypeCount` → 不扫
  - 慢路径 A：archetype 数量变 → `Refresh`（append-only 扫新 archetype）
  - 慢路径 B：matched archetype segment count 变（chunked 增长）→ `RefreshViewsOnly`（不重做 match，只重建 ChunkView）
- `Matches`：mask 预过滤 + `Signature.Contains` fallback for id ≥ 512
- 用户层 `MiniArch.Query` 是 struct facade：`GetChunks()` 零拷贝、`ForEachChunk` / `ForEachChunkParallel`、`OrderBy` 走 `ArrayPool<Entity>.Shared.Rent`
- `ForEachChunkParallel` 在 chunks < threads 时自动按 entity 子区间拆分，`[ThreadStatic] t_partitions` 避免每次分配
- 代码位置：`Core/Query.cs` + `Query.cs` + `World.QueryCache.cs`

### 7. Hierarchy
- side-table SoA 邻接表：`_parentByChild[id]` 直索引 + `_firstChild[id]` → `_childNext[slot]` → -1 链表 + slot free list
- 子节点用 slot 而非 id 索引（一个 parent 可有多个 child）
- `Link` vs `LinkRestored` 区分：前者 `unlinkFirst=true`，后者跳过（snapshot 恢复时旧 parent 已清）
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
- `Merge(a, b)` 是 15 行 `Array.Copy` 拼接，**不做语义折叠**——时序信息完整保留，跨帧 id 回收自然正确
- 两种 entity-id 模式（placeholder vs real），wire format 相同，由 `CommandStream.DeferredEntities` flag 控制 producer 行为

### 10. World（编排者）
- 拆分为 6 个 partial 文件：
  - `World.cs`：字段 + TryGet/Get/Has + Clone + Replay + archetype lookup
  - `World.EntityLifecycle.cs`：Create/Destroy + free list + 版本管理
  - `World.SnapshotBridge.cs`：snapshot/clone 用的 internal backdoor（`Reset`、`LinkSnapshot`、`SetSnapshot*`、`WriteFreeList`/`ReadFreeList`/`CopyFreeIdsFrom`、`FreeList`、`ValidateSnapshotEntitySlot`）
  - `World.Create.Generated.cs`：泛型重载 + `GetFirst<T>`
  - `World.QueryCache.cs`：Query 缓存管理
  - `World.StructuralChange.cs`：Add/Set/Remove（upsert 语义，`Add`/`Set` 是 alias）
- 结构变更核心：查 `EntityRecord` → 算目标签名 → edge cache → `MoveEntityCore`（带 catch rollback）+ `FinishMoveEntity` 分离，便于批量 materialize 复用

### 已删除的子系统
- **CommandBuffer**（2026-06-26）：per-entity 去重的录制器，被 CommandStream 取代。详见 `kb-command-stream.md` 的历史段落
- **DebugMetrics**：整个子系统删除（`kb-debug-metrics.md` 保留作历史）

## 已知问题

### P1. Query 失效粒度
- 当前两段式（archetypeCount + per-matched-archetype segment count），但 archetypeCount 一变仍触发对**所有** archetype 的 mask match 重扫
- 由于 append-only 且 mask 预过滤便宜，实测可接受；archetype 数 > 1000 时考虑 per-query-filter 分组失效
- 代码位置：`Core/Query.cs:104-128`

### P2. Edge Cache 直索引稀疏膨胀
- `_addDestinationCache` / `_removeDestinationCache` 用 `Archetype?[]` 按 componentId 直索引
- component id 稀疏（如 max=10000 但只有 2 个组件）时数组浪费内存
- 当前游戏场景下 component id 密集，trade-off 合理

### P3. Add/Set 语义合并
- `World.Add<T>` / `World.Set<T>` 都是 upsert，文档已明示是 alias（`World.StructuralChange.cs:11-22`）
- 严格 add 语义需要用户先 `Has<T>` 检查
- 当前是"够用的简单方案"——不打算改

### P4. Hierarchy 作为 side-table 的表达力缺失
- 无法写 `With<ChildOf>()` 这样的查询
- 候补方案：把 Parent 做成组件——代价是级联销毁变慢但表达力提升
- 详见 `kb-hierarchy-runtime.md`

### P5. EnsureReplayReservation 的 O(n) free list 扫描
- `World.EntityLifecycle.cs:523 RemoveFromFreeList` 是 O(freeIdCount) 线性扫描
- 大帧 lockstep replay（每 reserve 都走 fallback）下是潜在热点
- 候补：free list 反向 HashSet 索引；或在注释里至少承认 O(n)

## 可优化点

- **O1. Query entity 枚举跨 chunk 开销**：refresh 时把所有匹配 entity 预收集到连续 `Entity[]`
- **O2. Signature 不可变导致频繁分配**：≤4 组件用 stackalloc 或 interning
- **O3. Chunk swap-remove 的全列拷贝**：对大组件（如 256B Matrix4x4）单次 copy 成本高
- **O4. SubmitAndSnapshotAsync 的字段对调**：`CommandStream.cs:540-552` 是 12 个 `(frozen.X, _X) = (_X, spare.X)` 元组交换——加字段忘交换 = bug 且编译器不报。考虑反射驱动的测试或 `[FieldsMustBeSwapped]` attribute
- **O5. Replay free list 反向索引**：消除 P5 的 O(n) 扫描

## 设计张力

| 张力 | 选择 | 原因 |
|---|---|---|
| 全局 Registry vs World 隔离 | 全局 | 真实游戏场景正确；省一层间接 |
| 单线程写入 vs 并行读取 | 单线程写 | archetype ECS 经典约束 |
| Hierarchy 一等公民 vs 组件 | side-table | 正确性 > 表达力 |
| 大文件 vs partial 拆分 | partial 5 文件 | 按职责分组，保持编译单元聚焦 |
| 直索引 edge cache vs bounded cache | 直索引 `Archetype?[]` | O(1) 查找，简单可靠；稀疏 id 场景的代价已被承认 |
| CommandBuffer per-entity 去重 vs CommandStream typed store | CommandStream（独存） | 实测更快，去重语义无真实消费者，YAGNI |
| FrameDelta IR 折叠 vs 简单拼接 | 简单 byte 拼接 | 折叠状态机的复杂度转嫁到跨帧 id 回收 bug 上得不偿失；Array.Copy 简洁正确 |
| Submit 直接 Apply vs 统一走 Replay | 双路径 | Submit 是本地 canonical 路径无 Replay 不变式；详见 `kb-command-stream.md` |

## 做得好的地方

- **两层 archetype lookup**：Signature dict（权威）+ Mask dict（canonical-only，零分配 Replay 路径），每层职责单一
- **两段式 query 失效**：archetypeCount 粗扫 + per-archetype segment count 细扫
- **CommandStream typed store**：替代旧 entry stream 后反超 Friflo，详见 `kb-command-stream.md` vs Friflo 段
- **FrameDelta packed byte format**：单一 buffer + varint + tag，零拷贝网络发送，Merge 退化到 Array.Copy
- **`MoveEntityCore` + `FinishMoveEntity` 分离**：带 catch rollback，便于批量 materialize 复用
- **`[SkipLocalsInit]` / `AggressiveInlining` / `Unsafe.As<byte,T>`** 系统化性能卫生
- **Add/Set alias 文档化**、`Unsafe`/`Try` 前缀一致、`ICommandRecorder` 不硬塞输出方法——命名诚实贯穿全库

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
