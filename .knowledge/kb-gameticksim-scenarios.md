---
title: GameTickSim Scenarios
module: GameTickSim.Perf
description: GameTickSim isolated scenario benchmarks and BulletHell workload design rules
updated: 2026-06-30 (修正 front matter module 格式与路径漂移)
---
# GameTickSim Scenarios

## 这个模块是干什么的

- 这个模块负责：
  - 用固定时长场景对比 MiniArch、DefaultEcs、Arch 的 tick 吞吐、GC 和堆稳定性。
  - 保留从纯迭代、结构变化到 BulletHell 的逐级压力场景。
  - 用 BulletHell 系列模拟高频 create/destroy、query scan、archetype fragmentation 和复杂游戏逻辑。
- 这个模块不负责：
  - 证明 ECS core 行为正确；行为正确性仍由 `tests/MiniArch.Tests` 负责。
  - 替代专项 microbenchmark；结构变化、query、command buffer 仍应在各自 benchmark 中独立测量。

## 架构

- 核心组成：
  - `tools/perf/GameTickSim.Perf/ScenarioBenchmark.cs`：`RunAll` 注册孤立场景，并按 MiniArch、DefaultEcs、Arch 三方同构执行。
  - `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/GameTickComponents.cs`：三方 benchmark 共享组件定义，避免输入结构不一致。
  - `MeasureTimed`：固定 2 秒 warmup，然后按场景自己的 `durationSeconds` 测量 ops/s、ms/op、heap delta 和 GC。
- 数据流 / 控制流：
  - `Program.cs --scenarios <name>` 进入 `ScenarioBenchmark.RunAll`。
  - 每个场景内部创建三套 world，用同一随机种子生成同构实体流。
  - BulletHell 场景通常按 move/create/buff 或 AI/collision scan/destroy apply 分阶段累积耗时。

## 决策

- BulletHell 系列保留逐级载荷，而不是覆盖旧场景：
  - `K-BulletHell` 测高频创建、移动、碰撞扫描和销毁。
  - `L-BulletHellBuffs` 在 K 的基础上测 buff 结构性 Add/Remove 和 archetype fragmentation。
  - `M-BulletHellWarfare` 增加 3 boss、homing、minion AI、多目标碰撞、4 状态独立 timer 和更高 create/destroy 压力。
- 多状态不要复用单个 `StatusTimer`。同一实体可能同时有 Burning/Poisoned/Frozen/Shocked，所以 M 场景使用 `BurningTimer`、`PoisonedTimer`、`FrozenTimer`、`ShockedTimer` 分别移除对应 tag。
- Player bullet 在 M 场景不加 `Damage` 组件，而用固定 `playerBulletDamage`。这样 `With<Position>().With<Damage>()` 仍只代表 enemy bullet，避免 enemy/player projectile query 互相污染。
- 三方实现优先保持输入结构一致；允许 API 形态不同，但随机调用顺序、创建数量和组件组合应尽量对齐。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一个越来越接近真实游戏 tick 的压力阶梯，而不是单一 benchmark。
- 这个模块里最重要的抽象是：
  - 同构 workload：三方 ECS 必须吃同一类实体、组件和系统顺序。
  - 阶段分解：用 phase breakdown 判断瓶颈来自 iterate、create、buff、collision scan 还是 destroy apply。
- 常见误解：
  - 只看总 ops/s 就能定位问题。BulletHell 场景必须看 phase breakdown，否则 create、scan、buff 可能互相掩盖。
  - buff timer 可以共用一个组件。多状态同时存在时，共用 timer 会导致移除错误或禁止组合。

## 入口

- 第一次读或加功能，先看：
  - `tools/perf/GameTickSim.Perf/ScenarioBenchmark.cs`：场景注册、三方实现和 phase breakdown 都在这里。
  - `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/GameTickComponents.cs`：新增场景组件必须先在共享组件项目里定义。
  - `tools/perf/GameTickSim.Perf/Program.cs`：`--scenarios` 单场景入口。
- 修 bug，先看：
  - `RunBulletHell`、`RunBulletHellBuffs`、`RunBulletHellWarfare`：BulletHell 系列应先确认 query 选择是否互相污染。
  - `MeasureTimed`：确认测量时长、warmup 和 GC 口径是否符合当前结论。

## 坑点

- 历史上容易出问题的地方：
  - 给 player bullet 也加 `Damage` 后，enemy bullet query `With<Position>().With<Damage>()` 会误扫 player bullet。
  - 多状态共用 `StatusTimer` 会让一个状态过期时移除其他状态需要的 timer。
  - 在扫描 query 时立即 Destroy 会破坏遍历，BulletHell 系列应继续使用 scratch buffer 先收集后应用。
- 容易误判的地方：
  - `RunAll` 打印的 `GameTickData.DurationSeconds` 是全局提示；孤立场景内部可能传自己的 `durationSeconds` 给 `MeasureTimed`。
  - DefaultEcs 没有 phase breakdown，不代表没有执行相同逻辑；目前只给 MiniArch 和 Arch 打印分阶段数据。
- 改这里时要特别小心：
  - 新增场景后必须同步 `RunAll` 场景数组和 `PrintSummary` 的 `scenarioNames`，否则全量 summary 索引会错位。
  - 所有性能测量使用 `-c Release`，不要用 Debug 结果判断 ECS 性能。
