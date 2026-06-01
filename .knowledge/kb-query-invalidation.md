---
title: Query Invalidation System
module: MiniArch.Core Query
description: Per-archetype generation based query invalidation mechanism
updated: 2026-06-01
---
# Query Invalidation System

## 这个模块是干什么的

- 检测 Query 匹配的 archetype 是否发生变化
- 决定何时刷新 Query 的快照（archetype 和 chunk 列表）
- 避免不必要的快照重建

## 架构

- 核心组成：
  - **Archetype.Generation**：每个 archetype 维护自己的 generation 计数器
  - **Query._snapshotGenerations**：记录快照中每个 archetype 的 generation
  - **Query._snapshotArchetypeVersion**：记录快照时的 world archetype 版本
  - **World._archetypeVersion**：检测新 archetype 的创建
- 数据流：
  1. Archetype 执行 `ReserveEntity()` 或 `RemoveEntity()` 时递增自己的 `Generation`
  2. Query 访问 `MatchedArchetypes` 时调用 `EnsureMatchingSnapshot()`
  3. `EnsureMatchingSnapshot()` 检查：`_initialized` → `_world.ArchetypeVersion` → 每个匹配 archetype 的 `Generation`
  4. 有变化则调用 `BuildMatchingSnapshot()` 重建快照

## 决策

- **per-archetype generation 而非全局 generation**：只让相关 archetype 的 Query 失效
- 保留 `World._archetypeVersion` 来检测新 archetype 的创建（比遍历所有 archetype 更高效）
- `World._queryGeneration` 和 `BeginDeferredLayoutUpdates`/`EndDeferredLayoutUpdates`（deferred suppression）已被移除——per-archetype generation 不需要它们

## 认知模型

- 一个基于版本号的缓存失效系统

## 入口

- `src/MiniArch/Core/Query.cs`：`EnsureMatchingSnapshot()` 和 `BuildMatchingSnapshot()`
- `src/MiniArch/Core/Archetype.cs`：`Generation` 属性和 `ReserveEntity()`/`RemoveEntity()`

## 坑点

- 历史上全局 generation 导致不必要的 Query 失效
- `_initialized` 标志用于首次访问检测，不能移除
- `HasAnyArchetypeGenerationChanged()` 的检查顺序影响性能
