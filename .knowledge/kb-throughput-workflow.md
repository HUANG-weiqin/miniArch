---
title: Throughput Workflow
module: Workspace
description: Reusable fixed-duration throughput comparison workflow for MiniArch and Arch workloads
updated: 2026-06-01
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
  - `ThroughputCaseFactory.CreateAndRun` 创建具体 case 并调用 `WarmupAndMeasure<T>`（泛型约束，消除 interface dispatch 开销）
  - 每轮先 warmup，再跑固定时长循环
  - 每次循环累计 iteration count 和 checksum
  - 每个 engine 汇总 `avg/median/best ops/s`
  - 如果同时跑 `MiniArch` 和 `Arch`，再输出平均吞吐差距
  - 依赖 `MiniArch.Core` / `Arch` 提供真实热路径
  - 和 `kb-test-workflow.md` 互补：那页讲 benchmark 口径，这页讲固定时长吞吐口径

## 决策

- 当 benchmark short job 方差偏大时，优先补一条 fixed-duration throughput 对比，而不是继续堆 iteration count 盲猜均值。
- throughput runner 只输出 `ops/s` 和相对差距，不承担采样定位职责；热点定位仍然交给 sampling profiler。
- 首版先接 query workload，但 runner 结构必须允许后续挂接 `CreateMany`、`Remove`、`Destroy` 等热点。
- Execute 方法必须加 `[MethodImpl(NoOptimization)]`，防止 .NET 8 PGO 把 Set/Create/Destroy 调用当死存储消除。
- Execute 方法的 checksum 必须依赖 workload 的实际结果（Set 后读回值加入 checksum），不能只累加循环索引。

## 当前已验证的 throughput 结论

- `query-with-all-entity`，`EntityCount=100000`，`Duration=5s`，`Repeat=5` 下，`MiniArch` 平均 `31181 ops/s`，`Arch` 平均 `25993 ops/s`，`MiniArch` 领先 `+19.96%`
- `query-with-all-component-span`，`EntityCount=100000`，`Duration=10s`，`Repeat=5` 下，`MiniArch` 平均 `16783 ops/s`，`Arch` 平均 `17060 ops/s`，`MiniArch` 落后 `-1.62%`
- `query-with-all-eachspan-wide`，`EntityCount=100000`，`Duration=10s`，`Repeat=1` 下，`MiniArch` 平均 `6650 ops/s`，`Arch` 平均 `5826 ops/s`，`MiniArch` 领先 `+14%`
- component span 差距已从早期的 `-45.65%` 大幅缩小至 `-1.62%`；当前有效优化是按 matched archetype 外循环 hoist component column index、通过 internal chunk span 避免 `IReadOnlyList<Chunk>` 索引路径，并保留 row loop 的 `Unsafe.Add(ref base, row)` ref arithmetic。
- 解释 component span 差距时必须先区分测量口径：BDN 历史数据曾显示 MiniArch span 领先，但 fixed-duration throughput 显示落后；这通常说明要先隔离 harness / hot-loop / JIT codegen 差异，而不是直接归因到 query matching。
- `EachSpan` 当前只服务读路径；曾做过一次性写诊断来判断是否值得产品化 writable span API，结论是 **不值得**，因此诊断代码和 writable span API 都已删除。
- command-buffer 吞吐在所有 workload 上 MiniArch 均大幅领先 Arch（`+52%~+144%`）
- `set-single-component`（`EntityCount=1000`，`Duration=3s`，`Repeat=3`，NoOptimization + read-back）：`MiniArch` 98,783 vs `Arch` 60,058 → `MiniArch +64.5%`
- `set-two-components`（同上）：`MiniArch` 39,697 vs `Arch` 35,779 → `MiniArch +11.0%`
- `create-destroy-pairwise`（同上）：`MiniArch` 15,761 vs `Arch` 14,551 → `MiniArch +8.3%`

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

- 第一次读或加 workload，先看：
  - `scripts/throughput.ps1`：仓库内直接可跑的吞吐入口
  - `benchmarks/MiniArch.Benchmarks/ThroughputRunner.cs`：runner 如何组织 repeat / warmup / compare
  - `ThroughputCaseFactory`：新增 workload 的分发点
  - `BenchmarkWorldFactory.cs`：如何复用已有 world shape
- 跑自定义吞吐对比（如一次性诊断）：
  - `perf/Throughput.Perf`：自包含的 Release 控制台项目，可快速添加临时 **读路径** benchmark 方法而不影响正式 runner
  - 支持自定义 entityCount、warmup、measurement duration
- 修 bug，先看：
  - `tests/MiniArch.Tests/Core/ThroughputRunnerTests.cs`：参数解析和汇总契约

## 坑点

- 历史上容易出问题的地方：
  - MiniArch 和 Arch 没用同一个 world shape，导致吞吐对比失真
  - warmup 不一致，把 JIT/首次 materialize 混入 steady-state 吞吐
  - repeat 太少，只看单次 ops/s 就下结论
  - Execute 方法的 checksum 不依赖 Set 结果，导致 .NET 8 PGO 在 warmup 后把 Set 调用当死存储消除（MiniArch 受影响更大因为 Set 路径更短更易被 JIT 分析）
- 容易误判的地方：
  - `ops/s` 变快不代表分配也更好，仍要结合 benchmark/MemoryDiagnoser
  - 两边 checksum 都非零不代表 workload 等价，shape 和执行逻辑也必须一致
  - 如果 throughput 结果和 BDN 矛盾，先检查 Execute 方法是否被 JIT 死存储消除
  - 不要因为临时诊断能测到写行为，就把 writable span 当成正式能力保留；如果没有稳定需求，优先删掉诊断代码和 API，保持 `EachSpan` 只覆盖读路径。
  - 对 span 热路径追加 unsafe 前必须先看频率：row loop 每行执行，`Unsafe.Add(ref base, row)` 有价值；component-id lookup 如果已经 hoist 到每个 archetype 一次，额外把数组索引改成 unchecked unsafe 通常不值得保留。
  - Arch 的 `chunk.GetSpan<T>()` 返回 capacity 长度；如果 benchmark 用 `positions.Length` 循环，会读到最后 chunk 的 unused default slots。这个口径偏向增加 Arch 工作量，不是 MiniArch 落后的原因，但会影响绝对吞吐和 checksum 解释。
- 改这里时要特别小心：
  - 新 workload 必须同时给 `MiniArch` 和 `Arch` 实现，避免 runner 只测一边
  - 对 component-consuming workload，要明确是 `row-wise` 还是 `span` 口径，不能混用
  - 所有 Execute 方法必须加 `[MethodImpl(NoOptimization)]`，且 checksum 必须依赖 Set/Create/Destroy 的实际结果（读回值加入 checksum），否则 .NET 8 PGO 会优化掉核心 workload

## 标准流程

- 运行默认 query entity 吞吐对比：
  - `powershell -ExecutionPolicy Bypass -File scripts\\throughput.ps1`
- 运行 component span 吞吐对比：
  - `powershell -ExecutionPolicy Bypass -File scripts\\throughput.ps1 -Workload query-with-all-component-span`
- 运行宽查询（6 组件）EachSpan 吞吐对比：
  - `dotnet run -c Release --project perf\\Throughput.Perf`
- 缩短单轮时间做快速 smoke：
  - `powershell -ExecutionPolicy Bypass -File scripts\\throughput.ps1 -DurationSeconds 3 -RepeatCount 3`

