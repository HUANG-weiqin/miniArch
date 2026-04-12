---
title: Hierarchy Runtime
module: MiniArch.Core Hierarchy
description: Runtime-owned parent-child relations, cascade destroy semantics, and snapshot restore behavior
updated: 2026-04-12
---
# Hierarchy Runtime

## 这个模块是干什么的

- 这个模块负责：
  - 维护 entity 的单父 parent-child 关系
  - 提供 parent 查询和 direct children 列表查询
  - 在父实体销毁时扩展为 child-first 的级联销毁
  - 在 command buffer rewind 场景里为 destroy subtree 提供可回退的 parent-child 公开状态语义
  - 让 snapshot save/load 后 hierarchy 关系保持有效
- 这个模块不负责：
  - 把 hierarchy 暴露成 ECS component 或 query filter
  - 多父图、DAG 或任意图结构

## 架构

- 核心组成：
  - `src/MiniArch/Core/HierarchyTable.cs`：runtime-owned 关系表
  - `src/MiniArch/Core/World.cs`：对外 hierarchy API、destroy 集成和 snapshot 桥接点
  - `src/MiniArch/Core/WorldSnapshot.cs`：hierarchy link 的持久化读写
- 数据流 / 控制流：
  - `World.Link(parent, child)` 先做存活校验、拒绝自环和成环，再写入关系表
  - `World.GetChildren(parent)` 返回当前 direct children 的排序快照 `List<Entity>`
  - `World.Destroy(parent)` 先从 hierarchy 表收集整棵子树，再按 child-first 顺序逐个销毁
  - `World.ReplayWithReverse(...)` 在捕获 reverse frame 时，会先按当前 hierarchy 收集待 destroy 的 existing subtree，并把每个实体的组件快照和原 parent 一起记入 `ReverseFrameCommands.RestoredEntities`
  - `World.Rewind(...)` 的 hierarchy 安全顺序是先重建旧实体和组件、再按记录下来的 parent 恢复旧 hierarchy，之后才销毁 replay 期间新建且当前仍存活的实体；这样可避免新父节点的级联销毁误删刚恢复的旧实体
  - `WorldSnapshot.Save` 在 archetype/component 数据后写入 live hierarchy links
  - `WorldSnapshot.Load` 在实体和 chunk 恢复后重建 hierarchy links
- 和其他模块的交互方式：
  - 依赖 `World` 的 version/location 校验判断句柄是否仍然活着
  - 被 snapshot 模块读取和恢复
  - 不参与 archetype/signature/chunk 的结构变化

## 决策

- hierarchy 保持为 `World` 持有的 runtime side table，而不是 ECS component；这样不会污染 archetype 迁移、query 和 chunk 布局。
- destroy 语义固定为 cascade destroy，而不是“父死子孤儿”；这样 snapshot 恢复后行为保持一致，也更接近参考实现。
- hierarchy 直接复用 `default(Entity)` 作为“无 parent”值；这依赖底层 `Entity` 契约已经保证 `default(Entity)` 非法、真实实体从 `Version = 1` 起步。
- rewind 的 hierarchy 契约已经落地为“恢复 replay 前公开可观察关系”，而不是恢复 replay 过程中的所有临时中间态。
- destroy subtree 的回退语义已经固定：只恢复 replay 前已经存在并被级联销毁的 subtree；同一帧里新建后挂到这棵树上、随后一起被销毁的节点在 `Rewind(...)` 后保持不存在。
- 层级回退的端到端验收现在还要求：如果先回到中间历史点，再按原后续 frame 序列 replay，最终 hierarchy 公开可观察结果仍要与原终局一致。

## 认知模型

- 理解这个模块时，应该把它看成：
  - entity metadata 的一层关系索引，而不是存储在 chunk 里的业务数据
- 这个模块里最重要的抽象是：
  - `HierarchyTable`
  - child-first subtree destroy
- 常见误解：
  - 以为 hierarchy 应该天然是 ECS component
  - 以为销毁父节点时只需要清理 parent 指针，不需要扩展整棵子树
  - 以为 rewind 会把同帧临时挂进 subtree 的新节点也恢复出来；当前不会

## 入口

- 如果是第一次读这个模块，先看：
  - `src/MiniArch/Core/HierarchyTable.cs`：关系表结构、校验逻辑和子树收集
  - `src/MiniArch/Core/World.cs`：hierarchy API 和 destroy 集成点
  - `src/MiniArch/Core/WorldSnapshot.cs`：save/load 时 hierarchy 如何持久化
- 如果是修 bug，先看：
  - `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`：级联销毁、reparent 和 slot reuse 行为
  - `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：link/unlink 回退、destroy subtree 回退，以及“旧子树恢复但新建后代不恢复”
  - `tests/MiniArch.Tests/Core/QueryTests.cs`：级联销毁后多帧 rewind 的 query 恢复
  - `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`：存档恢复后的 hierarchy 契约
- 如果是加功能，先看：
  - `src/MiniArch/Ecs/World.cs`：user-facing façade 是否也要暴露对应 API

## 坑点

- 历史上容易出问题的地方：
  - 把 `default(Entity)` 当成空值，结果与合法实体 `(0, 0)` 混淆
  - destroy 时先删 parent 再清 children，导致旧关系残留或 slot reuse 串关系
  - snapshot 只恢复组件，不恢复 hierarchy links
  - rewind 捕获 destroy subtree 时只记 root，不记整棵 existing subtree，导致回退后 child/grandchild 丢失
  - 回退时先恢复 link 再恢复实体，导致 parent/child 校验在 restore 途中失败
  - 回退时如果先销毁 replay 期间新建的父节点，再恢复旧 subtree，级联销毁可能把刚恢复的旧实体一起带走
- 容易误判的地方：
  - 以为 `GetChildren()` 应该返回 live view；当前契约是排序后的快照 list
  - 以为 hierarchy 改动需要递增 archetype generation；实际上它不影响 query 匹配
  - 以为 hierarchy restore 只看 parent 指针就够了；当前 query 和 `GetChildren()` 也要一起回到 replay 前可观察结果
- 改这里时要特别小心：
  - parent 存储必须带完整 `Entity` 句柄，不能只存 `Id`
  - hierarchy 当前直接用 `default(Entity)` 表示“无 parent”；如果后面再次调整 entity 契约，这里的空值语义要一起复核

## 关联模块

- `kb-core-ecs.md`：hierarchy 依附的 entity lifecycle 和 location 校验
- `kb-snapshot-persistence.md`：hierarchy link 的 save/load 边界
- `kb-test-workflow.md`：对应的 lifecycle / persistence 验证入口
