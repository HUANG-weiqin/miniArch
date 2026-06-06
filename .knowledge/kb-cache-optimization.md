---
title: Cache & Memory Optimization Review
module: MiniArch.Core
description: Memory layout and cache behavior analysis of the ECS runtime, with optimization opportunities
updated: 2026-06-06
review: 2026-06-06 — 新增 World 数据结构优化实验顺序；P9 已实施（ThreadStatic buffer）；P10 已实施（archetype cache）
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
World
├── _versions: int[]           // 4B/entity, 独立数组
├── _locations: EntityLocation[] // 16B/entity, 独立数组
│   = (Archetype ref, ChunkIndex, RowIndex)
├── _archetypes: Dictionary<Signature, Archetype>
└── _freeIds: RecycledEntity[]

Archetype
├── _chunks: List<Chunk>       // Chunk ref 数组
├── _componentIdToColumnIndex: int[]  // shared with all Chunks
├── Edges: ArchetypeEdges      // MigrationPlan?[] per component ID
└── _generation: int

Chunk
├── _data: byte[]              // SoA packed, all columns in ONE array
├── _entities: Entity[]        // 8B/row, parallel to data
├── _columnByteOffsets: int[]  // per-column byte offset into _data
├── _elementSizes: int[]       // per-column element size
└── _componentIdToColumnIndex: int[]  // same ref as Archetype's
```

### 热路径分析

| 路径 | 每帧频率 | Cache 行为 |
|------|----------|------------|
| SpanEach.MoveNext (per row) | 数百万次 | ✅ Pointer bump，纯顺序 |
| SpanEach chunk transition | 数千次 | ✅ 3-5 次数组查找，可忽略 |
| Entity Get/Set/Has | 取决于游戏逻辑 | ⚠️ _locations[id] → 随机访问 |
| Entity Create/Destroy | 取决于游戏逻辑 | ⚠️ 2 数组 × 2-4 次随机访问 |
| Add/Remove component | 少见 | ⚠️ MigrationPlan.CopySharedData |
| Query.RefreshSnapshot | 每查询每帧 1 次 | ✅ 生成号比较 + 数组拷贝 |

## 决策

### 为什么整体评价是"已高度优化"

1. **迭代热路径几乎完美**：per-row = `Unsafe.Add(ref, 1)` × N，没有间接寻址
2. **自适应 chunk 容量（16KB）** 正好瞄准 L1 cache，一次迭代不换 cache
3. **ComponentMask256** 用 bitmask 做 signature 匹配，O(1) 无分支
4. **CopySmall 特化路径**（1/4/8/12/16 字节）覆盖了常见组件大小
5. **`SkipLocalsInit`** + **`MemoryMarshal.GetArrayDataReference`** 消除了运行时开销

### 实际可优化的点

## P0: World._versions 和 _locations 分离 ✅ 已修复 (2026-06-03)

**问题**：`_versions: int[]` 和 `_locations: EntityLocation[]` 是两个独立数组。每次实体操作（Create/Destroy/Get/Move）都要访问两个数组，造成 **2 次 cache miss**（索引相同但数组不同）。

**已实施**：合并为 `EntityRecord[] _records`：
```csharp
struct EntityRecord {
    int Version;        // 4 bytes
    Archetype Arch;     // 8 bytes (ref)
    int ChunkIndex;     // 4 bytes
    int RowIndex;       // 4 bytes
}  // 20 bytes → padded to 24 bytes
```

- 回退测试：HeroComing.Perf Movement 698.1 rounds/s（baseline 690），Attack 203.6（baseline 179），性能无退化
- 影响文件：`World.cs`（50+ 处引用点）、`WorldSnapshot.cs`、`WorldClone.cs`、新增 `EntityRecord.cs`
- `EntityLocation.cs` 仍然保留但不再被 World 内部使用

## P1: Chunk._data 列对齐不够

**问题**：`CreateStorage` 对齐到 `Min(elementSize, 8)` 字节。对于 4 字节组件（int/float），列起始地址只保证 4 字节对齐。AVX2 要求 32 字节对齐，AVX-512 要求 64 字节对齐。

**当前状态**：热路径不用 SIMD，所以目前不是问题。但如果未来想用 `Vector256<T>` 或 `Vector512<T>` 批量处理组件数据，对齐不足会触发跨 cache line 的未对齐加载。

**方案**：将 `AlignUp(totalBytes, Min(elementSize, 8))` 改为 `AlignUp(totalBytes, 64)`（cache line 对齐）。代价：每个组件列平均浪费 ~32 bytes padding（16KB chunk 内可忽略）。

**判断**：**现在不做，但记住**。等有 SIMD 需求时再改，改动极小（一行）。

## P2: SpanEach.Current 每行分配

**问题**：`Current` 属性每次返回 `new SpanEachRow<T...>(...)`，这是一个 struct 包含 N 个 `ref T` 字段 + Entity。虽然 struct 不堆分配，但每个 row 都要复制这些字段到调用方的变量。

**当前状态**：C# struct 返回是值拷贝，编译器通常优化为寄存器传递。对于 2-3 个组件 + Entity，总计约 32-40 bytes 的 struct，编译器可能通过寄存器或栈传递。实际测量后才知道是否有开销。

**判断**：**测量后决定**。如果迭代是瓶颈，可以考虑 inline callback 模式（`SpanEach<T>(ref T1 r1, ref T2 r2, Entity e)` delegate 或 function pointer），避免 struct 复制。

## P3: MigrationPlan.CopySharedData 的 copy 散布

**问题**：组件迁移时，`CopySharedData` 对每个 CopyEntry 分别访问 source chunk 和 dest chunk 的 `_data` 数组。N 个组件 = N 次 source↔dest 交叉访问。

**热路径影响**：这是结构变更的热路径（Add/Set/Remove component）。每次迁移都要把所有共享组件从旧 chunk 复制到新 chunk。

**方案 A**（简单）：将 `CopyEntry[]` 按 SourceColumnIndex 排序，使 source 侧访问从顺序变为局部顺序。

**方案 B**（激进）：对于小组件（≤16 bytes × ≤4 components），展开为内联拷贝循环，减少方法调用开销。

**判断**：**方案 A 值得做**（排序开销极小，copy 时 cache 更友好）。方案 B 等 profiling 数据。

## P4: ArchetypeEdges 稀疏数组

**问题**：`MigrationPlan?[]` 按 component ID 索引，如果 component ID 分配不连续（有空洞），浪费内存。例如只有 component ID 0 和 100，数组长度为 101，99 个 null。

**热路径影响**：这是冷路径（只在结构变更时查找 migration plan），内存浪费不影响迭代。

**判断**：**不做**。O(1) 查找比 Dictionary 的 hash 查找更可预测。除非 component ID 超过几千且有大量空洞，否则不值得改。

## P5: MigrationPlan.Build O(n*m) 线性搜索

**问题**：`MigrationPlan.Build()` 对每个 destination component 都线性扫描 source components（O(n*m)）。虽然 n 通常很小（<20），但 source 已有 `_componentIdToColumnIndex` 可以直接 O(1) 查找。

**热路径影响**：冷路径（只在 edge cache miss 时调用），影响微小。

**Fix**：用 `source.GetComponentIndexFast(componentType)` 替代内层循环。

**判断**：**值得做**，改动极小（~5 行），收益虽小但无成本。

## P6: Chunk.CopySmall 缺少 case 2

**问题**：`CopySmall` 支持 1/4/8/12/16 字节，缺少 2 字节 case。2 字节组件（`Half`、`short`、`ushort`、`char`）走 fallback `Unsafe.CopyBlockUnaligned`。

**Fix**：增加 `case 2: Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<short>(ref src));`

**判断**：**低优先级但成本为零**，建议补上。

## P7: CommandBuffer.ExtractAndSortComponents 在 Submit 时排序

**问题**：`CommandBuffer.Submit()` 处理每个 created entity 时调用 `ExtractAndSortComponents` 做 `Array.Sort`（O(n log n)）。批量创建大量 entity 时排序开销可累积。

**实测数据（2026-06-05，HeroComing.Perf Movement, 1000 chars）**：
- Submit 内部阶段分解：Created entities 占 75.4%，Existing ops 占 15.2%，Hierarchy <1%，Destroy 4.8%，Clear 4.6%
- 在 Created entities 阶段内：Array.Sort 仅占 Submit 总时间的 ~10%，不是主要瓶颈
- Slab scatter 假说已验证并否定：用单一大块连续 buffer 替代分散 byte[] slab，仅改善 ~2%

**Fix**：在 Record 时维护组件列表的排序顺序（二分插入），避免 Submit 时排序。

**判断**：**低优先级**。实测显示 Array.Sort 仅占 Submit 的 10%，即使完全消除也只能提升 ~10%。

## P9: CommandBuffer.BuildCreatedEntityComponents 每 entity 重复 ArrayPool rent/return ✅ 已修复 (2026-06-05)

**问题**：`BuildCreatedEntityComponents` 在每个 created entity 上做 3 次 ArrayPool rent + 3 次 return：
1. `ArrayPool<ComponentType>.Shared.Rent(count)` — ExtractAndSortComponents
2. `ArrayPool<CreatedComponent>.Shared.Rent(count)` — ExtractAndSortComponents
3. `ArrayPool<RawComponentValue>.Shared.Rent(count)` — BuildCreatedEntityComponents

加上 return 共 6 次 ArrayPool 操作/entity。HeroComing.Perf Movement 场景创建 ~3.9M entities/30s，即 ~23M ArrayPool 操作。

**实测影响**：
- 替换为 `[ThreadStatic]` 预分配 buffer 复用后：
  - Movement 吞吐量：669.5 → 818.3 rounds/s（+22.2%）
  - Attack 吞吐量：189.6 → 220.4 rounds/s（+16.2%）
  - GC Gen0：35 → 3（-91%）
- 同时将 ExtractAndSortComponents 的逻辑内联到 BuildCreatedEntityComponents，消除了一次跨方法调用和 ReturnExtracted 的开销

**已实施**：
- 新增 3 个 `[ThreadStatic]` 字段：`_tsExtractTypes`、`_tsExtractSources`、`_tsRawComponents`
- `BuildCreatedEntityComponents` 不再调用 `ExtractAndSortComponents`，改为内联提取 + 排序
- 不再 return buffer 到 ArrayPool，由 ThreadStatic 字段跨 Submit 复用
- Submit 循环的 finally 块不再调用 `ArrayPool<RawComponentValue>.Shared.Return`
- 原有 `ExtractAndSortComponents` 保留不动（仍被 frozen/snapshot 路径使用）

**影响文件**：`src/MiniArch/Core/CommandBuffer.cs` — BuildCreatedEntityComponents 重写

## P10: CommandBuffer.GetOrCreateArchetype 重复 Dictionary 查找 ✅ 已修复 (2026-06-05)

**问题**：`BuildCreatedEntityComponents` 在每个 created entity 上调用 `_world.GetOrCreateArchetype(key)`（Dictionary 查找）。即使 entity 共享同一个 component set，每次仍要创建 68 字节 `CreateArchetypeKey` struct、计算 7 值 hash、做 Dictionary bucket 遍历。

Pipeline 工作负载中，同批次 entity 几乎都是同一 archetype（如 500 个 MoveRequest 都一样）。

**实测数据**：
- PERF_DIAG 测量：`GetOrCreateArchetype` 占 Submit 总时间的 12.1%
- 添加 last-value cache（按 componentCount + typeHash 缓存）后降为 0.1%
- 净吞吐量提升：Movement 818.3 → ~842 rounds/s（+3%）

**已实施**：
- 新增 3 个字段：`_cachedArchetype`、`_cachedArchetypeCount`、`_cachedArchetypeHash`
- 在 BuildCreatedEntityComponents 中先检查 count 匹配 → 计算 typeHash 匹配 → 命中则跳过 Dictionary 查找
- 仅在 count 匹配时计算 hash（不同 count 直接走 Dictionary，避免双倍 hash 开销）
- Hash 算法与 `CreateArchetypeKey.GetHashCode()` 一致：`hash = count; for each id: hash = hash*31 + id`

**影响文件**：`src/MiniArch/Core/CommandBuffer.cs` — BuildCreatedEntityComponents 中的 archetype 查找逻辑

## P8: HierarchyTable.EnsureCapacity 用 Array.Fill 初始化

**问题**：`EnsureCapacity` 对 `_parentByChild`（Entity 类型）也使用 `Array.Fill` 填充 `NoEntity = default(Entity) = (0,0)`。全零初始化可以用更快的 `Array.Clear`。

**Fix**：对 `_parentByChild` 改用 `Array.Clear`，对 `_firstChild` 保留 Fill（因为 NoSlot = -1 不是零）。

**判断**：**微小优化**，优先级低。

## 认知模型

### 理解方式

把 MiniArch 的 cache 模型理解为 **"chunk 是 cache 友好的孤岛，entity lookup 是桥"**：
- chunk 内部：纯顺序访问，完美利用 prefetcher
- 跨 chunk：通过 archetype 的 chunk 数组顺序跳转，可预测
- 跨 entity：`_locations[id]` 是完全随机的，这是唯一不能被 prefetcher 帮助的地方

### 最重要的一条数据

**AdaptiveChunkTargetBytes = 16KB** 是整个 cache 策略的核心数字。它决定了每个 chunk 能装多少行，间接决定了：
- 迭代切换 chunk 的频率
- 每次 chunk transition 的开销摊销
- L1 cache 中能同时放几个 chunk 的数据

## 坑点

- **改 _locations 布局要同步改所有引用点**：World.cs 中至少 15+ 处直接访问 `_locations` 和 `_versions`，合并为 EntityRecord 后所有这些点都要更新
- **Entity record padding**：合并后 24 bytes/entity，比 20 bytes 多 20%。如果 entity 数量极大（>5M），要评估内存增长
- **Chunk._data 对齐改大后容量变化**：对齐从 8→64 会使每列浪费更多 padding，可能减少 chunk 内行数，影响迭代密度

## 入口

- **看迭代热路径**：`SpanQueryIterators.cs` 的 `MoveNext()` 方法
- **看实体随机访问**：`World.cs` 的 `GetRequiredLocation()`、`FinishMoveEntity()`
- **看 chunk 布局**：`Chunk.cs` 的 `CreateStorage()`、`GetComponentRefAt<T>()`
- **看迁移拷贝**：`MigrationPlan.cs` 的 `CopySharedData()`
