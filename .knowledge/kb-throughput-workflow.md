---
title: Throughput Workflow
module: Workspace
description: Reusable fixed-duration throughput comparison workflow for MiniArch and Arch workloads
updated: 2026-06-30 (修正脚本/共享基础设施路径漂移)
---
# Throughput Workflow

## 这个模块是干什么的

- 记录仓库内可复用的吞吐量对比流程
- 给 `MiniArch` 和 `Arch` 在相同 workload 下输出固定时长 `ops/s` 对比

## 架构

- 核心组成：
  - `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/ThroughputRunner.cs`
  - `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/BenchmarkWorldFactory.cs`
  - `tools/scripts/throughput.ps1`
- 数据流 / 控制流：
  - 根据 `workload + engine + entityCount` 构造固定 case → `WarmupAndMeasure<T>`（泛型约束，消除 interface dispatch）→ 先 warmup 再跑固定时长循环 → 每轮累计 iteration count 和 checksum → 输出 avg/median/best ops/s

## 决策

- 当 benchmark short job 方差偏大时，优先补 fixed-duration throughput 对比
- throughput runner 只输出 `ops/s` 和相对差距，不承担采样定位职责
- Execute 方法必须加 `[MethodImpl(NoOptimization)]` + checksum 依赖实际结果，防止 .NET 8 PGO 死存储消除
- MiniArch 的 `EachSpan` throughput case 已移除（API 层级变更为 ChunkView batch API），保留 `ComponentSpan` 和 `Wide ComponentSpan` 变体

## 认知模型

- 一套"同 workload、同预热、同持续时间"的吞吐对比框架

## 入口

- `tools/scripts/throughput.ps1`：直接可跑的吞吐入口
- `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/ThroughputRunner.cs`：runner 组织 repeat / warmup / compare
- `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/BenchmarkWorldFactory.cs`：复用已有 world shape
- `tools/perf/Throughput.Perf`：自包含的 Release 控制台，快速添加临时 benchmark
- `tests/MiniArch.Tests/Core/ThroughputRunnerTests.cs`：参数解析和汇总契约

## 坑点

- MiniArch 和 Arch 必须用同一个 world shape
- warmup 不一致会把 JIT/首次 materialize 混入 steady-state 吞吐
- checksum 不依赖 Set 结果时，.NET 8 PGO 会把 Set 调用当死存储消除
- `ops/s` 变快不代表分配也更好，仍需结合 MemoryDiagnoser
- Arch 的 `chunk.GetSpan<T>()` 返回 capacity 长度（含 unused default slots），会影响绝对吞吐和 checksum 解释
- 新 workload 必须同时给 MiniArch 和 Arch 实现
- MiniArch 的 EachSpan workload 已从 ThroughputRunner 移除，不要引用
