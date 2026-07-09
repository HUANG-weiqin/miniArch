---
title: 设计决策总纲 — 为什么是这样而不是那样
module: Meta
description: 集中解释 miniArch 每个子系统的设计选择、被拒绝的替代方案及其原因，以及常见"优化提案"为什么是误判。新人必读，读完这个再碰代码。
updated: 2026-07-09
---
# 设计决策总纲

> **读这个之前，你不需要先读其他 kb 页。**
> 这个文档的目的：让一个理解 ECS 基本概念的新人，在 10 分钟内理解 "为什么 miniArch 长这样"，并且不会再提出已被评估过并被拒绝的优化方案。

## 这个模块是干什么的

- 集中记载每个子系统的设计决策和推理链。
- 列出被考虑过的替代方案，以及为什么没选它们。
- 列出"听起来不错但实际上行不通"的优化提案，并解释为什么行不通。
- 这个模块不负责：详细实现、API 契约、性能数据——那些在各自的 `kb-*.md` 页里。

---

## 1. 三条基础约束

miniArch 的全部设计都从这三条硬约束推导出来。

### 1.1 实体操作必须是 O(1) 直接数组下标

```csharp
EntityRecord record = _records[entity.Id];  // 一条 LEADS，~0.3ns
```

这意味着 Entity.Id 必须是 `_records` 数组的密集下标。稀疏映射（Dictionary、二分查找）在热路径上会付出 10-30x 延迟代价。第三条约束（lockstep 多 host）会与这条约束产生张力——这是整个库最核心的设计张力。

### 1.2 热路径零 GC

- 所有 struct 字段必须是 `unmanaged` 类型，不能持有引用类型字段（GC write barrier 的代价远超预期——见 `kb-command-stream.md` §Struct 缩小）。
- 所有数组一次性分配，按 doubling 策略扩容。
- 查询结果通过 Span 零拷贝返回，不创建 `List<T>` 包装。

### 1.3 多 host 锁步确定性

lockstep 要求：N 个独立 host 的 World 在 replay 同一个 delta 序列后达到相同状态，且 entity slot 分配序列一致（`EnsureReplayReservation` 不变量）。

这意味着：
- Entity ID 分配必须是单线程确定的（free-list pop 顺序必须跨 host 一致）
- 组件数据 padding 必须为零（`GC.AllocateArray` 零初始化保证）
- 任何依赖 host-local 状态的逻辑都不能进入确定性路径

**第 1.1 条和第 1.3 条的直接冲突**：第 1.1 条要求 ID 密集（作为数组下标），第 1.3 条要求 ID 跨 host 不碰撞。DeferredEntities 机制就是调和这两条的产物——见 §2.3。

---

## 2. 子系统决策

### 2.1 Archetype + SoA 存储

**选择**：按组件集合（Signature）将实体分组到 Archetype，每个 Archetype 按列连续存储组件数据（Structure of Arrays）。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| Sparse set（per-component dense+p sparse 双数组） | 多组件 query 跨多个数组做交集——对 N 个组件，N 次数据依赖的跳转，cache miss 次数是 archetype 的 N 倍 |
| Per-entity AoS（struct 按行存） | 遍历 Position 时要跳过 Velocity、Health 等无关字段——浪费 2/3 的 cache line |
| 全局组件表（`T[entityId]` 直接索引） | 稀疏 100%——10 万个 slot 只有 1000 个实体有 Position，cache miss 灾难 |

**取舍**：archetype 的代价是结构变更时要跨 archetype 迁移实体（全量组件拷贝）。但这个代价发生频率远低于 query 频率——典型游戏帧：10 次结构变更 vs 500 次 query 迭代。热路径优先。

### 2.2 Entity = `(int Id, int Version)` 而非 packed ulong

**选择**：`Entity` 是两个独立 int 字段。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| 打包成 `ulong`（hi32=version, lo32=id） | Wire 上 placeholder `Entity(-1, seq)` 的 version 是 seq 而非真实 version——必须独立编码。运行时 `IsAlive`、`TryGetLocation` 需频繁单独读 Version——每次 `>> 32` 提取。分离编码本来就是 wire format 的正确形态 |

