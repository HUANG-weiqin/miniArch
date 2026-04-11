---
title: MiniArch Core ECS
module: MiniArch.Core
description: Target ECS architecture for entities, archetypes, typed columns, direct-index writes, signatures, and queries
updated: 2026-04-12
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
  - `World.Create<T...>` 当前为 `1..16` 个组件提供固定重载；它会先算目标签名，再把 entity 和组件直接写入最终 archetype，不经过 `Create -> Add -> Add` 的迁移链
  - `World.EnsureCapacity` 负责提前扩好 entity metadata 存储，避免 `Create` 只靠 `List<T>` 被动增长
  - `World.CreateMany` 先批量准备 entity id，再用 `Archetype` 的 chunk-batched reservation 一次性把一批实体落入空签名 archetype
  - `World` 内部把 entity version 和 entity location 分开存储：`_versions` 管版本校验，`_locations` 只保留 archetype/chunk/row，避免 metadata 热路径重复写 version
  - `Add/Remove` 先算目标签名，再复用 edge cache
  - `Set` 在组件已存在时直接定位到 typed column 的 row，原地写回，不触发迁移
  - `Archetype` 负责把实体放进可写 chunk，并优先复用已有空位的 chunk，而不是盲目只往最后一个 chunk 追加
  - `Chunk` 负责 dense row 的单个/批量插入、读取、swap-remove 和 direct-index 写入
  - `QueryBuilder` 负责累积 `With/Without/Any/Or` 过滤条件
  - `Query` 先缓存匹配 archetype，再暴露 chunk 枚举
  - query 读路径使用 world 发布的 archetype 数组快照和 query 自身发布的 matched-archetype 数组快照，避免共享可变列表
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
- 单实体带组件创建也应该直接落到目标签名 archetype；如果退回到 `Create` 后链式 `Add`，会制造只服务于迁移的中间 archetype 和空 chunk。
- 由于 C# 没有 variadic generics，带组件 `Create` 的高性能主路径需要采用固定 arity 重载；当前上限定在 `16`，在保持 typed-column 直写的前提下足够覆盖常见 archetype 初始化。
- `CreateMany` 应该复用一次空 archetype 查找和一次 upfront 容量保证；它不是“外面循环调用很多次 `Create`”的语法糖。
- `CreateMany` 的快路径还需要把“新 id 生成”和“chunk 落位”都批量化；如果仍然逐实体 `List.Add` 或逐实体 `ReserveEntity`，时间会显著落后于 Arch。
- free-list 场景的 `CreateMany` 也应该走 reserve-then-fill：先批量 reserve rows，再在同一轮里写 buffer、chunk entities 和 location metadata；先把 buffer 填满再复制进 chunk 会制造明显的双写。
- `CreateMany` 的 entity metadata 容量不能每次都 exact-fit；mixed/free-list 场景会在 `oldCount + freshCount` 的边界上反复付出 `List<T>` 扩容成本。批量追加时留少量余量，通常比把扩容留到下一次 mixed create 更稳。
- location metadata 不应该重复保存 version；version 已经由 world 级 metadata 持有，再在 location 里留一份会增加 mixed/free-list 路径的写放大与扩容成本。
- 空签名 archetype 只存 entity 列，默认 chunk size 可以比普通 archetype 更大；这样能减少 empty-world `CreateMany` 的 chunk/range 数量，而不会把组件列内存一并放大。
- `World` 默认 chunk 容量不能太小；过小的默认值会在结构迁移时制造大量微型 chunk，把分配和 GC 放大到不合理的程度。
- `Archetype` 不能只把写入目标锁死在最后一个 chunk；结构迁移把实体移走后，前面空掉的 chunk 必须可复用，否则 `Remove -> 空 archetype` 会无意义地重新分配 chunk。
- `ArchetypeEdges` 应该和其他热路径一样使用 component id 直索引，而不是继续停留在 `Dictionary<ComponentType, Archetype>`。
- 兼容构造仍然保留给直接 `new Archetype(...)` / `new Chunk(...)` 的测试和低频调用，但热路径不要依赖它。
- `Set` 的热路径应该是 direct-index 原地写，不应该为了更新一个已存在组件去做结构迁移。
- 在“world 不并发写入”的前提下，query 并发读优先用 copy-on-write 快照发布，而不是加锁；读路径保持数组遍历，写路径承担快照复制成本。
- query cache 应该发布整个 `Dictionary<QueryFilter, Query>` 快照，而不是在共享字典上做并发写入。
- 对照 `Arch` 源码时不要假设它会自动消除 `Create -> Add -> Add` 留下的中间 archetype；它同样保留这些 archetype，query 也是按 world archetype 列表全量匹配，空 archetype 只会在显式 `TrimExcess()` 后被移除。

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
  - `Create<T...>` 如果内部复用 `Add` 迁移路径，会重新留下中间态 archetype 和空 chunk
  - `CreateMany` 退化成外部循环调用 `Create`，导致空 archetype 查找和容量检查无法摊平
  - `CreateMany` 只做了 upfront capacity，但仍逐实体 `ReserveEntity` 或逐实体扩展 metadata，bulk create 时间仍会明显慢于 Arch
  - `CreateMany` 的 free-list 路径如果先生成完整 entity span 再复制进 chunk，会在 recycled/mixed benchmark 里形成额外的双写/三遍历热点
  - `CreateMany` 的 mixed benchmark 如果刚好落在 metadata 扩容边界上，测量结果会掺入 `List<T>` 扩容与零填充成本；要先分清这部分和 free-list 写入本体各占多少
  - location metadata 如果把 version 和位置绑在一个较大的 struct 里，会把 mixed 场景里的 `_locations` 扩容与逐实体写回成本放大
  - edge cache 继续用字典，导致热路径风格和 direct-index 存储体系脱节
  - query 把匹配 archetype 缓存在共享 `List<Archetype>` 上并原地清空/重建，会在并发只读时制造竞态
- 容易误判的地方：
  - 以为 `Set<T>` 永远只是原地写入
  - 以为 `Remove<T>` 不存在时应该报错，而不是直接返回
  - 以为“query 是读操作”就天然线程安全；如果底层缓存仍在共享可变集合上原地刷新，一样会出问题
  - 以为 Arch 的 query benchmark 更快，是因为它在逐个 `Add` 时“不会留下空 archetype”；源码和实测都说明它也会留下，差别更多来自匹配缓存、位集判断和 query 遍历实现成本
- 改这里时要特别小心：
  - `Chunk` 的列必须和 `Signature` 完全一致
  - `World` 的 entity version 不能和 location 脱钩
  - 性能验证必须看 `Arch` 对照数据，不能只看自己变快
  - 当前代码库里这页描述的是目标态，不是旧版 `Dictionary<ComponentType, object?>` 实现
  - 当前并发保证只覆盖“world 无写入时的 query 并发只读”；不要误把它扩展理解成读写并发安全
  - 如果并发读阶段第一次查询一个从未注册过的组件类型，`ComponentRegistry.GetOrCreate<T>()` 仍然会触发 registry 写入；这不属于当前保证范围

## 关联模块

- `kb-repo-overview.md`：仓库导航入口
- `kb-test-workflow.md`：对应行为覆盖和 benchmark 口径
- `tests/MiniArch.Tests/Core/*.cs`：行为验证
- `benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs`：`Create / CreateMany / Add / Set / Remove / Destroy` 热路径对比
