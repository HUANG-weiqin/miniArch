---
title: Query Invalidation System
module: MiniArch.Core Query
description: Global-version query invalidation — single int version, full rebuild on any archetype creation
updated: 2026-06-07 (修正：全局单版本号 _archetypeVersion vs _snapshotVersion，无 per-archetype generation)
---
# Query Invalidation System

## 这个模块是干什么的

- 检测 Query 匹配的 archetype 集合是否发生变化
- 决定何时刷新 Query 的快照（archetype 和 chunk 列表）
- 避免不必要的快照重建

## 架构

- 核心组成：
  - **World._archetypeVersion**：每次创建新 Archetype 时递增（全局版本号）
  - **Query._snapshotVersion**：记录快照时的 world archetype 版本
  - **Query._refreshLock**：double-check locking 用于并发只读场景

- 数据流：
  1. 新 Archetype 创建时 `World._archetypeVersion++`
  2. Query 访问 `MatchedArchetypes`/`GetChunkSpan()` 时调用 `EnsureRefreshed()`
  3. `if (_world.ArchetypeVersion != _snapshotVersion)` 不匹配则进入 `Refresh()`
  4. `Refresh()` 下用 lock double-check → `BuildSnapshot()` 全量重建
  5. `BuildSnapshot()` 遍历 `_world.Archetypes`，用 256-bit ComponentMask 做 required/excluded/any 匹配 → volatile write 发布新快照

- Chunk 快照：每个匹配的 archetype 贡献 1 个 chunk。所有迭代器路径正确处理 `Count == 0` 的 chunk。

## 决策

- **全局单版本号而非 per-archetype generation**：每个 archetype 只有一个 chunk，重建代价极低（仅遍历 archetype 数组算 bitmask match）。无需 per-archetype 粒度。
- **`Archetype.Generation` 和 `Archetype.OnOccupancyTransition` 均已移除**：全局 `_archetypeVersion` 是唯一版本来源。
- **不再过滤空 archetype**：flatten 后每个 archetype 只有一个 chunk，包含空的 chunk 不会造成迭代开销（`Count == 0` 的 chunk 在所有迭代器路径中跳过）。
- **256-bit ComponentMask 匹配**：`requiredMask`/`excludedMask`/`anyMask` 在 BuildSnapshot 开始时一次性计算，匹配时用位运算 O(1) 拒绝，仅大于 256 的 component id 走 fallback 数组搜索。

## 认知模型

- 一个基于全局版本号的缓存失效系统，只关心"archetype 列表是否变化"，不关心"已有 archetype 的 occupancy 变化"。

## 入口

- `src/MiniArch/Core/Query.cs`：`EnsureRefreshed()`、`Refresh()` 和 `BuildSnapshot()`
- `src/MiniArch/Core/World.cs`：`_archetypeVersion`，`GetOrCreateArchetype()` 中递增

## 坑点

- 全局版本号导致**任何新 archetype 都触发所有 query 全量重建**；当 archetype 数量极大（>1000）时可能需要更细粒度的失效
- `BuildSnapshot()` 使用 volatile write 发布快照（`_snapshotArchetypes`、`_snapshotChunks`、`_snapshotCount`、`_snapshotVersion`），并发只读场景安全
- `_snapshotVersion` 在 `BuildSnapshot()` 末尾写入（volatile），`EnsureRefreshed()` 通过非 volatile 读取——这在单线程写入、多线程只读场景下安全