**结论**：两个 int 是自然形态。打包省的是 0 字节（因为 struct layout 里两个 int 已经紧凑），付出的却是到处提取移位的痛。

### 2.3 DeferredEntities（placeholder → real ID）

**选择**：`CommandStream.DeferredEntities = true` 时 `Create()` 返回 `Entity(-1, seq)`，不碰 World ID 分配器。Snapshot 生成 placeholder delta，Replay 时分配 local real ID。

**为什么需要它**：这是 §1.1（ID 必须密集）和 §1.3（跨 host ID 不能碰撞）的直接冲突的解决方案。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| Host ID 分片（高位=hostId, 低位=localId） | host A 创建实体 `(host=0, local=42)` → host B replay delta → B 的 World 也有此实体 → B 的游戏逻辑 destroy 它 → 进 B 的 free-list → B 后续 Create 从 free-list pop 出 `(host=0, 42, version+1)` → "host ID 标识谁创建了实体" 的语义彻底崩了。per-host 分 free-list 则跨 host 实体不能共存于同一个 World，破坏 lockstep 的同构性 |
| 全局哈希查表（Dictionary 替代数组下标） | 放弃 §1.1——每次 entity lookup 变成 ~10-15ns 哈希查找 vs 当前 0.3ns。在 16.7ms 帧预算中占 ~6%，且污染 cache |
| 虚拟内存预留（VirtualAlloc sparse array） | 跨平台不可行（WASM、console），P/Invoke 复杂度与收益不匹配 |

**结论**：DeferredEntities 是调和两条硬约束的唯一可行解。placeholder/real 两套模式的每一个分支都有来自这三条约束的硬理由。

### 2.4 Hierarchy 作为 side-table 而非 Parent 组件

**选择**：`HierarchyTable`——SoA 邻接表，通过 `_parentByChild[id]` O(1) 直索引、`_firstChild[id]` → `_next[slot]` 链表遍历孩子。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| Parent 做成组件 `struct Parent { Entity Value; }` | 级联销毁从 O(subtree) 变成 O(N) 全表扫描。每次 `AddChild` / `RemoveChild` 触发 archetype 迁移——有 100 个孩子的父节点要迁移 100 次。且 query 只过滤组件类型、不能按组件值过滤——所以即使 Parent 是组件也无法写 `With<Parent>().Where(p => p == target)` |

**取舍**：side-table 牺牲的是 query 表达力（无法从 query 系统里看到层级关系），换回的是级联销毁 O(subtree) 和零迁移。kb-architecture-review 里那句 "正确性 > 表达力" 就是基于这个计算。

### 2.5 World 直写 + CommandStream 双路径

**选择**：单线程走 `world.Set(e, pos)` 直写（`archetype.WriteComponentRaw` → `Unsafe.CopyBlock`，单条指令到已知 offset），序列化/网络走 CommandStream 录制 → Submit/Replay。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| 统一 buffer（所有写入都进 per-component 缓冲，EndFrame Apply） | 直写的精髓是一步到位——记录、遍历、Apply 三条路径合成一条。加 buffer 至少 2x 工作量。且直写是立即可见的——调完就能读回来。如果走 buffer，调用方必须知道 "还没 Apply"——引入隐式帧边界 |

**结论**：双路径不是妥协，是服务于不同调用方。单线程追求延迟（直写），锁步延求可序列化（CommandStream）。合并会让两个场景都变慢。

### 2.6 FrameDelta.Concat → 已删除（2026-07-04）

**历史**：`Concat(a, b)` 曾存在为"合并多个 delta"提供方便，但只是纯字节 `Array.Copy` 拼接——不折叠、不压缩、不语义合并。两次 `Replay(a); Replay(b);` 完全等价，零信息增益。生产代码零调用方，仅测试和示例在自我证明。已删除。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| 语义折叠（Create+Destroy 同帧 → 抵消；Set+Set 同组件 → 只保留最后一个） | 折叠逻辑需要完整解析所有 op、构建中间状态、重新 emit——一个状态机。一旦折叠逻辑里有一个 corner case，跨帧 id 回收的正确性就会被破坏 |

