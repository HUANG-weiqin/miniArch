---
title: MiniArch Core ECS
module: MiniArch.Core
description: Target ECS architecture for entities, archetypes, typed columns, direct-index writes, signatures, and queries
updated: 2026-04-21
---
# MiniArch Core ECS

## 这个模块是干什么的

- 这个模块负责：
  - 创建、销毁和迁移实体
  - 管理 entity metadata 容量和批量创建
  - 给 command buffer 提供 deferred entity reservation、boxed structural mutation 和 batch replay 挂接点
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
  - `QueryFilter.cs`：query filter 的内部执行形状
  - `QueryDescription.cs`：可跨 world 复用的 query 描述，负责把用户想要的 `with/without/any` 组合保存成 world-agnostic 的类型集合
  - `Query.cs` / `QueryIterators.cs`：archetype 过滤和 chunk 遍历
  - `ArchetypeEdges.cs`：增删组件迁移缓存
- 数据流 / 控制流：
  - `World` 创建实体后放入空签名 archetype
  - `World.Create<T...>` 当前为 `1..16` 个组件提供固定重载；它会先算目标签名，再把 entity 和组件直接写入最终 archetype，不经过 `Create -> Add -> Add` 的迁移链
  - `World.EnsureCapacity` 负责提前扩好 entity metadata 存储，避免 `Create` 只靠 `List<T>` 被动增长
  - `World.CreateMany` 先批量准备 entity id，再用 `Archetype` 的 chunk-batched reservation 一次性把一批实体落入空签名 archetype
- `World` 内部把 entity version 和 entity location 分开存储：`_versions` 管版本校验，`_locations` 只保留 archetype/chunk/row，避免 metadata 热路径重复写 version
  - `World.Replay(in FrameCommands)` 会在 batch 模式下 materialize created entities、应用 link/unlink 和结构变化，并把 query layout publish 合并为一次
  - `Entity` 句柄契约里，`default(Entity)` 必须视为非法；真实实体从 `Version = 1` 起步，避免空值和第一个真实句柄混淆
  - `World.IsAlive(Entity)` 复用 `TryGetLocation` 的同一套校验，只有在 `id` 在范围内、`_locations[id]` 非空且 `_versions[id] == entity.Version` 时才返回 `true`
  - `Add/Remove` 先算目标签名，再复用 edge cache
  - `Set` 在组件已存在时直接定位到 typed column 的 row，原地写回，不触发迁移
  - `Archetype` 负责把实体放进可写 chunk，并优先复用已有空位的 chunk，而不是盲目只往最后一个 chunk 追加
- `Chunk` 负责 dense row 的单个/批量插入、读取、swap-remove 和 direct-index 写入
- `Chunk` 也应该暴露当前有效 entity 行的 span 视图，给 query / benchmark 这类纯读热路径直接扫 `_entities[0..Count)`，避免逐行 `GetEntity(row)` 的重复边界检查和调用成本
- `Query` 先缓存匹配 archetype，再暴露 chunk 枚举和 `GetChunkSpan()` 这类 span-first 读入口
- `QueryDescription` 只保存 `Type` 集合，不直接保存 `ComponentType`；真正进入执行时由 `MiniArch.Core.Query.Create(world, in description)` 或 `World.Query(in description)` 把它翻译成当前 world 的 `QueryFilter`，再复用现有 `Query` 缓存
  - query 读路径使用 world 发布的 archetype 数组快照和 query 自身发布的 matched-archetype 数组快照，避免共享可变列表
  - query cache 失效判定应该以 world 统一的 query generation 为准；archetype publish、query layout 变化、deferred layout flush 都只推进这一份 generation，warmed 读路径才能维持最小固定成本
  - query filter materialization 的内部实现应一次性收集 `ComponentType[]`，再交给 `QueryComponentSet.CreateFrom(...)` 原地排序/去重；不要在这条热路径上继续用链式 `Add()` 反复复制小数组
- 和其他模块的交互方式：
  - `World` 通过 `ComponentRegistry` 把类型映射成 `ComponentType`
- `World` 通过 `Signature` 定位 archetype
- `Archetype` 通过 component-to-column 索引把 `Set` 路径压成一次定位 + 一次写入
- `World.Destroy(...)`、`CollectCurrentDestroyClosure(...)` 和 `ReplayWithReverse(...)` 的 destroy 预处理会复用 world 内部 scratch，而不是每次临时 new `List` / `HashSet`
- `HierarchyTable.CollectDestroySubtree(...)` 现在接受 caller-owned visited / order 容器，避免在 subtree 收集时额外分配 traversal stack
- `Query` 的 warmed 热路径应该尽量只比较一份 world 侧统一 query generation；不要在热循环里同时读取 `ArchetypeGeneration` 和 `QueryLayoutGeneration` 两份状态。

