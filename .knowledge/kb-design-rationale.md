---
title: 设计决策总纲 — 为什么是这样而不是那样
module: Meta
description: 集中解释 miniArch 每个子系统的设计选择、被拒绝的替代方案及其原因，以及常见"优化提案"为什么是误判。新人必读，读完这个再碰代码。
updated: 2026-07-02 (O4 已在 d70c0c3 完成；新增 §3.7 Signature 分配误判；O1-O5 全部闭环)
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

### 2.6 FrameDelta：简单拼接而非语义折叠

**选择**：`Merge(a, b)` = 两行 `Array.Copy` 把两个 `byte[]` 缓冲区拼在一起。不解析、不折叠、不去重。

**考虑过的替代方案**：

| 替代方案 | 为什么拒绝 |
|---------|-----------|
| 语义折叠（Create+Destroy 同帧 → 抵消；Set+Set 同组件 → 只保留最后一个） | 折叠逻辑需要完整解析所有 op、构建中间状态、重新 emit——一个状态机。一旦折叠逻辑里有一个 corner case（比如 Destroy 发生在 Create 之前——来自另一个 delta），跨帧 id 回收的正确性就会被破坏。`Array.Copy` 简洁正确——时序信息完整保留 |

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

### 2.9 Structural Change（Add/Set/Remove）的 Strict 语义

**选择**：`World.Add<T>` 在组件已存在时抛异常；`World.Set<T>` 在组件不存在时抛异常。CommandStream 和 Replay 路径同样 strict。

**考虑过的替代方案**：我们最初作为历史包袱保留了 upsert（`Add`/`Set` 是同义别名），后来改为 strict。详见 `kb-core-ecs.md` 决策段。

**性能影响**：strict 的 `TryGetComponentIndex` 调用不是"为 strict 加的"——旧 upsert 代码同样需要它来判断是 Set-in-place 还是 Move 实体。热路径开销为零。只是把 `else` 分支从"静默迁移"改成了"抛异常"——异常分支在正确代码中永远不会被 CPU 预测到。

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

---

## 4. 真正的待办项

**无。** O1-O5 已全部评估并解决：

- O4（FrozenState 字段对调 struct 化）→ `d70c0c3` 已完成。`FrozenState` 现为 struct，`SwapOutState` 一次整体赋值，消除了字段漏交换这类 bug。
- O1/O3/O5 → 已评估，不值得做（见 §3.1、§3.6、§3.8）
- O2 → 已有 `CachedCreateArchetype` + edge cache 解决（见 §3.7）

库处于维护期。剩余工作是补测试覆盖、完善文档。

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
