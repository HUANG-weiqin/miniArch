---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes — 30s timed throughput test; also covers PipelineBenchmarkTests (history reference)
updated: 2026-06-30 (合并 kb-hero-pipeline-benchmarks.md)
---
# Hero Pipeline Regression Test

## 这个模块是干什么的

- 架构变更的一等回归门禁——改完必须跑它，通过才能提交
- 30 秒固定时长吞吐量测试，覆盖 movement + attack 两条链路
- 检测内存泄漏（heap delta 必须稳定）

## 架构

- `tools/perf/HeroComing.Perf/Program.cs`：单文件控制台应用
- 引用 `tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj` 获取 pipeline 代码
- 500 players + 500 enemies on 100x100 grid

## 当前 baseline（2026-06-30）

| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement（无 collision） | 1512.5 | 0.7 | 45374 | 稳定 |
| Attack（含 collision） | 958.9 | 1.0 | 28767 | 稳定 |

### 回归阈值

- Movement 吞吐量：≥1210 rounds/s（baseline 的 80%）
- Attack 吞吐量：≥767 rounds/s（baseline 的 80%）
- 内存：heap delta 不能持续增长（允许 ±10% 波动）

### 如果失败

吞吐量低于阈值 → 用 `kb-profiling-workflow.md` 的 CPU 采样流程定位热点：

```powershell
# 冷路径（query refresh/matching）：
dotnet run -c Release --project tests\MiniArch.Benchmarks -- profile-query --scenario with-all --temperature cold --entity-count 100000 --duration 8 --warmup 1

# 热路径（steady-state traversal）：
dotnet run -c Release --project tests\MiniArch.Benchmarks -- profile-query --scenario with-all --temperature hot --entity-count 100000 --duration 8 --warmup 1
```

已知热点路径见 `kb-cache-optimization.md` 热路径分析表 + `kb-query-invalidation.md`（`EnsureRefreshed` 快路径 vs `AppendNewArchetypes` 慢路径）。

> **阈值说明**：1512.5 × 80% = 1210（四舍五入）。此前写 1209 是舍入误差，已修正。

### PipelineBenchmarkTests（历史参考，非门禁）

这是 per-operation cycle 计数的微基准（门禁是 HeroComing.Perf 的 rounds/s，详见 `kb-perf-harnesses.md`）。

| Benchmark | Cycles/sec |
|-----------|-----------|
| Movement | 48,883 |
| Simple Attack | 25,946 |
| Attack + Trigger | 17,320 |
| Full Card Play + Collision | 13,678 |
| Full Card Play to Armor | 13,685 |

**架构**：`tests/HeroPipeline.Tests/PipelineBenchmarkTests.cs` + `Fixtures/CoreTestFixture.cs`。源码按原始命名空间（`Hero.*`）原样拷贝，使用 `Microsoft.NET.Sdk` 而非 `Godot.NET.Sdk`。数据日期 2026-05-29。**不跨工具比较 cycles/s 与 rounds/s**。
