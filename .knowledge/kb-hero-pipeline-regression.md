---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes - 30s timed throughput test
updated: 2026-06-01
---
# Hero Pipeline Regression Test

## 这个模块是干什么的

- 这个模块负责：
  - 作为架构变更的一等回归门禁，确保改动不破坏 pipeline 正确性和性能
  - 提供 30 秒固定时长吞吐量测试，覆盖 movement + attack 两条链路
  - 检测内存泄漏（heap delta 必须稳定）
- 这个模块不负责：
  - 精确性能对比（那是 BenchmarkDotNet 的职责）
  - 功能测试（那是 HeroPipeline.Tests 的职责）

## 架构

- 核心组成：
  - `perf/HeroComing.Perf/Program.cs`：单文件控制台应用
  - 引用 `tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj` 获取全部 pipeline 代码
  - 使用 `CoreTestFixture` + `CharacterTestFixture` 装配 runtime
- 数据流 / 控制流：
  - 500 players + 500 enemies on 100x100 grid
  - 每轮：500 个 MoveRequest + StepUntilStable()，或 500 个 AttackRequest + StepUntilStable()
  - 每 100 轮报告：rounds/s, heap MB, delta KB, working set, GC gen0/1/2
  - 最终汇总：总轮数、平均吞吐量、平均耗时、heap delta、内存稳定性

## 决策

- 选择 30 秒固定时长而非固定轮数：能稳定测出吞吐量，不受单轮波动影响
- 选择 500 实体而非 1000：在 attack 链路下仍能在合理时间完成足够轮数
- 选择 movement + attack 两条链路：movement 测纯 pipeline 开销，attack 加上 collision 瓶颈
- 吞吐量阈值 ±20%：太紧会因 CI 波动误报，太松失去回归检测意义

## 认知模型

- 理解这个模块时，应该把它看成：
  - 架构变更的"门卫"——改完代码必须跑它，通过才能提交
- 这个模块里最重要的指标是：
  - **吞吐量（rounds/s）**：movement ≥500, attack ≥33
  - **内存稳定性**：heap delta 必须在合理范围内，不能持续增长
  - **正确性**：不能崩溃、不能抛异常

## 入口

- 运行测试：
  - `dotnet run -c Release --project perf/HeroComing.Perf`
- 修改测试：
  - `perf/HeroComing.Perf/Program.cs`：唯一源文件
- 查看 baseline 数据：
  - 本文档"当前 baseline"节

## 坑点

- 必须用 `-c Release`：Debug 编译会慢 7 倍，且 NuGet 依赖是 Release 预编译包
- 不要在测试中加入额外逻辑：保持纯净的 pipeline 吞吐量测量
- 如果 CI 环境波动导致误报：先在本地确认，再调整阈值

## 当前 baseline（2026-06-01）

| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement（无 collision） | 691.6 | 1.4 | 20748 | 稳定 |
| Attack（含 collision） | 203.0 | 4.9 | 6091 | 稳定 |

### 回归阈值

- Movement 吞吐量：≥553 rounds/s（baseline 的 80%）
- Attack 吞吐量：≥162 rounds/s（baseline 的 80%）
- 内存：heap delta 不能持续增长（允许 ±10% 波动）
