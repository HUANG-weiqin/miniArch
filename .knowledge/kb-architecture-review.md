---
title: Architecture Mechanistic Review
module: MiniArch.Core
description: Mechanistic insight of the entire miniArch ECS library — one-line truths, minimal loops, state models, data flows, known issues, and optimization opportunities for each module
updated: 2026-06-01
---
# Architecture Mechanistic Review

## 这个模块是干什么的

- 这个模块是整个 miniArch ECS 的架构审视记录，覆盖：
  - 10 个核心子系统的机械化拆解（一句话真相 + 核心循环 + 状态模型 + 数据流）
  - 5 个真实问题（语义隐患、粒度不足、内存膨胀等）
  - 4 个可优化点
  - 3 个根本性的设计张力
- 这个模块不负责：
  - 替代各 `kb-core-ecs.md` / `kb-command-buffer-feasibility.md` 的详细实现记录
  - 列举具体 benchmark 数据（见 `kb-core-ecs.md` 的基准结果快照）

## 架构

### 全局一句话

miniArch = 一张 entity→location 表 + 按组件集合分组的密集 byte 存储 + 一套延迟命令录制器。所有功能（Add/Remove/Destroy/Query/Clone/Snapshot）都是这三个原语的组合。

### 重建直觉

如果从零重建，只需要三样东西：
1. 一张表 `_locations[id]` → (archetype, chunk, row)
2. 一个分组规则 Signature → Archetype
3. 一个搬家过程：swap-remove 旧位置 → 写入新位置 → 修表 → 标记过期

其他所有东西（bitmask、edge cache、arena slab、deferred suppression）都是加速。

### 全局依赖图

```
ComponentRegistry.Shared (全局 Type↔id)
       ↓
  ComponentType (int wrapper)
       ↓
  Signature (排序 ComponentType[] + bitmask)
       ↓
  Archetype (签名→chunk 列表 + edge cache)
       ↓           ↓
  Chunk (byte[])   Query (archetype 快照 + generation)
       ↓
  World (编排一切)
       ↓
  CommandBuffer → FrameDelta → World.Replay
  HierarchyTable (side-table)
  WorldSnapshot / WorldClone
```

---

## 模块 1：实体身份（Entity / EntityLocation / Version / FreeList）

### 一句话

Entity 是 `(id, version)` 二元组；World 维护两张并行数组 `_versions[]` 和 `_locations[]`，加一个栈式 free-list 复用已销毁 id。

### 核心循环

```
分配 id:
  if freeIds 非空: pop(id, version) → return (id, version)
  else: id = slotCount++; versions[id] = 1 → return (id, 1)

校验存活:
  return id < slotCount && locations[id] != null && versions[id] == entity.Version

销毁回收:
  versions[id]++            // 使旧句柄失效
  locations[id] = default   // 清空位置
  freeIds.Push(id, newVersion)
```

直接 `World.Create/Destroy` 走无锁 `_freeIds` 热路径；只有 `CommandBuffer` recording 的 deferred reservation 通过 `ReserveDeferredEntity` 内部锁串行化，因为并发保证只覆盖多 buffer 录制，不覆盖 world 并发写。

### 状态模型

- `_versions[id]`：版本号，销毁+1，防止悬空句柄复用
- `_locations[id]`：当前 (archetype, chunkIdx, row)，null = 已死
- `_freeIds[]` 栈：LIFO 回收

### 去幻觉

- `Entity.IsValid` 只检查 `Id>=0 && Version>0`，不检查存活。`IsAlive` 才查版本+位置。
- `EntityInfo` 是临时产物，每次从 `_locations + _versions` 组装，不是持久状态。

---

## 模块 2：存储（Chunk）

### 一句话

一块 `byte[]` 按列排布所有组件数据，通过 `列偏移 + row × 元素大小` 做 O(1) 定位，swap-remove 删除行。

### 核心循环

