# Chunk Storage Robustness — Phase 1 (F+G)

> **Target executor agent**: This plan is written for a junior / executor agent.
> Each task specifies **exact file paths, line numbers, old strings, new strings**.
> Run the verification command after each task before moving on.

**总目标**：消除 `_isChunked` 字段的双读负担，通过拆分"分配行"和"写入行"使每个方法自己单次读模式，且 `_isChunked` 从可变字段变成派生属性。

**不变量保证**：不改变外部 API、不改变双模式、不改变列偏移/段容量/swap-remove 语义。

---

## Task 1: 把 `ReserveRows` 改名 `AllocateRows`

**文件**：`src/MiniArch/Core/Archetype.Storage.cs`（内部实现）
**文件**：`src/MiniArch/Core/Archetype.cs`（字段，如果有引用）
**文件**：所有调用方（4 个位置，见下方）

### 1a: 重命名方法（Storage.cs:210）

旧：
```
    internal int ReserveRows(int count)
```
新：
```
    internal int AllocateRows(int count)
```

### 1b: 更新调用方 — `src/MiniArch/Core/World.EntityLifecycle.cs`

Line 44:
```csharp
// 旧：
        var startRow = archetype.ReserveRows(entities.Length);
// 新：
        var startRow = archetype.AllocateRows(entities.Length);
```

Line 274:
```csharp
// 旧：
        var startRow = archetype.ReserveRows(entities.Length);
// 新：
        var startRow = archetype.AllocateRows(entities.Length);
```

### 1c: 更新调用方 — `src/MiniArch/Core/WorldSnapshot.cs`

Line 469:
```csharp
// 旧：
        var startRow = archetype.ReserveRows(rowCount);
// 新：
        var startRow = archetype.AllocateRows(rowCount);
```

### 1d: 更新调用方 — `src/MiniArch/Core/WorldClone.cs`

Line 27:
```csharp
// 旧：
            var startRow = dstArch.ReserveRows(entities.Length);
// 新：
            var startRow = dstArch.AllocateRows(entities.Length);
```

### 1e: 更新测试文件中对该方法的直接调用

`grep` 搜索 `\.ReserveRows\(` 在 `tests/` 目录下，将测试文件中的调用全部改为 `.AllocateRows(`。

**验证**：`dotnet build` 通过（无 compile error）。

---

## Task 2: 把 `AddEntity` 重构为 `AllocateRows(1) + WriteEntityAt`

**文件**：`src/MiniArch/Core/Archetype.Storage.cs`

### 当前代码（lines 167-204）：

```
    internal int AddEntity(Entity entity)
    {
        if (_isChunked)
            return AddEntityChunked(entity);

        EnsureCapacity(_count + 1);
        if (!_isChunked)
        {
            var row = _count;
            _entities[row] = entity;
            _count++;
            return row;
        }
        return AddEntityChunked(entity);
    }

    private int AddEntityChunked(Entity entity)
    {
        var segIdx = _segmentCount - 1;
        for (var i = 0; i < _segmentCount; i++)
        {
            if (_segments[i].Count < _segments[i].Entities.Length)
            { segIdx = i; break; }
        }
        if (_segments[segIdx].Count >= _segments[segIdx].Entities.Length)
        {
            GrowChunked(1);
            segIdx = _segmentCount - 1;
        }
        ref var seg = ref _segments[segIdx];
        var localRow = seg.Count;
        var globalRow = segIdx * _segmentCapacity + localRow;
        seg.Entities[localRow] = entity;
        seg.Count++;
        _count++;
        _flatEntitiesGeneration++;
        return globalRow;
    }
```

### 新代码：

**删除 `AddEntityChunked` 方法（lines 183-204）**。它所做的事已经被 `AllocateRows`（分配行号）+ `WriteEntityAt`（写入 entity）覆盖。

**把 `AddEntity` 方法体替换为：**

```csharp
    internal int AddEntity(Entity entity)
    {
        var row = AllocateRows(1);
        WriteEntityAt(row, entity);
        return row;
    }
```

