---
title: User API Layering
module: MiniArch
description: Single-source public API around MiniArch.World/Entity/QueryDescription, description-based foreach query, batch ChunkView API, and the remaining MiniArch.Core advanced boundary
updated: 2026-06-22 (全库审阅: 修正过时文件路径)
---
# User API Layering

## 这个模块是干什么的

- 给普通游戏逻辑提供低心智负担的 ECS 入口
- 把核心概念收敛到一份公开定义：`MiniArch.World`、`MiniArch.Entity`、`MiniArch.QueryDescription`
- 默认查询收敛成唯一的 description-based `foreach`
- 明确 `MiniArch` 与 `MiniArch.Core` 的分层边界

## 架构

- 核心组成：
  - `src/MiniArch/Core/World.cs`（+ partial 文件）+ `Core/Entity.cs` + `Core/QueryDescription.cs`：唯一公开定义
  - `src/MiniArch/Query.cs`：默认层 entity-only `foreach` 查询 + `GetChunks()` batch API
  - `src/MiniArch/Core/ChunkView.cs`：public readonly struct，直接包裹 Archetype，带 `AggressiveInlining`
  - `src/MiniArch/Core/Query.cs`：advanced query 对象 (`MiniArch.Core.Query.Create(...)`)
- 数据流：
  - 普通用户从 `MiniArch.World` 进入 → `World.Query(in QueryDescription)` 返回 entity-only `MiniArch.Query`
  - `MiniArch.Query.GetChunks()` → `ReadOnlySpan<ChunkView>` → 用户用 `chunk.GetSpan<T>()` 批量访问组件
  - `MiniArch.Query.Advanced` 暴露 `MiniArch.Core.Query`（advanced 用法）
  - `World.GetFirst<T>()` → O(1) 按 component type 查找首个 entity

## 决策

- `World`、`Entity`、`QueryDescription` 只保留一份，统一放在 `MiniArch` 根命名空间
- typed query 家族（`Query<T>`、`Query<T1,T2>`、`QueryItem<>`、`QueryEnumerator<>`）已移除
- builder 风格 `World.Query()...Build()` 已移除
- `OrderBy(...)` 是默认层消费方式，不缓存排序结果
- `EachSpan` 当前只服务读路径
- `Query.GetChunks()` + `ChunkView` 是 batch API 的主入口，替代了 `.Advanced` 直接访问 Core.Query 的需求
- `World.GetFirst<T>()` 提供按组件类型快速查找首个 entity 的便捷 API

## 认知模型

- 一套共享核心概念 + 一层 advanced 类型集合 + batch API 层

## 入口

- `src/MiniArch/Core/World.cs` + `Core/QueryDescription.cs` + `Ecs/Query.cs` + `Ecs/ChunkView.cs`
- 修 bug：`tests/MiniArch.Tests/UserApi/UserQueryTests.cs` + `Core/QueryTests.cs`

## 坑点

- `MiniArch.Query` 是 struct wrapper，不能拿它做 identity 断言
- `MiniArch.Core.Query.Create(world, in description)` 现在是 advanced 唯一入口
- `EachSpan` 只支持 blittable 组件；含托管引用的组件必须用 chunk 手动迭代
- `Query.GetChunks()` 返回的 `ChunkView` 是 readonly struct，不能持有为长期引用
- `World.GetFirst<T>()` 依赖泛型静态缓存（`WeakReference<World>`），跨 World 使用安全但首次调用有缓存未命中开销
