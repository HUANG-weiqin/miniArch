---
title: Hierarchy Runtime
module: MiniArch.Core Hierarchy
description: Runtime-owned parent-child relations, cascade destroy semantics, and snapshot restore behavior
updated: 2026-06-08 (hierarchy API 在 World.cs 主文件中)
---
# Hierarchy Runtime

## 这个模块是干什么的

- 维护 entity 的单父 parent-child 关系
- 提供 parent 查询和 children 列表查询
- 在父实体销毁时扩展为 child-first 的级联销毁
- snapshot save/load 后 hierarchy 关系保持有效

## 架构

- 核心组成：
  - `src/MiniArch/Core/HierarchyTable.cs`：runtime-owned 关系表（邻接链表：`_parentByChild[id]` + `_firstChild[id]` + free-list 复用 slot）
  - `src/MiniArch/Core/World.cs`（主文件）：对外 API（`Link`、`GetChildren`、`Destroy` 级联）、destroy 集成
  - `src/MiniArch/Core/WorldSnapshot.cs`：hierarchy link 的持久化读写

## 决策

- hierarchy 保持为 World 持有的 runtime side table，不是 ECS component
- destroy 语义固定为 cascade destroy（child-first），而非"父死子孤儿"
- 复用 `default(Entity)` 作为"无 parent"值

## 认知模型

- entity metadata 的一层关系索引，而不是存储在 chunk 里的业务数据

## 入口

- `src/MiniArch/Core/HierarchyTable.cs`
- `src/MiniArch/Core/World.cs`（hierarchy API 和 destroy 集成点）
- `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`（级联销毁、reparent）

## 坑点

- parent 存储必须带完整 `Entity` 句柄，不能只存 `Id`
- destroy 时先删 parent 再清 children 会导致关系残留
- 内部路径走 `EnumerateChildren()`（struct enumerator），不走 `GetChildren()`（list 快照）
- hierarchy 改动不影响 query 匹配（不递增 archetype generation）