```
读取 T:
  ref byte ptr = &data[columnOffset[col] + row * elementSize[col]]
  return Unsafe.As<byte, T>(ptr)

写入 T:
  同上，直接赋值

删除 row:
  last = --Count
  if row != last:
    entities[row] = entities[last]    // 句柄交换
    for each col: Unsafe.CopyBlock(last → row)  // 组件字节交换
  entities[last] = default
```

### 状态模型

- `_data: byte[]`：所有列的连续字节存储
- `_entities: Entity[]`：每行一个实体句柄
- `_columnByteOffsets[]` / `_elementSizes[]`：列定位表
- `_componentIdToColumnIndex[]`：组件 id → 列索引 direct map
- `Count`：当前有效行数

### 去幻觉

- `CopySharedComponentsFrom` 同签名快速路径是逐列 byte block copy，不是 memcpy 整块。
- Boxed reader/writer delegate 只用于 snapshot 恢复等非热路径，正常 `Set<T>/Get<T>` 完全不走它们。
- Managed reference check 在 chunk 构造时 fail fast，flat byte 只面向 unmanaged 类型。

---

## 模块 3：Archetype

### 一句话

同一 Signature 的 Chunk 列表 + 有序"未满 chunk"索引，保证插入 O(1) 取可写 chunk。

### 核心循环

```
ReserveEntity:
  if TryTakeNonFull(out chunk): return chunk.Add(entity)
  else: return NewChunk().Add(entity)

RemoveEntity(chunkIdx, row):
  moved = chunks[chunkIdx].RemoveAt(row)  // swap-remove
  MarkChunkNonFull(chunkIdx)               // 可能重新入未满索引
  EntityCount--
  return moved                             // World 负责修 moved 的 _locations
```

### 去幻觉

- 有序未满索引可以有过期条目（chunk 已满但未移除），`TryTakeNonFull` 懒惰过滤。
- Archetype 本身不知道 entity id/version，只管 chunk 和 row。

---

## 模块 4：Signature & Edge Cache

### 一句话

Signature = 冻结的排序 `ComponentType[]` + 预算的 `long` bitmask；Edge Cache = 两个稀疏数组按 componentId 做 O(1) archetype 迁移查找。

### 核心循环

```
Signature.Add(component):
  if Contains(component): return this
  插入到排序位置，返回新 Signature

Signature.Contains(component):
  if id < 64 && !(mask & (1<<id)): return false   // bitmask 快速拒绝
  if len <= 4: 线性扫描
  else: 二分查找

Edge Cache:
  _addEdges[componentId] → 目标 Archetype
  _removeEdges[componentId] → 目标 Archetype
  未命中 → 算目标签名 → GetOrCreateArchetype → 双向缓存
```

### 去幻觉

- Bitmask 只覆盖 id 0~63 的组件。超过 64 种退化为二分查找。
- Edge cache 是双向的：`source.CacheAdd(comp, dest)` 同时做 `dest.CacheRemove(comp, source)`。

---

## 模块 5：组件类型系统

### 一句话

`ComponentType` 就是 int；`ComponentRegistry.Shared` 是全局 Type→int 双向映射（copy-on-write）；`Component<T>.ComponentType` 静态字段消除热路径查找。

### 核心循环

```
热路径: Component<T>.ComponentType   // 静态字段读
冷路径: Registry.GetOrCreate<T>()    // copy-on-write snapshot
```

### 去幻觉

- Registry 是全局单例，所有 World 共享。这是 FrameDelta 跨 World replay 的前提。
- `ComponentWriterCache` 的 delegate 只用于 CommandBuffer 的 byte→chunk 写入路径。

---

## 模块 6：Query 系统

### 一句话

QueryDescription（用户层 Type 集合）→ QueryFilter（运行时 ComponentType 集合）→ Query（快照匹配的 archetype/chunk 列表，generation 驱动惰性刷新）。

### 核心循环