注意：`AllocateRows` 内部已处理 `EnsureCapacity`、模式读、计数更新、`_flatEntitiesGeneration++`。`WriteEntityAt` 内部自己读 `_isChunked` 写 entity。

**验证**：
1. `dotnet build` 通过
2. `dotnet test --filter "FullyQualifiedName~ArchetypeTests"` 中与 AddEntity/Chunked 相关的测试通过

---

## Task 3: 删除 `_isChunked` 字段，改为派生属性

**文件**：`src/MiniArch/Core/Archetype.cs`

### 3a: 删除字段（line 34）

旧：
```
    private bool _isChunked;
```
**整行删除。**

### 3b: 改 `IsChunked` 属性（line 88）

旧：
```
    internal bool IsChunked => _isChunked;
```
新：
```
    internal bool IsChunked => _segments is not null;
```

### 3c: 删除 `_segments` 的 `null!` 初始值（line 35）

旧：
```
    private Segment[] _segments = null!;
```
新：
```
    private Segment[]? _segments;
```

### 3d: 删除 `ConvertToChunked` 中的 `_isChunked = true` 赋值（lines 51 和 92）

两处都删掉 `_isChunked = true;` 这行。`IsChunked` 现在由 `_segments is not null` 自动为 true。

位置 1（line 51）：`ConvertToChunked` fast path 中 `_entities = null!;` 和 `_data = null!;` 之间，删掉 `_isChunked = true;`

旧：
```csharp
            _entities = null!;
            _data = null!;
            _isChunked = true;
            return;
```
新：
```csharp
            _entities = null!;
            _data = null!;
            return;
```

位置 2（line 89-92）：`ConvertToChunked` general path 末尾

旧：
```csharp
        _columnByteOffsets = segOffsets;
        _entities = null!;
        _data = null!;
        _isChunked = true;
    }
```
新：
```csharp
        _columnByteOffsets = segOffsets;
        _entities = null!;
        _data = null!;
    }
```

### 3e: 检查所有赋值 `_isChunked` 的位置

运行 `rg "_isChunked\s*="` 确保只有 Constructor 和 ConvertToChunked 中存在赋值。如果还有其他地方赋值 `_isChunked`，必须也删掉。

### 3f: 检查所有读 `_isChunked` 的位置改为读 `IsChunked`

运行 `rg "_isChunked[^A-Za-z]"` 找出所有直接读字段的地方。这些地方必须改为读属性 `IsChunked`（已经没有 `_isChunked` 字段了，所以这是编译要求）。

注意：**不要改 `!` 逻辑**。原来的 `if (!_isChunked)` 改为 `if (!IsChunked)`，语义不变。

**验证**：
1. `dotnet build` 通过
2. `dotnet test` 全量通过（MiniArch.Tests + HeroPipeline.Tests）

---

## Task 4: 修订测试文件中的 `IsChunked` / `ForceChunkedForTesting` 断言

**文件**：所有 `tests/` 目录下引用 `IsChunked` 或 `_isChunked` 的 `.cs` 文件

### 4a: `ForceChunkedForTesting` 后不需要断言 `IsChunked == true`

当前大量测试：
```csharp
archetype.ForceChunkedForTesting();
Assert.True(archetype.IsChunked);
```

`ForceChunkedForTesting` 调用 `ConvertToChunked()` → `_segments` 被赋值 → `IsChunked` 自动 true。**断言仍然成立**，但可以简化：不需要该断言（它在升 chunk 后总是 true，已不是"检查点"）。

此步可选（不删断言也能编译通过），但如果执行 agent 有时间，建议删掉所有 `Assert.True(archetype.IsChunked)`（仅保留 `Assert.False` 用于验证初始非 chunked 状态）。

搜索命令：
```
rg "Assert\.True.*IsChunked" tests/
rg "Assert\.False.*IsChunked" tests/
```
- `Assert.False(archetype.IsChunked)` → 保留（验证构造后原始为 flat）
- `Assert.True(archetype.IsChunked)` → 删掉（不再有意思）

### 4b: 确认没有 `_isChunked` 直接读取