## 决策

- 用 `ComponentType` 而不是直接用 `Type` 作为运行时 key，保持签名和缓存更轻。
- 把数据迁移逻辑放在 `World`，把存储细节放在 `Archetype` 和 `Chunk`，避免职责混杂。
- 用 `Signature` 作为 archetype key，保证等价组件集合始终落在同一个 storage family。
- query filter 也统一用 `Signature` 表达，避免额外的 query-only 组件集合结构。
- 用 chunk 级迭代而不是 entity 级全表扫描，保留局部性和后续优化空间。
- `Set<T>` / `Add<T>` 的原地写入路径要优先走 typed columns + 组件 id -> 列索引表，避免 `object` 盒化和 chunk 字典查找。
- `World` 侧可以对泛型组件类型做按 `T` 的注册缓存，减少热路径里的重复 registry 查找。
- command buffer 对运行时的扩展应该尽量复用 world 现有的 free-list、version 和迁移能力，而不是再维护一套平行实体生命周期。
- query builder / query 链式 API 也应该复用 world 侧的按 `T` component cache；如果它们绕开缓存直接打 `ComponentRegistry`，query build 的固定 CPU 成本会比必要值更高。
- `QueryDescription` 要保持 world-agnostic，内部应存 `Type` 而不是 `ComponentType`；否则无法安全跨 world 持久复用。
- `QueryDescription` 作为值对象参与缓存 key 时，不能把内部 `Type[]` 直接暴露给外部；公开视图必须防止调用方篡改内部存储。
- `World` 的 entity metadata 需要显式容量管理；如果只依赖 `List<T>` 的自然扩容，`Create` 的分配会长期高于合理水平。
- deferred entity reservation 不能直接把 reserved entity 视作 alive；只有 replay materialize 后 `_locations[id]` 才能变成可见实体。
- `default(Entity)` 不应该是合法句柄；如果把 `(0,0)` 当成活体 entity，所有 optional/out/default 初始化场景都会变成隐性 bug 源。
- 单实体带组件创建也应该直接落到目标签名 archetype；如果退回到 `Create` 后链式 `Add`，会制造只服务于迁移的中间 archetype 和空 chunk。
- 由于 C# 没有 variadic generics，带组件 `Create` 的高性能主路径需要采用固定 arity 重载；当前上限定在 `16`，在保持 typed-column 直写的前提下足够覆盖常见 archetype 初始化。
- `CreateMany` 应该复用一次空 archetype 查找和一次 upfront 容量保证；它不是“外面循环调用很多次 `Create`”的语法糖。
- `CreateMany` 的快路径还需要把“新 id 生成”和“chunk 落位”都批量化；如果仍然逐实体 `List.Add` 或逐实体 `ReserveEntity`，时间会显著落后于 Arch。
- free-list 场景的 `CreateMany` 也应该走 reserve-then-fill：先批量 reserve rows，再在同一轮里写 buffer、chunk entities 和 location metadata；先把 buffer 填满再复制进 chunk 会制造明显的双写。
- `CreateMany` 的 entity metadata 容量不能每次都 exact-fit；mixed/free-list 场景会在 `oldCount + freshCount` 的边界上反复付出 `List<T>` 扩容成本。批量追加时留少量余量，通常比把扩容留到下一次 mixed create 更稳。
- location metadata 不应该重复保存 version；version 已经由 world 级 metadata 持有，再在 location 里留一份会增加 mixed/free-list 路径的写放大与扩容成本。
- 空签名 archetype 只存 entity 列，默认 chunk size 可以比普通 archetype 更大；这样能减少 empty-world `CreateMany` 的 chunk/range 数量，而不会把组件列内存一并放大。
- `World` 默认 chunk 容量不能太小；过小的默认值会在结构迁移时制造大量微型 chunk，把分配和 GC 放大到不合理的程度。
- 默认 `World()` 的非空 archetype 也不应一律固定在 `128 entities/chunk`；更合理的是按每实体近似字节数把 chunk 调到接近固定目标字节数，这样 query world 不会因为“小 chunk 过碎”被额外的 chunk 遍历次数拖慢。
- 但显式传入的非默认 `chunkCapacity` 仍应被视为调用方的确定性约束，尤其是测试和调试场景；不要把它静默改写成自适应值。
- `Archetype` 不能只把写入目标锁死在最后一个 chunk；结构迁移把实体移走后，前面空掉的 chunk 必须可复用，否则 `Remove -> 空 archetype` 会无意义地重新分配 chunk。
- 但“可复用前面空 chunk”不等于“每次插入都线性扫所有 chunk”；remove-heavy / fragmented 场景最终还是要有 non-full chunk 的显式索引，否则 chunk 数一多，插入和 batch reserve 都会退化成扫描成本。
- `ArchetypeEdges` 应该和其他热路径一样使用 component id 直索引，而不是继续停留在 `Dictionary<ComponentType, Archetype>`。
- 兼容构造仍然保留给直接 `new Archetype(...)` / `new Chunk(...)` 的测试和低频调用，但热路径不要依赖它。
- `Set` 的热路径应该是 direct-index 原地写，不应该为了更新一个已存在组件去做结构迁移。
- 在“world 不并发写入”的前提下，query 并发读优先用 copy-on-write 快照发布，而不是加锁；读路径保持数组遍历，写路径承担快照复制成本。
- query cache 应该发布整个 `Dictionary<QueryFilter, Query>` 快照，而不是在共享字典上做并发写入。
- 对大命中 query，读路径真正昂贵的通常不是 filter builder，而是逐 row 的 accessor 开销；如果 chunk 读 API 只能通过 `GetEntity(row)` / `GetComponent(row)` 这种逐元素方法走，边界检查和调用成本会在 `100k+` 档位开始放大。
- 对 entity-only 的 query 消费，`ReadOnlySpan<Entity>` 这类批量读接口通常能立刻降低 CPU，且不会引入额外分配；它是比重构 query cache 更低风险的第一步优化。
- 对 steady-state query 遍历，优先走 `Query.GetChunkSpan()` + `Chunk.GetEntities()` 这类批量读路径；保留 `Chunks` 枚举器作为兼容 API，但不要把它当作 benchmark / profiling 的首选消费方式。
- 对 component-consuming query，优先走 typed chunk 的 `Chunk.GetComponentSpan<T>()`，不要在热循环里逐 row 调 `GetComponent<T>(..., row)`；后者会把列查找、边界检查和方法调用成本重复放大。
- `Chunk.GetComponentSpan<T>()` 只适用于 world/archetype 创建出的 typed chunk；直接 `new Chunk(Signature, ...)` 得到的是兼容用 untyped chunk，这条 API 在那种实例上应视为不可用。
- 结合当前 query sampling，warmed query 的第一优先优化点应放在 traversal / accessor 路径，而不是优先重写 `BuildMatchingArchetypes`；matching 目前只是小头。
- row-wise 组件访问热路径中，`GetComponentIndex()` 已被 `TryGetColumnIndices(...)` 批量 hoist 出 row loop；benchmark 和 profiling runner 的 row-wise 路径已切换到预解析列索引 + `GetComponentAt<T>(columnIndex, row)`，不再逐行调用 `GetComponent<T>(componentType, row)`。
- 如果 query 不只缓存 `Archetype[]`，还缓存了扁平 `Chunk[]`，失效条件就不能只看 archetype 集合变化；同 archetype 内新增或复用 chunk 也必须触发 query snapshot 刷新。
- typed column 的收益会被 `Array` 抽象层吃掉一部分；如果迁移/删除路径仍长期停留在 `Array.Copy` / `Array.Clear` 的逐列调用上，结构变化 benchmark 的 CPU 还会继续偏高。
- typed chunk 的删除路径不应该无差别清空所有列尾槽位；值类型列可以直接保留旧位模式，只有引用类型或“包含引用字段的 struct”才需要清尾，避免无意义的写带宽和 GC 压力。
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
  - 认为 reserved entity handle 已经等于 live entity
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
  - `World.cs`：deferred reservation / replay / batch query invalidation 挂接点

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
- command buffer 如果在 replay 时退回逐条发布 query layout generation，会把 batch replay 重新退化成立即生效路径的可见性成本
- `CreateMany` 的 mixed benchmark 如果刚好落在 metadata 扩容边界上，测量结果会掺入 `List<T>` 扩容与零填充成本；要先分清这部分和 free-list 写入本体各占多少
- location metadata 如果把 version 和位置绑在一个较大的 struct 里，会把 mixed 场景里的 `_locations` 扩容与逐实体写回成本放大
- edge cache 继续用字典，导致热路径风格和 direct-index 存储体系脱节
- query 把匹配 archetype 缓存在共享 `List<Archetype>` 上并原地清空/重建，会在并发只读时制造竞态
- query builder / query fluent API 如果直接调用 `ComponentRegistry.GetOrCreate<T>()`，会绕过 `World` 已经存在的泛型缓存；这不会破坏功能，但会把 query build 的固定成本抬高。
- `QueryDescription` 新增后，`World` 如果额外维护 `QueryDescription -> QueryFilter` 缓存，也要沿用 copy-on-write 发布，不要在共享字典上原地写入。
- `QueryDescription` 的公开类型视图如果把内部数组直接返回出去，会把“值语义 + 缓存 key”契约打穿；这类问题在功能测试里不容易显形，但会在复用或并发共享 description 时变成非确定行为。
- `QueryComponentSet.Add` / `Signature.Add` 这种“每次链式调用都复制一份小数组”的设计在 query build 不频繁时可以接受，但如果 benchmark 把 build 放进测量区，分配会稳定体现在结果里。
- `QueryComponentSet.CreateFrom(ComponentType[])` 是内部热路径的批量入口；它应优先用于 `World.CreateQueryComponentSet` 这种一次性 materialization，而不是继续在循环里调用 `Add()`。
- `Chunk.RemoveAt` 对所有列都做 `Array.Clear`，即使列元素不含引用也照样清空；这会在 remove/destroy 高频场景里制造纯写带宽开销，而不是必要的 GC 保护。
- 判断 typed 列尾槽位是否需要清空时，不能只看 `Type.IsValueType`；带引用字段的 struct 同样需要 clear，运行时判定应以 `RuntimeHelpers.IsReferenceOrContainsReferences<T>()` 为准。
- query benchmark 里如果 world shape 仍通过多次 `Add` 逐步长成，会残留一批历史空 archetype；它们不一定代表 steady-state query 的真实读取成本。
- query profiling 里如果 top1 看起来只是某个外层 `Execute` 包装函数，不要直接把锅甩给 wrapper；常见原因是 `Chunk.GetEntity(row)`、chunk 枚举等小函数被 JIT inline 后折叠进调用者。
- 扁平 chunk snapshot 如果仍然只绑定 `ArchetypeGeneration`，会在“同 archetype 内追加到新 chunk”时静默读到过期 chunk 列表；这种 bug 只有在 chunk 容量很小或 world 很大时才容易暴露。
- 如果默认 world 的 query 吞吐落后明显，而 profiling 又显示 refresh 不是热点，先检查“匹配 chunk 数是否显著多于 Arch”；这通常说明问题在 chunk 粒度，而不是 query matching 本身。
- 自适应 chunk 容量只应覆盖默认 world；如果把显式 `chunkCapacity: 4` 这类测试配置也自动放大，会直接破坏很多依赖固定 chunk 边界的行为测试和诊断场景。
- 容易误判的地方：
  - 以为 `Set<T>` 永远只是原地写入
  - 以为 `Remove<T>` 不存在时应该报错，而不是直接返回
  - 以为“query 是读操作”就天然线程安全；如果底层缓存仍在共享可变集合上原地刷新，一样会出问题
  - 以为 Arch 的 query benchmark 更快，是因为它在逐个 `Add` 时“不会留下空 archetype”；源码和实测都说明它也会留下，差别更多来自匹配缓存、位集判断和 query 遍历实现成本
