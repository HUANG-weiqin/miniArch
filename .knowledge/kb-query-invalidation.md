---
title: Query Invalidation System
module: MiniArch.Core Query
description: Sorted archetype snapshot + full-scan cache rebuild on count change, per-archetype chunk-view-shape refresh for chunked growth
updated: 2026-10-12
---
# Query Invalidation System

## 这个模块是干什么的

- 检测 Query 匹配的 archetype 集合是否发生变化
- 决定何时刷新 Query 的快照（archetype + chunk view 列表）
- 变化时**全量重建**而非增量 append（archetype 创建是冷路径，全量扫描可接受）

## 架构

### World 内的查询复用

- `World._queries` 是唯一的 `Dictionary<QueryDescription, QueryCache>`；`GetAdvancedQuery` 命中时直接返回已有 cache，miss 时才编译 `QueryFilter` 并构造 `QueryCache`。
- 描述中的 Required/Excluded/Any 是不可变、去重且规范排序的 Type 集合，Exact 参与值相等；同一 World 的等价描述共享同一 cache，不同 World 不共享。
- 首次 materialize 用 `Volatile.Read` + 字典 copy-on-write + CAS 发布，竞争失败者重新查当前表；已发布字典在正常查询期间不原地修改。Dispose 清理这张表；`InitializeSnapshotSlots` 仍拒绝已有 query cache 的 World。
- `QueryFilter` 仍是 cache 的匹配数据，不是另一张独立缓存。2026-10-12 删除了旧 `Description → Filter → Cache` 两级查表及第二套发布循环；公共 Query API、匹配和失效语义不变。

### QueryCache 内的结果失效

- 核心组成：
  - **`World.ArchetypeCount`**：当前 archetype 数组长度（archetype 创建时只增不减）
  - **`QueryCache._lastArchetypeCount`**：上次刷新时记录的 archetype 数量
  - **`QueryCache._requiredMask/_excludedMask/_anyMask`**：构造时预计算的 filter bitmask，不可变
  - **`QueryCache._refreshLock`**：double-check locking 用于并发只读场景
  - **`QueryCache._archetypeExpectedViews[]`**：跟踪每个匹配 archetype 的 chunk view shape（non-chunked = -1；chunked = SegmentCount），检测分段增长和 non-chunked → single-segment chunked 晋升

- **两段式失效**（`src/MiniArch/Core/QueryCache.cs:105-129` `EnsureRefreshed`）：
  1. 快路径：`_world.ArchetypeCount == _lastArchetypeCount` → 跳过 archetype 匹配阶段
  2. 慢路径 A：archetype 数量变 → `Refresh()` → `RebuildCache()`（**全量扫描 0..N**，archetype 排序插入可能出现在任何位置，不再支持 append-only）
  3. 慢路径 B：已有匹配 archetype 的 view shape 变（chunked 增长，或 non-chunked 晋升为 single-segment chunked）→ `RefreshViewsOnly()`（不重做 match，只重建 ChunkView）

- 数据流：
  1. 新 Archetype 创建时 `PublishArchetypeSnapshot()` 按稳定组件类型身份集合排序插入（`FindInsertIndex` 二分查找 + 分段 `Array.Copy`），原子替换更大的 archetype 数组
  2. QueryCache 访问 `MatchedArchetypes` / `GetChunkViewSpan()` 时调用 `EnsureRefreshed()`
  3. `if (_world.ArchetypeCount != _lastArchetypeCount)` 数量变化则进入 `Refresh()`
  4. 已有匹配 archetype 的 view shape 变化也触发 `RefreshViewsOnly()`；注意 non-chunked 和 single-segment chunked 的 view 数都为 1，但 `ChunkView` 内部 segment index 分别是 -1 / 0，必须刷新
  5. `Refresh()` 下用 lock double-check → `RebuildCache()`（全量扫描 0..N，一次 pass 计数 + 计算 view 数，二次 pass 填充快照，三次 clear trailing）

- Chunk 快照：每个匹配的 archetype 贡献 1 个 chunk（单块模式）或 N 个 chunk（分段模式，每个 Segment 一个 ChunkView）。

## 决策

