---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes — 30s timed throughput test; also covers PipelineBenchmarkTests (history reference)
updated: 2026-07-06 (刷新 baseline: Movement 2052.7 / Attack 1246.8; 阈值同步)
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
- 默认运行只测量并打印结果，不写 baseline
- `--check-baseline`：读取本页阈值并作为门禁比较，低于阈值时进程返回非 0
- `--update-baseline`：人工确认刷新基线时才写回本页，只替换 baseline/阈值区块

## 当前 baseline（2026-07-06）

| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement（无 collision） | 2052.7 | 0.5 | 61582 | 稳定 |
| Attack（含 collision） | 1246.8 | 0.8 | 37404 | 稳定 |

### 回归阈值

- Movement 吞吐量：≥1642 rounds/s（baseline 的 80%）
- Attack 吞吐量：≥997 rounds/s（baseline 的 80%）
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

> **阈值说明**：baseline × 80% 四舍五入。随 baseline 刷新同步（当前：2052.7 × 80% ≈ 1642，1246.8 × 80% ≈ 997）。

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