### 2.7 ComponentRegistry 全局而非 per-World

**选择**：`ComponentRegistry.Shared` 进程级全局单例。

**为什么**：`Component<T>.ComponentType` 是泛型静态字段——泛型特化在 CLR 层面是 per-`T` 的，必须在进程内唯一。一旦 per-World，整个 `ComponentStore<T>`、`Signature`、edge cache、`FrameDelta` 的 component id 路径全部需要 World 上下文穿透。
假设引入世界级 id 映射表：replay 期间每个 op 都要查表做 id 转换——热路径上再加一次间接寻址。而纯字典映射失败也难排查。当前的单例做法省掉这一整层的复杂度——注册顺序确定性（启动一次性注册）在真实游戏中是自然成立的。

### 2.8 Query 两段式失效而非 per-filter 跟踪

**选择**：`archetypeCount` 粗扫 + per-matched-archetype `segmentCount` 细扫。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| Per-filter 增量失效（每个 query 只 track 自己的 matched archetype 列表） | 稳态下当前方案只是一次 `cmp`，0.3ns 就返回，与 per-filter 的 `cmp` 无差异。非稳态下 Refresh 的成本（1000 次 `ulong &` ≈ 250ns）比创建新 archetype 本身（~5000-10000ns）小两个数量级。在帧预算 16.7ms 里占 0.0015%。优化它是用代码复杂度换噪音级别的 CPU 时间 |

**结论**：两段式已经是最简最优解。per-filter 跟踪是一个假痛点。

### 2.9 Structural Change（Add/Set/Remove）的当前语义

**选择**：`World.Add<T>` 是 strict add：组件不存在时迁移到新增组件的 archetype，组件已存在时抛异常；`World.Set<T>` 是 strict set：组件不存在时抛异常；`World.Remove<T>` 在组件不存在时 no-op。

**为什么 Add 保持 strict**：`Add<T>` 与 `Set<T>` 分别表达"新增组件"和"写已有组件"，概念唯一。重复写已有组件必须走 `Set<T>`，否则同一事实存在两个写入口，调用方和 replay 日志都更难审计。历史记录中曾讨论过 Add-as-overwrite 的收敛问题（见 `kb-code-review-findings.md` B1/B4），但当前代码与回归测试 `Add_component_that_already_exists_throws` 以 strict Add 为准。

**性能影响**：`TryGetComponentIndex` 调用不是"多余防御"——Add 必须先判断组件是否已存在，已存在时 fail-fast，缺失时才走 edge cache + MoveEntity。这个检查是语义分支本身，不应为了热路径删除。

### 2.10 FrameDelta 不内置来源标识

**选择**：FrameDelta 不携带任何可序列化的来源指纹（host id / source id）。来源标识是传输层职责，由用户在 FrameDelta 外面包一层 Envelope。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| 在 FrameDelta 里内置可序列化 sourceId | 传输层信封（frame number、ack 等）是网络游戏的必需品，不是可选项——Envelope 里多一个 `SourceHostId` 字段不构成额外代价。sourceId 语义（host id？player id？session id？）由具体网络架构决定，核心猜不准，硬编码反而限制灵活性。FrameDelta 是纯操作序列，混入传输层元数据违反概念唯一，且增加热路径序列化体积 |

**现状**：`CommandStream.Replay(delta, resolveSlots)` 由用户显式控制——默认不解析，对 own delta 传 `true`。详见 `kb-deferred-create-design.md` §决策 #5。

### 2.11 Change Tracking（Watch pull-event 模型）

**选择**：纯 pull-event 模型，三个统一入口：

- `world.Watch<TComponent, THandler>(QueryDescription?)` → `ChangeWatch<TComponent, THandler>`：值变更追踪。
- `world.Watch<TComponent, TValue, THandler>(QueryDescription?)` → `ChangeWatch<TComponent, TValue, THandler>`：投影值变更追踪。
- `world.Watch<THandler>(QueryDescription)` → `TransitionWatch<THandler>`：结构变更追踪。

