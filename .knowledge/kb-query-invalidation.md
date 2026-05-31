---
title: Query Invalidation System
module: Core ECS
description: Per-archetype generation based query invalidation mechanism
updated: 2026-05-31
---
# Query Invalidation System

## 这个模块是干什么的

- 这个模块负责：
  - 检测 Query 匹配的 archetype 是否发生变化
  - 决定何时刷新 Query 的快照（archetype 和 chunk 列表）
  - 避免不必要的快照重建，提升性能
- 这个模块不负责：
  - 实际的 archetype 匹配逻辑（由 QueryFilter 处理）
  - chunk 数据的存储和访问（由 Archetype/Chunk 处理）

## 架构

- 核心组成：
  - **Archetype.Generation**: 每个 archetype 维护自己的 generation 计数器
  - **Query._snapshotGenerations**: 记录快照中每个 archetype 的 generation
  - **Query._snapshotArchetypeVersion**: 记录快照时的 world archetype 版本
  - **World._archetypeVersion**: 检测新 archetype 的创建
- 数据流 / 控制流：
  1. Archetype 执行 `ReserveEntity()` 或 `RemoveEntity()` 时递增自己的 `Generation`
  2. Query 访问 `MatchedArchetypes` 时调用 `EnsureMatchingSnapshot()`
  3. `EnsureMatchingSnapshot()` 检查：
     - `_initialized` 标志（首次访问）
     - `_world.ArchetypeVersion` 是否变化（新 archetype 创建）
     - 每个匹配 archetype 的 `Generation` 是否变化
  4. 如果有任何变化，调用 `BuildMatchingSnapshot()` 重建快照

## 决策

- **为什么选择 per-archetype generation 而不是全局 generation**：
  - 全局 generation 会导致任何结构变更都让所有 Query 失效
  - per-archetype generation 只在相关 archetype 变化时才失效
  - 例如：给实体添加 Velocity 不会让 `With<Position>` 的 Query 失效
- **为什么保留 World._queryGeneration**：
  - 用于 deferred suppression 机制（`BeginDeferredLayoutUpdates` / `EndDeferredLayoutUpdates`）
  - 确保 deferred 模式下的正确性
- **为什么使用 _archetypeVersion**：
  - 检测新 archetype 的创建，避免遗漏匹配的新 archetype
  - 比遍历所有 archetype 检查是否匹配更高效

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一个基于版本号的缓存失效系统
- 这个模块里最重要的抽象是：
  - **Generation**: archetype 级别的版本号，用于检测变化
  - **ArchetypeVersion**: world 级别的版本号，用于检测新 archetype
- 常见误解：
  - 认为 `World._queryGeneration` 仍然用于 Query 失效检查（实际上已移除）
  - 认为任何结构变更都会让所有 Query 失效（现在是 per-archetype 粒度）

## 入口

- 第一次读或加功能，先看：
  - `src/MiniArch/Core/Query.cs`: `EnsureMatchingSnapshot()` 和 `BuildMatchingSnapshot()`
  - `src/MiniArch/Core/Archetype.cs`: `Generation` 属性和 `ReserveEntity()`/`RemoveEntity()`
- 修 bug，先看：
  - `src/MiniArch/Core/Query.cs`: `HasAnyArchetypeGenerationChanged()` 检查逻辑
  - `src/MiniArch/Core/World.cs`: `_archetypeVersion` 和 `GetOrCreateArchetype()`

## 坑点

- 历史上容易出问题的地方：
  - 全局 generation 导致不必要的 Query 失效
  - deferred 模式下的 generation 递增时机
- 容易误判的地方：
  - 认为 `_initialized` 标志是多余的（实际上用于首次访问检测）
  - 认为 `_archetypeVersion` 检查是多余的（实际上用于新 archetype 检测）
- 改这里时要特别小心：
  - `HasAnyArchetypeGenerationChanged()` 的检查顺序
  - `BuildMatchingSnapshot()` 中 `_initialized` 和 `_snapshotArchetypeVersion` 的设置时机
