---
title: Cache & Memory Optimization Review
module: MiniArch.Core
description: Memory layout and cache behavior analysis of the ECS runtime, with optimization opportunities
updated: 2026-06-08 (Edge cache 改为 bounded 4-slot LRU、补充 P12: Edge Cache bounded 优化、public API AggressiveInlining)
---
# Cache & Memory Optimization Review

## 结论

MiniArch 的迭代热路径已经高度优化（pointer-bump、SoA、自适应 chunk 容量、无 bounds check），迭代本身几乎没有 cache 浪费。可优化空间集中在三个方向：**结构变更/批量创建少做小操作**、**实体随机访问减少 record cache miss**、**query 只在 chunk membership 变化时刷新快照**。

当前实验优先级：
1. CommandBuffer 按 archetype 批量 materialize（最可能收益）
2. Query 快照失效拆分
3. `EntityRecord` 压缩为无引用 metadata
4. NativeMemory + 64B alignment（高天花板，但单独替换 `byte[]` 不保证收益）
5. MigrationPlan 小优化
6. Empty chunk active list（条件执行）

完整实验计划见 `docs/plans/2026-06-06-world-data-structure-optimization-experiments.md`。

## 架构

### 内存布局总览

```
World (partial files)
├── _records: EntityRecord[]   // 16B/entity: (Archetype ref, RowIndex, Version)
├── _archetypes: Dictionary<Signature, Archetype>
├── _freeIds: RecycledEntity[]
└── _archetypeVersion: int     // 全局版本号

Archetype (partial files: Archetype.cs + Archetype.Storage.cs)
├── _data: byte[]              // SoA packed, all columns in ONE array
├── _entities: Entity[]        // 8B/row, parallel to data
├── _columnByteOffsets: int[]  // per-column byte offset into _data
├── _elementSizes: int[]       // per-column element size
├── _componentIdToColumnIndex: int[]  // component id → column index
├── _addEdgeCacheSlots: (ComponentType, Archetype)[]  // bounded 4-slot LRU
└── _removeEdgeCacheSlots: (ComponentType, Archetype)[]  // bounded 4-slot LRU
          ↓
Chunk (internal readonly struct view) / ChunkView (public readonly struct view)
```

### 热路径分析

| 路径 | 每帧频率 | Cache 行为 |
|------|----------|------------|
| SpanEach.MoveNext (per row) | 数百万次 | ✅ Pointer bump，纯顺序 |
| SpanEach chunk transition | 数千次 | ✅ 3-5 次数组查找，可忽略 |
| Entity Get/Set/Has | 取决于游戏逻辑 | ⚠️ _records[id] → 随机访问 |
| Entity Create/Destroy | 取决于游戏逻辑 | ⚠️ 2 数组 × 2-4 次随机访问 |
| Add/Remove component | 少见 | ⚠️ Archetype.CopySharedComponentsFrom |
| Query.RefreshSnapshot | 每查询每帧 1 次 | ✅ 生成号比较 + 数组拷贝 |

## 决策

### 为什么整体评价是"已高度优化"

1. **迭代热路径几乎完美**：per-row = `Unsafe.Add(ref, 1)` × N，没有间接寻址
2. **自适应 chunk 容量（16KB）** 正好瞄准 L1 cache，一次迭代不换 cache
3. **ComponentMask**（256-bit bitmask, 4× `ulong`）用 bitmask 做 signature 匹配，O(1) 无分支
4. **CopySmall 特化路径**（1/2/4/8/12/16 字节）覆盖了常见组件大小
5. **`SkipLocalsInit`** + **`MemoryMarshal.GetArrayDataReference`** 消除了运行时开销

### 实际可优化的点

## P0: World._versions 和 _locations 分离 ✅ 已修复 (2026-06-03)

**已实施**：合并为 `EntityRecord[] _records`：
```csharp
struct EntityRecord {
    Archetype? Archetype;  // 8 bytes (null = unoccupied)
    int RowIndex;          // 4 bytes
    int Version;           // 4 bytes
}  // 16 bytes — 自然 8 字节对齐
```

- 影响文件：World partial 文件族、`WorldSnapshot.cs`、`WorldClone.cs`、`EntityRecord.cs`

## P1: Chunk._data 列对齐不够

**当前状态**：热路径不用 SIMD，所以目前不是问题。

**判断**：**现在不做，但记住**。等有 SIMD 需求时再改，改动极小（一行）。

