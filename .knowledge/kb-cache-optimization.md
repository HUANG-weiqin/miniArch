---
title: Cache & Memory Optimization Review
module: MiniArch.Core
description: Memory layout and cache behavior analysis of the ECS runtime, with optimization opportunities
updated: 2026-06-22 (P16: SubmitAndSnapshotAsync 双缓冲消除 Dictionary/HashSet 分配)
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

已完成的优化（P0-P16）涵盖：EntityRecord 合并、edge cache 改 bounded LRU、CopySmall 2-byte fast path、ThreadStatic buffer 复用、公共 API AggressiveInlining、Entity IComparable 去重、**本地 archetype 缓存删除**、**SubmitAndSnapshotAsync Dictionary/HashSet 双缓冲复用**。

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
├── _addDestinationCache: Archetype?[]      // 直索引，component id → 目标 archetype
└── _removeDestinationCache: Archetype?[]   // 直索引，component id → 目标 archetype
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
3. **ComponentMask**（512-bit bitmask, 8× `ulong`）用 bitmask 做 signature 匹配，O(1) 无分支
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

## P4: Edge Cache 直索引 ✅ 已修复 (2026-06-08)

**当前状态**：`_addDestinationCache: Archetype?[]` / `_removeDestinationCache: Archetype?[]` 按 component ID 直索引。O(1) 查找，简单可靠。component ID 稀疏时数组可能膨胀，但当前场景组件 ID 紧凑，不是问题。

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

## P12: Edge Cache 直索引 ✅ 已修复 (2026-06-08)

同 P4，当前使用 `Archetype?[]` 按 componentId 直索引。

## P13: CommandBuffer 去重排序零分配 ✅ 已修复 (2026-06-09)

**问题**：CommandBuffer 的 `DeduplicateExistingDestroyEntities` 使用 `Array.Sort<Entity>(entities, count, EntityDestroyComparer.Instance)`，自定义 `IComparer<T>` 导致 .NET 内部每调用分配 ~64 bytes delegate 闭包。每 tick 调用一次，10 秒积累 ~955 KB。

**根因**：`Entity` 没有实现 `IComparable<Entity>`，必须用自定义 comparer。

**Fix**：给 `Entity` 加 `IComparable<Entity>`，改为 `Array.Sort(entities, 0, count)` 使用默认比较器（零分配）。删除 `EntityDestroyComparer` 和 `CompareEntity`。

**影响文件**：`Entity.cs`、`CommandBuffer.cs`

## P14: CommandStream 本地 archetype cache 16 槽 ✅ 已修复 (2026-06-09)

**问题**：`ResolveArchetypeForSpan` 只有 4 槽本地缓存。HeroComing 场景每 tick 两个 Submit 创建不同实体类型，原型种类超过 4 种。缓存未命中时走 `new ComponentType[n]`（~24 bytes），每 Submit 一次，累积 ~24 KB / 100 rounds。

**Fix**：`ArchetypeCacheSize` 4 → 16。16 槽足够容纳所有稳态原型组合（实际 <10 种），cache miss 归零。

**影响文件**：`CommandStream.cs`

## P15: 删除本地 archetype 缓存，改用 World.TryGetArchetype ✅ 已修复 (2026-06-10)

**问题**：P14 扩大缓存只是补丁——CommandBuffer 和 CommandStream 各自维护本地 LRU 缓存，镜像了 World 中已经存在的 archetype 状态。缓存 miss 时仍然走 `new ComponentType[n]` 分配。

**Fix**：
- `World.TryGetArchetype(ReadOnlySpan<ComponentType>)`：零分配线性扫描，用 `Signature.Contains` 做顺序无关的集合比较。archetype 总数始终很小（< 50），扫描成本可忽略。
- `CommandStream.ResolveArchetypeForSpan`：`stackalloc` 类型到栈上，调用 `TryGetArchetype`，消除 `new ComponentType[]` 分配。
- `CommandBuffer.BuildCreatedEntityComponents`：复用 ThreadStatic buffer，调用 `TryGetArchetype`。
- **删除**：多槽缓存字段、`ArchetypeCacheEntry`、`ComputeComponentHash`、`LookupArchetypeCache`、`InsertArchetypeCache`、`EnsureArchetypeCacheValid`、`World.CreateArchetypeCacheGeneration` 属性。

