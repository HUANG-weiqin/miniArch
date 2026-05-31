---
title: Debug Metrics
module: MiniArch.DebugMetrics
description: DEBUG-only metrics snapshots and pasteable diagnostic reports for MiniArch World and CommandBuffer allocation pressure
updated: 2026-05-31
---
# Debug Metrics

## 这个模块是干什么的

- 这个模块负责：
  - 在 DEBUG 构建里记录内部扩容和拷贝压力。
  - 给用户提供可直接粘贴的文本报告。
  - 区分 `CommandBuffer` 的 overflow、array grow、slab rent、snapshot copy。
  - 区分 `World` 的 entity metadata grow 和 destroy scratch grow。
- 这个模块不负责：
  - 在 Release 热路径里做计数。
  - 做全局 diagnostics center、事件回调或日志系统。
  - 自动给出优化策略；报告只暴露足够定位的事实。

## 架构

- 核心组成：
  - `src/MiniArch/Core/DebugMetrics.cs`：公开 `WorldDebugMetrics` 与 `CommandBufferDebugMetrics` 快照类型。
  - `World.GetDebugMetrics()` / `World.GetDebugReport()`：world metadata 与 destroy scratch 报告入口。
  - `CommandBuffer.GetDebugMetrics()` / `CommandBuffer.GetDebugReport()`：recording buffer、slab、snapshot 报告入口。
- 数据流 / 控制流：
  - 用户在 DEBUG 构建里跑目标 workload。
  - workload 触发内部路径，例如 `InlineMap.Set` overflow、ops pool/lookup 扩容、ArrayPool slab rent、`FrameDelta.DeepCopyOwnedData()`。
  - 对象本地累积计数器。
  - 用户调用 `GetDebugReport()` 拿稳定多行文本并提交给维护者。
- 和其他模块的交互方式：
  - 依赖 `CommandBuffer` 的 record/snapshot 路径。
  - 依赖 `World.EnsureCapacity` / entity storage growth / destroy scratch capacity。
  - 由 `tests/MiniArch.Tests/Core/DebugMetricsTests.cs` 覆盖 Debug 与 Release 行为。

## 决策

- 采用 pull-style snapshot API，而不是 callback/event/session：
  - 用户只需要在 workload 后拉一次报告。
  - 不引入跨对象生命周期或线程订阅问题。
  - 不污染正常 gameplay 代码路径。
- API 在 Release 中仍存在，但返回 disabled/default：
  - 用户代码不用包自己的 `#if DEBUG` 才能编译。
  - 真正计数字段和累加语句必须放在 `#if DEBUG` 内。
- 报告 attached to owner：
  - `World` 只报告 world-owned metadata/scratch。
  - `CommandBuffer` 只报告 command-buffer-owned overflow/grow/slab/snapshot。
  - 不新增全局状态，避免多 world / 多 buffer 混淆。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 每个对象自己的 DEBUG 黑匣子飞行记录仪。
- 这个模块里最重要的抽象是：
  - `WorldDebugMetrics`：world storage 压力快照。
  - `CommandBufferDebugMetrics`：command recording 与 snapshot 压力快照。
  - `GetDebugReport()`：用户粘贴给维护者的事实报告。
- 常见误解：
  - 以为指标是性能 benchmark；它不是，只是定位 allocation/grow 来源。
  - 以为 Release 也会累积计数；Release 下报告显示 disabled。

## 入口

- 如果是第一次读这个模块，先看：
  - `src/MiniArch/Core/DebugMetrics.cs`：所有公开字段。
  - `src/MiniArch/Core/CommandBuffer.cs` 的 `GetDebugReport()`：CommandBuffer 报告格式。
  - `src/MiniArch/Core/World.cs` 的 `GetDebugReport()`：World 报告格式。
- 如果是修 bug，先看：
  - `tests/MiniArch.Tests/Core/DebugMetricsTests.cs`：API 契约与 Release disabled 契约。
- 如果是加功能，先看：
  - `CommandBuffer.CopyData` / `CopyComponentFromChunk`：slab rent 计数。
  - `CommandBuffer.Snapshot` / `SubmitAndSnapshotAsync`：snapshot deep copy 计数。
  - `World.EnsureCapacity` / `EnsureEntityCapacity` / `EnsureDestroyScratchCapacity`：world grow 计数。

## 坑点

- 历史上容易出问题的地方：
  - 只统计 `Snapshot()`，漏掉 `SubmitAndSnapshotAsync()` 后台构建 delta 的 deep copy。
  - 把报告 API 包进 `#if DEBUG` 导致用户代码 Release 不可编译。
  - 为了指标引入 global diagnostics，反而让多 world 场景的归因变差。
- 容易误判的地方：
  - `InlineMap` 第 5 个不同 component type 才会分配 overflow；覆盖已有 key 不算 overflow allocation。
  - `ArrayPool<byte>.Rent(size)` 返回的 slab 长度可能大于请求 size；报告里的 `slab_rent_bytes` 记录实际 rent 长度。
  - `FrameDelta.DeepCopyOwnedData()` 的 byte 计数不包含 created components array 本身，只表示 owned component data bytes。
- 改这里时要特别小心：
  - 新增计数器时，字段和累加都必须在 `#if DEBUG` 内，Release 只保留默认快照/disabled report。
  - 不要让指标改变 `Submit()` / `Snapshot()` / `Replay()` 的语义或对象所有权。
  - 报告 label 要稳定，用户会把文本粘贴回来用于定位。

## 关联模块

- `kb-core-ecs.md`：world storage 与 Release 热路径约束。
- `kb-command-buffer-feasibility.md`：CommandBuffer recording、slab arena 和 snapshot 路径。
- `kb-snapshot-persistence.md`：`FrameDelta` owned data 与 replay 边界。
