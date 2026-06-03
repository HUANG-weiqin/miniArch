---
title: Cache & Memory Optimization Review
module: MiniArch.Core
description: Memory layout and cache behavior analysis of the ECS runtime, with optimization opportunities
updated: 2026-06-03
---
# Cache & Memory Optimization Review

## 结论

MiniArch 的迭代热路径已经高度优化（pointer-bump、SoA、自适应 chunk 容量、无 bounds check），迭代本身几乎没有 cache 浪费。可优化空间集中在两个方向：**实体随机访问的 cache miss** 和 **结构变更时的批量拷贝开销**。

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

## P0: World._versions 和 _locations 分离

**问题**：`_versions: int[]` 和 `_locations: EntityLocation[]` 是两个独立数组。每次实体操作（Create/Destroy/Get/Move）都要访问两个数组，造成 **2 次 cache miss**（索引相同但数组不同）。

**热路径影响**：
- `GetRequiredLocation`：读 `_locations[id]` → 读 `_versions[id]` = 2 次随机访问
- `DestroySingle`：读 `_locations[id]` + 写 `_locations[id]` + 读 `_versions[id]` + 写 `_versions[id]` = 4 次跨数组访问
- `FinishMoveEntity`：写 `_locations` × 2 = 跨数组写

**方案**：合并为 `EntityRecord[]`：
```csharp
struct EntityRecord {
    int Version;        // 4 bytes
    Archetype Arch;     // 8 bytes (ref)
    int ChunkIndex;     // 4 bytes
    int RowIndex;       // 4 bytes
}  // 20 bytes → padding to 24 bytes
```

**Trade-off**：
- 优点：任何实体操作只需 1 次 cache miss 而非 2 次
- 缺点：每个 entity 从 4+16=20 bytes 变为 24 bytes（+20% 内存），因为 struct padding
- 判断：entity 数量通常 < 1M（24MB vs 20MB），内存增幅可接受。cache 命中率提升对频繁随机访问的价值更大

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
