---
title: MiniArch Core ECS
module: MiniArch.Core
description: Runtime ECS architecture for entities, archetypes, chunks, signatures, and queries
updated: 2026-04-11
---
# MiniArch Core ECS

## 这个模块是干什么的

- 这个模块负责：
  - 创建、销毁和迁移实体
  - 维护签名到 archetype 的映射
  - 用 chunk 做 dense SoA 存储
  - 让 query 先按 archetype 过滤，再按 chunk 迭代
- 这个模块不负责：
  - 编辑器集成
  - 多线程作业系统
  - 事件系统

## 架构

- 核心组成：
  - `World.cs`：实体生命周期、archetype 生成、query 缓存
  - `Archetype.cs`：chunk 列表、实体计数、迁移入口
  - `Chunk.cs`：实体列和组件列的密集存储
  - `Signature.cs`：组件集合键
  - `QueryFilter.cs` / `QueryBuilder.cs`：链式 query filter 构造
  - `Query.cs` / `QueryIterators.cs`：archetype 过滤和 chunk 遍历
  - `ArchetypeEdges.cs`：增删组件迁移缓存
- 数据流 / 控制流：
  - `World` 创建实体后放入空签名 archetype
  - `Add/Remove/Set` 先算目标签名，再复用 edge cache
  - `Archetype` 负责把实体放进可写 chunk
  - `Chunk` 负责 dense row 的插入、读取和 swap-remove
  - `QueryBuilder` 负责累积 `With/Without/Any/Or` 过滤条件
  - `Query` 先缓存匹配 archetype，再暴露 chunk 枚举
- 和其他模块的交互方式：
  - `World` 通过 `ComponentRegistry` 把类型映射成 `ComponentType`
  - `World` 通过 `Signature` 定位 archetype
  - `Query` 依赖 `World.ArchetypeGeneration` 判断是否需要刷新缓存

## 决策

- 用 `ComponentType` 而不是直接用 `Type` 作为运行时 key，保持签名和缓存更轻。
- 把数据迁移逻辑放在 `World`，把存储细节放在 `Archetype` 和 `Chunk`，避免职责混杂。
- 用 `Signature` 作为 archetype key，保证等价组件集合始终落在同一个 storage family。
- query filter 也统一用 `Signature` 表达，避免额外的 query-only 组件集合结构。
- 用 chunk 级迭代而不是 entity 级全表扫描，保留局部性和后续优化空间。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一条从 entity id 到 dense storage 的映射链
- 这个模块里最重要的抽象是：
  - `World`
  - `Signature`
  - `Archetype`
  - `Chunk`
- 常见误解：
  - 认为 query 直接遍历实体
  - 认为结构变化只是简单的集合增删

## 入口

- 如果是第一次读这个模块，先看：
  - `World.cs`：完整控制流入口
  - `Signature.cs`：archetype 的 key 规则
  - `Chunk.cs`：底层存储布局
- 如果是修 bug，先看：
  - `World.cs`：实体迁移和版本校验
  - `QueryIterators.cs`：chunk 枚举是否漏项
- 如果是加功能，先看：
  - `Archetype.cs`：chunk 扩展点
  - `ArchetypeEdges.cs`：迁移缓存扩展点

## 坑点

- 历史上容易出问题的地方：
  - 迁移后没更新 moved entity 的 location
  - archetype generation 没递增，query 缓存失效
  - swap-remove 只动了 entity 没动组件列
- 容易误判的地方：
  - 以为 `Set<T>` 永远只是原地写入
  - 以为 `Remove<T>` 不存在时应该报错，而不是直接返回
- 改这里时要特别小心：
  - `Chunk` 的列必须和 `Signature` 完全一致
  - `World` 的 entity version 不能和 location 脱钩

## 关联模块

- `kb-repo-overview.md`：仓库导航入口
- `kb-test-workflow.md`：对应行为覆盖
- `tests/MiniArch.Tests/Core/*.cs`：行为验证