搜索：
```
rg "_isChunked" tests/
```
如有直接读 `_isChunked` 的位置，改为 `IsChunked`。

**验证**：`dotnet test` 全量通过。

---

## Task 5: 清理 `Archetype.Storage.cs` 注释和引用

### 5a: 更新 XML doc

在 `Archetype.cs` line 6-22 的 `<remarks>` 中，更新对双模式"non-chunked vs chunked"的描述。当前描述提到 `_isChunked` 字段——由于字段已删，改为描述 "flat arrays（`_entities`/`_data`）vs Segment arrays（`_segments`）"。

### 5b: 更新 `_isChunked` 注释

搜索 `Storage.cs` 和 `Archetype.cs` 中任何提到 `_isChunked` 的注释，更新或删除。

搜索命令：
```
rg "_isChunked" src/MiniArch/
```

**验证**：`dotnet build` 无 warning，`dotnet test` 全量通过。

---

## Task 6: 门禁验证

### 6a:
```bash
dotnet test -c Release
```

### 6b:
```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

阈值：Movement >= 1210, Attack >= 767。若低于阈值 → 检查 regressions，回退改动。

### 6c: 如果 6b 通过
```bash
dotnet run -c Release --project tools/perf/GameTickSim.Perf
```
确认场景基准无回归。

---

## Phase 1 完成标准

- [ ] Task 1-5 全部通过
- [ ] `dotnet test` MiniArch.Tests 全部通过
- [ ] `dotnet test` HeroPipeline.Tests 全部通过
- [ ] `dotnet run -c Release -- project tools/perf/HeroComing.Perf --check-baseline` 通过
- [ ] 无任何 `_isChunked` 字段残留（`rg "_isChunked" src/` 零结果）

---

## 回退方案

若任何一步失败且 15 分钟内不能修复：
```bash
git checkout -- src/MiniArch/Core/Archetype.cs
git checkout -- src/MiniArch/Core/Archetype.Storage.cs
git checkout -- tests/
```
回退到 F+G 前状态。

---

# Phase 2: 晋升路径硬化

> **前置条件**：Phase 1 全部完成且 `dotnet test` 全量通过。

## Task 7: `ConvertToChunked` 删除 fast path，统一为单一路径

**文件**：`src/MiniArch/Core/Archetype.Storage.cs`

### 7a: 删除 fast path（lines 39-52）

当前 ConvertToChunked 有两个路径：
- Fast path (39-52)：`_capacity == _segmentCapacity` 时直接 wrap 现有数组为 segment[0]（零拷贝）
- General path (54-91)：`_capacity != _segmentCapacity` 时分配新的 segment-sized 数组并 copy

删除 fast path，统一为 general path。

旧（lines 37-91）：
```csharp
    private void ConvertToChunked()
    {
        if (_capacity == _segmentCapacity)
        {
            _segments = new Segment[1];
            _segments[0] = new Segment
            {
                Entities = _entities,
                Data = _data,
                Count = _count
            };
            _segmentCount = 1;
            _entities = null!;
            _data = null!;
            return;
        }

        var segOffsets = ComputeColumnLayout(_elementSizes, _segmentCapacity).Offsets;
        var segCount = Math.Max(1, (_count + _segmentCapacity - 1) / _segmentCapacity);
        _segments = new Segment[segCount];
        _segmentCount = segCount;

        for (var s = 0; s < segCount; s++)
        {
            var segStart = s * _segmentCapacity;
            var rowsInSeg = Math.Min(_segmentCapacity, _count - segStart);

            var segEntities = new Entity[_segmentCapacity];
            Array.Copy(_entities, segStart, segEntities, 0, rowsInSeg);

            var segData = CreateStorageBytes(_signature, _componentTypes, _segmentCapacity);
            for (var col = 0; col < _elementSizes.Length; col++)
            {
                var elemSize = _elementSizes[col];
                var columnBytes = rowsInSeg * elemSize;
                if (columnBytes <= 0) continue;
                ref var srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data),
                    _columnByteOffsets[col] + segStart * elemSize);
                ref var dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(segData),
                    segOffsets[col]);
                Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
            }

            _segments[s] = new Segment
            {
                Entities = segEntities,
                Data = segData,
                Count = rowsInSeg
            };
        }

        _columnByteOffsets = segOffsets;
        _entities = null!;
        _data = null!;
    }
