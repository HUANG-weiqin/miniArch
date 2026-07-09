---
title: Chunk 存储
module: MiniArch.Core
description: Archetype 存储架构 — 单块模式（默认）和分段模式（阈值后自动切换），包括 SoA 布局、跨段 swap-remove、查询分段迭代
updated: 2026-07-09
---
# Chunk 存储

## 这个模块是干什么的

- ChunkView（public）是 Archetype/Segment 的**只读 readonly struct 视图**
- 在**单块模式**（`_segments` 为 null，即 `!IsChunked`）下，一个 Archetype 对应一个 ChunkView
- 在**分段模式**（`_segments` 非 null，即 `IsChunked`）下，每个 Segment 对应一个 ChunkView
- 真正的存储（`_data: byte[]`、`_entities: Entity[]`、列偏移、元素大小等）全部直属于 `Archetype`
- 存储操作代码在 `Archetype.Storage.cs` partial 文件中

## 架构

### 2.1 两种模式

**单块模式**（默认，零额外开销）：
```
  _entities: Entity[]
  _data: byte[]
  扩容: 翻倍重分配（和原先完全一致）
```

**分段模式**（超过阈值后自动切换）：
```
  _segments: Segment[]
  Segment {
      Entities: Entity[]
      Data: byte[]
      Count: int
  }
```

### 2.2 阈值

```csharp
// 每段目标 2 MB，实体数根据组件大小动态计算
const int TargetSegmentBytes = 2 * 1024 * 1024;
int SegmentEntityCapacity = perEntity > 0 ? Math.Max(16, TargetSegmentBytes / perEntity) : 65536;
// 当 capacity * 2 > SegmentEntityCapacity 时，触发分段切换
// 例：Position (8 bytes) → 262144 entities/segment；Position+Velocity (16 bytes) → 131072 entities/segment
```

### 2.3 行号映射

- **单块模式**：行号 = 直接数组索引
- **分段模式**：`globalRow / SegmentEntityCapacity` → 段索引，`globalRow - segIdx * SegmentEntityCapacity` → 段内行（段大小由组件布局动态决定）
- `EntityRecord.RowIndex` 始终存全局行号，不变

### 2.4 存储相关文件

- `Archetype.cs`：字段、`Segment` 内部结构体、常量
- `Archetype.Storage.cs`：存储操作（EnsureCapacity、AddEntity、RemoveAt、组件读写等）
- `ChunkView.cs`：public readonly struct 视图，非分段下包裹 Archetype，分段下包裹 Segment
- `Core/QueryCache.cs`：内部查询实现，分段下每个 Segment 产生一个 ChunkView

### 2.5 ChunkView 的分段适配

```csharp
// segmentIndex = -1 表示单块模式，>= 0 表示分段模式中的特定段
internal ChunkView(Archetype archetype, int segmentIndex = -1)
public int Count =>
    _segmentIndex >= 0
        ? _archetype.GetSegmentCount(_segmentIndex)
        : _archetype.EntityCount;
public Span<T> GetSpan<T>() where T : unmanaged
{
    if (_archetype.IsChunked)
        return _archetype.GetSegmentComponentSpan<T>(_segmentIndex, colIdx);
    return _archetype.GetComponentSpan<T>(Component<T>.ComponentType);
}
```

## 分段存储细节

### 3.1 ConvertToChunked（统一拷贝路径）

当 `_capacity * 2 > SegmentEntityCapacity` 检测到需要扩容且容量超阈值时自动执行。总是分配标准 Segment 并将平坦数组数据逐段拷贝：

```csharp
ConvertToChunked():
    segOffsets = ComputeColumnLayout(_elementSizes, _segmentCapacity).Offsets
    segCount = Math.Max(1, (_count + _segmentCapacity - 1) / _segmentCapacity)
    _segments = new Segment[segCount]
    for each segment:
        Array.Copy(_entities, ...)        // 实体数据
        CreateStorageBytes(...)           // 新 segment byte[]
        CopyBlockUnaligned column data    // 组件列数据
    _columnByteOffsets = segOffsets
    _entities = null
    _data = null
    AssertConvertedInvariants()
```

