---
title: Hero Pipeline Benchmarks
module: HeroPipeline.Tests
description: Hero project pipeline benchmarks ported to miniArch for performance profiling
updated: 2026-05-29
---
# Hero Pipeline Benchmarks

## 这个模块是干什么的

- 这个模块负责：
  - 提供 Hero 项目 5 个端到端 pipeline benchmark 的本地运行能力
  - 覆盖 Movement / SimpleAttack / AttackWithTrigger / FullCardPlayWithCollision / FullCardPlayToArmor 五条链路
  - 让 MiniArch 团队能在不依赖 Godot 的情况下定位 pipeline 性能瓶颈
- 这个模块不负责：
  - Hero 项目的功能测试
  - Godot Bridge 层的验证
  - MiniArch 核心运行时的实现

## 架构

- 核心组成：
  - `EcsBaseline/`（25 文件）：纯自包含管线，组件(8) + 系统(6) + 表/处理器(10) + Slot(2)
  - `EcsSystemRuntime/`（4 文件）：MiniArchRuntime, FrameView, FrameContext, ISystem，不含 Bridge
  - `GameplayEcs/`（52 文件）：Characters(30) + Cards(11) + Trigger(2) + TurnSerial(6) + Collision(3)
  - `PipelineBenchmarkTests.cs` + `Fixtures/` + `Support/`（7 文件）：5 个 benchmark 入口 + 4 个 fixture + 2 个 helper
- 数据流 / 控制流：
  - Validation → RuleDispatch → EffectDispatch → Trigger → ModifierApply → Spawn
  - 所有 5 个 benchmark 共享同一套 runtime 和 fixture 体系
- 和其他模块的交互方式：
  - 依赖 `MiniArch.Core`（通过 ProjectReference）
  - 源码保留 `Hero.*` 命名空间，不修改原始命名空间

## 决策

- 使用 git worktree（`.worktrees/hero-pipeline-benchmarks`）隔离工作，不影响 main 分支
- 源码按原始命名空间（`Hero.Ecs`, `Hero.GameplayEcs.*`）原样拷贝，不做 namespace 重写
- 排除 5 个不需要的文件：GameplaySpawnKindCatalog, CharacterActionBootstrap, CardDrawBootstrap, TurnSerialRegistrations, TurnEffectRegistrations
- 排除 EcsSystemRuntime/Bridge/（Godot 层代码）
- 使用标准 `Microsoft.NET.Sdk` 而非 `Godot.NET.Sdk`，去掉 Godot 依赖

## 认知模型

- 理解这个模块时，应该把它看成：
  - Hero 项目 pipeline 逻辑在 MiniArch 上的可独立运行的镜像
- 这个模块里最重要的抽象是：
  - `CoreTestFixture`：runtime + 核心系统装配
  - `MiniArchRuntime`：Tick 驱动的 pipeline 执行器
  - Pipeline 6 阶段流程：Validation → RuleDispatch → EffectDispatch → Trigger → ModifierApply → Spawn

## 入口

- 如果是第一次读这个模块，先看：
  - `PipelineBenchmarkTests.cs`：5 个 benchmark 入口，最直观的端到端用法
  - `Fixtures/CoreTestFixture.cs`：理解 runtime 和系统如何装配
  - `EcsBaseline/Systems/`：6 个核心系统的实现
- 如果是定位性能瓶颈，先看：
  - `EcsBaseline/`（25 文件）+ `EcsSystemRuntime/MiniArchRuntime.cs`：pipeline 核心逻辑全在这里
  - GameplayEcs 主要是注册表和 Bootstrap，对性能影响较小

## 坑点

- 历史上容易出问题的地方：
  - 不要试图把 `Hero.*` 命名空间改成 `MiniArch.*`，这会导致大量文件需要同步修改
  - 不要引入 `EcsSystemRuntime/Bridge/`，那是 Godot 层代码
- 容易误判的地方：
  - 以为需要 GameplayEcs 全部 57 个文件，实际只需要 52 个
  - 以为 csproj 需要显式 Compile Include，实际上 SDK 默认自动包含所有 .cs

## 当前 benchmark 结果（2026-05-29）

### Release 配置（推荐用于性能对比）

| Benchmark | Cycles/sec | Avg/cycle |
|---|---|---|
| Movement | 48,883 | 0.0205 ms |
| Simple Attack | 25,946 | 0.0385 ms |
| Attack + Trigger | 17,320 | 0.0577 ms |
| Full Card Play + Collision | 13,678 | 0.0731 ms |
| Full Card Play to Armor | 13,685 | 0.0731 ms |

### Debug 配置（用于开发调试）

| Benchmark | Cycles/sec | Avg/cycle |
|---|---|---|
| Movement | 6,878 | 0.1454 ms |
| Simple Attack | 3,662 | 0.2730 ms |
| Attack + Trigger | 2,515 | 0.3976 ms |
| Full Card Play + Collision | 1,994 | 0.5014 ms |
| Full Card Play to Armor | 2,028 | 0.4930 ms |

### 性能对比说明

- Release 比 Debug 快约 7 倍（符合预期，因 NuGet 依赖是 Release 预编译包）
- 历史数据（2026-05-25）是在 Debug 配置下运行的
- 当前代码在 Debug 配置下比历史数据提升约 43%-70%

## 关联模块

- `kb-test-workflow.md`：MiniArch 测试和 benchmark 的通用方法论
- `kb-core-ecs.md`：被 benchmark 使用的 ECS 运行时