- **描述直接缓存 QueryCache**：所有查询入口都已有 `QueryDescription`，其规范化与值相等足以复用 cache；不再为中间 filter 单独保存一张 COW 表。Filter 留在 cache 中承担匹配，不引入新公共抽象。
- **数量变化时全量重建**：archetype 对象不会因普通销毁被移除，但有序 snapshot 的新条目可能插在中间；不能把旧 count 之后的尾部视为所有新增项。实际 owner 是 `RebuildCache`，旧 `AppendNewArchetypes` 描述已失效。
- **archetype 数量代替全局版本号**：`ArchetypeCount` 直接用 archetype 数组长度，语义更清晰。
- **filter mask 预计算到构造时**：`_requiredMask`/`_excludedMask`/`_anyMask` 在 Query 构造时一次性计算为 readonly 字段，匹配时直接使用。
- **512-bit ComponentMask 手动展开**：8×ulong 逐个比较，手动展开无循环。在 <64 组件的典型场景下只需 1 条 AND 指令。
- **分段 / 模式变化检测**：`_archetypeExpectedViews[]` 记录 view shape，而不是单纯 view count。segment 增长或 non-chunked → single-segment chunked 晋升时，只重建 ChunkView，不重做 archetype match。

## 认知模型

- 先按描述复用一个 QueryCache，再由该 cache 决定结果是否过期；“找到同一查询”和“刷新查询结果”是两个不同职责，不需要两张身份缓存。
- 结果失效仍有两段：
  - 第一段：archetype 数量没变 → 直接进入第二段；数量改变 → 全量重建匹配和 views。
  - 第二段：每个已匹配 archetype 的 view shape 没变 → 跳过刷新；shape 改变 → 仅重建 views。

## 入口

- `src/MiniArch/Core/QueryCache.cs`：`EnsureRefreshed()`、`Refresh()`、`RebuildCache()`、`RefreshViewsOnly()`
- `src/MiniArch/Core/World.QueryCache.cs`：`GetAdvancedQuery` 的单缓存发布、archetype snapshot 发布
- `src/MiniArch/Core/World.cs`：缓存字段/清理、fresh-world slot guard、`ArchetypeCount` 属性
- `tests/MiniArch.Tests/Core/QueryTests.cs`：热缓存复用/零分配、冷启动并发等价描述共享、仅有 query 的 World 初始化拒绝
- `tests/MiniArch.Tests/Core/QueryFilterTests.cs`：类型集合规范化、Any 顺序、Exact 隔离

## 坑点

- 不要把有序 archetype snapshot 当成 append-only 数组；旧文档中的增量尾扫不适用于当前 `RebuildCache`
- `Refresh` / `RefreshViewsOnly` 都用 `lock (_refreshLock)` + volatile publish，并发只读场景安全
- `_archetypeExpectedViews[]` 是 reader 快路径的"ChunkView 已刷新"信号，必须在 `_snapshotChunkViews` 和 `_chunkViewCount` 发布之后再写；否则另一个 reader 可能看到 shape 已匹配但仍读到旧 `ChunkView`
- Query 缓存代码在 `World.QueryCache.cs` partial 文件中，修改时要注意 partial 类的编译范围
- 分段模式下 chunk 数量 = 所有匹配 archetype 的 segment 数之和，不等于 archetype 数量
- 不能只比较 chunk view 数量：non-chunked 和 single-segment chunked 都是 1 个 view，但旧 `ChunkView(-1)` 在晋升后会访问 segment `-1`；必须比较 mode-aware shape
- `EnsureRefreshed` 每次访问结果都会调用：先比较一个 archetype count，再检查已匹配 archetype 的 view shape；不能把它描述成固定两次 int 比较

## 竞品对比

| | miniArch | Arch | Friflo |
|---|---|---|---|
| 失效检测 | archetype 数量 + per-archetype view shape（两段式） | Archetypes 集合 hash code | archetype 数量比较 |
| 重建策略 | 数量变化时全量匹配；shape 变化时仅重建 views | 全量重建 | 增量 append |
| 匹配算法 | 8×ulong 手动展开 | uint[] + SIMD Vector | Vector256 / 4×long fallback |
