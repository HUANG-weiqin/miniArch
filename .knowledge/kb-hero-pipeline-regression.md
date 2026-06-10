---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes - 30s timed throughput test
updated: 2026-06-10
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

## 当前 baseline（2026-06-10）

| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement-Buffer（无 collision） | 1416.9 | 0.7 | 42507 | 稳定 |
| Movement-Stream（无 collision） | 1734.3 | 0.6 | 52030 | 稳定 |
| Attack-Buffer（含 collision） | 843.9 | 1.2 | 25318 | 稳定 |
| Attack-Stream（含 collision） | 1124.7 | 0.9 | 33740 | 稳定 |

### 回归阈值

- Movement 吞吐量：≥1133 rounds/s（baseline 的 80%）
- Attack 吞吐量：≥675 rounds/s（baseline 的 80%）
- 内存：heap delta 不能持续增长（允许 ±10% 波动）