所有 Watch 类型遵循相同的两阶段模式：
1. `Snapshot(World)`：记录当前 world 状态的 baseline（dense array by entity.Id）。
2. `Diff(World)`：重新扫描 world，对比 baseline，先收集所有 diff 到内部 buffer 再逐条回调 `IChangeHandler.OnChange` / `ITransitionHandler.OnChange`。

Watch 不向 World 注册、不拦截 `Set`/`Add`/`Remove`、不维护 per-type registry。每个 Watch 实例独立持有自己的 dense arrays，互不干扰。

**为什么替换旧 API 的拆分设计（TrackValueChanges + CreateDenseValueDiff + TrackTransitions）**：

旧架构有三条独立路径——`TrackValueChanges<T>()`（public boundary diff）、`CreateDenseValueDiff<TComponent,TValue,TProjector>()`（high-perf explicit shadow diff）、`TrackTransitions(QueryDescription)`（structure transitions）。三条路径各有不同的内部机制：shared registry vs per-handle dense shadow vs dispatch-based log。用户需要在两个 value diff API 之间做选择，且共享 registry 在 World 中添加了侵入式状态。Watch 模型将三者统一为同一模式（Snapshot + Diff），取消 world 注册、取消 dispatch、取消两套 value API 的冗余。旧文件（`SharedValueChanges.cs`、`TransitionLog.cs`、`ChangeTracker.cs`、`SharedTrackerRegistry.cs`、`DenseValueDiff.cs`、`IValueProjector.cs`、`IValueChangeSink.cs`、`IChangeQuery.cs`、`ValueChange.cs`、`Transition.cs`）全部删除，零兼容 shim。

**取舍**：Watch 模型将扫描成本完全放在 `Diff` 阶段（O(匹配实体数)），换取写入热路径零成本（`Set`/`Add`/`Remove` 无 watch 分支）。两阶段 buffer 机制确保 callback 安全（handler 可在回调中 mutate world），但增加一次内存拷贝。相比旧模型的 per-type shared registry + live dispatch，Watch 模型更简单、更可预测、零 world 侵入。

**考虑过的替代方案**：
| 替代方案 | 为什么拒绝 |
|---------|-----------|
| push 式 event 回调（observer 模式，Set 时内联触发 handler） | handler 是任意用户代码，在 mutation 热路径执行→读 host-local 状态即破坏 §1.3 锁步；handler 分配破坏 §1.2 零 GC；且重复 FrameDelta 的 mutation 日志角色违反概念唯一 |
| GetForWrite\<T> 写变体 API | 新增 API 面，违反 YAGNI；Snapshot/Diff 已能捕获 `GetRef<T>` / chunk span 直接写，无需新写 API |
| world-shared per-component 注册表（旧 `SharedTrackerRegistry`） | Watch 不注册 World，允许多个 watch 同一组件独立消费、独立 baseline、互不干扰。旧架构的 shared per-type tracker 存在多 handle 共享同一个 `ChangeTracker<T>` 导致意外相互影响的问题（一个 handle 的 ClearAll 影响所有同类型 handle） |
| 保留旧 `TrackValueChanges` + `CreateDenseValueDiff` 并存 | 两个入口语义重叠（都是 value diff），强制用户做不必要的性能/便利权衡。统一为 `ChangeWatch` 消除选择负担 |
| per-chunk version counter（旧 `ChangeQuery` 的 `ModifiedChunks()` 机制） | 需要在 write 路径维护 per-chunk dirty version，且与 `TrackTransitions` 机制正交。Watch 的纯 Snapshot/Diff 方案不需要任何 write-time 维护 |
| net-value diff 自动取消（A→B→A） | 旧 `TrackValueChanges` 实现了自动 net diff 取消。Watch 模型不提供此功能——用户看到的 raw old/new 值是 snapshot 与 diff 时的对比结果。如果需要取消语义，用户自行在 handler 中处理。此决策是为了保持简单和可预测 |
| 世界级 transition dispatch（旧 `IChangeQuery.OnTransition` 机制） | World 在 Add/Remove/Destroy 时遍历所有注册的 `IChangeQuery` 并 dispatch transition。Watch 模型完全不做 dispatch——TransitionWatch 通过集合对比（Snapshot vs current）发现变化。这不仅消除了 World 中的 dispatch 遍历，还让 transition 发现与结构操作解除耦合 |