```

新：
```csharp
    private void ConvertToChunked()
    {
        var segOffsets = ComputeColumnLayout(_elementSizes, _segmentCapacity).Offsets;
        var segCount = Math.Max(1, (_count + _segmentCapacity - 1) / _segmentCapacity);
        _segments = new Segment[segCount];
        _segmentCount = segCount;

        for (var s = 0; s < segCount; s++)
        {
            var segStart = s * _segmentCapacity;
            var rowsInSeg = Math.Min(_segmentCapacity, _count - segStart);

            var segEntities = new Entity[_segmentCapacity];
            Array.Copy(_entities, segStart, segEntities, 0, rowsInSeg);

            var segData = CreateStorageBytes(_signature, _componentTypes, _segmentCapacity);
            for (var col = 0; col < _elementSizes.Length; col++)
            {
                var elemSize = _elementSizes[col];
                var columnBytes = rowsInSeg * elemSize;
                if (columnBytes <= 0) continue;
                ref var srcRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data),
                    _columnByteOffsets[col] + segStart * elemSize);
                ref var dstRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(segData),
                    segOffsets[col]);
                Unsafe.CopyBlockUnaligned(ref dstRef, ref srcRef, (uint)columnBytes);
            }

            _segments[s] = new Segment
            {
                Entities = segEntities,
                Data = segData,
                Count = rowsInSeg
            };
        }

        _columnByteOffsets = segOffsets;
        _entities = null!;
        _data = null!;

        AssertConvertedInvariants();
    }
```

### 7b: 添加 DEBUG 断言方法

在 `ConvertToChunked` 方法之后（现约 line 90），新增：

```csharp
    [Conditional("DEBUG")]
    private void AssertConvertedInvariants()
    {
        Debug.Assert(_entities is null, "Flat entities array must be null after promotion.");
        Debug.Assert(_data is null, "Flat data buffer must be null after promotion.");
        Debug.Assert(_segments is not null, "Segments must be non-null after promotion.");
        for (var i = 0; i < _segmentCount; i++)
            Debug.Assert(_segments[i].Entities.Length == _segmentCapacity,
                $"Segment {i} entity capacity ({_segments[i].Entities.Length}) != _segmentCapacity ({_segmentCapacity}).");
        var total = 0;
        for (var i = 0; i < _segmentCount; i++)
            total += _segments[i].Count;
        Debug.Assert(total == _count,
            $"Segment count sum ({total}) != _count ({_count}) after promotion.");
    }
```

### 7c: 需要在 `Archetype.cs` 顶部加 `using System.Diagnostics;` 确保 `Debug.Assert` 可用

检查 `Archetype.Storage.cs` 顶部（line 1）：已有 `using System.Diagnostics;` → 不需要额外添加。

**验证**：`dotnet build -c Debug` 通过，`dotnet test -c Debug` 全量通过（断言在 Debug 模式下运行）。

---

## Task 8: 晋升边缘测试

**文件**：`tests/MiniArch.Tests/Core/ArchetypeTests.cs`

在文件末尾（class 关闭 `}` 之前）新增以下测试。

### 8a: 晋升阈值正下方不应触发晋升

```csharp
    // segCap=2048 for Component1024. capacity=2048 gives _capacity*2=4096 > segCap=2048 → promote.
    // capacity=1024 gives _capacity*2=2048 == segCap → NOT promote (must be >).
    // This test: capacity=2047 → _capacity*2=4094 > 2048 → promote boundary.
    // We test at exactly _segmentCap/2: capacity=1024, doubling hits segCap exactly → NO promote.
    [Fact]
    public void EnsureCapacity_at_exact_segment_capacity_boundary_does_not_promote()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        // segCap=2048. capacity=1024: _capacity*2=2048 == segCap → no promote.
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 1024);

        for (var i = 0; i < 1024; i++)
            archetype.AddEntity(new Entity(i + 1, 1));

        Assert.False(archetype.IsChunked);
        // Adding one more: _count+1=1025 > capacity=1024 → EnsureCapacity → _capacity*2=2048 > 2048? No, ==.
        archetype.AddEntity(new Entity(1025, 1));
        Assert.False(archetype.IsChunked); // Still flat — doubling hit exactly, no promotion
    }
