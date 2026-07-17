---
title: Performance Harnesses Disambiguation
module: Meta
description: Matrix of the 7 performance harnesses in miniArch — what each measures, current baselines, and which one is the regression gate
updated: 2026-07-17
---
# Performance Harnesses Disambiguation

## 结论

miniArch 有 **多套性能测试工具**，以下矩阵记录主要工具。它们用相似的词汇（rounds/s、cycles/s、ticks/s、ops/s）但测量完全不同的东西。**唯一作为架构回归门禁的是 `HeroComing.Perf`**；Watch API 发布/优化时额外跑 `WatchApi.Perf`。

## 消歧矩阵

| Harness | 项目路径 | 测量什么 | 单位 | 当前 baseline | 是门禁？ | KB 页 |
|---------|---------|---------|------|--------------|---------|-------|
| **HeroComing.Perf** | `tools/perf/HeroComing.Perf` | 30s 固定时长 movement+attack 全帧吞吐 | rounds/s | Movement 2052.7 / Attack 1246.8（截至 2026-07-06，需人工 `--update-baseline` 刷新） | ✅ **AGENTS.md §5 强制** | `kb-hero-pipeline-regression.md` |
| **PipelineBenchmarkTests** | `tests/HeroPipeline.Tests` | HeroPipeline 微基准（per-operation cycle 计数） | cycles/s | Movement 48883 / Attack 25946 | ❌ 历史参考（已合并到 `kb-hero-pipeline-regression.md`） | `kb-hero-pipeline-regression.md` |
| **SubmitAndSnapshotAsync 内联测量** | `kb-cache-optimization.md` "SubmitAndSnapshotAsync 双缓冲池化" | CommandStream async submit+snapshot 稳态吞吐 | rounds/s | Movement-Stream 1818 / Attack-Stream 1101 | ❌ 优化验证 | `kb-cache-optimization.md` |
| **GameTickSim.Perf** | `tools/perf/GameTickSim.Perf` | 场景化三方对比（MiniArch vs Arch vs DefaultEcs） | ticks/s | 见各场景 | ❌ 竞品对比 | `kb-gameticksim-scenarios.md` |
| **FrifloGameScenarios.Perf** | `tools/perf/FrifloGameScenarios.Perf` | 15 场景跨 ECS 对比（MiniArch vs Friflo 3.x vs Arch 2.x），含子弹地狱/MMO/RPG/AI 等 | ticks/s | 见各场景 | ❌ 竞品对比 | `kb-ecs-comparison.md` |
| **CommandStream.Profile** | `tools/perf/CommandStream.Profile` | CommandStream 专剖：7 个微场景，含 existing-set-multi 与 record/submit/snapshot/clear 分阶段 | ticks/s | 无固定 baseline | ❌ CPU sampling 辅助 | `kb-command-stream.md` |
| **ParallelRecord.Perf** | `tools/perf/ParallelRecord.Perf` | 并行 CommandStream 录制扩展性测试——顺序 vs 并行，3 种配置，含分区策略比较 | ops/s | 无固定 baseline | ❌ 设计验证 | `kb-command-stream.md` |
| **WatchApi.Perf** | `tools/perf/WatchApi.Perf` | Watch API 专项：ChangeWatch/Projected/TransitionWatch 秒级吞吐、steady-state allocation、发布验证 | ops/s | 见 `kb-change-tracking.md` WatchApi.Perf 段 | ❌ API 发布/优化验证 | `kb-change-tracking.md` |
| **DestroyMany.Perf** | `tools/perf/DestroyMany.Perf` | `Destroy(ReadOnlySpan<Entity>)` / `Destroy(query)` / `Clear(query)` vs guarded `for Destroy`，稳态吞吐 + steady-state alloc + threshold sweep + correctness verify | speedup / us/op | 2026-07-10 稳态：full dense 1.9×；query 2.3×；Clear 4.0×；cascade 1.4×；sweep crossover ≈30% | ❌ API 专项证明 | `kb-core-ecs.md` |

## 如何选择

