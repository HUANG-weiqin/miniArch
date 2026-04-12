---
title: Throughput Workflow
module: Workspace
description: Reusable fixed-duration throughput comparison workflow for MiniArch and Arch workloads
updated: 2026-04-12
---
# Throughput Workflow

## 这个模块是干什么的

- 这个模块负责：
  - 记录仓库内可复用的吞吐量对比流程
  - 给 `MiniArch` 和 `Arch` 在相同 workload 下输出固定时长 `ops/s` 对比
  - 说明什么时候该用 throughput runner，而不是只看 BenchmarkDotNet short job
- 这个模块不负责：
  - 替代功能正确性测试
  - 替代 CPU sampling
  - 替代分配分析

## 架构

- 核心组成：
  - `benchmarks/MiniArch.Benchmarks/ThroughputRunner.cs`
  - `scripts/throughput.ps1`
  - `benchmarks/MiniArch.Benchmarks/BenchmarkWorldFactory.cs`
- 数据流 / 控制流：
  - 先根据 `workload + engine + entityCount` 构造固定 case
  - 每轮先 warmup，再跑固定时长循环
  - 每次循环累计 iteration count 和 checksum
  - 每个 engine 汇总 `avg/median/best ops/s`
  - 如果同时跑 `MiniArch` 和 `Arch`，再输出平均吞吐差距
- 和其他模块的交互方式：
  - 依赖 `BenchmarkWorldFactory` 提供同构 world shape
  - 依赖 `MiniArch.Core` / `Arch` 提供真实热路径
  - 和 `kb-test-workflow.md` 互补：那页讲 benchmark 口径，这页讲固定时长吞吐口径

## 决策

- 当 benchmark short job 方差偏大时，优先补一条 fixed-duration throughput 对比，而不是继续堆 iteration count 盲猜均值。
- throughput runner 只输出 `ops/s` 和相对差距，不承担采样定位职责；热点定位仍然交给 sampling profiler。
- 首版先接 query workload，但 runner 结构必须允许后续挂接 `CreateMany`、`Remove`、`Destroy` 等热点。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一套“同 workload、同预热、同持续时间”的吞吐对比框架
- 这个模块里最重要的抽象是：
  - `ThroughputOptions`
  - `IThroughputCase`
  - `ThroughputEngineSummary`
- 常见误解：
  - 认为 `ops/s` 可以替代 BenchmarkDotNet 的全部信息
  - 认为 fixed-duration runner 可以回答热点分布问题

## 入口

- 如果是第一次读这个模块，先看：
  - `scripts/throughput.ps1`：仓库内直接可跑的吞吐入口
  - `benchmarks/MiniArch.Benchmarks/ThroughputRunner.cs`：runner 如何组织 repeat / warmup / compare
- 如果是修 bug，先看：
  - `tests/MiniArch.Tests/Core/ThroughputRunnerTests.cs`：参数解析和汇总契约
- 如果是加 workload，先看：
  - `ThroughputCaseFactory`：新增 workload 的分发点
  - `BenchmarkWorldFactory.cs`：如何复用已有 world shape

## 坑点

- 历史上容易出问题的地方：
  - MiniArch 和 Arch 没用同一个 world shape，导致吞吐对比失真
  - warmup 不一致，把 JIT/首次 materialize 混入 steady-state 吞吐
  - repeat 太少，只看单次 ops/s 就下结论
- 容易误判的地方：
  - `ops/s` 变快不代表分配也更好，仍要结合 benchmark/MemoryDiagnoser
  - 两边 checksum 都非零不代表 workload 等价，shape 和执行逻辑也必须一致
- 改这里时要特别小心：
  - 新 workload 必须同时给 `MiniArch` 和 `Arch` 实现，避免 runner 只测一边
  - 对 component-consuming workload，要明确是 `row-wise` 还是 `span` 口径，不能混用

## 标准流程

- 运行默认 query entity 吞吐对比：
  - `powershell -ExecutionPolicy Bypass -File scripts\\throughput.ps1`
- 运行 component span 吞吐对比：
  - `powershell -ExecutionPolicy Bypass -File scripts\\throughput.ps1 -Workload query-with-all-component-span`
- 缩短单轮时间做快速 smoke：
  - `powershell -ExecutionPolicy Bypass -File scripts\\throughput.ps1 -DurationSeconds 3 -RepeatCount 3`

## 关联模块

- `kb-test-workflow.md`：benchmark 与 throughput 的配合方式
- `kb-profiling-workflow.md`：热点定位方法
- `scripts/throughput.ps1`：当前可直接复用的吞吐入口