---

## 3. 常见误判优化

这些优化提案在初次接触代码时很自然会被想到——它们确实"听起来对"。但它们全都被评估过并被拒绝了。

### 3.1 "Query per-filter 增量失效"

**提案**：每个 query 自己 track matched archetype 列表，新 archetype 出现时只通知匹配上的 query，避免全局 mask-match 重扫。

**为什么不对**：稳态 = 一次 `cmp`（`archetypeCount == _lastCount`），已经是 per-filter 能做到的最优情况。非稳态 = mask-match 重扫成本 ≈ 250ns，是 archetype 创建成本的 2%。节省这 2% 要用一堆 event 通知 + per-query 列表维护换。

### 3.2 "查询结果 entity 列表跨帧缓存"

**提案**：同一个 query 帧间复用上一帧的 entity 列表，省掉重建。

**为什么不对**：(1) 需要 entity 列表的场景是冷路径——query 热路径是 `chunk.GetSpan<T>()`，完全不碰 entity 列表。(2) 帧间有 create/destroy/结构变更时缓存即失效，命中率在战斗帧可能不到 20%。(3) 失效检查需要遍历所有 matched archetype 比对各版本号——成本跟重建 entity 列表本身（n×8B Array.Copy 段内复制）在同一个量级。

### 3.3 "Entity 打包成 ulong"

**提案**：`Entity(ulong)` 代替 `Entity(int Id, int Version)`。

**为什么不对**：wire 上 placeholder `Entity(-1, seq)` 的 Version 是 seq 而非真实 version——必须独立编码。运行时 `IsAlive` / `TryGetLocation` 需频繁单独读 Version——打包后每次提取要多一个 `>> 32`。且两个 int 在 struct layout 里已经是紧凑的 8 字节——打包不省任何空间。

### 3.4 "Parent 做成组件"

**提案**：Parent 关系作为组件，通过 query 系统发现子实体。

**为什么不对**：级联销毁变成每次在当前帧中执行一次完整查询定位子实体——O(N) 替代 O(subtree)。AddChild / RemoveChild 变成结构性组件增减——一次 100 孩子的父节点要迁移孩子实体通过父子关系的父/子集；每增删一个 parent 要迁移到不同 archetype。而当前 query 系统只按组件类型过滤，不能按组件值过滤——所以即使 Parent 成为组件，仍然不能用 `With<Parent>().Where(p == target)` 直接找到特定父节点的所有孩子，需要额外构建遍历机制。side-table 给出的是完整且高性能的层级访问，而不是寄望于 query 能力的扩展。

### 3.5 "统一 buffer——所有写入走 CommandStream"

**提案**：去掉 World 直写路径，全部写入进 CommandStream 缓冲，EndFrame 统一 Apply。

**为什么不对**：World 直写的精髓是一条指令到已知 offset——`archetype.WriteComponentRaw(colIdx, rowIdx, src)` → `Unsafe.CopyBlock`。加入缓冲后变成：Store → for-loop → Apply → CopyBlock。中间至少加 2x 工作量。而且直写是立即可见的——同一帧内写入后立刻可读。走缓冲意味着必须理解 "当前帧还没 Apply" 的隐式约束。双路径不是技术债——它们服务不同的场景：直写给单线程低延迟，CommandStream 给锁步可序列化。

### 3.6 "free-list 反向 HashSet 加速 EnsureReplayReservation"

**提案**：用 HashSet 做 O(1) free-list 存在性检查，替代当前 O(n) 线性扫描。

**为什么不对**：Reserve 路径在正常 replay 流程中不构成热点——每个实体的 Reserve 只做一次（分配 ID），而每个 id 在 delta 中其余 op（Create / Add / Set / Remove / Destroy）中的使用次数远大于 Reserve。改它的收益为零，维护成本加一个数据结构。

### 3.7 "Signature 分配太多，用 interning 或 stackalloc"