```

### 8b: 晋升边界 +1 应触发晋升

```csharp
    [Fact]
    public void EnsureCapacity_one_past_segment_boundary_promotes()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        // segCap=2048. capacity=1025: _capacity*2=2050 > 2048 → promote.
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 1025);

        for (var i = 0; i < 1025; i++)
            archetype.AddEntity(new Entity(i + 1, 1));

        Assert.False(archetype.IsChunked);
        archetype.AddEntity(new Entity(1026, 1)); // triggers EnsureCapacity → promote
        Assert.True(archetype.IsChunked);
        Assert.Equal(1026, archetype.EntityCount);
        Assert.Equal(1026, archetype.GetEntities().Length);
    }
```

### 8c: 晋升后 RemoveAt 正确性

```csharp
    [Fact]
    public void RemoveAt_after_promotion_preserves_row_mapping()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: 4);

        // Add entities until promotion occurs naturally via doubling.
        while (!archetype.IsChunked)
        {
            var e = new Entity(archetype.EntityCount + 1, 1);
            var row = archetype.AddEntity(e);
            archetype.SetComponentAtTyped(0, row, new Component1024 { Value = archetype.EntityCount });
        }

        var count = archetype.EntityCount;
        Assert.True(count > 0);

        // Remove middle entity via swap-remove.
        archetype.RemoveAt(count / 2, out var movedEntity);

        // Verify entities via GetEntities() (flat cache).
        var entities = archetype.GetEntities();
        Assert.Equal(count - 1, entities.Length);

        // Verify component data at deleted row is now the moved entity's data.
        var movedComponent = archetype.GetComponentAt<Component1024>(0, count / 2);
        Assert.True(movedComponent.Value > 0);
    }
```

**验证**：
```bash
dotnet test --filter "FullyQualifiedName~ArchetypeTests.EnsureCapacity_at_exact_segment_capacity_boundary|FullyQualifiedName~ArchetypeTests.EnsureCapacity_one_past_segment_boundary|FullyQualifiedName~ArchetypeTests.RemoveAt_after_promotion_preserves_row_mapping"
```

---

# Phase 3: 跨段操作 + 备份恢复硬化

## Task 9: 审查 RemoveAt 所有调用方对 movedEntity.RowIndex 的更新

**纯审查任务**，不需要改代码。打开以下 5 个调用方逐一确认：

| 文件 | 行号 | 调用用途 | 预期行为 |
|---|---|---|---|
| `World.EntityLifecycle.cs` | 202 | DestroySingle 中的 swap-remove | movedEntity 有效时 → `records[movedEntity.Id].RowIndex = info.RowIndex` ✅ |
| `World.StructuralChange.cs` | 57 | MoveEntityCore catch rollback | row 为最后行（AddEntity 返回值），`RemoveAt` 内 row==lastGlobalRow → movedEntity=default → 不 swap ✅ |
| `World.StructuralChange.cs` | 71 | FinishMoveEntity 移除源行 | movedEntity 有效时 → `records[movedEntity.Id].RowIndex = sourceInfo.RowIndex` ✅ |
| `World.StructuralChange.cs` | 124 | ApplyComponentAdd 异常回退 | 同 line 57 模式 ✅ |
| `World.StructuralChange.cs` | 186 | ApplyComponentAdd boxed 异常回退 | 同 line 57 模式 ✅ |

**结论**：全部正确。记入 kb-code-review-findings.md。

---

## Task 10: 补 CopyColumnFrom 4 模式组合测试

**文件**：`tests/MiniArch.Tests/Core/ArchetypeTests.cs`

在 class 末尾新增测试。

`CopyColumnFrom` 在 `Archetype.Storage.cs:843-912` 处理 4 种模式组合。已有 kb 条目 A9 验证其安全性但缺覆盖测试。

### 10a: (chunked → chunked) CopyColumnsFrom 跨 arch 复制

```csharp
    [Fact]
    public void CopyColumnsFrom_chunked_to_chunked_preserves_all_data()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        var src = new Archetype(sig, [typeof(Component1024)], capacity: 4096);
        for (var i = 0; i < 3000; i++)
        {
            var e = new Entity(i + 1, 1);
            src.AddEntity(e);
            src.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        src.ForceChunkedForTesting();
        Assert.True(src.IsChunked);

        var dst = new Archetype(sig, [typeof(Component1024)], capacity: 4096);
        dst.AllocateRows(3000);
        for (var i = 0; i < 3000; i++)
            dst.WriteEntityAt(i, new Entity(i + 10001, 1));
        dst.ForceChunkedForTesting();
        Assert.True(dst.IsChunked);

        dst.CopyColumnsFrom(src, 3000);
        for (var i = 0; i < 3000; i++)
            Assert.Equal(i + 100, dst.GetComponentAt<Component1024>(0, i).Value);
    }
