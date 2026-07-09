---
title: CommandStream Game Steady-State Perf
module: CommandStreamGame.Perf
description: Independent MiniArch CommandStream vs Friflo vs Arch command-buffer + reused-world steady game benchmark
updated: 2026-07-09
---
# CommandStream Game Steady-State Perf

## 这个模块是干什么的

- 这个模块负责：
  - 用独立 perf 项目对比 MiniArch CommandStream、Friflo、Arch 的 command buffer + World/Store 全链路。
  - 模拟长期稳定游戏 tick：query 读取、record 命令、submit/playback 应用到复用 world。
  - 输出吞吐、checksum、live count、GC、heap delta，以及 query/record/apply 阶段占比。
- 这个模块不负责：
  - 替代 `perf/CommandBuffer.Perf` 的 microbenchmark。
  - 证明 ECS 行为正确；行为正确性仍由单元测试覆盖。

## 架构

- 核心组成：
  - `tools/perf/CommandBufferGame.Perf/Program.cs`：组件、runner、三方场景实现都在单文件中，保持独立。
  - `MiniArchCommandStreamSteadyCombatWorld`：`MiniArch.World` + `MiniArch.Core.CommandStream.Submit()`。
  - `FrifloSteadyCombatWorld`：`EntityStore` + `GetCommandBuffer()` + `ReuseBuffer = true` + `Playback()`。
  - `ArchSteadyCombatWorld`：`Arch.Core.World` + `Arch.Buffer.CommandBuffer.Playback(world, true)`。
- 数据流 / 控制流：
  - setup 一次创建 actor/projectile 稳态世界。
  - 每 tick 先 query actor/projectile，收集待销毁 projectile。
  - record 阶段 set actor 热组件、toggle status、destroy projectile、spawn projectile。
  - playback/submit 后活体数量保持稳定。

## 决策

- 新建独立项目，而不是扩展旧 `CommandBuffer.Perf`：旧项目偏 microbenchmark，且包含 world 重建等不适合真实稳态对比的因素。
- Friflo/Arch 使用 NuGet 包版本，保证结果可复现；源码路径只用于核对 API 行为。
- 不使用 `List.RemoveAt(0)` 等 benchmark 容器伪影；销毁目标来自 projectile query scratch buffer。
- Arch 的 CommandBuffer 创建返回 buffered entity，不能直接作为后续真实 handle 保存；因此新 projectile 通过后续 query 参与生命周期，而不是外部队列追踪。
- 2026-06-26：移除 `MiniArchSteadyCombatWorld`（CommandBuffer 版）。它与 `MiniArchCommandStreamSteadyCombatWorld` 已是同一实现（CommandBuffer 类被删，alias 一并移除），重复场景只产生相同数字。MiniArch 场景现统一走 CommandStream。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一个"真实帧管线"压测：读世界 → 录制结构变化 → 批量应用 → 下一帧继续。
- 这个模块里最重要的抽象是：
  - 稳态世界：不在测量循环内重建 world/store。
  - 同构 tick：三方吃相同规模、相同阶段、相同命令数量。
- 常见误解：
  - checksum 三方必须完全相等。不同 ECS 的 entity id 分配和 chunk 顺序可能不同，checksum 主要用于防止 JIT 消除和粗略 sanity check。
  - 只看总 ticks/s 就足够。phase 占比可以判断瓶颈在 query、record 还是 playback/apply。

## 入口

- 第一次读或加功能，先看：
  - `tools/perf/CommandBufferGame.Perf/Program.cs`：完整场景实现和输出格式。
  - `docs/internal/plans/2026-06-09-commandbuffer-game-steady-design.md`：设计背景与公平性规则。
  - `docs/internal/plans/2026-06-09-commandbuffer-game-steady-plan.md`：实现步骤。
- 修 bug，先看：
  - `MiniArchCommandStreamSteadyCombatWorld.RecordCommands`、`FrifloSteadyCombatWorld.RecordCommands`、`ArchSteadyCombatWorld.RecordCommands`：各方命令数量是否仍对齐。
  - `QueryWorld`：销毁 scratch 收集逻辑是否引入容器伪影或漏算 live count。

## 坑点

- 历史上容易出问题的地方：
  - Arch CommandBuffer 的 `Create()` 返回 buffered entity，playback 后外部变量不会变成真实 entity handle。
  - Friflo CommandBuffer 要复用必须设置 `ReuseBuffer = true`，否则 playback 后继续记录会抛异常。
- 容易误判的地方：
  - Debug 运行的结果无意义，必须使用 `-c Release`。
  - heap delta 受包内部缓存和运行时行为影响，要结合 GC counts 与长时间趋势看。
- 改这里时要特别小心：
  - 改 spawn/destroy 数量时，三方都必须同步改。
  - 改 query 条件时，确保 actor query 不误扫 projectile，projectile query 不误扫 actor。