**提案**：Create/Add/Remove 时 `new Signature(types)` 每次堆分配，用池化或 interning 消掉。

**为什么不对**：创建路径已有 `CachedCreateArchetype` 泛型静态缓存——同一组件组合的第二次 `Create<T...>` 直接命中缓存中提前存储的 Signature，零分配。Add/Remove 路径已有 edge cache——同一 archetype 迁移方向第二次命中缓存，零 `Signature.Add/Remove`。唯一分配场合是冷启动（每对组件组合 / 每对迁移方向只发生一次），冷缓存里一次 ~60B gen0 分配微不足道。

### 3.8 "chunk swap-remove 对大组件的代价"

**提案**：按列独立索引避免删除时全量拷贝超大组件（如 Matrix4x4）。

**为什么不对**：Swap-remove 需要对每个组件列做一次拷贝。大组件 (256B) 比小组件 (8B) 慢 32x 是物理事实，但要消除它就需要每个组件列独立维护自己的 swap 逻辑——本质是引入 sparse set 混搭。这会炸掉当前 "列偏移 = _columnByteOffsets[col] + row × _elementSizes[col]" 的简单公式，把整个存储层复杂度推高且 query 路径要承受分散访问的额外 cache miss。不值得。

### 3.9 "拆分热路径类型用 abstract + override"

**提案**：把 CommandStream 这种"按 mode 行为不同"的热路径类型，拆为 `public abstract class Base` + 多个 `public sealed class Sub : Base`，用 `abstract` 方法 + `override` 实现特化。

**为什么不对**：在 .NET 8 上，generic virtual 方法（`Add<T>` / `Set<T>` / `Remove<T>`）**不**被 JIT 可靠 devirtualize，即使 receiver 静态类型是 sealed 子类。每次调用付虚表查证 + 阻止 inline。CommandStream 拆分时实测 HeroComing.Perf regression ~10%（Movement 1917→1737，Attack 1205→1063）。详见 `kb-code-review-findings.md` CS9。

**正确做法**：Base class 只提供 `protected *Core()` helper 方法（无同步），子类直接从自己的 `public` 非虚拟方法调用。比起 `abstract`+`override` 快 7-8%（去掉了虚表查证和 mode flag 分支判断）。Base class 不公开不抛出——调用方在编译时就无法通过基类引用调用 mutator，把运行时的 `NotSupportedException` 变成了编译错误。

**判定标准**：在 ECS 热路径（每帧调用几千次的 API）上，绝对不要用 abstract+override。Base class 上的公共 mutator 要么是合法（Submit / Snapshot / Clear / Replay），要么不存在（Create / Add / Set 等不暴露）。在 base class 内部递归调 *Core helper 而不是公共 mutator。

### 3.10 "push 式 event 回调 / inline observer"

**提案**：`world.OnChange<T>(handler)`，Set/Add/Remove 时内联触发 handler，让用户"注册 event 直接用"。

**为什么不对**：(1) handler 是任意用户代码，在 mutation 热路径执行——读 `DateTime.Now` 等 host-local 状态立即破坏 §1.3 锁步确定性。(2) handler 分配（closure/LINQ/lambda）破坏 §1.2 热路径零 GC。(3) 概念唯一：FrameDelta 已是所有 mutation 的完整日志，"组件被 Add 了"已被表示为 delta 里那条 Add op，再建一套并行回调通知是同一事实的两个表示。(4) 对渲染层这个主要场景反而更差：回调在逻辑更新中途触发（entity 可能下一行就 destroy），必须排队留到渲染阶段处理=重新发明轮询；且散落回调 cache 敌友性差于批量 chunk 遍历。

**正确做法**：pull 式变更检测——Track\<T> 游标 + ModifiedChunks/Transitions。确定性安全（mutation 只 bump 派生数据）、零 GC（纯 int/long）、cache 友好（chunk 批量）。详见 `kb-change-tracking.md`。

### 3.11 "防御性检查删掉，Release 模式全速跑"