```

### 10b: (flat → chunked) CopyColumnsFrom

```csharp
    [Fact]
    public void CopyColumnsFrom_flat_to_chunked_copies_correctly()
    {
        var registry = new ComponentRegistry();
        var comp = registry.GetOrCreate<Component1024>();
        var sig = new Signature(comp);

        var src = new Archetype(sig, [typeof(Component1024)], capacity: 4);
        for (var i = 0; i < 100; i++)
        {
            src.AddEntity(new Entity(i + 1, 1));
            src.SetComponentAtTyped(0, i, new Component1024 { Value = i + 100 });
        }
        Assert.False(src.IsChunked);

        var dst = new Archetype(sig, [typeof(Component1024)], capacity: 4096);
        dst.AllocateRows(100);
        for (var i = 0; i < 100; i++)
            dst.WriteEntityAt(i, new Entity(i + 10001, 1));
        dst.ForceChunkedForTesting();
        Assert.True(dst.IsChunked);

        dst.CopyColumnsFrom(src, 100);
        for (var i = 0; i < 100; i++)
            Assert.Equal(i + 100, dst.GetComponentAt<Component1024>(0, i).Value);
    }
```

**验证**：
```bash
dotnet test --filter "FullyQualifiedName~ArchetypeTests.CopyColumnsFrom"
```

---

## Task 11: RestoreFlatBackup round-trip 测试

**文件**：`tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`（已有 CaptureState/RestoreState 测试，在此追加）

### 11a: capture flat → promote → restore → verify

```csharp
    [Fact]
    public void Capture_nonchunked_promoted_during_prediction_restores_correctly()
    {
        var world = new World();
        // Create entities in a flat archetype.
        for (var i = 0; i < 100; i++)
        {
            var e = world.Create();
            world.Add(e, new Component128 { Value = i });
        }

        var snap = world.CaptureState();

        // Prediction: create enough entities to promote the archetype to chunked.
        for (var i = 0; i < 250; i++)
        {
            var e = world.Create();
            world.Add(e, new Component128 { Value = i + 1000 });
        }

        // Verify archetype was promoted.
        foreach (var arch in world.Archetypes)
           if (arch.EntityCount > 0)
               Assert.True(arch.IsChunked, "Archetype should be chunked after bulk create.");

        world.RestoreState(snap);

        // Verify restored state matches pre-prediction.
        Assert.Equal(100, world.EntityCount);
        foreach (var arch in world.Archetypes)
            Assert.Equal(100, arch.EntityCount);
    }

    private struct Component128 { public int Value; public long A; public long B; public long C; public long D; public long E; public long F; public long G; public long H; public long I; public long J; public long K; public long L; public long M; public long N; public long O; }