```
匹配 archetype:
  archMask = archetype.Signature.ComponentMask
  if (requiredMask & archMask) != requiredMask: SKIP
  if (excludedMask & archMask) != 0: SKIP
  → PASS

迭代 entity:
  for chunk in snapshotChunks:
    entities = chunk.GetEntityStorage()
    for row 0..chunk.Count: yield entities[row]

失效检查 (per-archetype generation):
  if !_initialized || world.ArchetypeVersion != snapshotArchetypeVersion: REFRESH
  for each snapshot archetype: if archetype.Generation != snapshotGenerations[i]: REFRESH
  else: 用缓存
  REFRESH: lock → 双检 → BuildMatchingSnapshot → swap scratch/snapshot → 更新 generations
```

### 去幻觉

- Query 匹配的是 archetype，不是 entity。匹配的 archetype 里所有 entity 都命中。
- 快照是非原子的（archetype 和 chunk 数组分开写入）。安全性靠"world 无并发写"前提。
- `OrderedQuery` 是消费层 materialization + sort，不进入 core query cache。

---

## 模块 7：Hierarchy

### 一句话

邻接链表 hierarchy：`_parentByChild[id]` 指向父节点，`_firstChild[id]` + 链表 slot 管理子节点，free-list 复用 slot。

### 核心循环

```
Link(parent, child):
  验证无环 → Unlink(child) → AllocateSlot → 头插法 → linkCount++

Destroy 子树:
  if linkCount == 0: 完全跳过 hierarchy side-table
  if root 无 children: 只清自身 parent/slot
  DFS 后序遍历（visited generation 防重）→ 逐个 DestroySingle
```

### 去幻觉

- Hierarchy 是 side-table，不参与 archetype 签名，不能用 Query 查询。
- Children 枚举跳过已死 entity（`world.IsAlive`），链表里可以有脏条目。
- 没有任何 hierarchy link 时，Destroy 不应该查 `HasChildren` / `RemoveDestroyed`；否则纯 ECS create/destroy 会为可选 side-table 付 8-12% 级别固定成本。

---

## 模块 8：CommandBuffer

### 一句话

每个线程一个 buffer，用 InlineMap(4 内联 + 链表 overflow) 按 entity×componentType 去重录制命令，arena slab 存组件数据，Submit 直接回放到 World。

### 核心循环

```
录制: slab.Write(value) → InlineMap.Set(typeId, ptr) 去重
Submit: BeginDeferred → materialize/apply/link/destroy → EndDeferred → Clear
```

### 去幻觉

- 没有编译 步骤。录制时直接去重 + 提交时直接回放。
- Arena slab 从 `ArrayPool<byte>.Shared.Rent`，Clear 时归还，稳态零 GC。
- OverflowPool 没有 per-entry free，只做 append + wholesale clear。

---

## 模块 9：FrameDelta

### 一句话

命令的 IR 表示（9 个 typed 列表），Merge 用 per-entity 状态机折叠两个 delta 为一个。

### 核心循环

```
Merge(a, b):
  per-entity squash: fold a's commands, fold b's commands (状态机)
  状态机: Add→Set=Add(new), Add→Remove=取消, Set→Set=Set(new),
          Set→Remove=Remove, Remove→Add=Set, Remove→Set=Set
  Create+Destroy=Reserve+Release
```

### 去幻觉

- `DeepCopyOwnedData()` 是 O(totalBytes) 深拷贝，这是让 delta 独立于 buffer 的代价。
- Link+Unlink 不互消——不知道原始 link 状态。

---

## 模块 10：World（编排者）

### 一句话

World 拥有身份表、archetype 字典、query 缓存、hierarchy；所有变更经过 World，它负责迁移、位置更新和 query 失效。

### 核心循环

```
结构变更:
  entity → _locations[id] → (archetype, chunk, row)
  算目标签名 → edge cache → dest archetype
  source chunk → memcpy 共有列 → dest chunk
  source swap-remove → 修 moved entity 的 _locations
  _locations[entity.Id] = new location
  archetype._generation++  // per-archetype query 失效

Deferred Suppression:
  Begin: _suppressionCount++
  End: if _suppressionCount==0 && _dirty: AdvanceQueryGeneration()
  注意: per-archetype generation 实施后，deferred suppression 对 Query 已无实际效果。
  archetype._generation 在 ReserveEntity/RemoveEntity 中直接 ++，不受 suppression 控制。
  AdvanceQueryGeneration() 只增加 World._queryGeneration（Query 不再读取它）。
  遗留代码，可清理。
```

