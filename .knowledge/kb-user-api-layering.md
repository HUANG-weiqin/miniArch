---
title: User API Layering
module: MiniArch.Ecs
description: User-facing MiniArch.Ecs facade design, QueryDescription, query foreach shape, and the boundary to MiniArch.Core
updated: 2026-04-12
---
# User API Layering

## 这个模块是干什么的

- 这个模块负责：
  - 给普通游戏逻辑提供低心智负担的 ECS 入口
  - 把默认查询包装成可直接 `foreach` 的结果流
  - 让常见读取通过 `TryGet<T>` 完成，而不是先接触结构细节
  - 明确 `MiniArch.Ecs` 和 `MiniArch.Core` 的分层边界
- 这个模块不负责：
  - 替代底层 runtime storage
  - 隐藏所有 advanced API
  - 改写 `MiniArch.Core` 的 query 缓存与 chunk 布局

## 架构

- 核心组成：
  - `src/MiniArch/Ecs/World.cs`：普通入口 world facade
  - `src/MiniArch/Ecs/Entity.cs`：普通入口 entity facade
  - `src/MiniArch/Ecs/Query.cs`：单组件/双组件查询、entity 查询和零分配枚举器
  - `src/MiniArch/Ecs/QueryDescription.cs`：普通层可复用查询描述
  - `src/MiniArch/Core/World.cs`：提供 facade 需要的 direct read bridge
  - `src/MiniArch/Core/Chunk.cs`：提供 facade 枚举器需要的 typed array bridge
- 数据流 / 控制流：
  - 普通用户从 `MiniArch.Ecs.World` 进入
  - `World.Query<T>()` / `World.Query<T1, T2>()` 先复用底层 `MiniArch.Core.Query` 缓存
  - `World.Query(in QueryDescription)` 复用底层 `MiniArch.Core.QueryDescription`，再 materialize 成普通层 entity query
  - facade 枚举器直接扫 matched chunks 和 typed column arrays
  - 每次迭代返回轻量 item，不创建中间集合，不走接口枚举
  - `World.IsAlive(Entity)` 直接转发到底层 `MiniArch.Core.World.IsAlive(Entity)`，让普通用户用同一个入口判断句柄是否仍然和当前 world 匹配
- 和其他模块的交互方式：
  - 依赖 `MiniArch.Core.World` / `Query` / `Chunk`
  - 由 `tests/MiniArch.Tests/UserApi/UserQueryTests.cs` 做用户入口回归验证
  - 通过 `World.Advanced` 暴露到底层 world，必要时允许回退到 advanced API

## 决策

- 普通入口放在 `MiniArch.Ecs`，不直接放 `MiniArch` 根命名空间。
- 这样做不是审美选择，而是为避免 `MiniArch.Tests.*` 这类嵌套命名空间里的 `World` / `Entity` 名称解析被根命名空间 façade 劫持。
- 普通查询新增 facade，而不是直接改写 `MiniArch.Core.Query` 的 public 形状；底层 query 仍然服务 advanced 遍历、benchmark 和 profiling。
- `MiniArch.Ecs.QueryDescription` 只是普通层可复用描述，最终还是由 `MiniArch.Core.QueryDescription` 驱动底层 filter materialize。
- 双组件查询默认返回 item 而不是单纯 `Entity`，因为目标就是避免普通逻辑再退回 “query entity -> 再手动找组件”。
- `IsAlive` 也属于普通入口层该提供的常用句柄检查，不应该要求普通用户先切到 `Advanced` 再做版本/位置校验。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 面向游戏逻辑的薄包装层，不是第二套 ECS runtime
- 这个模块里最重要的抽象是：
  - `MiniArch.Ecs.World`
  - `MiniArch.Ecs.Query<T>` / `Query<T1, T2>`
- 常见误解：
  - 以为 façade 分层等于要把 `MiniArch.Core` 变成 internal
  - 以为默认 `foreach` 必然要牺牲热路径性能

## 入口

- 如果是第一次读这个模块，先看：
  - `src/MiniArch/Ecs/World.cs`：普通 API 的入口面
  - `src/MiniArch/Ecs/Query.cs` / `src/MiniArch/Ecs/QueryDescription.cs`：默认 `foreach` 的结果形状、entity query 和查询描述
- 如果是修 bug，先看：
  - `tests/MiniArch.Tests/UserApi/UserQueryTests.cs`：普通 API 契约
  - `src/MiniArch/Core/Chunk.cs`：typed array bridge 是否仍匹配 façade 枚举器
- 如果是加功能，先看：
  - `src/MiniArch/Ecs/World.cs`：是否应属于普通 API
  - `src/MiniArch/Core/World.cs`：是否需要新增 bridge，而不是直接暴露更多底层细节

## 坑点

- 历史上容易出问题的地方：
  - 把普通 façade 放进 `MiniArch` 根命名空间，导致嵌套命名空间中的类型解析冲突
  - 让查询 item 直接保存组件值，导致每次迭代拷贝大 struct
  - 为了普通 `foreach` 去复用 `IEnumerable` / `IEnumerator` 接口路径，带来装箱或隐藏分配
- 容易误判的地方：
  - 以为“零 GC”就必须用 `ref struct`；当前实现用 struct enumerator + 数组引用也能做到
  - 以为普通 API 越少越好；如果少到还得自己写 `Has/TryGet` 拼装，心智负担并没有下降
- 改这里时要特别小心：
  - façade 名称不要再和 `MiniArch.Core` 或测试命名空间形成同名劫持
  - query item 属性如果改成返回值而不是按数组索引取值，要重新评估拷贝成本

## 关联模块

- `kb-core-ecs.md`：底层 runtime 架构与 query 缓存来源
- `kb-test-workflow.md`：普通 API 与 core API 的验证入口
- `src/MiniArch/README.md`：对外说明当前 API 分层
