---
title: Architecture Mechanistic Review
module: MiniArch.Core
description: Mechanistic insight of the entire miniArch ECS library — one-line truths, minimal loops, state models, data flows, known issues, and optimization opportunities
updated: 2026-06-13 (ComponentMask 256→512-bit, edge cache 改回直索引, 修正已删除文件声明, 分段存储模式)
---
# Architecture Mechanistic Review

## 这个模块是干什么的

- 整体架构审视记录：核心子系统的机械化拆解、真实问题、可优化点、设计张力
- 不替代各 `kb-*.md` 页的详细实现记录

## 全局一句话

miniArch = 一张 entity→location 表 + 按组件集合分组的**可增长**密集 byte 存储 + 一套延迟命令录制器。所有功能都是这三个原语的组合。

## 全局依赖图

```
ComponentRegistry.Shared (全局 Type↔id)
  ↓
ComponentType (int wrapper) → Signature (排序 ComponentType[] + ComponentMask 512-bit)
  ↓
Archetype (byte[] 存储 + Entity[] + edge cache 内联) → Chunk (internal readonly struct 视图)
  ↓                                                    → ChunkView (public readonly struct 视图)
Query (archetype 快照 + 全局 _snapshotVersion)
  ↓
World (拆分为 partial 文件，编排一切)
  ↓
CommandBuffer → FrameDelta → World.Replay
HierarchyTable (side-table)
WorldSnapshot / WorldClone
```

## 核心子系统

### 1. 实体身份 (Entity / Version / FreeList)
- Entity = `(id, version)` 二元组；World 维护 `_records[]` 单表 + free-list
- 分配：free-list pop 或 slotCount++；销毁：record.Version++ + 清空 Archetype/RowIndex + free-list push
- 代码位置：`World.EntityLifecycle.cs`

### 2. 存储 (Archetype)
- `byte[]` 按列排布所有组件数据，swap-remove 删除行
- 读取/写入：`Unsafe.As<byte, T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), columnOffset + row * elementSize))`
- `_componentIdToColumnIndex[]`：component id → 列索引 direct map
- 每个 Archetype 有且仅有一个 Chunk（internal readonly struct 视图）和一个 ChunkView（public readonly struct 视图），无 multi-chunk
- 代码位置：`Archetype.cs`（字段/构造/metadata）+ `Archetype.Storage.cs`（存储操作）

### 3. Archetype
- 存储持有者：`_data: byte[]`、`_entities: Entity[]`、`_columnByteOffsets`、`_elementSizes`、`_count`、`_capacity`
- `_addDestinationCache` / `_removeDestinationCache`：`Archetype?[]` 内联直索引 edge cache
- `CopySharedComponentsFrom`：遍历 destination signature，只复制 source 也有的组件
- Edge cache 最近改为 bounded 4-slot（`_addEdgeCacheSlots`/`_removeEdgeCacheSlots`），LRU 淘汰

### 4. Signature & ComponentMask
- Signature：排序 `ComponentType[]` + `ComponentMask`（512-bit，8 × `ulong`，id < 512 时快速匹配）
- Edge Cache：`Archetype?[]` 按 componentId 直索引（内联在 Archetype 上）

### 5. 组件类型系统
- `ComponentType` = int；`ComponentRegistry.Shared` 全局 copy-on-write
- `Component<T>.ComponentType` 静态字段消除热路径查找

### 6. Query 系统
- QueryDescription（`Type` 集合）→ QueryFilter（`ComponentType` 集合）→ Query（快照匹配的 archetype/chunk 列表）
- 失效：`World.ArchetypeCount` 与 `Query._lastArchetypeCount` 比较，数量不匹配时全量重建快照
- 快照重建：遍历 `world.Archetypes`，用 512-bit ComponentMask 做 required/excluded/any 匹配
- `OrderedQuery` 是消费层 materialization + sort
- `ChunkView` 提供每个 chunk/segment 的 typed 列访问（`GetSpan<T>()`）
- 代码位置：`World.QueryCache.cs`（缓存管理）

### 7. Hierarchy
- 邻接链表：`_parentByChild[id]` + `_firstChild[id]` + 链表 slot 管理子节点
- Destroy 子树：DFS 后序遍历 → 逐个 DestroySingle