- **旧 fast path 已删**：曾当 `_capacity == _segmentCapacity` 时将平坦数组直接当 segment[0] 零拷贝包装。统一到拷贝路径的理由：分段不变式一致执行、消除一个模式分支。
- 转换后状态：`_entities = null`，`_data = null`，`_segments` 填充，`AssertConvertedInvariants()` 在 DEBUG 下验证全部不变式（segment 等长、count 求和一致）。

### 3.2 GrowChunked（逐段扩容零拷贝）

分段后扩容只追加新段，不碰旧段数据。每段固定大小 `SegmentEntityCapacity`。

### 3.3 跨段 swap-remove

只缩减最后一个非空段（`lastSeg`）的 `Count`。删除行不在 `lastSeg` 时，将 `lastSeg` 最后一行拷贝到删除行位置，更新被移动实体的 `EntityRecord.RowIndex`。删除后 `lastSeg` 可能 `Count < segCap`（留空洞）；若 `lastSeg` 后还有预分配空段，则出现"非末段未满"——这是合法中间状态（§3.6），`AllocateRows` 下次会优先补这个空洞。

```csharp
RemoveAt(globalRow):
    lastSeg = 最后一个 Count>0 的段
    // 如果删的就是 lastSeg 末行，直接 Count--（无 swap）
    // 否则将 lastSeg 末行跨段拷贝到删除位置，再 Count--
    CopySegmentColumn(srcSegIdx, srcLocalRow, destSegIdx, destLocalRow)
    // 更新被移动实体的行号
```

### 3.4 查询分段迭代

`Query` 的 `EnsureRefreshed()` 检测：
1. 全局 Archetype 数量变化 → 增量追加
2. 已有匹配 Archetype 的 `SegmentCount` 变化 → 全量刷新

分段后的 Archetype 在匹配时，每个 Segment 被包装为一个独立的 ChunkView。

### 3.5 GetEntityStorage 平坦缓存（2026-06-21）

分段模式下 `GetEntityStorage()` 原来每次调用 `new Entity[_count]` + 逐段 `Array.Copy`，在 `QueryEnumerator.MoveNext()` 热路径上造成每帧分配。

修复：引入 `_flatEntitiesGeneration` 计数器 + 缓存数组 `_cachedFlatEntities`。实体布局变更（AllocateRows/RemoveAt/WriteEntityAt/GrowChunked 等）时递增计数器；`GetEntityStorage()` 仅在计数器与缓存版本不匹配时重建平坦数组，否则返回缓存引用——零分配零拷贝。

### 3.6 真正的段不变量：非空段连续在前（2026-07-05 修正）

> 曾误以为"除末段外所有段必须满（`Count == segCap`）"。这是**错误**的过强不变量，会把 `RemoveAt` 后的合法中间状态判为非法（commit `40165f7` 加此断言后打破 20 个测试）。正确不变量如下。

`GetSegmentAndLocal(globalRow) = (globalRow >> shift, globalRow & mask)` 依赖的是**每段占固定 segCap 个行号**（容量等长），**不是**段都满。因此：

- ✅ **合法**：段内 `Count < segCap`（空洞）。`RemoveAt` 跨段 swap-remove 后 `lastSeg` 缩一格留下空洞；`AllocateRows` 按"填首个非满段"补上（见 `BUG_flat_entity_index_mismatches_global_row_when_segment_hole_exists`）。例：`seg0=2047, seg1=0, seg2=0`。
- ✅ **合法**：尾部连续空段。`EnsureCapacity`/`GrowChunked` 预分配的空段。例：`seg0=128(满), seg1=72, seg2=0`。
- ❌ **非法**：空段夹在非空段之间。例：`seg0=0, seg1=5`。否则 `AllocateRows` 分配 `globalRow = _count = 5`，但 `GetSegmentAndLocal(5) = (0, 5)` 指向空 `seg0` 的越界槽，行号映射错位。

`AssertSegmentInvariants` 因此检查"**非空段连续在前**"（`seenEmpty` 后不允许再出现 `Count > 0` 的段），而非"非末段必须满"。所有操作（`RemoveAt`/`AllocateRows`/`RestoreFlatBackup`/`GrowChunked`/`ConvertToChunked`）都维护此不变量。

