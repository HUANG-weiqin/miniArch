---
title: Chunk 存储
module: MiniArch.Core
description: Archetype 的 readonly struct 视图 — Chunk(internal) 和 ChunkView(public) 都不持有存储，所有数据归属 Archetype
updated: 2026-06-08 (拆分 Archetype.Storage.cs、新增 public ChunkView、区分 internal Chunk 和 public ChunkView)
---
# Chunk 存储

## 这个模块是干什么的

- Chunk（internal）和 ChunkView（public）都是 Archetype 的**只读 readonly struct 视图**，不是存储持有者
- 每个 Archetype **有且仅有一个 Chunk/ChunkView** 实例，包裹 Archetype 引用暴露只读查询接口
- 真正的存储（`_data: byte[]`、`_entities: Entity[]`、列偏移、元素大小等）全部直属于 `Archetype`
- 存储操作代码在 `Archetype.Storage.cs` partial 文件中

## 架构

- **两个视图类型**：
  - `Chunk.cs`（internal）：给 Query 内部迭代器使用，代理 Archetype 的只读接口
  - `Ecs/ChunkView.cs`（public）：给用户 batch API 使用，直接包裹 Archetype，带 `AggressiveInlining`
- 核心组成：
  - `_archetype: Archetype` — 被包裹的 Archetype 引用
  - 所有数据通过 Archetype 代理访问
- 数据流：
  - 读取：`GetComponentSpanAt<T>(col)` → 代理到 `_archetype.GetComponentSpanAt<T>(col)`
  - 实体：`GetEntities()` → 代理到 `_archetype.GetEntities()`
  - Span：`GetSpan<T>()` → `_archetype.GetComponentSpan<T>(Component<T>.ComponentType)`
- **真正的存储归属 Archetype**（`Archetype.cs` 字段 + `Archetype.Storage.cs` 操作）

## 决策

- **Chunk 不再是存储单元**：将 Chunk 降级为 readonly struct 视图，消除多 Chunk 复杂度
- **每个 Archetype 精确对应一个 Chunk/ChunkView**：查询接口永远返回长度为 1 的 span
- **SoA 布局仍在 Archetype 中保持**：列式排列、同一组件的所有行在内存中连续
- **swap-remove 在 Archetype.Storage.cs 中实现**：逐列复制最后一行数据
- **不支持托管引用组件**：`flat byte[]` 不含 GC 跟踪，在 Archetype 构造时 fail fast
- **ChunkView 方法加 `[MethodImpl(AggressiveInlining)]`**：用户 batch API 路径必须内联

## 认知模型

- Chunk/ChunkView = **Archetype 的一张只读门票**
- ChunkView 是面向用户的公开接口，Chunk 是面向内部迭代器的接口

## 入口

- 第一次读：`Chunk.cs`（internal 视图）→ `Ecs/ChunkView.cs`（public 视图）→ `Archetype.Storage.cs`（存储操作）
- 修 bug：`Archetype.Storage.cs` 的 `EnsureCapacity()` / `RemoveAt()`

## 坑点

- Chunk 和 ChunkView 都是 readonly struct，不可存字段、不可作为可写状态持有者
- 所有存储扩容逻辑在 `Archetype.Storage.cs` 上，视图不提供 EnsureCapacity
- `GetChunkSpan()[0]` 总是访问唯一视图，不再需要遍历多个 chunk
- ChunkView 的 `GetSpan<T>()` 需要 `where T : struct` 约束
- 如果未来需要超过单块 `byte[]` 限制（~2GB）的场景，需要重新引入多存储块方案
