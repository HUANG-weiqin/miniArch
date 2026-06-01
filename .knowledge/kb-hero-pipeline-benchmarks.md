---
title: Hero Pipeline Benchmarks
module: HeroPipeline.Tests
description: Hero project pipeline benchmarks ported to miniArch for performance profiling
updated: 2026-06-01
---
# Hero Pipeline Benchmarks

## 这个模块是干什么的

- 提供 Hero 项目 5 个端到端 pipeline benchmark 的本地运行能力
- 覆盖 Movement / SimpleAttack / AttackWithTrigger / FullCardPlayWithCollision / FullCardPlayToArmor

## 架构

- `tests/HeroPipeline.Tests/` 下三个源文件组 + 测试入口：
  - `EcsBaseline/`（21 文件）：纯自包含管线（组件、系统、表、处理器）
  - `EcsSystemRuntime/`（4 文件）：MiniArchRuntime, FrameView, FrameContext, ISystem
  - `GameplayEcs/`（34 文件）：Characters / Cards / Trigger / TurnSerial / Collision
  - `PipelineBenchmarkTests.cs` + `Fixtures/` + `Support/`

## 决策

- 源码按原始命名空间（`Hero.Ecs`、`Hero.GameplayEcs.*`）原样拷贝
- 使用标准 `Microsoft.NET.Sdk` 而非 `Godot.NET.Sdk`

## 当前结果（2026-05-29, Release）

| Benchmark | Cycles/sec | Avg/cycle |
|---|---|---|
| Movement | 48,883 | 0.0205 ms |
| Simple Attack | 25,946 | 0.0385 ms |
| Attack + Trigger | 17,320 | 0.0577 ms |
| Full Card Play + Collision | 13,678 | 0.0731 ms |
| Full Card Play to Armor | 13,685 | 0.0731 ms |

## 入口

- `PipelineBenchmarkTests.cs`：5 个 benchmark 入口
- `Fixtures/CoreTestFixture.cs`：runtime 和系统装配

## 坑点

- 不要改 `Hero.*` 命名空间（太多文件）
- 不要引入 `EcsSystemRuntime/Bridge/`（Godot 层代码）