## 决策

- **单块模式零退化**：`!IsChunked`（flat 模式）时所有路径和原先完全一致（只多一个分支预测）
- **阈值按组件大小动态计算**：`SegmentEntityCapacity = Max(16, 2MB / bytesPerEntity)`，目标每段 2 MB。2MB 的选择基于：超过 85000 字节的 `byte[]` 分配在 LOH（大对象堆），而 2MB 远高于此但仍是可控的单次分配。更小的段（如 256KB）会增加 segment 数量和遍历开销；更大的段则减少遍历开销但增加单次晋升分配的峰值内存。2MB 是经验平衡点。
- **行号映射用除法而非二分**：所有段**容量**等长（`Entities.Length == segCap`），`globalRow / segCap` 即可定位段号。段内 `Count` 可不满（空洞合法，见 §3.6）
- **`ChunkView.Count` 总是读 Archetype 实时计数**：避免因 CommandBuffer 延迟提交导致的 stale count 问题（2026-06-13 bugfix）
- **不支持托管引用组件**：`flat byte[]` 不含 GC 跟踪，在 Archetype 构造时 fail fast
- **`AddEntity = AllocateRows(1) + WriteEntityAt`**：分配和写入分离设计。`AllocateRows` 负责扩容/模式切换后分配行号，`WriteEntityAt` 负责写入实体标识。每个方法独立读取 `IsChunked`，消除了老代码中 "EnsureCapacity 可能切换模式→调用方必须重检" 的 bug 类。代码位置：`Archetype.Storage.cs:222-226`
- **`ConvertToChunked` 统一拷贝路径**：旧 fast path（`_capacity == _segmentCapacity` 时零拷贝包装平坦数组）已删除。理由：分段 invariant（`Entities.Length == _segmentCapacity`、列偏移基于 `_segmentCapacity`）在所有路径一致执行，消除一个模式分支。

## 认知模型

- 单块模式：Archetype = 1 个连续存储块 = 1 个 ChunkView
- 分段模式：Archetype = N 个 Segment = N 个 ChunkView
- EntityRecord 始终存全局行号，行号映射对 World 层完全透明

## 入口

- **核心逻辑**：`Archetype.Storage.cs` 的 `EnsureCapacity()` / `AddEntity()` / `RemoveAt()`
- **查询适配**：`Query.cs` 的 `AppendNewArchetypes()` + `ChunkView.cs`
- **测试**：`ArchetypeTests.cs` 的 `Chunked_mode_*` 测试方法

## Storage Invariants（集中参考）

> 以下不变量分散在多个 kb 页中，这里集中供存储层 refactor 时参考。
> 破环后会失败哪些测试见第三列。