```

> 注：`Component128` 约 128 bytes → segCap ≈ 16384。200 entities 在 flat 模式，+250 = 450 → 可能仍在 flat 模式（capacity 从 4 double 到 512）。需要调整数量确保触发晋升。
>
> **修正**：用 `Component1024`（segCap=2048）替代，初始 entities=100（flat），预测 +4000 = 4100 > 2048 → 晋升。

**修改后的测试**：

```csharp
    [Fact]
    public void Capture_nonchunked_promoted_during_prediction_restores_correctly()
    {
        var world = new World();
        for (var i = 0; i < 100; i++)
        {
            var e = world.Create();
            world.Add(e, new Component1024 { Value = i });
        }

        var snap = world.CaptureState();

        // Prediction: add enough to promote Component1024 archetype (segCap=2048).
        for (var i = 0; i < 4000; i++)
        {
            var e = world.Create();
            world.Add(e, new Component1024 { Value = i + 1000 });
        }

        world.RestoreState(snap);
        Assert.Equal(100, world.EntityCount);
    }

    private unsafe struct Component1024 { public int Value; public fixed byte Pad[1020]; }
```

**验证**：
```bash
dotnet test --filter "FullyQualifiedName~WorldSnapshotTests.Capture_nonchunked_promoted_during_prediction_restores_correctly"
```

---

## Task 12: CopyFromChunked 池复用 edge case 测试

**文件**：`tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`

`CopyFromChunked` 的池复用 bug（`BUG_chunked_restore_pooled_larger_backup_arrays_overflow_smaller_destination`）已修复并转为回归测试。追加一个多次 capture/restore 循环测试。

### 12a: 多次 capture → restore 循环

```csharp
    [Fact]
    public void Capture_restore_cycle_twice_with_chunked_archetype_is_stable()
    {
        var world = new World();
        // Create enough entities with Component1024 to trigger chunked mode.
        for (var i = 0; i < 3000; i++)
        {
            var e = world.Create();
            world.Add(e, new Component1024 { Value = i });
        }

        var snap1 = world.CaptureState();

        // Modify world.
        for (var i = 0; i < 1000; i++)
        {
            var e = world.Create();
            world.Add(e, new Component1024 { Value = i + 9000 });
        }

        world.RestoreState(snap1);
        Assert.Equal(3000, world.EntityCount);

        var snap2 = world.CaptureState(); // pool reuse — backup arrays from snap1

        world.RestoreState(snap2); // restore from pool-recycled arrays
        Assert.Equal(3000, world.EntityCount);
    }
```

**验证**：
```bash
dotnet test --filter "FullyQualifiedName~WorldSnapshotTests.Capture_restore_cycle_twice_with_chunked_archetype_is_stable"
```

---

# Phase 4: QueryCache + Flat Cache 收尾

## Task 13: ExpectedViewShape 的 `-1` 哨兵显式化

**文件**：`src/MiniArch/Core/QueryCache.cs`

### 13a: 替换 line 277-278

旧：
```csharp
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ExpectedViewShape(Archetype archetype) =>
        archetype.IsChunked ? archetype.SegmentCount : -1;
```

新：
```csharp
    private const int NonChunkedShape = -1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ExpectedViewShape(Archetype archetype) =>
        archetype.IsChunked ? archetype.SegmentCount : NonChunkedShape;
```

### 13b: 更新 `_archetypeExpectedViews` 字段注释（line 33-37）

旧：
```csharp
    // Tracks expected view shape per archetype (indexed parallel to _snapshotArchetypes).
    // Non-chunked and chunked-with-one-segment both expose one view, but they
    // need different ChunkView segment indices (-1 vs 0). Encode the mode too.
    private int[] _archetypeExpectedViews = [];
```

新：
```csharp
    // Tracks expected view shape per archetype (indexed parallel to _snapshotArchetypes).
    // Non-chunked = NonChunkedShape (-1), chunked = SegmentCount.
    // Even when both have 1 view, the ChunkView segment index differs (-1 vs 0).
    private int[] _archetypeExpectedViews = [];