---

## 发现的真实问题

### P1. Query 失效粒度太粗 ✅ 已实现

~~当前任何结构变更 → `_queryGeneration++` → 所有 Query 快照失效。~~

**已实施方案**：per-archetype generation。每个 Archetype 维护自己的 `long _generation`，Query 记录 `long[] _snapshotGenerations`。只有被修改的 archetype 对应的 Query 条目需要刷新。详见 `kb-query-invalidation.md`。

**遗留**：`World._queryGeneration` 和 deferred suppression (`Begin/EndDeferredLayoutUpdates`) 不再被 Query 读取，可清理。

### P2. Edge Cache 稀疏数组膨胀

`_addEdges: Archetype?[]` 按 componentId 直接索引，数组长度 = 遇到的最大 componentId + 1。
大多数 archetype 只和 2~6 种组件有迁移关系（N 组件 archetype 最多 N add + N remove edge）。
当组件 ID 稀疏分布时（如 `[Position=0, EnemyTag=150]`），数组 151 slot 中只有 2 个非 null。

**实际影响评估**：
- 组件 ID 顺序分配（0, 1, 2, ...）。如果 archetype 用的组件 ID 连续（如 0-5），数组恰好 6 slot，零浪费。
- 只有高 ID 组件跨区引用时才膨胀（如 ID=150 的 tag 组件出现在简单 archetype 里）。
- 100 个 archetype × 平均 10 有效 edge × 8B/ptr = 8KB 有效数据；若全按 maxId=199 分配 = 320KB 浪费。
- 1000 个 archetype → 3.2MB 浪费。但当前 Hero 场景组件种类 < 20，问题未显现。

**候选方案**：
1. **Compact sorted array + binary search**（推荐）：存 `(componentId, Archetype)` 对，按 id 排序。
   edge 数通常 2~10，binary search = 2~4 次比较，与 L1 cache 的 direct-index 性能几乎相同。
   内存精确 = edge_count × 16B，零浪费。插入排序（仅 archetype 创建时，冷路径）。
2. 小 open-addressing hash：capacity = next_pow2(2 * edges)。略复杂，收益不明显。
3. 阈值混合：maxId < 32 用稀疏数组，否则切 compact array。增加分支但两全。

**风险**：低。Edge cache 只在结构变更（Add/Remove component）时访问，非热路径。

### P3. Add/Set 语义合并的隐患

`Add<T>` 和 `Set<T>` 调用同一个 `ApplyTypedAddOrSet`。组件不存在时 Set 会静默添加，违反最小惊讶原则。用户以为在更新值，实际在改变结构，bug 难以发现。

**候选方案**：公开 `Set` 应先检查组件是否存在，不存在时 fail-fast。合并语义保留为内部 API（CommandBuffer 需要），公开为 `AddOrSet`。

### P4. Hierarchy 作为 side-table 的表达力缺失

无法写 `With<ChildOf>()` 或 `Without<Parent>()` 这样的查询。"找所有根实体"需要遍历整个 hierarchy table 而不是走 archetype 过滤。

**候选方案**：把 Parent 做成组件，Link/Unlink = Add/Remove，级联销毁通过 CleanupTag + 系统实现。hierarchy 自然融入查询体系。代价是级联销毁变慢但表达力大幅提升。与当前 YAGNI 哲学冲突——如果不需要查询 hierarchy 关系，side-table 更简单。

### P5. FrameDelta 深拷贝成本

`Snapshot()` 每次 O(totalBytes) 深拷贝。大帧（数千实体×多组件）下是帧同步场景瓶颈。`Merge` 又要再做一次。

