---
title: Cache & Memory Optimization Review
module: MiniArch.Core
description: Memory layout, cache behavior analysis, applied optimizations, and remaining opportunities for the ECS runtime
updated: 2026-07-09
---
# Cache & Memory Optimization Review

## 结论

MiniArch 的迭代热路径已经高度优化（pointer-bump、SoA、自适应 chunk 容量、无 bounds check），迭代本身几乎没有 cache 浪费。剩余优化空间集中在：**结构变更/批量创建少做小操作**、**实体随机访问减少 record cache miss**、**query 只在 chunk membership 变化时刷新快照**。

> 已删除子系统的优化记录（CommandBuffer 去重排序、TryGetArchetype 线性扫描、ResolveArchetypeForSpan 4 槽缓存等）已移除——相关代码于 2026-06-26 (CommandBuffer) 和 2026-06-29 (TryGetArchetype 死代码) 删除。历史可查 git log。

## 内存布局总览

```
World (7 partial files — 详见 kb-architecture-review.md §10)
├── _records: EntityRecord[]   // 16B/entity: (Archetype ref, RowIndex, Version)
├── _archetypes: Dictionary<Signature, Archetype>
├── _archetypeByMask: Dictionary<ComponentMask, Archetype>  // canonical-only, Replay 零分配路径
├── _freeIds: RecycledEntity[]
└── _createArchetypeCacheGeneration: int     // CreateArchetypeCache generation 失效

Archetype (3 partial files — 详见 kb-core-ecs.md)
├── _data: byte[]              // SoA packed, all columns in ONE array (non-chunked mode)
├── _segments: Segment[]       // chunked mode (promoted when capacity exceeds threshold)
├── _entities: Entity[]        // 8B/row, parallel to data (non-chunked)
├── _columnByteOffsets: int[]  // per-column byte offset into _data
├── _elementSizes: int[]       // per-column element size
├── _componentIdToColumnIndex: int[]  // component id → column index
├── _addDestinationCache: Archetype?[]      // 直索引，component id → 目标 archetype
└── _removeDestinationCache: Archetype?[]   // 直索引，component id → 目标 archetype
           ↓
ChunkView (public readonly struct view, 直接包裹 Archetype 或其 Segment)
```

## 热路径分析

| 路径 | 每帧频率 | Cache 行为 |
|------|----------|------------|
| 迭代 MoveNext (per row) | 数百万次 | ✅ Pointer bump，纯顺序 |
| chunk transition | 数千次 | ✅ 3-5 次数组查找，可忽略 |
| Entity Get/Set/Has | 取决于游戏逻辑 | ⚠️ `_records[id]` → 随机访问 |
| Entity Create/Destroy | 取决于游戏逻辑 | ⚠️ 2 数组 × 2-4 次随机访问 |
| Add/Remove component | 少见 | ⚠️ `Archetype.CopySharedComponentsFrom` |
| Query invalidation check | 每查询每帧 1 次 | ✅ 两段式: archetypeCount int-compare + per-archetype segment count (详见 `kb-query-invalidation.md`) |

## 已应用的优化

### EntityRecord 合并（2026-06-03）

`World._versions` 和 `_locations` 合并为单个 `EntityRecord[] _records`：
```csharp
struct EntityRecord {
    Archetype? Archetype;  // 8 bytes (null = unoccupied)
    int RowIndex;          // 4 bytes
    int Version;           // 4 bytes
}  // 16 bytes — 自然 8 字节对齐
```
减半 entity metadata 的 cache footprint。影响文件：World partial 文件族、`WorldSnapshot.cs`、`WorldClone.cs`、`EntityRecord.cs`。

### Edge Cache 直索引（2026-06-08）

`_addDestinationCache: Archetype?[]` / `_removeDestinationCache: Archetype?[]` 按 component ID 直索引。O(1) 查找，简单可靠。component ID 稀疏时数组可能膨胀，但当前场景组件 ID 紧凑，trade-off 合理。

> **单一事实来源**：edge cache 的完整 trade-off 分析在 `kb-architecture-review.md` §P2（已知问题）。

### CopySmall 2-byte fast path（2026-06-07）

`Archetype.Storage.cs` 的 `CopySmall` 增加 `case 2: Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<short>(ref src));`，覆盖最常见的小组件拷贝。

### 公共 API AggressiveInlining

`ChunkView.GetSpan<T>()`、`ChunkView.GetEntities()`、`Query.GetChunks()` 等公共薄转发方法必须加 `[MethodImpl(AggressiveInlining)]`。遗漏时 JIT 不内联，纯迭代场景退化 41%。

### SubmitAndSnapshotAsync 双缓冲池化（2026-06-22）

