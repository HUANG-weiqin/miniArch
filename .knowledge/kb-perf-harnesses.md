---
title: Performance Harnesses Disambiguation
module: Meta
description: Matrix of the 6 performance harnesses in miniArch — what each measures, current baselines, and which one is the regression gate
updated: 2026-07-09
---
# Performance Harnesses Disambiguation

## 结论

miniArch 有 **6 套独立的性能测试工具**，它们用相似的词汇（rounds/s、cycles/s、ticks/s、ops/s）但测量完全不同的东西。**唯一作为架构回归门禁的是 `HeroComing.Perf`**；Watch API 发布/优化时额外跑 `WatchApi.Perf`。

## 消歧矩阵

| Harness | 项目路径 | 测量什么 | 单位 | 当前 baseline | 是门禁？ | KB 页 |
|---------|---------|---------|------|--------------|---------|-------|
| **HeroComing.Perf** | `tools/perf/HeroComing.Perf` | 30s 固定时长 movement+attack 全帧吞吐 | rounds/s | Movement 1512 / Attack 959 | ✅ **AGENTS.md §5 强制** | `kb-hero-pipeline-regression.md` |
| **PipelineBenchmarkTests** | `tests/HeroPipeline.Tests` | HeroPipeline 微基准（per-operation cycle 计数） | cycles/s | Movement 48883 / Attack 25946 | ❌ 历史参考（已合并到 `kb-hero-pipeline-regression.md`） | `kb-hero-pipeline-regression.md` |
| **SubmitAndSnapshotAsync 内联测量** | `kb-cache-optimization.md` "SubmitAndSnapshotAsync 双缓冲池化" | CommandStream async submit+snapshot 稳态吞吐 | rounds/s | Movement-Stream 1818 / Attack-Stream 1101 | ❌ 优化验证 | `kb-cache-optimization.md` |
| **GameTickSim.Perf** | `tools/perf/GameTickSim.Perf` | 场景化三方对比（MiniArch vs Arch vs DefaultEcs） | ticks/s | 见各场景 | ❌ 竞品对比 | `kb-gameticksim-scenarios.md` |
| **CommandStream.Profile** | `tools/perf/CommandStream.Profile` | CommandStream 专剖：6 个微场景，含 record/submit/snapshot/clear 分阶段 | ticks/s | 无固定 baseline | ❌ CPU sampling 辅助 | `kb-command-stream.md` |
| **WatchApi.Perf** | `tools/perf/WatchApi.Perf` | Watch API 专项：ChangeWatch/Projected/TransitionWatch 秒级吞吐、steady-state allocation、发布验证 | ops/s | 见 `kb-change-tracking.md` WatchApi.Perf 段 | ❌ API 发布/优化验证 | `kb-change-tracking.md` |

## 如何选择

```
你的任务是什么？
├── 架构变更后验证没退化 → HeroComing.Perf --check-baseline（唯一门禁）
├── 对比 MiniArch vs Arch/DefaultEcs/Friflo → GameTickSim.Perf
├── 验证 CommandStream 优化效果 → SubmitAndSnapshotAsync 内联测量
├── 验证 Watch API 吞吐/分配发布状态 → WatchApi.Perf
├── 微观 per-operation 分析 → PipelineBenchmarkTests
├── 聚焦 CommandStream 热点定位 → CommandStream.Profile + dotnet-trace
└── CPU 采样找其他热点 → 见 kb-profiling-workflow.md（不是 harness，是工具）
```

## 为什么数字差这么多？

- **HeroComing.Perf 1512 rounds/s** ≈ 每秒 1512 个完整游戏帧（500 players + 500 enemies on 100×100 grid，含 query + command + submit 全链路）
- **PipelineBenchmarkTests 48883 cycles/s** ≈ 每秒执行 48883 次 movement pipeline 循环（无 entity 创建，纯 query+逻辑，cycle 级微基准）
- **SubmitAndSnapshotAsync 1818 rounds/s** ≈ 每秒 1818 个全帧（同 HeroComing 场景但走 async submit+snapshot 路径，比同步 Submit 路径略快因为并行）
- **GameTickSim ticks/s** ≈ 各场景自定义（Combat 3274、ParticleStorm 1691 等），每个场景测量不同的 workload 组合
- **CommandStream.Profile ticks/s** ≈ 同一 workload 内的 ticks/s 可比对；不同 workload 的数字差异由 record 密度/结构变更频率决定
- **WatchApi.Perf ops/s** ≈ 每秒执行 Watch scenario operation 的次数（如一次 `Diff`，或 churn 场景中的 mutation + `Diff` + `Snapshot`），用于同一 scenario 内 before/after 比较，不与 Hero rounds/s 比较

> **关键**：不要跨 harness 比较数字。同 harness 内的 before/after 对比才有意义。

## 运行方式

```bash
# 门禁（必须 -c Release）
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline

# 人工刷新 baseline（不要用于普通门禁）
dotnet run -c Release --project tools/perf/HeroComing.Perf --update-baseline

# 竞品对比
dotnet run -c Release --project tools/perf/GameTickSim.Perf

# PipelineBenchmarkTests
dotnet test tests/HeroPipeline.Tests -c Release

# CommandStream 热点定位（配合 dotnet-trace）
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4

# Watch API 专项吞吐/分配验证（秒级 warmup + measure）
dotnet run -c Release --project tools/perf/WatchApi.Perf -- --entity-count 10000 --warmup-seconds 2 --duration-seconds 5

# 详细 workflow 见各 KB 页
```