**影响文件**：`World.cs`, `CommandStream.cs`, `CommandBuffer.cs`（净 -107 行）

**注意**：不要对 archetype 快照排序——Query 的 `AppendNewArchetypes` 依赖追加式语义。

## 认知模型

### 理解方式

把 MiniArch 的 cache 模型理解为 **"chunk 是 cache 友好的孤岛，entity lookup 是桥"**：
- chunk 内部：纯顺序访问，完美利用 prefetcher
- 跨 chunk：通过 archetype 的 chunk 数组顺序跳转，可预测
- 跨 entity：`_records[id]` 是完全随机的，这是唯一不能被 prefetcher 帮助的地方

### 最重要的一条数据

**DefaultChunkCapacity = 128** 决定 Archetype 初始容量（行数），间接决定了迭代切换 chunk 的频率。

## P16: SubmitAndSnapshotAsync 双缓冲消除 Dictionary/HashSet 分配 ✅ 已修复 (2026-06-22)

**问题**：`SwapOutState()` 每帧 `new Dictionary<Entity, HierarchyIntent>()`（~360 bytes）和 `_unavailableEntities = null`（下次 Destroy 重新 `new HashSet`，~200 bytes）。稳态下每帧 ~560 bytes 堆分配。对比之下，`Submit()` 同步路径已用 `.Clear()` 复用，稳态零 GC。

**根因**：`SwapOutState` 需要把当前录制状态"冻结"交给后台线程 build delta，同时给主线程换上全新的录制状态。原实现直接 `new` 全部容器。

**Fix**：双缓冲——后台 Task 完成后，其读过的 Dictionary/HashSet 回收到 spare 字段，下一次 `SwapOutState` 优先用 spare（`.Clear()` 复用内部数组），仅在首次或后台未完成时 fallback 到 `new`。
- `TryReclaimPending()`：检查 `_pendingTask.IsCompleted`，若完成则把 `_pendingFrozen` 的 Dictionary/HashSet 存入 `_spareHierarchy` / `_spareUnavailable`
- `SwapOutState()`：开头调 `TryReclaimPending()`，然后用 `_spareHierarchy ?? new Dictionary()` + `.Clear()` 替代 `new`
- `SubmitAndSnapshotAsync()`：记录 `_pendingFrozen` / `_pendingTask` 供下帧回收
- 线程安全：`Task.IsCompleted` 有内存屏障，true 时后台线程已停止读取 frozen 状态

**未消除的分配**（后续可优化）：`new FrozenState` 对象、`new ComponentStore?[N]` 数组、`Task.Run` 闭包 + Task 对象。这些合计 ~300-900 bytes/call（取决于 ComponentTypeCount）。

**影响文件**：`CommandStream.cs`，`CommandStreamTests.cs`（+1 测试：引用相等验证复用）

**回归门禁**：Movement-Stream 1766 rounds/s，Attack-Stream 1045 rounds/s，均远超阈值，内存稳定。

## 坑点

- **`World._archetypeVersion` 已更名为 `_createArchetypeCacheGeneration`**：用于 CreateArchetypeCache 的 generation 失效
- **Edge cache 使用直索引 `Archetype?[]`**：按 componentId 直索引，O(1) 查找。component ID 稀疏时数组可能膨胀
- **公共 API 薄转发方法必须加 `[MethodImpl(AggressiveInlining)]`**：遗漏会导致迭代退化
- **Archetype 的 `CreateStorage` 列对齐**：对齐从 8→64 会使每列浪费更多 padding

## 入口

- **看迭代热路径**：`SpanQueryIterators.cs` 的 `MoveNext()` 方法
- **看实体随机访问**：`World.cs` 的 `TryGetLocation()`、`World.StructuralChange.cs` 的 `FinishMoveEntity()`
- **看 storage 布局**：`Archetype.Storage.cs` 的 `EnsureCapacity()`、`GetComponentRefAt<T>()`
- **看迁移拷贝**：`Archetype.Storage.cs` 的 `CopySharedComponentsFrom()`、`CopySmall()`
- **看 edge cache**：`Archetype.cs` 的 `_addDestinationCache` / `_removeDestinationCache`
