---
title: Query Invalidation System
module: MiniArch.Core Query
description: Sorted archetype snapshot + full-scan cache rebuild on count change, per-archetype chunk-view-shape refresh for chunked growth
updated: 2026-07-19
---
# Query Invalidation System

## 这个模块是干什么的

- 检测 Query 匹配的 archetype 集合是否发生变化
- 决定何时刷新 Query 的快照（archetype + chunk view 列表）
- 变化时**全量重建**而非增量 append（archetype 创建是冷路径，全量扫描可接受）

## 架构

- 核心组成：
  - **`World.ArchetypeCount`**：当前 archetype 数组长度（archetype 创建时只增不减）
  - **`Query._lastArchetypeCount`**：上次刷新时记录的 archetype 数量
  - **`Query._requiredMask/_excludedMask/_anyMask`**：构造时预计算的 filter bitmask，不可变
  - **`Query._refreshLock`**：double-check locking 用于并发只读场景
  - **`Query._archetypeExpectedViews[]`**：跟踪每个匹配 archetype 的 chunk view shape（non-chunked = -1；chunked = SegmentCount），检测分段增长和 non-chunked → single-segment chunked 晋升

- **两段式失效**（`src/MiniArch/Core/QueryCache.cs:105-129` `EnsureRefreshed`）：
  1. 快路径：`_world.ArchetypeCount == _lastArchetypeCount` → 跳过 archetype 匹配阶段
  2. 慢路径 A：archetype 数量变 → `Refresh()` → `RebuildCache()`（**全量扫描 0..N**，archetype 排序插入可能出现在任何位置，不再支持 append-only）
  3. 慢路径 B：已有匹配 archetype 的 view shape 变（chunked 增长，或 non-chunked 晋升为 single-segment chunked）→ `RefreshViewsOnly()`（不重做 match，只重建 ChunkView）

- 数据流：
  1. 新 Archetype 创建时 `PublishArchetypeSnapshot()` 按 signature 排序插入（`FindInsertIndex` 二分查找 + 分段 `Array.Copy`），原子替换更大的 archetype 数组
  2. Query 访问 `MatchedArchetypes`/`GetChunkSpan()` 时调用 `EnsureRefreshed()`
  3. `if (_world.ArchetypeCount != _lastArchetypeCount)` 数量变化则进入 `Refresh()`
  4. 已有匹配 archetype 的 view shape 变化也触发 `RefreshViewsOnly()`；注意 non-chunked 和 single-segment chunked 的 view 数都为 1，但 `ChunkView` 内部 segment index 分别是 -1 / 0，必须刷新
  5. `Refresh()` 下用 lock double-check → `RebuildCache()`（全量扫描 0..N，一次 pass 计数 + 计算 view 数，二次 pass 填充快照，三次 clear trailing）

- Chunk 快照：每个匹配的 archetype 贡献 1 个 chunk（单块模式）或 N 个 chunk（分段模式，每个 Segment 一个 ChunkView）。

## 决策

- **增量 append-only 而非全量重建**：archetype 是 append-only（创建后不删除），所以 `_lastArchetypeCount` 之后的 archetype 集合就是"自上次刷新起新增的"。append-only 扫描代价随新增量缩放，不随总 archetype 数增长。
- **名字诚实**：`AppendNewArchetypes` 的名字就是字面意思——append 新 archetype 进快照。历史上的 kb 描述说"名字具有误导性，实际全量重建"是错的，已修正。
- **archetype 数量代替全局版本号**：`ArchetypeCount` 直接用 archetype 数组长度，语义更清晰。
- **filter mask 预计算到构造时**：`_requiredMask`/`_excludedMask`/`_anyMask` 在 Query 构造时一次性计算为 readonly 字段，匹配时直接使用。
- **512-bit ComponentMask 手动展开**：8×ulong 逐个比较，手动展开无循环。在 <64 组件的典型场景下只需 1 条 AND 指令。
- **分段 / 模式变化检测**：`_archetypeExpectedViews[]` 记录 view shape，而不是单纯 view count。segment 增长或 non-chunked → single-segment chunked 晋升时，只重建 ChunkView，不重做 archetype match。

## 认知模型

- 一个基于 archetype 数量比较 + chunk view shape 比较的**两段式**缓存失效系统：
  - 第一段：archetype 数量没变 → 直接进入第二段
  - 第二段：每个已匹配 archetype 的 view shape 没变 → 完全跳过刷新
  - 任一段失配 → 走对应增量刷新路径

## 入口

- `src/MiniArch/Core/QueryCache.cs`：`EnsureRefreshed()`、`Refresh()`、`AppendNewArchetypes()`、`RefreshViewsOnly()`
- `src/MiniArch/Core/World.QueryCache.cs`：Query 缓存管理、archetype snapshot 发布
- `src/MiniArch/Core/World.cs`：`ArchetypeCount` 属性

## 坑点

- `AppendNewArchetypes` 是真正的增量 append，**不要**把它当全量重建改——重写时若改成全量，会破坏"archetype 数量回到 0 后再增长"等边界场景下的 `_lastArchetypeCount` 语义
- `Refresh` / `RefreshViewsOnly` 都用 `lock (_refreshLock)` + volatile publish，并发只读场景安全
- `_archetypeExpectedViews[]` 是 reader 快路径的"ChunkView 已刷新"信号，必须在 `_snapshotChunkViews` 和 `_chunkViewCount` 发布之后再写；否则另一个 reader 可能看到 shape 已匹配但仍读到旧 `ChunkView`
- Query 缓存代码在 `World.QueryCache.cs` partial 文件中，修改时要注意 partial 类的编译范围
- 分段模式下 chunk 数量 = 所有匹配 archetype 的 segment 数之和，不等于 archetype 数量
- 不能只比较 chunk view 数量：non-chunked 和 single-segment chunked 都是 1 个 view，但旧 `ChunkView(-1)` 在晋升后会访问 segment `-1`；必须比较 mode-aware shape
- `EnsureRefreshed` 每次访问 query 都会被调用，必须是极轻量的"两个 int 比较"快路径

## 竞品对比

| | miniArch | Arch | Friflo |
|---|---|---|---|
| 失效检测 | archetype 数量 + per-archetype view shape（两段式） | Archetypes 集合 hash code | archetype 数量比较 |
| 重建策略 | **增量 append**（只扫新 archetype） | 全量重建 | 增量 append |
| 匹配算法 | 8×ulong 手动展开 | uint[] + SIMD Vector | Vector256 / 4×long fallback |
