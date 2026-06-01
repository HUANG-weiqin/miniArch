---
title: Architecture Mechanistic Review
module: MiniArch.Core
description: Mechanistic insight of the entire miniArch ECS library — one-line truths, minimal loops, state models, data flows, known issues, and optimization opportunities
updated: 2026-06-01
---
# Architecture Mechanistic Review

## 这个模块是干什么的

- 整体架构审视记录：10 个核心子系统的机械化拆解、真实问题、可优化点、设计张力
- 不替代各 `kb-*.md` 页的详细实现记录

## 全局一句话

miniArch = 一张 entity→location 表 + 按组件集合分组的密集 byte 存储 + 一套延迟命令录制器。所有功能都是这三个原语的组合。

## 全局依赖图

```
ComponentRegistry.Shared (全局 Type↔id)
  ↓
ComponentType (int wrapper) → Signature (排序 ComponentType[] + bitmask)
  ↓
Archetype (签名→chunk 列表 + edge cache)
  ↓           ↓
Chunk (byte[])   Query (archetype 快照 + per-archetype generation)
  ↓
World (编排一切)
  ↓
CommandBuffer → FrameDelta → World.Replay
HierarchyTable (side-table)
WorldSnapshot / WorldClone
```

## 10 个核心子系统

### 1. 实体身份 (Entity / Version / FreeList)
- Entity = `(id, version)` 二元组；World 维护 `_versions[]` 和 `_locations[]` 并行数组 + free-list
- 分配：free-list pop 或 slotCount++；销毁：version++ + 清空 location + free-list push

### 2. 存储 (Chunk)
- `byte[]` 按列排布所有组件数据，swap-remove 删除行
- 读取/写入：`Unsafe.As<byte, T>(&data[columnOffset + row * elementSize])`
- `_componentIdToColumnIndex[]`：component id → 列索引 direct map

### 3. Archetype
- 同一 Signature 的 Chunk 列表 + non-full chunk LIFO 栈
- `ReserveEntity`：从栈顶取可写 chunk 或新建 chunk

### 4. Signature & Edge Cache
- Signature：排序 `ComponentType[]` + `long` bitmask（id < 64 时快速拒绝）
- Edge Cache：`Archetype?[]` 按 componentId 直索引（双向缓存，Add 和 Remove 互为逆操作）

### 5. 组件类型系统
- `ComponentType` = int；`ComponentRegistry.Shared` 全局 copy-on-write
- `Component<T>.ComponentType` 静态字段消除热路径查找

### 6. Query 系统
- QueryDescription（`Type` 集合）→ QueryFilter（`ComponentType` 集合）→ Query（快照匹配的 archetype/chunk 列表）
- 失效：per-archetype `Generation`（long），只刷新变更的 archetype
- `OrderedQuery` 是消费层 materialization + sort

### 7. Hierarchy
- 邻接链表：`_parentByChild[id]` + `_firstChild[id]` + 链表 slot 管理子节点
- Destroy 子树：DFS 后序遍历 → 逐个 DestroySingle

### 8. CommandBuffer
- InlineMap(4 内联 + 链表 overflow) 按 entity×componentType 去重录制
- Arena slab 存组件数据（`ArrayPool<byte>.Shared.Rent`），Submit 直接回放

### 9. FrameDelta
- 命令 IR（9 个 typed 列表），Merge 用 per-entity 状态机折叠两个 delta 为一个
- `DeepCopyOwnedData()` 是 O(totalBytes) 深拷贝，独立于 buffer

### 10. World（编排者）
- 拥有身份表、archetype 字典、query 缓存、hierarchy
- 结构变更：查 location → 算目标签名 → edge cache → 迁移 → 修 location → archetype generation++

## 已知问题

### P1. Query 失效粒度太粗 ✅ 已修复
- 已实现 per-archetype generation；`World._queryGeneration` 和 deferred suppression (`BeginDeferredLayoutUpdates`/`EndDeferredLayoutUpdates`) 已移除

### P2. Edge Cache 稀疏数组膨胀
- `_addEdges: Archetype?[]` 可能因组件 ID 稀疏分布而膨胀（当前 Hero 场景 < 20 组件，问题未显现）
- 候补方案：compact sorted array + binary search（推荐），或阈值混合

### P3. Add/Set 语义合并的隐患
- `Add<T>` 和 `Set<T>` 调用同一个 `ApplyTypedAddOrSet`；Set 在组件不存在时静默添加
- 候补方案：公开 Set 应 fail-fast，合并语义保留为内部 `AddOrSet`

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

## 做得好的地方

- flat byte chunk + generation-based per-archetype query invalidation + InlineMap 4-slot + overflow + edge cache 双向缓存 + `[SkipLocalsInit]`/`AggressiveInlining`/`Unsafe.As<byte,T>` 消除 bounds check