```

**验证**：`dotnet build` + `dotnet test --filter "FullyQualifiedName~QueryTests"` 全通过。

---

## Task 14: `_flatEntitiesGeneration` 递增覆盖审查 + DEBUG 守护

**纯审查任务**。以下 7 个递增点覆盖所有 layout 变更：

| 行号 | 方法 | 触发场景 |
|---|---|---|
| 110 | `GrowChunked` | 新 segment 加入 |
| 215 | `AllocateRows` chunked path | 预分配行更新 segment counts |
| 229 | `WriteEntityAt` chunked path | 写入 entity 到 segment |
| 293 | `RemoveAt` chunked（无需 swap） | 删除 entity 从末段 |
| 307 | `RemoveAt` chunked（需 swap） | 跨段 swap-remove |
| 739 | `RebuildFlatEntities` | 显式重建标记 |
| 825 | `RestoreFlatBackup` chunked | 从备份恢复覆盖 segment 数据 |

**验证**：每个递增点都是必要的——没有遗漏，也没有多余。记入 kb-chunk-storage.md。

### 14b: 添加 DEBUG 缓存一致性断言

在 `Archetype.Storage.cs` 中 `GetEntityStorageUnsafe` 方法（line 330）末尾的 return 之前添加：

```csharp
    [Conditional("DEBUG")]
    private void AssertFlatCacheConsistent()
    {
        if (!IsChunked || _cachedFlatEntities is null) return;
        if (_cachedFlatEntitiesGeneration != _flatEntitiesGeneration) return;
        var total = 0;
        for (var i = 0; i < _segmentCount; i++)
            total += _segments[i].Count;
        Debug.Assert(_cachedFlatEntities.Length >= total,
            $"Flat cache size {_cachedFlatEntities.Length} < total segment count {total}.");
    }
```

在 `GetEntityStorageUnsafe` 的 return 前调用：
```csharp
        AssertFlatCacheConsistent();
        return _cachedFlatEntities!;
```

**验证**：`dotnet test -c Debug` 全量通过（断言在 Debug 下生效）。

---

# Phase 5: 门禁 + 文档

## Task 15: 性能门禁

### 15a: 全量测试
```bash
dotnet test -c Release
```

### 15b: 回归门禁
```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

阈值：Movement >= 1210 rounds/s, Attack >= 767 rounds/s。若低于阈值 → 检查回归，必要时回退。

### 15c: 场景基准
```bash
dotnet run -c Release --project tools/perf/GameTickSim.Perf
```

---

## Task 16: 知识库更新

### 16a: `kb-chunk-storage.md`

更新以下段：
- **两种模式** 段：移除 `_isChunked` 字段引用，改为 `IsChunked => _segments is not null`
- **认知模型** 段：新增 `AllocateRows` 作为分配行号的统一入口
- **坑点** 段：删除旧坑点 `_isChunked re-check`（已不再是问题），新增 F+G 后的不变量说明

### 16b: `kb-code-review-findings.md`

更新真 bug 索引：
- `BUG_reserverows_deadlocks_*`：从未修复移到已修复
- `BUG_clone_deadlocks_*`：从未修复移到已修复
- `BUG_flat_entity_index_mismatches_*`：从未修复移到已修复
- 新增条目：Phase 3 RemoveAt 审查结论（所有调用方正确处理 RowIndex）
- 新增条目：Phase 3 CopyColumnFrom 4 模式验证
- 新增条目：Phase 4 `_flatEntitiesGeneration` 覆盖审查

### 16c: `kb-architecture-review.md`

- 双存储模式描述：更新 `_isChunked` → `IsChunked` 派生属性
- 新增 F+G 设计决策条目：为什么 `AddEntity` = `AllocateRows(1) + WriteEntityAt`

---

## 全部 Phase 完成标准

- [ ] Phase 1-5 全部 pass
- [ ] `dotnet test` MiniArch.Tests 全部通过
- [ ] `dotnet test` HeroPipeline.Tests 全部通过
- [ ] `dotnet run -c Release -- project tools/perf/HeroComing.Perf --check-baseline` 通过
- [ ] `rg "_isChunked" src/` 零结果
- [ ] 知识库文件更新完成
