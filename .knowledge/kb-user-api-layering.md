---
title: User API Layering
module: MiniArch
description: Single-source public API around MiniArch.World/Entity/QueryDescription, description-based foreach query, and the remaining MiniArch.Core advanced boundary
updated: 2026-04-12
---
# User API Layering

## 这个模块是干什么的

- 这个模块负责：
  - 给普通游戏逻辑提供低心智负担的 ECS 入口
  - 把核心概念收敛到一份公开定义：
    - `MiniArch.World`
    - `MiniArch.Entity`
    - `MiniArch.QueryDescription`
  - 把默认查询收敛成唯一的 description-based `foreach`
  - 让常见读取通过 `TryGet<T>` 完成
  - 明确 `MiniArch` 与 `MiniArch.Core` 的分层边界
- 这个模块不负责：
  - 替代底层 runtime storage
  - 隐藏所有 advanced API
  - 改写 `MiniArch.Core.Query` 的 chunk 缓存与快照布局

## 架构

- 核心组成：
  - `src/MiniArch/Core/World.cs`：唯一公开 `World`
  - `src/MiniArch/Core/Entity.cs`：唯一公开 `Entity`
  - `src/MiniArch/Core/QueryDescription.cs`：唯一公开 `QueryDescription`
  - `src/MiniArch/Ecs/Query.cs`：默认层 entity-only `foreach` 查询包装
  - `src/MiniArch/Core/Query.cs`：advanced query 对象与 `Query.Create(...)`
- 数据流 / 控制流：
  - 普通用户从 `MiniArch.World` 进入
  - `World.Query(in QueryDescription)` 返回默认层 entity-only `MiniArch.Query`
  - `MiniArch.Query.Advanced` 暴露对应的 `MiniArch.Core.Query`
  - advanced 用户也可以直接走 `MiniArch.Core.Query.Create(world, in description)`
  - `TryGet<T>` 走 `World` 上的 direct read 路径
- 和其他模块的交互方式：
  - 依赖 `MiniArch.Core.Query` / `Chunk` / `CommandBuffer` / `WorldSnapshot`
  - 由 `tests/MiniArch.Tests/UserApi/UserQueryTests.cs` 做普通入口回归验证
  - 由 `tests/MiniArch.Tests/Core/*.cs` 验证 advanced query 和 runtime 语义

## 决策

- `World`、`Entity`、`QueryDescription` 只保留一份，统一放在 `MiniArch` 根命名空间。
- 这些类型属于 ECS 的基础语言，不应在默认层和 advanced 层重复定义。
- typed query 家族被移除：
  - `Query<T>`
  - `Query<T1, T2>`
  - `QueryItem<...>`
  - `QueryEnumerator<...>`
- 默认查询统一只保留 `QueryDescription` 入口，避免 “按 1~2 个组件特判” 把公开 API 撑大。
- builder 风格 `World.Query()...Build()` 也被移除；advanced 查询统一显式写成 `MiniArch.Core.Query.Create(world, in description)`。
- 因为根命名空间会真实暴露 `World` / `Entity`，仓库内部测试与 benchmark 命名空间同步改成 `MiniArchTests.*` / `MiniArchBenchmarks`，避免名字解析被外层命名空间劫持。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一套共享核心概念 + 一层 advanced 类型集合
- 这个模块里最重要的抽象是：
  - `MiniArch.World`
  - `MiniArch.QueryDescription`
  - `MiniArch.Query`
  - `MiniArch.Core.Query`
- 常见误解：
  - 以为“只有一份 QueryDescription”就等于也只能有一份查询结果形状
  - 以为去掉 typed query façade 就意味着不能再下沉到 chunk 级遍历

## 入口

- 如果是第一次读这个模块，先看：
  - `src/MiniArch/Core/World.cs`：唯一 `World`
  - `src/MiniArch/Core/QueryDescription.cs`：唯一查询描述
  - `src/MiniArch/Ecs/Query.cs`：默认层 entity-only 查询
  - `src/MiniArch/Core/Query.cs`：advanced query factory 与 chunk 级消费
- 如果是修 bug，先看：
  - `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`：普通 API 契约
  - `tests/MiniArch.Tests/Core/QueryTests.cs`：advanced query 缓存与并发读取契约
- 如果是加功能，先看：
  - `src/MiniArch/Core/World.cs`：是否应属于共享 `World`
  - `src/MiniArch/Core/Query.cs`：是否应属于 advanced query，而不是再引入第二套描述语言

## 坑点

- 历史上容易出问题的地方：
  - 让 `World` / `Entity` / `QueryDescription` 在两层重复出现，迫使用户先判断“这次该 new 哪一份”
  - 用 builder/generic query 快捷入口继续扩张公开 API，导致 `QueryDescription` 不是唯一查询语言
  - 仓库内部仍使用 `MiniArch.Tests.*` / `MiniArch.Benchmarks` 命名空间，导致根命名空间类型解析冲突
- 容易误判的地方：
  - 以为删除 typed query 就等于必须放弃 advanced query
  - 以为默认层只剩 entity 枚举后，`MiniArch.Core.Query` 就应该删除；实际上 advanced query 仍然承担 chunk 快照与 profiling 入口
- 改这里时要特别小心：
  - `MiniArch.Query` 是 struct wrapper，不能再拿它做 identity 断言
  - `MiniArch.Core.Query.Create(world, in description)` 现在是 advanced 唯一入口；改这里要同步 tests/benchmarks
  - 如果未来再扩查询能力，优先扩 `QueryDescription` 或 advanced query 消费方式，不要重新引入 typed query 家族

## 关联模块

- `kb-core-ecs.md`：底层 runtime 架构与 query 缓存来源
- `kb-test-workflow.md`：普通 API 与 core API 的验证入口
- `src/MiniArch/README.md`：对外说明当前 API 分层