`SwapOutState()` 每帧分配 Dictionary/HashSet/数组的问题，通过**整体 FrozenState 双缓冲**解决：
- 后台 Task 完成后，整个 FrozenState（对象 + 所有内部数组 + Dictionary + HashSet）回收到 `_spareFrozen`
- 下一次 `SwapOutState` 直接交换 `_frozen` / `_spareFrozen` 两个 `FrozenState` 对象引用，旧 `_frozen` 作为只读快照交给 submit/build，spare 成为新的 recording state
- 闭包消除：`Task.Run(() => ...)` → 静态委托 `s_buildFromFrozen` + `Task.Factory.StartNew`
- **未消除的分配**（YAGNI 边界）：仅剩 `Task<FrameDelta>`（~80B，TAP 语义必需）和 `new FrameDelta()` 返回值

**重要：FrozenState 边界。** 录制数据应放进 `FrozenState`，这样 async 路径只需交换对象引用；仅录制期/重置期使用、不被后台 worker 读取的标量（如 cache/dirty flags）才留在 `CommandStreamCore` 上，并必须在 `SwapOutState()` / `Clear()` 中重置。

**回归门禁数据**（2026-06-22，见 `kb-commandstream-game-perf.md`）：
- Movement-Stream: 1766 → 1818 rounds/s（+3%）
- Attack-Stream: 1045 → 1101 rounds/s（+5%）
- 内存稳定，无 GC Gen2 增长

> **注意**：这里的 Movement-Stream 1818 rounds/s 是 `SubmitAndSnapshotAsync` 路径的独立测量，与 `HeroComing.Perf` 回归门禁的 1512 rounds/s（`Submit` 路径）是不同 harness——详见 `kb-perf-harnesses.md`。

### CommandStream store cache / dirty flags（2026-07-05）

Hero perf 瓶颈继续推进时，保留了 4 个核心库内的低层微优化（详见 `kb-command-stream.md` “Hero perf CommandStream record/submit 微优化”）：

- `CommandStream.Set<T>` alive-first：mixed frame 中 existing Set 跳过 pending-batch probe。
- `GetOrCreateStore<T>()` 2-slot LRU cache：重复/交替组件类型少走 `Stores` 数组访问。
- `_hasStoreCommands` / `_hasParallelStoreWrites` dirty flags：无命令 `Submit()` 少扫 store 表；parallel 写入仍 seal。
- `ComponentStore<T>.ApplyToWorld` hoist `Component<T>.ComponentType`。

**结论**：收益主要在 CommandStream record path；`existing-set` proxy 有明确改善，full HeroComing 单轮有较大噪声但门禁通过。不要把 no-promotion cache 当作已验证优化——该变体已因证据不足回退。

## 认知模型

把 MiniArch 的 cache 模型理解为 **"chunk 是 cache 友好的孤岛，entity lookup 是桥"**：
- chunk 内部：纯顺序访问，完美利用 prefetcher
- 跨 chunk：通过 archetype 的 segment 数组顺序跳转，可预测
- 跨 entity：`_records[id]` 是完全随机的，这是唯一不能被 prefetcher 帮助的地方

## 剩余优化机会

| 方向 | 状态 | 备注 |
|------|------|------|
| 列对齐 SIMD | **暂不做** | 热路径不用 SIMD；有需求时一行改 `_columnByteOffsets` 对齐 |
| `MoveNext` per-row 值拷贝 | **测量后决定** | C# struct 返回通常优化为寄存器传递 |
| `CopySharedComponentsFrom` copy 排序 | **方案 A 值得做** | 排序开销极小，copy 时 cache 更友好 |
| `EntityRecord` 压缩为无引用 metadata | **探索中** | 消除 8B Archetype 引用，但需间接寻址 |
| NativeMemory + 64B alignment | **高天花板** | 单独替换 `byte[]` 不保证收益 |
| MigrationPlan 小优化 | **低优先级** | |

## 坑点

- **`World._createArchetypeCacheGeneration`**：CreateArchetypeCache generation 失效——新增 archetype 后递增
- **Edge cache 直索引**：按 componentId 直索引，component ID 稀疏时数组可能膨胀（见 `kb-architecture-review.md` §P2）
- **公共 API 薄转发必须加 `[AggressiveInlining]`**：遗漏会导致迭代退化 41%
- **`Archetype.CreateStorage` 列对齐**：对齐从 8→64 会使每列浪费更多 padding（当前不做）

## 入口

- **看迭代热路径**：`Core/Query.cs` 的 `EnsureRefreshed()` / `AppendNewArchetypes()`
- **看实体随机访问**：`World.cs` 的 `TryGetLocation()`、`World.StructuralChange.cs` 的 `FinishMoveEntity()`
- **看 storage 布局**：`Archetype.Storage.cs` 的 `EnsureCapacity()`、`GetComponentRefAt<T>()`
- **看迁移拷贝**：`Archetype.Storage.cs` 的 `CopySharedComponentsFrom()`、`CopySmall()`
- **看 edge cache**：`Archetype.cs` 的 `_addDestinationCache` / `_removeDestinationCache`
- **看 FrozenState 池化**：`CommandStream.cs` 的 `SwapOutState()` / `TryReclaimPending()`
