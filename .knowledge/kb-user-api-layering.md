---
title: User API Layering
module: MiniArch
description: Single-source public API around MiniArch.World/Entity/QueryDescription, description-based foreach query, and the remaining MiniArch.Core advanced boundary
updated: 2026-06-01
---
# User API Layering

## 这个模块是干什么的

- 给普通游戏逻辑提供低心智负担的 ECS 入口
- 把核心概念收敛到一份公开定义：`MiniArch.World`、`MiniArch.Entity`、`MiniArch.QueryDescription`
- 默认查询收敛成唯一的 description-based `foreach`
- 明确 `MiniArch` 与 `MiniArch.Core` 的分层边界

## 架构

- 核心组成：
  - `src/MiniArch/Core/World.cs` + `Core/Entity.cs` + `Core/QueryDescription.cs`：唯一公开定义
  - `src/MiniArch/Ecs/Query.cs`：默认层 entity-only `foreach` 查询
  - `src/MiniArch/Core/Query.cs`：advanced query 对象 (`MiniArch.Core.Query.Create(...)`)
  - `src/MiniArch/Core/SpanQueryIterators.cs`：`EachSpan<T1..T8>()` 零分配 ref struct 迭代器
- 数据流：普通用户从 `MiniArch.World` 进入 → `World.Query(in QueryDescription)` 返回 entity-only `MiniArch.Query` → `MiniArch.Query.Advanced` 暴露 `MiniArch.Core.Query`

## 决策

- `World`、`Entity`、`QueryDescription` 只保留一份，统一放在 `MiniArch` 根命名空间
- typed query 家族（`Query<T>`、`Query<T1,T2>`、`QueryItem<>`、`QueryEnumerator<>`）已移除
- builder 风格 `World.Query()...Build()` 已移除
- `OrderBy(...)` 是默认层消费方式，不缓存排序结果（每次枚举独立租用内部 buffer）
- `EachSpan` 当前只服务读路径；writable span 因 YAGNI 已否决，诊断代码已删除

## 认知模型

- 一套共享核心概念 + 一层 advanced 类型集合

## 入口

- `src/MiniArch/Core/World.cs` + `Core/QueryDescription.cs` + `Ecs/Query.cs` + `Core/Query.cs`
- 修 bug：`tests/MiniArch.Tests/UserApi/UserQueryTests.cs` + `Core/QueryTests.cs`

## 坑点

- `MiniArch.Query` 是 struct wrapper，不能拿它做 identity 断言
- `MiniArch.Core.Query.Create(world, in description)` 现在是 advanced 唯一入口
- `EachSpan` 只支持 blittable 组件；含托管引用的组件必须用 chunk 手动迭代
