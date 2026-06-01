---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes - 30s timed throughput test
updated: 2026-06-01
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

## 当前 baseline（2026-06-01）

| 链路 | rounds/s | Avg ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement | 692.6 | 1.4 | 20778 | 稳定 |
| Attack | 191.4 | 5.2 | 5741 | 稳定 |

- Movement 阈值：≥554 rounds/s（baseline 80%）
- Attack 阈值：≥153 rounds/s（baseline 80%）

## 入口

- 运行：`dotnet run -c Release --project perf/HeroComing.Perf`
- 修改：`perf/HeroComing.Perf/Program.cs`

## 坑点

- 必须用 `-c Release`（Debug 慢 7 倍）
- 30s 固定时长而非固定轮数（不受单轮波动影响）
- 测试会自动更新 baseline 数据（见 AGENTS.md 架构变更回归门禁）
