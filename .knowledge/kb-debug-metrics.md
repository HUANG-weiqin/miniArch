---
title: Debug Metrics
module: MiniArch.DebugMetrics
description: DEBUG-only metrics snapshots and pasteable diagnostic reports for MiniArch World and CommandBuffer allocation pressure
updated: 2026-06-01
---
# Debug Metrics

## 这个模块是干什么的

- 在 DEBUG 构建里记录内部扩容和拷贝压力
- 给用户提供可直接粘贴的文本报告

## 架构

- 核心组成：`src/MiniArch/Core/DebugMetrics.cs`（`WorldDebugMetrics` + `CommandBufferDebugMetrics` 快照类型）
- API：`World.GetDebugMetrics()` / `World.GetDebugReport()`、`CommandBuffer.GetDebugMetrics()` / `CommandBuffer.GetDebugReport()`
- 数据流：用户跑 workload → 内部路径积累计数器 → 调用 `GetDebugReport()` 拿稳定多行文本

## 决策

- pull-style snapshot API，不引入 callback/event/session
- API 在 Release 中仍存在但返回 disabled 值；计数字段和累加语句在 `#if DEBUG` 内
- 报告 attached to owner（World 只报告 world-owned，CommandBuffer 只报告 buffer-owned），不新增全局状态

## 认知模型

- 每个对象自己的 DEBUG 黑匣子飞行记录仪

## 入口

- `src/MiniArch/Core/DebugMetrics.cs`：所有公开字段
- `tests/MiniArch.Tests/Core/DebugMetricsTests.cs`：API 契约与 Release disabled 契约

## 坑点

- 新增计数器时，字段和累加都必须在 `#if DEBUG` 内，Release 只保留默认快照
- 报告 label 要稳定——用户会把文本粘贴回来定位
