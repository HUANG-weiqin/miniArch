---
title: Query Invalidation System
module: MiniArch.Core Query
description: Incremental append-only query invalidation — archetype count comparison, only new archetypes scanned
updated: 2026-06-13 (实际实现是全量重建，非增量 append；ComponentMask 256→512-bit；_snapshotVersion 已移除)
---
# Query Invalidation System

## 这个模块是干什么的

- 检测 Query 匹配的 archetype 集合是否发生变化
- 决定何时刷新 Query 的快照（archetype 和 chunk 列表）
- 变化时全量重建快照（遍历所有 archetype，重新匹配）

## 架构

- 核心组成：
  - **World.ArchetypeCount**：当前 archetype 数组长度（archetype 创建时原子增长）
  - **Query._lastArchetypeCount**：上次刷新时记录的 archetype 数量
  - **Query._requiredMask/_excludedMask/_anyMask**：构造时预计算的 filter bitmask，不可变
  - **Query._refreshLock**：double-check locking 用于并发只读场景
  - **Query._archetypeExpectedViews[]**：跟踪每个匹配 archetype 的 segment 数量，检测分段增长

- 数据流：
  1. 新 Archetype 创建时 `PublishArchetypeSnapshot()` 原子替换更大的 archetype 数组
  2. Query 访问 `MatchedArchetypes`/`GetChunkSpan()` 时调用 `EnsureRefreshed()`
  3. `if (_world.ArchetypeCount != _lastArchetypeCount)` 数量变化则进入 `Refresh()`
  4. 分段模式下，已有匹配 archetype 的 `SegmentCount` 变化也触发 `Refresh()`
  5. `Refresh()` 下用 lock double-check → `AppendNewArchetypes()`
  6. `AppendNewArchetypes()` 遍历**所有** archetype 重新匹配，重建快照数组

- Chunk 快照：每个匹配的 archetype 贡献 1 个 chunk（单块模式）或 N 个 chunk（分段模式，每个 Segment 一个 ChunkView）。

## 决策

- **全量重建而非增量 append**：`AppendNewArchetypes` 名字具有误导性——实际每次调用都遍历所有 archetype 重新匹配并重建快照。archetype 数量通常很小（<50），全量重建成本可忽略。
- **archetype 数量代替全局版本号**：`ArchetypeCount` 直接用 archetype 数组长度，语义更清晰。
- **filter mask 预计算到构造时**：`_requiredMask`/`_excludedMask`/`_anyMask` 在 Query 构造时一次性计算为 readonly 字段，匹配时直接使用。
- **512-bit ComponentMask 手动展开**：8×ulong 逐个比较，手动展开无循环。在 <64 组件的典型场景下只需 1 条 AND 指令。
- **分段增长检测**：`_archetypeExpectedViews[]` 跟踪每个匹配 archetype 的 segment 数量，segment 增长时触发快照重建。

## 认知模型

- 一个基于 archetype 数量比较的缓存失效系统。数量变化时全量重建快照。

## 入口

- `src/MiniArch/Core/Query.cs`：`EnsureRefreshed()`、`Refresh()` 和 `AppendNewArchetypes()`
- `src/MiniArch/Core/World.QueryCache.cs`：Query 缓存管理、archetype snapshot 发布
- `src/MiniArch/Core/World.cs`：`ArchetypeCount` 属性

## 坑点

- `AppendNewArchetypes()` 名字暗示增量，实际是全量重建——修改时注意
- `AppendNewArchetypes()` 使用 volatile write 发布快照，并发只读场景安全
- Query 缓存代码在 `World.QueryCache.cs` partial 文件中，修改时要注意 partial 类的编译范围
- 分段模式下 chunk 数量 = 所有匹配 archetype 的 segment 数之和，不等于 archetype 数量

## 竞品对比

| | miniArch | Arch | Friflo |
|---|---|---|---|
| 失效检测 | archetype 数量比较 | Archetypes 集合 hash code | archetype 数量比较 |
| 重建策略 | **全量重建** | 全量重建 | **增量 append** |
| 匹配算法 | 8×ulong 手动展开 | uint[] + SIMD Vector | Vector256 / 4×long fallback |
