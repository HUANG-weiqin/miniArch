---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes - 30s timed throughput test
updated: 2026-06-08
---
# Hero Pipeline Regression Test

## 这个模块是干什么的

- 架构变更的一等回归门禁——改完必须跑它，通过才能提交
- 30 秒固定时长吞吐量测试，覆盖 movement + attack 两条链路
- 检测内存泄漏（heap delta 必须稳定）

## 架构

- `perf/HeroComing.Perf/Program.cs`：单文件控制台应用
- 引用 `tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj` 获取 pipeline 代码
- 500 players + 500 enemies on 100x100 grid

## 当前 baseline（2026-06-08）

| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement（无 collision） | 1032.2 | 1.0 | 30966 | 稳定 |
| Attack（含 collision） | 632.6 | 1.6 | 18979 | 稳定 |

### 回归阈值

- Movement 吞吐量：≥825 rounds/s（baseline 的 80%）
- Attack 吞吐量：≥506 rounds/s（baseline 的 80%）
- 内存：heap delta 不能持续增长（允许 ±10% 波动）
