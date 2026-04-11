---
title: Test Workflow
module: MiniArch.Tests
description: How the test suite is organized and how to run it
updated: 2026-04-11
---
# Test Workflow

## 这个模块是干什么的

- 这个模块负责：
  - 验证 ECS core 的行为
  - 覆盖实体生命周期、chunk 存储、结构迁移和 query
  - 为 future agent 提供回归判断
- 这个模块不负责：
  - 业务特性设计
  - 性能基准
  - 运行时代码实现

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
- 数据流 / 控制流：
  - 单元测试先锁定局部行为
  - 集成测试再验证迁移链路
  - `scripts\verify.ps1` 统一跑 build + test
- 和其他模块的交互方式：
  - 直接依赖 `MiniArch.Core`
  - 通过 `World` 和 `Query` 验证外部可见行为
  - 不直接测试私有实现细节

## 决策

- 每个核心概念一个测试文件，便于按模块定位问题。
- 集成测试只保留一条完整迁移路径，避免重复覆盖。
- 验证脚本和测试项目分离，方便 agent 在需要时只跑局部测试。

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
- 如果是修 bug，先看：
  - 对应功能的测试文件
  - `scripts\test.ps1`
- 如果是加功能，先看：
  - `QueryTests.cs`：query 行为约束
  - `QueryFilterTests.cs`：链式 filter 和 builder 契约
  - `ChunkTests.cs`：存储密度约束

## 坑点

- 历史上容易出问题的地方：
  - 只跑局部测试，没看整体迁移是否破坏
  - 断言太宽泛，漏掉 chunk 级行为
- 容易误判的地方：
  - 认为 query 结果对了，chunk 顺序就一定对了
  - 认为 entity 还活着，version 也一定没错
- 改这里时要特别小心：
  - 测试名要稳定，方便 agent 用 `--filter` 定位
  - 集成测试不要过度依赖实现细节

## 关联模块

- `kb-core-ecs.md`：被测试的运行时模块
- `kb-repo-overview.md`：如何启动验证流程
- `scripts/test.ps1`：测试入口
