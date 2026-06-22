---
title: Chunk 存储
module: MiniArch.Core
description: Archetype 存储架构 — 单块模式（默认）和分段模式（阈值后自动切换），包括 SoA 布局、跨段 swap-remove、查询分段迭代
updated: 2026-06-22 (全库审阅: 确认实现与文档一致)
---
# Chunk 存储

## 这个模块是干什么的

- ChunkView（public）是 Archetype/Segment 的**只读 readonly struct 视图**
- 在**单块模式**（`_isChunked == false`）下，一个 Archetype 对应一个 ChunkView
- 在**分段模式**（`_isChunked == true`）下，每个 Segment 对应一个 ChunkView
- 真正的存储（`_data: byte[]`、`_entities: Entity[]`、列偏移、元素大小等）全部直属于 `Archetype`
- 存储操作代码在 `Archetype.Storage.cs` partial 文件中

## 架构

### 2.1 两种模式

**单块模式**（默认，零额外开销）：
```
_isChunked = false
  _entities: Entity[]
  _data: byte[]
  扩容: 翻倍重分配（和原先完全一致）
```

**分段模式**（超过阈值后自动切换，零拷贝转换）：
```
_isChunked = true
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
- `Query.cs（Core）`：内部查询实现，分段下每个 Segment 产生一个 ChunkView

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

### 3.1 ConvertToChunked（零拷贝）

当 `_capacity * 2 > SegmentEntityCapacity` 且下次需要扩容时，自动执行：

```csharp
ConvertToChunked():
    _segments[0] = new Segment {
        Entities = _entities,   // 直接转移引用
        Data = _data,           // 直接转移引用
        Count = _count
    }
    _segmentCount = 1
    _isChunked = true
    _entities = null
    _data = null
```

### 3.2 GrowChunked（逐段扩容零拷贝）

分段后扩容只追加新段，不碰旧段数据。每段固定大小 `SegmentEntityCapacity`。

### 3.3 跨段 swap-remove

只在末段产生空洞。删除行不在末段时，将末段最后一行拷贝到删除行位置，更新被移动实体的 `EntityRecord.RowIndex`。

```csharp
RemoveAt(globalRow):
    lastSeg = _segments[_segmentCount - 1]
    // 如果在末段，直接段内 swap-remove
    // 如果不在末段，将末段最后一行跨段拷贝到删除位置
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

修复：引入 `_flatEntitiesGeneration` 计数器 + 缓存数组 `_cachedFlatEntities`。实体布局变更（AddEntityChunked/RemoveAt/WriteEntityAt/ReserveRows/GrowChunked 等）时递增计数器；`GetEntityStorage()` 仅在计数器与缓存版本不匹配时重建平坦数组，否则返回缓存引用——零分配零拷贝。

## 决策

- **单块模式零退化**：`_isChunked == false` 时所有路径和原先完全一致（只多一个分支预测）
- **阈值按组件大小动态计算**：`SegmentEntityCapacity = Max(16, 2MB / bytesPerEntity)`，目标每段 2 MB，确保切换前已有足够大的单块
- **行号映射用除法而非二分**：因为所有段（除末段外）固定大小，`globalRow / SegmentEntityCapacity` 即可定位
- **`ChunkView.Count` 总是读 Archetype 实时计数**：避免因 CommandBuffer 延迟提交导致的 stale count 问题（2026-06-13 bugfix）
- **不支持托管引用组件**：`flat byte[]` 不含 GC 跟踪，在 Archetype 构造时 fail fast

## 认知模型

- 单块模式：Archetype = 1 个连续存储块 = 1 个 ChunkView
- 分段模式：Archetype = N 个 Segment = N 个 ChunkView
- EntityRecord 始终存全局行号，行号映射对 World 层完全透明

## 入口

- **核心逻辑**：`Archetype.Storage.cs` 的 `EnsureCapacity()` / `AddEntity()` / `RemoveAt()`
- **查询适配**：`Query.cs` 的 `AppendNewArchetypes()` + `ChunkView.cs`
- **测试**：`ArchetypeTests.cs` 的 `Chunked_mode_*` 测试方法

## 坑点

- `EnsureCapacity` 可能在非分段路径中将 Archetype **切换为分段模式**，`AddEntity` 和 `ReserveRows` 必须在 `EnsureCapacity` 返回后**重新检查 `_isChunked`**（2026-06-13 bugfix）
- `ChunkView.Count` 不能用字段缓存实体计数，必须实时读取 Archetype，否则 CommandBuffer 延迟提交导致 chunk.Count 大于 span 长度 → IndexOutOfRangeException
- `GetSegmentAndLocal` 假设除末段外所有段都满（段大小 = `SegmentEntityCapacity`，由组件布局动态决定），如果出现不等长段需要改用二分查找