**候选方案**：引用计数 slab，多个 delta 共享同一份数据，copy-on-write 时才复制。或按列存储组件数据，merge 时按列 memcpy。

---

## 可优化点

### O1. Query entity 枚举的跨 chunk 开销

`QueryEnumerator.MoveNext()` 每次跨 chunk 要：`_chunks[chunkIdx]` → `GetEntityStorage()` → 重置 rowIdx。对于 entity-only query 可以 refresh 时把所有匹配 entity 预收集到连续 `Entity[]`，枚举变成 `entities[index++]`。

### O2. Signature 不可变性导致频繁分配

每次 `Signature.Add/Remove` 都分配新的 `ComponentType[]`。高频 Add/Remove 场景产生大量短命对象。候选方案：对 ≤4 组件用 stack-allocated span 做中间计算，或用 Signature interning。

### O3. Chunk swap-remove 的全列拷贝

swap-remove 时对每个列做 `Unsafe.CopyBlock`。10 个列 = 10 次 copy。对大组件（如 256B 的 Matrix4x4）单次 copy 成本高。候选方案：lazy swap（标记 dirty row，下次读取时交换），但增加复杂度，可能不值得。

### O4. CommandBuffer entity reservation 时机

`Create()` 立即从 World 预留真实 entity id。录制了但没 Submit 则 id 被浪费。候选方案：两阶段 reservation（临时 placeholder → Submit 时批量替换），但令录制期间 entity 间引用变复杂。当前方案更简单，未 submit 的 buffer 本身就是 bug。

---

## 根本性的设计张力

### T1. 全局 ComponentRegistry vs World 隔离

全局 registry 让跨 world replay 简单，但 world 无法独立演化。注册过的类型无法注销，registry 只增不减。这是"简单性 vs 灵活性"的取舍，对游戏场景选择简单性是对的。

### T2. 单线程写入 vs 并行读取

当前模型：world 写入时不能读。这是 archetype ECS 的经典约束。突破它需要 double-buffered world state 或 per-chunk lock，复杂度会爆炸。当前选择务实。

### T3. Hierarchy 一等公民 vs 组件

side-table 享受不到 archetype 查询优化。但做成组件引入"组件存 Entity 引用"的悬空问题。side-table 通过 `_parentByChild` 的 version 检查自然处理悬空引用。这是"正确性 > 表达力"的合理选择。

---

## 做得好的地方

- flat byte chunk：C# struct 组件天然 blittable，byte storage 消除泛型约束和类型检查开销
- generation-based per-archetype query invalidation：O(N matched) generation 比较决定是否重建，只刷新变更的 archetype
- InlineMap 4-slot + overflow：精准命中"大多数实体每帧 < 4 个组件变更"的实际分布
- deferred layout suppression：N 次 query 失效合并为 1 次
- edge cache 双向缓存：Add 和 Remove 互为逆操作
- 全模块热路径 `[SkipLocalsInit]` + `AggressiveInlining` + `Unsafe.As<byte,T>` 消除 bounds check

## 如果只做一件事

~~最值得做的是 **per-archetype query invalidation**。当前粗粒度失效在结构变更密集场景下是最大的性能瓶颈来源，改进方案清晰、风险可控。~~ ✅ 已实现

## 决策

- 本文档作为架构审视的长期记录，与 `kb-core-ecs.md` 互补：后者记录"怎么实现"，本文档记录"怎么理解"和"哪里可以更好"
- 问题描述不含具体修复方案代码，只记录方向和候选方案
- 发现的优先级排序：P3 > P2 > P5 > P4（P1 已实现，按影响面 × 修复成本排序）

## 认知模型

- 理解 miniArch 时，应该把它看成：一条从 entity id 到密集类型化存储的映射链 + 一个延迟命令录制器
- 最核心的三个原语：位置表、签名分组、搬家过程
- 常见误解：以为 Query 遍历 entity（实际遍历 archetype→chunk→row）；以为 Add/Set 是两条路径（实际合并为一条）；以为 reserved entity 等于 live entity