**提案**：`Get<T>()` 中的实体存活检查（ID bounds + 版本号 + 占用位）、`GetRecordFast` 中的 ID 越界检查、`RestoreState` 中的跨世界检查等，在 Release 模式下跳过，因为"每次调用多几条指令"。

**实测证据（2026-07-06，HeroComing.Perf 30s + CommandBuffer throughput-cb 3s）：**

| 基准 | 无检查 | 全部检查 | 差异 |
|------|--------|---------|------|
| HeroComing Movement (r/s) | 2117 | 2084 | **-1.6%** ±1.6% 噪声 |
| HeroComing Attack (r/s) | 1300 | 1227 | **-5.6%** ±5.9% 噪声 |
| CommandBuffer 1000/CreateHeavy (ops/s) | 11987 | 11581 | **-3.4%** ±3.5% 噪声 |
| CommandBuffer 10000/DenseExisting (ops/s) | 962 | 925 | **-3.8%** ±10.5% 噪声 |

**所有差异都在正常运行噪声范围内。添加检查后零可测性能下降。**

**为什么**：每次 `Get<T>()` 增加的开销：
1. 1 次 `_disposed` bool 读取（已 L1 缓存）
2. 1 次 uint 范围比较（`entity.Id >= _entitySlotCount`）
3. 1 次 `record.IsOccupied` 空值检查
4. 1 次 `record.Version == entity.Version` 比较
5. 1 条从不执行的分支（正确代码永不触发异常）

CPU 分支预测器将未触发分支的成本融合为零。额外指令与周围逻辑融合（同一条 cache line），不额外产生 cache miss。结论已由 6 次独立运行 × 2 套基准测试 × 2 个场景确认。

**提案拒绝理由**：这些检查对用户可见的正确性有可测收益（将 4 个"静默数据损坏"场景转化为 `InvalidOperationException` + 1 个"灾难性跨世界恢复"被拦截），但对性能的代价为**零可测影响**。删掉它们是在用正确性换不存在性能收益。

**建议**：在 hot path（`Get<T>` / `GetRef<T>`、`GetRecordFast`）上保持无条件检查；在 cold path（结构变更的 `AssertValidRow`、原型创建的 `AssertPositiveElementSize`）上更应保持无条件。此条适用所有"删防御换速度"的同类提案。**先跑基准再说话。**

---

## 4. 真正的待办项

没有剩余“应做的大重构/性能优化”。O1-O5 已全部评估并解决或拒绝：

- O4（FrozenState 字段对调）→ 已完成。`FrozenState` 是被整体换出/回收的引用对象；`SwapOutState` 做单次对象引用交换，不再逐字段 swap，消除了字段漏交换这类 bug。
- O1/O3/O5 → 已评估，不值得做（见 §3.1、§3.6、§3.8）
- O2 → 已有 `CachedCreateArchetype` + edge cache 解决（见 §3.7）

库处于维护期。剩余工作是补测试覆盖、完善文档，以及处理 `kb-code-review-findings.md` 中记录的少量 P2 defense-in-depth 设计债。

---

## 认知模型

- 理解 miniArch 的最佳隐喻：**一张 entity→location 的扁平数组 + 按组件集合分组的密集列存 + 一套延迟命令录制器**。所有功能都是这三个原语的组合。
- 三个基础约束（§1）是所有设计的根因。在读任何单个子系统的决策（§2）时，追溯它回到哪条约束。
- 常见误判优化（§3）之所以看起来对，是因为没做"回到约束"这一步。每个条目追溯到基础约束就能看出为什么是误判。

## 入口

- 新人：本文档 → `kb-architecture-review.md`（全局审视）→ 按 `INDEX.md` 路标进入各子模块
- 在想"能不能改成 X"之前：查本文档 §2 和 §3，看是否已被评估
- 提出新方案前：检查方案是否同时满足 §1 的三条约束

## 坑点

- 最大的坑：不清楚约束就提优化——回头看 §1 再动手
- 这个文档的"为什么拒绝"只记录了当时考虑过的理由。如果游戏场景的假设变了（比如不再做 lockstep），那么某条约束不成立，对应的设计就可能需要重新评估——但这必须显式确认约束变化为前提