### 8. CommandBuffer
- InlineMap(4 内联 + 链表 overflow) 按 entity×componentType 去重录制
- Arena slab 存组件数据（`ArrayPool<byte>.Shared.Rent`），Submit 直接回放
- `[ThreadStatic]` 预分配 buffer 复用（替代 per-entity ArrayPool rent/return）
- bounded archetype cache（componentCount + typeHash 缓存，跳过重复 Dictionary 查找）
- 新增 `Clone()` 方法：深拷贝整个 CommandBuffer，用于 snapshot/replay 场景
- CreatedComponent struct 缩小：从 16B → 12B（移除冗余字段）

### 9. FrameDelta
- 命令 IR（typed 列表），Merge 用 per-entity 状态机折叠两个 delta 为一个
- FrameDelta struct 已缩小：移除冗余字段（如 `_recorder`）

### 10. World（编排者）
- **拆分为 5 个 partial 文件**：World.cs（字段+读写+hierarchy+clone+replay）、World.EntityLifecycle.cs（Create/Destroy）、World.Create.Generated.cs（泛型重载+GetFirst）、World.QueryCache.cs（Query 缓存）、World.StructuralChange.cs（Add/Set/Remove）
- 拥有身份表（`EntityRecord[]`）、archetype 字典、query 缓存、hierarchy
- 结构变更：查 `EntityRecord` → 算目标签名 → edge cache → `CopySharedComponentsFrom` → 修 `EntityRecord` → `_archetypeVersion++`
- 新增 `GetFirst<T>()`：O(1) 按 component type 查找首个 entity（复用 `CreateArchetypeCache<T>` 泛型静态缓存）

### 已删除的子系统
- **DebugMetrics**：整个子系统已删除（`DebugMetrics.cs`、`WorldDebugMetrics`、`CommandBufferDebugMetrics`、对应测试 `DebugMetricsTests.cs`）

## 已知问题

### P1. Query 失效全局版本号
- 当前使用 `_lastArchetypeCount` 与 `World.ArchetypeCount` 比较：任何新 Archetype 创建都会使所有查询全量重建
- 这是"够用的简单方案"：由于每个 Archetype 只有 1 个 Chunk（单块模式）或 N 个 Segment（分段模式），重建代价低（仅遍历 archetype 数组算 bitmask match）
- 如果将来 archetype 数量极大（>1000），可考虑改为按 query filter 分组失效或 per-archetype 版本

### P2. Edge Cache 直索引
- `_addDestinationCache` / `_removeDestinationCache` 使用 `Archetype?[]` 按 componentId 直索引
- 优点：O(1) 查找，无 LRU 开销
- 缺点：component ID 稀疏时数组膨胀（如 ID=10000 但只有 2 个组件）

### P3. Add/Set 语义合并的隐患
- 当前实现中 `Set` 在组件不存在时会静默添加（`Set` 已与 `Add` 合并路径）
- 候补方案：公开 Set 应 fail-fast，合并语义保留为内部方法

### P4. Hierarchy 作为 side-table 的表达力缺失
- 无法写 `With<ChildOf>()` 这样的查询
- 候补方案：把 Parent 做成组件——代价是级联销毁变慢但表达力提升

### P5. FrameDelta 深拷贝成本
- `Snapshot()` 每次 O(totalBytes) 深拷贝，大帧下是瓶颈
- 候补方案：引用计数 slab + copy-on-write

## 可优化点

- O1. Query entity 枚举的跨 chunk 开销：refresh 时把所有匹配 entity 预收集到连续 `Entity[]`
- O2. Signature 不可变性导致频繁分配：≤4 组件用 stackalloc 或 interning
- O3. Chunk swap-remove 的全列拷贝：对大组件（如 256B Matrix4x4）单次 copy 成本高
- O4. CommandBuffer entity reservation 时机：`Create()` 立即预留——未 submit 则 id 被浪费

## 设计张力

| 张力 | 选择 | 原因 |
|---|---|---|
| 全局 Registry vs World 隔离 | 全局简单性 | 游戏场景正确 |
| 单线程写入 vs 并行读取 | 单线程写 | archetype ECS 经典约束 |
| Hierarchy 一等公民 vs 组件 | side-table | 正确性 > 表达力 |
| 大文件 vs partial 拆分 | partial 拆分 | 按职责分组，保持编译单元聚焦 |
| 直索引 edge cache vs bounded cache | 直索引 `Archetype?[]` | O(1) 查找，简单可靠 |

## 做得好的地方

- flat byte storage + 单块/分段双模式 + 全局版本号失效 + ComponentMask 512-bit + InlineMap + 直索引 edge cache + `[SkipLocalsInit]`/`AggressiveInlining`/`Unsafe.As<byte,T>` ——详见 `kb-core-ecs.md` 决策和 `kb-chunk-storage.md`