- 改这里时要特别小心：
  - `Chunk` 的列必须和 `Signature` 完全一致
  - `World` 的 entity version 不能和 location 脱钩
  - `Entity` 的“无效句柄”不能再依赖 `default(Entity)` 之外的隐式约定；当前契约是 `Version > 0` 才算有效
  - `IsAlive` 不应该单独再维护一份“活着”状态；它必须和 `TryGetLocation` 共用同一条 version/location 校验链，避免 destroy/recycle 后出现双重真值来源
  - 性能验证必须看 `Arch` 对照数据，不能只看自己变快
  - 当前代码库里这页描述的是目标态，不是旧版 `Dictionary<ComponentType, object?>` 实现
- 当前并发保证只覆盖“world 无写入时的 query 并发只读”；不要误把它扩展理解成读写并发安全
- `ComponentRegistry.GetOrCreate<T>()` 现在对并发懒注册是安全的，读路径无锁，写路径只在首次注册新类型时加小锁

## 关联模块

- `kb-repo-overview.md`：仓库导航入口
- `kb-snapshot-persistence.md`：运行时 layout 和 snapshot format 的分层边界
- `kb-test-workflow.md`：对应行为覆盖和 benchmark 口径
- `tests/MiniArch.Tests/Core/*.cs`：行为验证
- `benchmarks/MiniArch.Benchmarks/StructuralChangeBenchmarks.cs`：`Create / CreateMany / Add / Set / Remove / Destroy` 热路径对比
