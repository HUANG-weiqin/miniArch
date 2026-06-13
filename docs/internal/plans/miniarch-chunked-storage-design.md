# mini-SoA 分段存储实施计划

**目标：** 在 Archetype 单块存储接近容量上限时，无缝切换到分段模式。分段前零开销，分段后逐段扩容零拷贝，跨段 swap-remove 不留碎片。

**架构约束：** 不改公开 API，不改 Query/World 外部行为，不改热路径 Span 扫描。

---

## 1. 核心设计

### 1.1 两种模式

```
_isChunked = false（常态）
  _entities: Entity[]
  _data: byte[]              ← 和现在完全一样
  扩容: 翻倍重分配

_isChunked = true（触阈值后）
  _segments: Segment[]
  _segmentOffsets: int[]      ← 段前缀和，log2 二分找行
  Segment {
      Entities: Entity[]
      Data: byte[]            ← 整段 SoA，列布局和单块相同
      Count: int
  }
```

### 1.2 阈值

```csharp
// 每段最多 65536 个实体，段内 SoA 大小 ≈ bytesPerEntity * 65536
const int SegmentEntityCapacity = 65536;

// 当 capacity * 2 > SegmentEntityCapacity 时切换分段
```

### 1.3 行号映射

单块模式：行号 = 直接数组索引。分段模式：行号 → 段偏移表二分 → (段索引, 段内行)。实体记录存全局行号不变。

---

## 2. 逐段扩容：零拷贝

分段后扩容只追加新段，不碰旧段数据：

```
GrowChunked(need):
    while need > 0:
        seg = new Segment {
            Entities = new Entity[SegmentEntityCapacity],
            Data = CreateStorage(sig, types, SegmentEntityCapacity)
        }
        _segments.Add(seg)
        _segmentOffsets.Add(_segmentOffsets.Last + SegmentEntityCapacity)
        need -= SegmentEntityCapacity
```

---

## 3. 跨段 swap-remove：只末段浪费

```csharp
RemoveAt(globalRow):
    lastSeg = _segments[_segmentCount - 1]
    lastLocalRow = lastSeg.Count - 1
    lastGlobalRow = _segmentOffsets[_segmentCount - 1] + lastLocalRow

    // 找到删除行所在段
    segIdx = BinarySearchSegment(globalRow)
    localRow = globalRow - _segmentOffsets[segIdx]

    // 跨段拷贝所有列的数据（列布局相同，直接 CopyBlock）
    CopyAllColumnsFrom(lastSeg, lastLocalRow, _segments[segIdx], localRow)

    // 搬实体引用
    movedEntity = lastSeg.Entities[lastLocalRow]
    _segments[segIdx].Entities[localRow] = movedEntity

    // 更新实体记录的全局行号
    World._records[movedEntity.Id].RowIndex = globalRow

    // 末段减一，不需要更新偏移表（末段位置没变）
    lastSeg.Count--
    _count--
```

如果删除行恰好在末段，直接段内 swap-remove，零跨段开销。

---

## 4. 切换：零拷贝

```csharp
ConvertToChunked():
    _segments = new Segment[1]
    _segments[0] = new Segment {
        Entities = _entities,   // 直接转移引用
        Data = _data,           // 直接转移引用
        Count = _count
    }
    _segmentOffsets = [0]
    _segmentCount = 1
    _entities = null
    _data = null
    _isChunked = true
```

---

## 5. 实施步骤

### Task 1：加字段和阈值常量

文件：`src/MiniArch/Core/Archetype.cs`

- 加 `_isChunked: bool`，默认 `false`
- 加 `_segments: Segment[]`，默认 `null`
- 加 `_segmentOffsets: int[]`，默认 `null`
- 加 `_segmentCount: int`，默认 `0`
- 定义 `Segment` 内部结构体
- 定义 `SegmentEntityCapacity` 常量（65536）

### Task 2：加行号映射辅助方法

文件：`src/MiniArch/Core/Archetype.Storage.cs`

- `BinarySearchSegment(globalRow)` → (segIdx, localRow)
- `GetSegmentAndLocal(globalRow)` → 同上（AggressiveInlining）
- 两方法在 `_isChunked == false` 时不被调用（调用方先判断模式）

### Task 3：改 EnsureCapacity

文件：`src/MiniArch/Core/Archetype.Storage.cs`

```
EnsureCapacity(required):
    if required <= _capacity: return
    if !_isChunked && _capacity * 2 > SegmentEntityCapacity:
        ConvertToChunked()
        GrowChunked(required - _count)
        return
    if !_isChunked:
        double _capacity, allocate new arrays, copy (现有逻辑)
    else:
        GrowChunked(required - _capacity)
```

### Task 4：改 Storage 访问方法

文件：`src/MiniArch/Core/Archetype.Storage.cs`

涉及方法（每个加 `_isChunked` 分支）：

| 方法 | 分段逻辑 |
|---|---|
| `AddEntity` | 写到末段，末段满了先 `GrowChunked` |
| `RemoveAt` | 跨段 swap-remove（见第 3 节） |
| `GetColumnSpan` | 返回当前段的 Span |
| `ReadComponent` | 先映射行号再读段内数据 |
| `WriteComponent` | 同上 |
| `ReadComponentRaw` | 同上 |
| `WriteComponentRaw` | 同上 |
| `CopyAllColumnsFrom` | 支持跨段拷贝（两个不同的 Data 数组） |
| `RemoveAt` 同段快速路径 | segIdx == segCount-1 → 段内 swap，不跨段 |

### Task 5：改 ReserveRows / CreateMany

- `ReserveRows(count)` 在分段后不预留连续空间，改为逐段分配或一次性落到新段
- `CreateMany` 需要适配分段后的 AddEntity 语义

### Task 6：Query 适配分段

文件：`src/MiniArch/Core/Query.cs`

- `Query.Refresh()`：分块后 archetype 加新段 → 需要刷新 ChunkView 列表。加 `_lastSegmentCount` 或 generation 编号。
- `Query.GetChunks()`：分段后每个 Segment 映射一个 ChunkView。ChunkView 加 `SegmentIndex` 字段。

### Task 7：公共 API 不改

`World.GetChunks()` / `QueryDescription.Query()` 等公开 API 签名不变。ChunkView 内部适应分段。调用方代码不改一行。

### Task 8：补测试

文件：`tests/MiniArch.Tests/Core/ArchetypeTests.cs`

- 单块模式下所有现有测试继续通过（验证零退化）
- 新增分段模式测试：
  - 小阈值强行切分段
  - 分段后增删查改正确性
  - 跨段 swap-remove 正确性
  - 分段扩容的正确性
  - Query 在分段后的正确性

### Task 9：回归门禁

```
dotnet test
dotnet run -c Release --project tools/perf/HeroComing.Perf
```

Movement ≥ 866 rounds/s, Attack ≥ 200 rounds/s，否则回退。

---

## 6. 不变性保障

- 单块模式下 `_isChunked == false`，所有路径和现在完全一致（只多一个分支，预测命中）
- EntityRecord 不增字段不变布局（仍存全局行号）
- 公开 API 不增不改
- SteadyState 场景下单块模式零 GC 不变
