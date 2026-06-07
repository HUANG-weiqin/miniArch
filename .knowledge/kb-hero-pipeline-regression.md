---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes - 30s timed throughput test
updated: 2026-06-07
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

## 当前 baseline（2026-06-07）

| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement（无 collision） | 1070.0 | 0.9 | 32099 | 稳定 |
| Attack（含 collision） | 254.4 | 3.9 | 7632 | 稳定 |

### 回归阈值

- Movement 吞吐量：≥855 rounds/s（baseline 的 80%）
- Attack 吞吐量：≥203 rounds/s（baseline 的 80%）
- 内存：heap delta 不能持续增长（允许 ±10% 波动）
- Attack Gen0 GC：≤10（30s 测试窗口）

## GC 调优历史

### 2026-06-07: CommandBuffer archetype cache 优化

**问题**：Attack Gen0 = 23（vs Movement 3），每轮分配 125 KB。

**根因**：`CommandBuffer.BuildCreatedEntityComponents()` 使用单 entry archetype cache。Attack 管线在 Tick 2 交替创建 12 组件和 7 组件实体，cache 反复 miss，每次 miss 分配 `new ComponentType[]` + `new Signature`（~122 bytes），1000 次 = ~125 KB/round。

**修复**：将单 entry cache 替换为固定 8 entry 的 bounded cache（`ArchetypeCacheEntry[]`），以 `(hash, count)` 做 quick reject + exact compare。`LookupArchetypeCache()` 和 `InsertArchetypeCache()` 两个方法同时用于 `BuildCreatedEntityComponents()`（单线程路径）和 `SubmitFromFrozen()`（异步路径）。

**结果**：Attack 每轮分配 125 KB → 0.70 KB（179x 降低），Gen0 23 → 4。吞吐量无变化（254 r/s），无时间开销。