## P2: SpanEach.Current 每行分配

**当前状态**：C# struct 返回是值拷贝，编译器通常优化为寄存器传递。

**判断**：**测量后决定**。

## P3: Archetype.CopySharedComponentsFrom 的 copy 散布

**判断**：**方案 A 值得做**（排序开销极小，copy 时 cache 更友好）。方案 B 等 profiling 数据。

## P4: Edge Cache 改为 bounded 4-slot LRU ✅ 已修复 (2026-06-08)

**问题**：原来的 `_addDestinationCache: Archetype?[]` 按 component ID 直索引，如果 component ID 分配不连续（有空洞），浪费内存。

**Fix**：改为 bounded 4-slot LRU cache（`_addEdgeCacheSlots` / `_removeEdgeCacheSlots`）。4 slot 足够覆盖当前 Hero 场景（< 20 组件），LRU 淘汰最近最少使用的 edge。

**状态**：已实施。消除了稀疏数组膨胀问题。

## P5: CopySharedComponentsFrom O(n*m) 查找

**判断**：**当前已足够优**，`GetComponentIndexFast` 是 O(1) direct map 查找。

## P6: Archetype.CopySmall 2-byte fast path ✅ 已修复 (2026-06-07)

**Fix**：增加 `case 2: Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<short>(ref src));`

## P7: CommandBuffer.ExtractAndSortComponents 在 Submit 时排序

**判断**：**低优先级**。实测显示 Array.Sort 仅占 Submit 的 10%。

## P9: CommandBuffer.BuildCreatedEntityComponents 每 entity 重复 ArrayPool rent/return ✅ 已修复 (2026-06-05)

**已实施**：替换为 `[ThreadStatic]` 预分配 buffer 复用。

## P10: CommandBuffer.GetOrCreateArchetype 重复 Dictionary 查找 ✅ 已修复 (2026-06-05)

**已实施**：last-value cache（按 componentCount + typeHash 缓存）。

## P8: HierarchyTable.EnsureCapacity 用 Array.Fill 初始化

**判断**：**微小优化**，优先级低。

## P11: 公共 API 薄转发方法 AggressiveInlining ✅ 已修复

**问题**：`ChunkView.GetSpan<T>()`、`ChunkView.GetEntities()`、`Query.GetChunks()` 等公共 API 改为薄转发后遗漏了 `AggressiveInlining`。JIT 不内联时纯迭代场景退化 41%。

**Fix**：所有 public 薄转发方法加 `[MethodImpl(AggressiveInlining)]`。

## P12: Edge Cache 从直索引改为 bounded LRU ✅ 已修复 (2026-06-08)

同 P4，已实施。

## 认知模型

### 理解方式

把 MiniArch 的 cache 模型理解为 **"chunk 是 cache 友好的孤岛，entity lookup 是桥"**：
- chunk 内部：纯顺序访问，完美利用 prefetcher
- 跨 chunk：通过 archetype 的 chunk 数组顺序跳转，可预测
- 跨 entity：`_records[id]` 是完全随机的，这是唯一不能被 prefetcher 帮助的地方

### 最重要的一条数据

**DefaultChunkCapacity = 128** 决定 Archetype 初始容量（行数），间接决定了迭代切换 chunk 的频率。

## 坑点

- **EntityRecord 布局已优化为 16 字节**：`(Archetype ref, RowIndex, Version)` 无 padding
- **Edge cache 已改为 bounded 4-slot LRU**：不再使用 `Archetype?[]` 直索引，miss 时需要重新计算目标 archetype
- **公共 API 薄转发方法必须加 `[MethodImpl(AggressiveInlining)]`**：遗漏会导致迭代退化
- **Archetype 的 `CreateStorage` 列对齐**：对齐从 8→64 会使每列浪费更多 padding

## 入口

- **看迭代热路径**：`SpanQueryIterators.cs` 的 `MoveNext()` 方法
- **看实体随机访问**：`World.cs` 的 `TryGetLocation()`、`World.StructuralChange.cs` 的 `FinishMoveEntity()`
- **看 storage 布局**：`Archetype.Storage.cs` 的 `EnsureCapacity()`、`GetComponentRefAt<T>()`
- **看迁移拷贝**：`Archetype.Storage.cs` 的 `CopySharedComponentsFrom()`、`CopySmall()`
- **看 edge cache**：`Archetype.cs` 的 `_addEdgeCacheSlots` / `_removeEdgeCacheSlots`
