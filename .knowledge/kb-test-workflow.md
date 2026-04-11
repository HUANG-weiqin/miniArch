---
title: Test Workflow
module: MiniArch.Tests
description: How the test suite and mixed structural-change benchmarks are organized and how to run them
updated: 2026-04-11
---
# Test Workflow

## 这个模块是干什么的

- 这个模块负责：
  - 验证 ECS core 的行为
  - 覆盖实体生命周期、chunk 存储、结构迁移和 query
  - 作为 typed-column / direct-index 重构后的行为回归网
  - 提供 `Create / CreateMany / Add / Set / Remove / Destroy` 的 benchmark 口径
  - 单独保留 query 相关的性能对比口径
  - 为 future agent 提供回归判断
- 这个模块不负责：
  - 业务特性设计
  - 核心运行时代码实现

## 架构

- 核心组成：
  - `ComponentRegistryTests.cs`
  - `EntityTests.cs`
  - `SignatureTests.cs`
  - `ChunkTests.cs`
  - `ArchetypeTests.cs`
  - `WorldLifecycleTests.cs`
  - `QueryTests.cs`
  - `QueryFilterTests.cs`
  - `IntegrationTests.cs`
  - `StructuralChangeBenchmarks.cs`
- 数据流 / 控制流：
  - 单元测试先锁定局部行为
  - 集成测试再验证迁移链路
  - mixed structural-change benchmark 用固定种子生成同一批 `Create / Add / Set / Remove / Destroy` 操作脚本
  - benchmark 只比较同构输入下的 Arch / MiniArch 热路径，不承担正确性证明
  - setup、world 构建和脚本生成都放在测量区外
  - `scripts\verify.ps1` 统一跑 build + test
- 和其他模块的交互方式：
  - 直接依赖 `MiniArch.Core`
  - 通过 `World` 和 `Query` 验证外部可见行为
  - 不直接测试私有实现细节

## 决策

- 每个核心概念一个测试文件，便于按模块定位问题。
- 集成测试只保留一条完整迁移路径，避免重复覆盖。
- 验证脚本和测试项目分离，方便 agent 在需要时只跑局部测试。
- 结构变化相关测试必须保留 `Set` 的 in-place 语义断言，因为这是 typed-column / direct-index 重构的核心安全网。
- `ArchetypeTests` 需要覆盖“复用前面空掉的 chunk”这一行为；否则 `Remove` benchmark 的分配回退很难在功能测试里暴露出来。
- `WorldLifecycleTests` 需要覆盖 `EnsureCapacity` 和 `CreateMany`，否则 `Create` 的分配优化和批量语义很容易在重构时被回退。
- `WorldLifecycleTests` 还要覆盖 `CreateMany` 的跨 chunk 顺序和二次批量追加语义，否则批量 reservation 很容易只保住“能跑”而丢掉位置正确性。
- `ArchetypeEdges` 的 direct-index 化是性能目标本身，可以用一条小范围的结构测试锁定，避免静默退回字典实现。
- mixed structural-change benchmark 默认使用 `20/20/20/20/20` 的均衡分布，并用固定种子生成同一条随机脚本。
- benchmark 必须同时看时间和分配，不能只看平均耗时。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 运行时行为的安全网
- 这个模块里最重要的抽象是：
  - 行为断言
  - 回归信号
- 常见误解：
  - 认为测试只要能编译就够了
  - 认为集成测试可以代替对关键路径的单元测试

## 入口

- 如果是第一次读这个模块，先看：
  - `IntegrationTests.cs`：最完整的端到端例子
  - `WorldStructuralChangeTests.cs`：结构迁移的关键行为
  - `StructuralChangeBenchmarks.cs`：`Create / CreateMany / Add / Set / Remove / Destroy` 与 Arch 的时间和分配对照
- 如果是修 bug，先看：
  - 对应功能的测试文件
  - `scripts\test.ps1`
  - `scripts\benchmark.ps1`：benchmark 入口，必要时配合 `--filter`
- 如果是加功能，先看：
  - `QueryTests.cs`：query 行为约束
  - `QueryFilterTests.cs`：链式 filter 和 builder 契约
  - `ChunkTests.cs`：存储密度约束
  - `ArchetypeTests.cs`：chunk 复用和可写 chunk 选择策略
  - `WorldStructuralChangeTests.cs`：`Set` / `Add` / `Remove` 的结构变化边界
  - `StructuralChangeBenchmarks.cs`：性能回归口径

## 坑点

- 历史上容易出问题的地方：
  - 只跑局部测试，没看整体迁移是否破坏
  - 断言太宽泛，漏掉 chunk 级行为
  - 只看运行时，不看分配和 GC
  - `Remove` 只看时间变快，却没发现 archetype 没复用已有空 chunk，导致分配被隐藏放大
  - `Create` 只看时间，不看 entity metadata 扩容带来的分配回退
  - 加了 `CreateMany` 却没把它纳入 benchmark，导致 bulk path 长期失真
  - `CreateMany` 只看分配下降，却没确认是否还在逐实体落位，导致 bulk time 仍明显慢于 Arch
  - 混合 benchmark 没有固定种子，导致 MiniArch 和 Arch 的输入不一致
- 容易误判的地方：
  - 认为 query 结果对了，chunk 顺序就一定对了
  - 认为 entity 还活着，version 也一定没错
- 改这里时要特别小心：
  - 测试名要稳定，方便 agent 用 `--filter` 定位
  - 集成测试不要过度依赖实现细节
  - `Set` 相关测试要先确认核心是否已经切到 typed-column / direct-index；如果没有，先保留适配点，不要伪造新行为
  - benchmark 输出要和 Arch 在相同 entity 布局、相同操作脚本下对齐，否则对比没有意义

## 关联模块

- `kb-core-ecs.md`：被测试的运行时模块
- `kb-repo-overview.md`：如何启动验证流程
- `scripts/test.ps1`：测试入口
- `benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs`：分项 structural-change benchmark 与 mixed structural-change benchmark
