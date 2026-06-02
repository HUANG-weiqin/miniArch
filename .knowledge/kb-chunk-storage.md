---
title: Chunk 存储
module: MiniArch.Core
description: 每个 Archetype 的密集组件列存储 — byte[] SoA 布局，可增长，达上限后自动开新 chunk
updated: 2026-06-02
---
# Chunk 存储

## 这个模块是干什么的

- Chunk 是 Archetype 的存储单元：一个 Chunk 存储属于同一 Signature 的若干实体
- 组件数据以 SoA（列式）方式排列在单块 `byte[]` 中，同一组件的所有行在内存中连续
- Chunk 从~16KB 初始容量开始，按需增长到~512KB；超过 512KB 后 Archetype 自动开新 chunk
- Chunk 本身不感知 entity 的生命周期语义，只负责按行列存取字节

## 架构

- 核心组成：
  - `_data: byte[]` — 单块连续内存，存放所有列的数据
  - `_entities: Entity[]` — 并行数组，和 byte 数据保持同样的行索引
  - `_columnByteOffsets: int[]` — 每列在 `_data` 中的起始偏移
  - `_elementSizes: int[]` — 每列的单个元素字节大小
  - `_componentIdToColumnIndex: int[]` — component id → 列索引的直接映射
  - `_maxCapacity: int` — 该 chunk 的逻辑最大实体数（满容标记）
- 数据流：
  - 写入：`SetComponentAtTyped(col, row, value)` → 计算 offset = `_columnByteOffsets[col] + row * _elementSizes[col]` → `Unsafe.As<byte, T>`
  - 读取：`GetComponentSpanAt<T>(col)` → 返回 `Span<T>`（连续内存的直接视图）
  - 扩容：`EnsureCapacity(n)` → 新 byte[] + 每列 memcpy 旧数据到新位置
  - 删除：`RemoveAt(row)` → swap-remove（最后一行覆盖被删行，每列逐列 move）

## 决策

- **SoA 布局**：选择列式而非行式，因为游戏逻辑绝大多数是遍历所有实体的同一个组件（Position、Velocity 等）
- **可增长 chunk + 上限后开新 chunk**：Hybrid 方案——大多数小 archetype 只需 1 个 chunk 获得连续内存访问性能；大 archetype 到~512KB 后优雅回退到多 chunk
- **按字节计算容量**（而非实体数）：`min = 16KB, max = 512KB`，实际实体上限 = `512KB / bytesPerEntity`，自适应组件大小
- **swap-remove**：删除用 swap-remove 保证 chunk 内始终紧凑，代价是每次删除需要逐列复制最后一行的数据
- **不支持托管引用组件**：`flat byte[]` 不含 GC 跟踪，含托管引用的组件在 chunk 构造时直接抛出

## 认知模型

- 理解 Chunk 时，应该把它看成：**一块在需要时会变大的连续内存**
- Chunk 的主要工作就是：给列一个偏移，然后按 `offset + row * elementSize` 读写
- 常见误解：
  - `Capacity` 属性返回的是 `_maxCapacity`（逻辑上限），不是 `_entities.Length`（实际缓冲区大小）
  - `Count` 是活着的实体数，`Capacity - Count` 是剩余可写入空间
  - chunk 满了不会修改现有数据——Archetype 会检测到并创建一个新 chunk

## 入口

- 第一次读或加功能，先看：
  - `Chunk.cs`：20 个方法，核心就是列偏移计算和 EnsureCapacity
  - `Archetype.cs`：管理 chunk 列表 + 非满栈
  - `World.cs:GetMaxChunkCapacity()`：容量计算逻辑
- 修 bug，先看：
  - `Chunk.EnsureCapacity()`：扩容搬运是否正确处理了每列偏移的变化
  - `Chunk.RemoveAt()`：swap-remove 是否更新了被移动实体的 location

## 坑点

- `EnsureCapacity` 需要重新计算 `_columnByteOffsets`（新容量下每列起始位置不同），所以不能只是 `Array.Resize`，必须重新 CreateStorage
- 扩容时旧数据每列是连续区间，用 `CopyBlockUnaligned` 整列搬移，不要逐行搬
- `Capacity` 现在返回 `_maxCapacity`（logical），不是 `_entities.Length`（physical），测试和外部调用需要注意
- 显式指定 `chunkCapacity` 的 World 构造调用时，`_adaptiveChunkCapacity` 为 false，`GetMaxChunkCapacity` 仍然生效（因为它是另外的逻辑）
- byte[] 不支持含托管引用的 unmanaged 组件——构造时 `ThrowIfManagedComponent` 会 fail-fast
- 连续内存模式下，空 chunk 不会被缩容——如果 burst 创建后大量 destroy，内存仍维持在峰值（这是选择 Hybrid 方案的理由之一，超限后新 chunk 的空 chunk 可释放）