```
你的任务是什么？
├── 架构变更后验证没退化 → HeroComing.Perf --check-baseline（唯一门禁）
├── 对比 MiniArch vs Arch/DefaultEcs/Friflo → GameTickSim.Perf（或 FrifloGameScenarios.Perf 的 15 场景跨库比较）
├── 验证 CommandStream 优化效果 → SubmitAndSnapshotAsync 内联测量
├── 验证 Watch API 吞吐/分配发布状态 → WatchApi.Perf
├── 证明 Destroy(ReadOnlySpan<Entity>)/Destroy(query) 快于 guarded for Destroy → DestroyMany.Perf
├── 微观 per-operation 分析 → PipelineBenchmarkTests
├── 聚焦 CommandStream 热点定位 → CommandStream.Profile + dotnet-trace
├── 评估 CommandStream 顺序 vs 并行录制扩展性 → ParallelRecord.Perf
└── CPU 采样找其他热点 → 见 kb-profiling-workflow.md（不是 harness，是工具）
```

## 为什么数字差这么多？

- **HeroComing.Perf 2052.7 rounds/s** ≈ 每秒 2052.7 个完整游戏帧（500 players + 500 enemies on 100×100 grid，含 query + command + submit 全链路）
- **PipelineBenchmarkTests 48883 cycles/s** ≈ 每秒执行 48883 次 movement pipeline 循环（无 entity 创建，纯 query+逻辑，cycle 级微基准）
- **SubmitAndSnapshotAsync 1818 rounds/s** ≈ 每秒 1818 个全帧（同 HeroComing 场景但走 async submit+snapshot 路径；2026-06-22 历史 datapoint，当前 HeroComing baseline 已更新，原对比逻辑可能已不成立）
- **GameTickSim ticks/s** ≈ 各场景自定义（Combat 3274、ParticleStorm 1691 等），每个场景测量不同的 workload 组合
- **FrifloGameScenarios.Perf ticks/s** ≈ 15 个跨库对比场景（S1-S15），详见 `kb-ecs-comparison.md`
- **CommandStream.Profile ticks/s** ≈ 同一 workload 内的 ticks/s 可比对；不同 workload 的数字差异由 record 密度/结构变更频率决定
- **ParallelRecord.Perf ops/s** ≈ 并行录制相对顺序录制的加速比；用于验证分区策略（range vs component vs per-component-per-thread）在不同 workload 下的扩展性
- **WatchApi.Perf ops/s** ≈ 每秒执行 Watch scenario operation 的次数（如一次 `Diff`，或 churn 场景中的 mutation + `Diff` + `Snapshot`），用于同一 scenario 内 before/after 比较，不与 Hero rounds/s 比较
- **DestroyMany.Perf speedup** ≈ 同一 world shape 下 guarded `for Destroy` 总耗时 / batch API 总耗时；setup 不计时，适合证明 Destroy batch API 本身，不可和 Hero rounds/s 比。

> **关键**：不要跨 harness 比较数字。同 harness 内的 before/after 对比才有意义。

## 运行方式

```bash
# 门禁（必须 -c Release）
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline

# 人工刷新 baseline（不要用于普通门禁）
dotnet run -c Release --project tools/perf/HeroComing.Perf --update-baseline

# 竞品对比
dotnet run -c Release --project tools/perf/GameTickSim.Perf

# FrifloGameScenarios 15 场景跨库对比
dotnet run -c Release --project tools/perf/FrifloGameScenarios.Perf

# PipelineBenchmarkTests
dotnet test tests/HeroPipeline.Tests -c Release

# CommandStream 热点定位（配合 dotnet-trace）
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4

# 并行录制扩展性评估
dotnet run -c Release --project tools/perf/ParallelRecord.Perf

# Watch API 专项吞吐/分配验证（秒级 warmup + measure）
dotnet run -c Release --project tools/perf/WatchApi.Perf -- --entity-count 10000 --warmup-seconds 2 --duration-seconds 5

# Destroy(ReadOnlySpan<Entity>) / Destroy(query) API 专项证明（内置 correctness verify）
dotnet run -c Release --project tools/perf/DestroyMany.Perf

# 详细 workflow 见各 KB 页
```