| 不变量 | 描述 | 定义位置 | 相关测试 |
|--------|------|---------|---------|
| **Swap-remove 语义** | `RemoveAt(row)` 把最后一行移到被删行位置，`EntityRecord.RowIndex` 必须同步更新 | 本页 §3.3 + `kb-core-ecs.md` 坑点 | `ArchetypeTests` / `WorldStructuralChangeTests` |
| **列偏移公式** | 元素定位 = `_columnByteOffsets[col] + row * _elementSizes[col]` | `kb-cache-optimization.md` 内存布局 | 几乎所有读组件的测试（`ChunkTests`, `QueryTests`, `QueryFilterTests`） |
| **容量增长** | 单块模式 doubling；超过 `_capacity * 2 > _segmentEntityCapacity` 时晋升为分段 | 本页 §2.2 + §3.1 | `ArchetypeTests` / `WorldLifecycleTests` |
| **晋升单向** | chunked 模式不回退为单块 | `kb-architecture-review.md` §2 | `ArchetypeTests.Chunked_mode_*` |
| **Padding 零初始化** | `GC.AllocateArray`（零初始化）分配，组件 struct padding 确定为 0 → checksum 安全 | `kb-snapshot-persistence.md` Checksum 段 | `WorldSnapshotTests` (checksum) / `FrameDeltaDeterminismTests` |
| **段容量等长** | 所有段 `Entities.Length == SegmentEntityCapacity`（容量等长），`GetSegmentAndLocal` 用除法定位行号空间。段内 `Count` **可以** `< segCap`（RemoveAt 空洞，合法） | 本页 坑点 + §3.6 | `ArchetypeTests.Chunked_mode_*` / `AllocateRows_skips_empty_tail_segments_and_fills_first_available` |
| **`IsChunked` 重检（历史）** | 曾经的坑：`EnsureCapacity` 可能切换模式，老 API 返回后须重检 `_isChunked`。已解决：`AddEntity = AllocateRows(1) + WriteEntityAt`，每个方法内部单次读取 `IsChunked` | 本页 决策 + 坑点（历史） | `ArchetypeTests` / `CommandStreamTests` (materialize 路径) |
| **`_flatEntitiesGeneration` 失效** | 布局变更（AllocateRows/WriteEntityAt/RemoveAt/GrowChunked/RestoreFlatBackup/RebuildFlatEntities）时递增 | 本页 §3.5 | `ChunkTests.GetEntities` / `QueryEnumerator` 热路径 |
| **RowIndex 全局性** | `EntityRecord.RowIndex` 始终是全局行号，模式透明 | 本页 认知模型 | `WorldStructuralChangeTests` / `IntegrationTests` |
| **Load 不能走 Add/Set/Remove** | 否则会挤压重排快照 chunk 边界 | `kb-snapshot-persistence.md` 决策 | `WorldSnapshotTests` (save+load round-trip) |

## 坑点

- **(已解决)** `EnsureCapacity` 可能在非分段路径中将 Archetype **切换为分段模式**，老的 `ReserveRows` 和 `AddEntity` 必须在 `EnsureCapacity` 返回后重新检查 `_isChunked`（2026-06-13 bugfix）。当前代码已重构为 `AddEntity = AllocateRows(1) + WriteEntityAt`，每个方法内部单次读取 `IsChunked`，消除了调用方二次重检的 bug 类。历史记录保留供参考。
- `ChunkView.Count` 不能用字段缓存实体计数，必须实时读取 Archetype，否则 CommandBuffer 延迟提交导致 chunk.Count 大于 span 长度 → IndexOutOfRangeException
- `GetSegmentAndLocal` 假设所有段**容量**等长（`Entities.Length == SegmentEntityCapacity`），从而 `globalRow / segCap` 能定位段号。它**不**假设段都满——段内 `Count < segCap` 的空洞是合法中间状态（`RemoveAt` 产生，`AllocateRows` 按"填首个非满段"补上）。真正禁止的是"空段夹在非空段之间"（见下条 + §3.6），否则 `globalRow = _count` 的分配会与除法映射错位

### DEBUG 不变式断言

- **`AssertSegmentInvariants()`**（`Archetype.Storage.cs`，`[Conditional("DEBUG")]`）：在 `AllocateRows`/`WriteEntityAt`/`RemoveAt`/`RestoreFlatBackup`/`RebuildFlatEntities` 等状态变更后调用。验证：① 每段 `Entities.Length == _segmentCapacity`（容量等长）；② `Count <= Entities.Length`；③ **非空段连续在前**（`seenEmpty` 后不再有 `Count>0` 的段）；④ `sum(Count) == _count`。**注意**：不检查"非末段必须满"——这是历史误判（commit `40165f7` 曾误加，2026-07-05 修正为"非空段连续"，见 §3.6）。
- **`AssertConvertedInvariants()`**（`Archetype.Storage.cs`，`[Conditional("DEBUG")]`）：在 `ConvertToChunked` 末尾调用。验证：`_entities is null`、`_data is null`、`_segments is not null`、每个 segment 的 `Entities.Length == _segmentCapacity`、所有 segment 的 `Count` 之和等于 `_count`。
- **`AssertFlatCacheConsistent()`**（`Archetype.Storage.cs`，`[Conditional("DEBUG")]`）：在 `GetEntityStorageUnsafe` 返回前调用。验证：平坦缓存存在且 generation 匹配时，缓存数组长度 >= 所有 segment 的 Count 之和。
