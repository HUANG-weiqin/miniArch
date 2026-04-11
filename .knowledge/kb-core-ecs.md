---
title: MiniArch Core ECS
module: MiniArch.Core
description: Target ECS architecture for entities, archetypes, typed columns, direct-index writes, signatures, and queries
updated: 2026-04-11
---
# MiniArch Core ECS

## 这个模块是干什么的

- 这个模块负责：
  - 创建、销毁和迁移实体
  - 管理 entity metadata 容量和批量创建
  - 维护签名到 archetype 的映射
  - 用 chunk 做 dense SoA 存储
  - 让 `Set` 走 typed-column / direct-index 的原地写入路径
  - 让 query 先按 archetype 过滤，再按 chunk 迭代
- 这个模块不负责：
  - 编辑器集成
  - 多线程作业系统
  - 事件系统

## 架构

- 核心组成：
  - `World.cs`：实体生命周期、archetype 生成、query 缓存
  - `Archetype.cs`：chunk 列表、实体计数、结构变化入口
  - `Chunk.cs`：实体列和 typed component columns 的密集存储
  - `Signature.cs`：组件集合键
  - `QueryFilter.cs` / `QueryBuilder.cs`：链式 query filter 构造
  - `Query.cs` / `QueryIterators.cs`：archetype 过滤和 chunk 遍历
  - `ArchetypeEdges.cs`：增删组件迁移缓存
- 数据流 / 控制流：
  - `World` 创建实体后放入空签名 archetype
  - `World.EnsureCapacity` 负责提前扩好 entity metadata 存储，避免 `Create` 只靠 `List<T>` 被动增长
  - `World.CreateMany` 先批量准备 entity id，再用 `Archetype` 的 chunk-batched reservation 一次性把一批实体落入空签名 archetype
  - `Add/Remove` 先算目标签名，再复用 edge cache
  - `Set` 在组件已存在时直接定位到 typed column 的 row，原地写回，不触发迁移
  - `Archetype` 负责把实体放进可写 chunk，并优先复用已有空位的 chunk，而不是盲目只往最后一个 chunk 追加
  - `Chunk` 负责 dense row 的单个/批量插入、读取、swap-remove 和 direct-index 写入
  - `QueryBuilder` 负责累积 `With/Without/Any/Or` 过滤条件
  - `Query` 先缓存匹配 archetype，再暴露 chunk 枚举
- 和其他模块的交互方式：
  - `World` 通过 `ComponentRegistry` 把类型映射成 `ComponentType`
  - `World` 通过 `Signature` 定位 archetype
  - `Archetype` 通过 component-to-column 索引把 `Set` 路径压成一次定位 + 一次写入
  - `Query` 依赖 `World.ArchetypeGeneration` 判断是否需要刷新缓存

## 决策

- 用 `ComponentType` 而不是直接用 `Type` 作为运行时 key，保持签名和缓存更轻。
- 把数据迁移逻辑放在 `World`，把存储细节放在 `Archetype` 和 `Chunk`，避免职责混杂。
- 用 `Signature` 作为 archetype key，保证等价组件集合始终落在同一个 storage family。
- query filter 也统一用 `Signature` 表达，避免额外的 query-only 组件集合结构。
- 用 chunk 级迭代而不是 entity 级全表扫描，保留局部性和后续优化空间。
- `Set<T>` / `Add<T>` 的原地写入路径要优先走 typed columns + 组件 id -> 列索引表，避免 `object` 盒化和 chunk 字典查找。
- `World` 侧可以对泛型组件类型做按 `T` 的注册缓存，减少热路径里的重复 registry 查找。
- `World` 的 entity metadata 需要显式容量管理；如果只依赖 `List<T>` 的自然扩容，`Create` 的分配会长期高于合理水平。
- `CreateMany` 应该复用一次空 archetype 查找和一次 upfront 容量保证；它不是“外面循环调用很多次 `Create`”的语法糖。
- `CreateMany` 的快路径还需要把“新 id 生成”和“chunk 落位”都批量化；如果仍然逐实体 `List.Add` 或逐实体 `ReserveEntity`，时间会显著落后于 Arch。
- `World` 默认 chunk 容量不能太小；过小的默认值会在结构迁移时制造大量微型 chunk，把分配和 GC 放大到不合理的程度。
- `Archetype` 不能只把写入目标锁死在最后一个 chunk；结构迁移把实体移走后，前面空掉的 chunk 必须可复用，否则 `Remove -> 空 archetype` 会无意义地重新分配 chunk。
- `ArchetypeEdges` 应该和其他热路径一样使用 component id 直索引，而不是继续停留在 `Dictionary<ComponentType, Archetype>`。
- 兼容构造仍然保留给直接 `new Archetype(...)` / `new Chunk(...)` 的测试和低频调用，但热路径不要依赖它。
- `Set` 的热路径应该是 direct-index 原地写，不应该为了更新一个已存在组件去做结构迁移。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一条从 entity id 到 dense typed storage 的映射链
- 这个模块里最重要的抽象是：
  - `World`
  - `Signature`
  - `Archetype`
  - `Chunk`
- 常见误解：
  - 认为 query 直接遍历实体
  - 认为结构变化只是简单的集合增删
  - 认为 `Set` 和 `Add` 是同一条路径

## 入口

- 如果是第一次读这个模块，先看：
  - `World.cs`：完整控制流入口
  - `Signature.cs`：archetype 的 key 规则
  - `Chunk.cs`：底层存储布局和 direct-index 写入点
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
  - typed chunk 和 slow chunk 共存时，不能让 world 热路径误走 slow path
  - archetype 只复用最后一个 chunk，导致前面已经空掉的 chunk 永远闲置，`Remove` 分配和 GC 被错误放大
  - `Create` 没有 upfront capacity 管理，导致 entity metadata 在批量创建时不断扩容
  - `CreateMany` 退化成外部循环调用 `Create`，导致空 archetype 查找和容量检查无法摊平
  - `CreateMany` 只做了 upfront capacity，但仍逐实体 `ReserveEntity` 或逐实体扩展 metadata，bulk create 时间仍会明显慢于 Arch
  - edge cache 继续用字典，导致热路径风格和 direct-index 存储体系脱节
- 容易误判的地方：
  - 以为 `Set<T>` 永远只是原地写入
  - 以为 `Remove<T>` 不存在时应该报错，而不是直接返回
- 改这里时要特别小心：
  - `Chunk` 的列必须和 `Signature` 完全一致
  - `World` 的 entity version 不能和 location 脱钩
  - 性能验证必须看 `Arch` 对照数据，不能只看自己变快
  - 当前代码库里这页描述的是目标态，不是旧版 `Dictionary<ComponentType, object?>` 实现

## 关联模块

- `kb-repo-overview.md`：仓库导航入口
- `kb-test-workflow.md`：对应行为覆盖和 benchmark 口径
- `tests/MiniArch.Tests/Core/*.cs`：行为验证
- `benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs`：`Create / CreateMany / Add / Set / Remove / Destroy` 热路径对比
