---
title: Chunk 存储
module: MiniArch.Core
description: Archetype 的 readonly struct 视图 — Chunk 不再持有存储，所有数据归属 Archetype
updated: 2026-06-07 (修正：Archetype 扁平化后 Chunk 改为不再持存储，仅作为 Archetype 的 readonly 视图)
---
# Chunk 存储

## 这个模块是干什么的

- Chunk 是 Archetype 的**只读 readonly struct 视图**，不是存储持有者
- 每个 Archetype **有且仅有一个 Chunk** 实例，Chunk 包裹 Archetype 引用暴露公开只读查询接口
- 真正的存储（`_data: byte[]`、`_entities: Entity[]`、列偏移、元素大小等）全部直属于 `Archetype`
- Chunk 不参与实体生命周期管理，不负责扩容/收缩，仅提供组件数据的安全访问

## 架构

- 核心组成：
  - `_archetype: Archetype` — 被包裹的 Archetype 引用
  - 所有数据通过 Archetype 代理访问（`Count` → `_archetype.EntityCount`、`GetEntities()` → `_archetype.GetEntities()` 等）
- 数据流：
  - 读取：`GetComponentSpanAt<T>(col)` → 代理到 `_archetype.GetComponentSpanAt<T>(col)`
  - 实体：`GetEntities()` → 代理到 `_archetype.GetEntities()`
  - 内部路径（非公开 API）直接委托到 Archetype 的底层存储操作
- **真正的存储归属 Archetype**，参见 `kb-core-ecs.md` 的架构描述

## 决策

- **Chunk 不再是存储单元**：将 Chunk 降级为 readonly struct 视图，消除多 Chunk 复杂度
- **每个 Archetype 精确对应一个 Chunk**：查询接口（`GetChunkSpan()`）永远返回长度为 1 的 span
- **SoA 布局仍在 Archetype 中保持**：列式排列、同一组件的所有行在内存中连续
- **swap-remove 仍在 Archetype 中实现**：删除用 swap-remove 保证紧凑性，逐列复制最后一行数据
- **不支持托管引用组件**：`flat byte[]` 不含 GC 跟踪，在 Archetype 构造时 fail fast

## 认知模型

- 理解 Chunk 时，应该把它看成：**Archetype 的一张只读门票**
- Chunk 的主要工作就是：暴露 `Count`、`GetEntities()`、`GetComponentSpan<T>()`、`GetComponentSpanAt<T>()` 给外部只读查询
- 常见误解：
  - Chunk 不再有自己的 `_data` / `_entities` / `_columnByteOffsets`——这些都在 Archetype 上
  - `Capacity` 是 Archetype 的物理容量，不是 Chunk 的概念
  - `Count` 是活着的实体数

## 入口

- 第一次读或加功能，先看：
  - `Chunk.cs`：93 行，全部是 Archetype 的委托方法
  - `Archetype.cs`：真正的存储和实体操作
- 修 bug，先看：
  - `Archetype.EnsureCapacity()`：扩容搬运是否正确处理了每列偏移的变化
  - `Archetype.RemoveAt()`：swap-remove 是否更新了被移动实体的 location

## 坑点

- Chunk 是 readonly struct（值类型），不可存字段、不可作为可写状态持有者
- 所有存储扩容逻辑在 Archetype 上，Chunk 不提供直接的 EnsureCapacity
- Chunk 没有被引用篡改风险——struct 的 `_archetype` 字段不允许外部修改
- `GetChunkSpan()[0]` 总是访问唯一 Chunk 视图，不再需要遍历多个 chunk
- 如果未来需要超过单块 `byte[]` 限制（~2GB）的场景，需要重新引入多存储块方案
