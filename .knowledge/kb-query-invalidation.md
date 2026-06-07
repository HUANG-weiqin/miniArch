---
title: Query Invalidation System
module: MiniArch.Core Query
description: Single-version query invalidation — only rebuilds when new archetypes are created
updated: 2026-06-07
---
# Query Invalidation System

## 这个模块是干什么的

- 检测 Query 匹配的 archetype 集合是否发生变化
- 决定何时刷新 Query 的快照（archetype 和 chunk 列表）
- 避免不必要的快照重建

## 架构

- 核心组成：
  - **World._archetypeVersion**：每次创建新 Archetype 时递增
  - **Query._snapshotArchetypeVersion**：记录快照时的 world archetype 版本
  - **Query._initialized**：标记首次访问

- 数据流：
  1. 新 Archetype 创建时 `World._archetypeVersion++`
  2. Query 访问 `MatchedArchetypes`/`GetChunkSpan()` 时调用 `EnsureMatchingArchetypes()`
  3. `HasAnyArchetypeGenerationChanged()` 检查：`_initialized` → `_world.ArchetypeVersion != _snapshotArchetypeVersion`
  4. 版本不匹配则调用 `BuildMatchingArchetypeSnapshot()` 重建

- Chunk 快照：每个匹配的 archetype 贡献 1 个 chunk（无论是否为空）。所有迭代器路径正确处理 `Count == 0` 的 chunk。

## 决策

- **单版本号而非 per-archetype generation**：因为 flatten 后每个 archetype 只有一个 chunk，只需知道"是否有新 archetype 出现"。无需跟踪 occupancy 变化——空的 chunk 在 snapshot 中无害。
- **`Archetype.Generation` 已移除**：per-archetype generation 曾是查询失效机制的一部分，但后来被双版本号（archetypeVersion + nonEmptyArchetypeVersion）取代。在进一步 YAGNI 清理中，双版本号被合并为单一 `_archetypeVersion`，`Archetype.Generation` 和 `Archetype.OnOccupancyTransition` 回调均已移除。
- **不再过滤空 archetype**：flatten 后每个 archetype 只有一个 chunk，包含空的 chunk 不会造成迭代开销（`Count == 0` 的 chunk 在所有迭代器路径中跳过）。

## 认知模型

- 一个基于版本号的缓存失效系统，但只关心"archetype 列表是否变化"，不关心"已有 archetype 的 occupancy 变化"。

## 入口

- `src/MiniArch/Core/Query.cs`：`EnsureMatchingArchetypes()` 和 `BuildMatchingArchetypeSnapshot()`
- `src/MiniArch/Core/World.cs`：`_archetypeVersion`，`GetOrCreateArchetype()` 中递增

## 坑点

- `_initialized` 标志用于首次访问检测，不能移除
- `HasAnyArchetypeGenerationChanged()` 只检查全局 `_archetypeVersion`，不需要遍历任何 archetype
- `_snapshotArchetypeVersion` 在 `BuildMatchingArchetypeSnapshot()` 中写入（非 volatile），`HasAnyArchetypeGenerationChanged()` 通过非 volatile 读取——这在单线程写入、多线程只读场景下安全
