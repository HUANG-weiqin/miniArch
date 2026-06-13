# Flat Byte[] Chunk 存储重构

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 将 Chunk 的列存储从 `Array[]`（managed typed array per column）重构为单块 `byte[]` + offset 寻址，消除每次行迁移的 N 次 `Array.Copy(1)` 开销。

**Architecture:** Chunk 内部用一块连续 `byte[]` 存储所有列数据，按 `(columnByteOffset + row * elementSize)` 寻址。外部 API（World、CommandBuffer、Query）不变。核心收益：`CopySharedComponentsFrom` 和 `CopyRemovedRow` 从 N 次虚调用变成 N 次 `Unsafe.CopyBlockUnaligned`（内联 memcpy），且为未来单次 memcpy 整行铺路。

**Tech Stack:** C# unsafe code, `System.Runtime.CompilerServices.Unsafe`, `System.Runtime.InteropServices.MemoryMarshal`

---

## 背景：为什么需要重构

当前 `Chunk._columns: Array[]` 的瓶颈：

```
CopySharedComponentsFrom (每次 entity 迁移):
  for each component:
    Array.Copy(srcArray, srcRow, dstArray, dstRow, 1)
    // → 类型检查 + bounds check + 虚方法调用，每次复制 1 个元素

CopyRemovedRow (swap-remove):
  for each column:
    Array.Copy(column, last, column, row, 1)  // 同样问题
```

10K DenseExisting 场景：每个 entity 5 次 Add/Set → 5 次迁移 → 每次 ~10 次 Array.Copy(1) → 500K 次虚调用。

## 设计

### 新的 Chunk 内部布局

```
Chunk:
  _data: byte[]              // 一整块连续内存，存所有列
  _entities: Entity[]        // 不变
  _columnByteOffsets: int[]  // column[i] 在 _data 中的起始偏移
  _elementSizes: int[]       // column[i] 的元素 byte 大小
  _columnRequiresClear: bool[]  // 不变
  _componentIdToColumnIndex: int[]  // 不变

_data 内存布局 (capacity=4, 2 columns: Position(8B), Velocity(8B)):
  [PPPP|VVVV]
  offset 0:  Position[0..3]   (4 * 8 = 32 bytes)
  offset 32: Velocity[0..3]   (4 * 8 = 32 bytes)
  total: 64 bytes
```

每列偏移 = `sum(elementSize[j] * capacity, j=0..i-1)`。

### 寻址公式

```
ref T GetComponentRef<T>(int columnIndex, int row):
  offset = _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex]
  return ref Unsafe.As<byte, T>(ref _data[offset])
```

### 迁移复制

```csharp
void CopySharedComponentsFrom(Chunk source, int sourceRow, int destinationRow):
  for each component in dest._signature:
    if source has component:
      srcOff = source._columnByteOffsets[srcColIdx] + sourceRow * source._elementSizes[srcColIdx]
      dstOff = _columnByteOffsets[dstColIdx] + destinationRow * _elementSizes[dstColIdx]
      Unsafe.CopyBlockUnaligned(ref _data[dstOff], ref source._data[srcOff], (uint)_elementSizes[dstColIdx])
```

### Span 访问

```csharp
ReadOnlySpan<T> GetComponentSpanAt<T>(int columnIndex):
  offset = _columnByteOffsets[columnIndex]
  count = Count
  return MemoryMarshal.Cast<byte, T>(_data.AsSpan(offset, count * _elementSizes[columnIndex]))
```

### 需要改动的文件

| 文件 | 改动 | 大小 |
|---|---|---|
| `Chunk.cs` | 重写内部存储，~15 个方法 | **大** |
| `Archetype.cs` | 构造时传入 `elementSizes` | 小 |
| `WorldClone.cs` | `Columns` → 新 API | 小 |
| `WorldSnapshot.cs` | `Columns` → 新 API | 小 |
| `World.cs` | `chunk.Columns[i]` 的 raw writer 调用 → 新 API | 小 |
| `ComponentWriterCache.cs` | `ColumnWriterDelegate` 签名可能变 | 小 |
| `ChunkTests.cs` | `GetColumns` 反射测试 → 改用新 API | 小 |
| `ThroughputRunner.cs` | 无变化（用 public API） | 无 |
| `QueryIterators.cs` | 无变化（用 Chunk public 方法） | 无 |

### 不变的接口（Chunk 的 public/internal 方法签名）

以下方法签名**不变**，只改内部实现：

- `int Add(Entity, IReadOnlyDictionary<ComponentType, object?>)`
- `int Add(Entity)`
- `object? GetComponent(ComponentType, int row)`
- `T GetComponent<T>(ComponentType, int row)`
- `T GetComponentAt<T>(int columnIndex, int row)`
- `ReadOnlySpan<T> GetComponentSpan<T>(ComponentType)`
- `ReadOnlySpan<T> GetComponentSpanAt<T>(int columnIndex)`
- `ref T GetComponentRef<T>(int columnIndex)`
- `void SetComponent(ComponentType, int row, object?)`
- `void SetComponent<T>(ComponentType, int row, in T)`
- `void SetComponentAtTyped<T>(int columnIndex, int row, in T)`
- `void CopySharedComponentsFrom(Chunk, int srcRow, int dstRow)`
- `bool RemoveAt(int row, out Entity movedEntity)`
- `int ReserveRows(int count)`
- `Span<Entity> GetReservedEntities(int startRow, int count)`
- `bool TryGetComponentIndex / TryGetColumnIndices`
- `int[] GetComponentIdToColumnMap()`

**唯一需要变化的接口：**

- `Array[] Columns` 属性 → 需要重新考虑。外部使用者：
  1. `World.cs:1040,1063` — `columnWriter(chunk.Columns[columnIndex], row, source)` — ColumnWriterDelegate 接收 `Array`
  2. `WorldSnapshot.cs:231` — `codec.Write(writer, chunk.Columns[columnIndex], count)` — 写序列化
  3. `WorldSnapshot.cs:286` — `codec.Read(reader, chunk.Columns[runtimeColumnIndex], rowCount)` — 读序列化
  4. `WorldClone.cs:40-45` — `Array.Copy(srcCols[col], dstCols[col], count)` — clone 批量复制

### `Columns` 属性的处理方案

**保留 `Columns` 属性但改为 lazy 创建 `Array[]` view**（仅用于 snapshot/clone/raw writer）：

这是最安全的方案。`Columns` 很少被调用（只在 snapshot/clone 路径），热路径（query iteration、migration）全部走 `Unsafe.As` 直接寻址。

或者更简单：**给这些特殊调用者加几个专用方法**：

- `void WriteColumnRaw(int columnIndex, BinaryWriter writer, int count)` — 序列化用
- `void ReadColumnRaw(int columnIndex, BinaryReader reader, int count)` — 反序列化用
- `void CopyAllColumnsFrom(Chunk source, int count)` — clone 用
- `void WriteComponentRaw(int columnIndex, int row, byte* source, ColumnWriterDelegate writer)` — raw writer 用

这样 `Columns` 属性完全移除，所有外部访问都通过 Chunk 方法。

## 实施任务

### Task 1: 给 Chunk 添加 elementSize 基础设施

**Files:**
- Modify: `src/MiniArch/Core/Chunk.cs`

**Step 1: 添加新字段和修改构造函数**

在 Chunk 中添加 `_data`, `_columnByteOffsets`, `_elementSizes` 字段。构造函数接收 `int[] elementSizes` 参数。暂时**保留** `_columns` 字段（双写期），确保所有现有测试通过。

```csharp
// 新增字段
private readonly byte[] _data;
private readonly int[] _columnByteOffsets;
private readonly int[] _elementSizes;
```

构造函数变化：
- 接收 `int[] elementSizes`（每个列的 `Unsafe.SizeOf<T>()`）
- 计算 `_columnByteOffsets` = cumulative sum of `elementSize * capacity`
- 分配 `_data = new byte[totalBytes]`
- **保留 `_columns` 创建不变**（过渡期）

**Step 2: 运行测试确认无回归**

Run: `dotnet test -c Release`
Expected: 全部通过（双写，新字段不影响现有逻辑）

**Step 3: Commit**

```
feat(chunk): add flat byte[] storage fields alongside existing Array[] columns
```

---

### Task 2: 实现基于 `byte[]` 的核心读写方法

**Files:**
- Modify: `src/MiniArch/Core/Chunk.cs`

**Step 1: 实现 typed 读写方法（使用 `byte[]` 路径）**

重写以下方法的内部实现，从 `_data` + `_columnByteOffsets` + `_elementSizes` 寻址：

```csharp
internal ref T GetComponentRef<T>(int columnIndex)
{
    var offset = _columnByteOffsets[columnIndex];
    return ref Unsafe.As<byte, T>(ref _data[offset]);
}

internal void SetComponentAtTyped<T>(int columnIndex, int row, in T value)
{
    var offset = _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex];
    Unsafe.As<byte, T>(ref _data[offset]) = value;
}

internal T GetComponentAt<T>(int columnIndex, int row)
{
    var offset = _columnByteOffsets[columnIndex] + row * _elementSizes[columnIndex];
    return Unsafe.As<byte, T>(ref _data[offset]);
}

internal ReadOnlySpan<T> GetComponentSpanAt<T>(int columnIndex)
{
    var offset = _columnByteOffsets[columnIndex];
    var count = Count;
    return MemoryMarshal.Cast<byte, T>(_data.AsSpan(offset, count * _elementSizes[columnIndex]));
}
```

公共泛型方法也改为使用新路径：

```csharp
public T GetComponent<T>(ComponentType component, int row)
{
    ValidateRow(row);
    var columnIndex = GetComponentIndex(component);
    return GetComponentAt<T>(columnIndex, row);
}

public ReadOnlySpan<T> GetComponentSpan<T>(ComponentType component)
{
    var columnIndex = GetComponentIndex(component);
    return GetComponentSpanAt<T>(columnIndex);
}
```

**Step 2: 实现 boxed 读写方法**

`_columns` 仍保留，用于 boxed 路径（`GetComponent(object)`、`SetComponent(object)`、`Add(entity, components)`）。这些是冷路径（仅 CommandBuffer 的 boxed record 和 snapshot），后续 Task 处理。

**Step 3: 运行测试**

Run: `dotnet test -c Release`
Expected: 全部通过。typed 路径已切换到 `byte[]`，boxed 路径仍用 `_columns`。

**Step 4: Commit**

```
feat(chunk): implement typed read/write via flat byte[] storage
```

---

### Task 3: 重写迁移和删除的复制逻辑

**Files:**
- Modify: `src/MiniArch/Core/Chunk.cs`

**Step 1: 重写 `CopySharedComponentsFrom`**

```csharp
internal void CopySharedComponentsFrom(Chunk source, int sourceRow, int destinationRow)
{
    ArgumentNullException.ThrowIfNull(source);
    source.ValidateRow(sourceRow);
    ValidateRow(destinationRow);

    var components = _signature.AsSpan();
    for (var index = 0; index < components.Length; index++)
    {
        var component = components[index];
        if (!source.TryGetComponentIndex(component, out var sourceColumnIndex))
        {
            continue;
        }

        var size = (uint)_elementSizes[index];
        var srcOff = source._columnByteOffsets[sourceColumnIndex] + sourceRow * source._elementSizes[sourceColumnIndex];
        var dstOff = _columnByteOffsets[index] + destinationRow * size;
        Unsafe.CopyBlockUnaligned(ref _data[dstOff], ref source._data[srcOff], size);
    }
}
```

**Step 2: 重写 `CopyRemovedRow` 和 `ClearRemovedTail`**

```csharp
private void CopyRemovedRow(int row, int last)
{
    for (var index = 0; index < _columnByteOffsets.Length; index++)
    {
        var size = (uint)_elementSizes[index];
        var srcOff = _columnByteOffsets[index] + last * size;
        var dstOff = _columnByteOffsets[index] + row * size;
        Unsafe.CopyBlockUnaligned(ref _data[dstOff], ref _data[srcOff], size);
        if (_columnRequiresClear[index])
        {
            var clearOff = _columnByteOffsets[index] + last * size;
            Unsafe.InitBlockUnaligned(ref _data[clearOff], 0, size);
        }
    }
}

private void ClearRemovedTail(int last)
{
    for (var index = 0; index < _columnByteOffsets.Length; index++)
    {
        if (_columnRequiresClear[index])
        {
            var size = (uint)_elementSizes[index];
            var clearOff = _columnByteOffsets[index] + last * size;
            Unsafe.InitBlockUnaligned(ref _data[clearOff], 0, size);
        }
    }
}
```

**Step 3: 运行测试**

Run: `dotnet test -c Release`
Expected: 全部通过。迁移和删除路径已切换到 `Unsafe.CopyBlockUnaligned`。

**Step 4: Commit**

```
feat(chunk): rewrite migration and swap-remove with Unsafe.CopyBlockUnaligned
```

---

### Task 4: 修改 Archetype 构造链传入 elementSizes

**Files:**
- Modify: `src/MiniArch/Core/Archetype.cs`
- Modify: `src/MiniArch/Core/World.cs` (ResolveComponentTypes / GetOrCreateArchetype 区域)

**Step 1: Archetype 传递 elementSizes 到 Chunk**

在 `Archetype` 中：
- 新增 `_elementSizes: int[]` 字段
- 构造时从 `Type[]` 计算 `Unsafe.SizeOf<T>()`（或使用 `ComponentWriterCache.GetSize`）
- `CreateChunk()` 传递给 Chunk

在 `World.GetOrCreateArchetype` 中：
- `ResolveComponentTypes` 已经返回 `Type[]`
- 新增 `ResolveElementSizes(Type[])` 辅助方法，利用 `ComponentWriterCache.GetSize` 或直接 `Unsafe.SizeOf`

**Step 2: 运行测试**

Run: `dotnet test -c Release`
Expected: 全部通过。

**Step 3: Commit**

```
feat(archetype): pass element sizes to Chunk construction
```

---

### Task 5: 移除 `Array[] _columns`，处理外部依赖

**Files:**
- Modify: `src/MiniArch/Core/Chunk.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/WorldClone.cs`
- Modify: `src/MiniArch/Core/WorldSnapshot.cs`
- Modify: `src/MiniArch/Core/ComponentWriterCache.cs`

**Step 1: 给 Chunk 添加 raw 写入方法替代 `Columns` 访问**

```csharp
internal void WriteComponentRaw(int columnIndex, int row, byte* source, int elementSize)
{
    var offset = _columnByteOffsets[columnIndex] + row * elementSize;
    Unsafe.CopyBlockUnaligned(ref _data[offset], ref *source, (uint)elementSize);
}
```

**Step 2: 修改 World.cs 的 raw writer 调用点 (line 1040, 1063)**

当前：
```csharp
columnWriter(chunk.Columns[columnIndex], row, source);
```

改为：
```csharp
chunk.WriteComponentRaw(columnIndex, row, source, elementSize);
```

或保留 ColumnWriterDelegate 但改为接收 `byte[]` + offset 模式。需要评估哪种更干净。

**Step 3: 给 Chunk 添加序列化辅助方法**

```csharp
internal void WriteColumnTo(int columnIndex, BinaryWriter writer, int count)
{
    var offset = _columnByteOffsets[columnIndex];
    var size = _elementSizes[columnIndex];
    writer.Write(_data.AsSpan(offset, count * size));
}

internal void ReadColumnFrom(int columnIndex, BinaryReader reader, int count)
{
    var offset = _columnByteOffsets[columnIndex];
    var size = _elementSizes[columnIndex];
    reader.BaseStream.ReadExactly(_data.AsSpan(offset, count * size));
}
```

**Step 4: 修改 WorldSnapshot.cs 使用新方法**

Write 路径 (line 231):
```csharp
// 旧: GetColumnCodec(runtimeType).Write(writer, chunk.Columns[columnIndex], chunk.Count);
// 新: chunk.WriteColumnTo(columnIndex, writer, chunk.Count);
```

Read 路径 (line 286):
```csharp
// 旧: GetColumnCodec(runtimeType).Read(reader, chunk.Columns[runtimeColumnIndex], rowCount);
// 新: chunk.ReadColumnFrom(runtimeColumnIndex, reader, rowCount);
```

注意：当前 WorldSnapshot 的 ColumnCodec 做了 `where T : unmanaged` 约束的泛型序列化。改为 byte[] 直接读写后，可以绕过泛型 codec，但需要确保 byte 级别的序列化兼容。如果组件包含引用类型（当前已不支持 snapshot），此改动无影响。

**Step 5: 修改 WorldClone.cs 使用新方法**

```csharp
// 旧:
//   var srcCols = srcChunk.Columns;
//   var dstCols = dstChunk.Columns;
//   Array.Copy(srcCols[col], dstCols[col], srcChunk.Count);

// 新: 给 Chunk 添加 CopyAllColumnsFrom 方法
internal void CopyAllColumnsFrom(Chunk source, int count)
{
    Unsafe.CopyBlockUnaligned(ref _data[0], ref source._data[0], (uint)_data.Length);
}
```

或逐列复制（更安全）：
```csharp
for (var col = 0; col < _columnByteOffsets.Length; col++)
{
    var srcOff = source._columnByteOffsets[col];
    var dstOff = _columnByteOffsets[col];
    var size = (uint)(count * _elementSizes[col]);
    Unsafe.CopyBlockUnaligned(ref _data[dstOff], ref source._data[srcOff], size);
}
```

**Step 6: 移除 `_columns` 字段和 `Columns` 属性**

移除：
- `private readonly Array[] _columns;`
- `internal Array[] Columns => _columns;`
- `CreateColumns` 静态方法

保留 boxed 路径：`Add(Entity, IReadOnlyDictionary)` 和 `GetComponent(ComponentType, int row)` 和 `SetComponent(ComponentType, int row, object?)` 需要用 unsafe 从 `_data` 读/写 boxed 值。改法：

```csharp
internal object? GetComponent(ComponentType component, int row)
{
    ValidateRow(row);
    var columnIndex = GetComponentIndex(component);
    // 用 RuntimeHelpers.GetUninitializedObject + 内存复制
    // 或者保留一个 lazy 的 Array[] view（仅 boxed 路径用）
    // 最简方案：用 TypedUnsafeReadAsObject 辅助
}
```

**最简方案**：boxed 路径（`SetValue`/`GetValue`）改为用 `Unsafe.As<byte, T>(ref _data[offset])` 拿 ref，然后装箱。但 Chunk 不知道 T。需要传入 `Type` 信息或在 boxed 方法中使用 `RuntimeHelpers.GetObjectValue`。

实际最简：给 Chunk 加一个 `Type[] _componentTypes` 字段（构造时已有），boxed 方法里用 reflection 或 cached delegate 做 read/write。但这引入开销。

**推荐方案**：这些 boxed 方法只在 CommandBuffer record 阶段使用（冷路径），保留 `_columns` 字段**仅用于 boxed 路径**，但 `CopySharedComponentsFrom`、`CopyRemovedRow`、typed 读写全部走 `byte[]`。这样改动最小，boxed 路径零风险。

最终决定：**保留 `_columns` 仅用于 boxed 路径（Add with dictionary, GetComponent boxed, SetComponent boxed），其他全部走 `byte[]`**。

**Step 7: 运行测试**

Run: `dotnet test -c Release`
Expected: 全部通过。

**Step 8: Commit**

```
refactor(chunk): remove Columns property, use flat byte[] for typed paths
```

---

### Task 6: 更新测试

**Files:**
- Modify: `tests/MiniArch.Tests/Core/ChunkTests.cs`

**Step 1: 修改 `GetColumns` 反射测试**

测试中 `GetColumns` 通过反射获取 `_columns`。如果保留 `_columns` 则测试不变。如果移除，则改为使用 Chunk 的 public typed API 验证。

**Step 2: 运行全部测试**

Run: `dotnet test -c Release`
Expected: 全部通过。

**Step 3: Commit**

```
test(chunk): update tests for flat byte[] storage
```

---

### Task 7: 吞吐量验证

**Files:**
- 无代码改动

**Step 1: 运行 CommandBuffer 吞吐 benchmark**

Run: `dotnet run -c Release -- -f "*CommandBufferBenchmarks*"`
工作目录: `benchmarks/MiniArch.Benchmarks`

**Step 2: 运行 ThroughputRunner**

Run: `dotnet run -c Release` in ThroughputRunner 项目（如果有独立项目）

**Step 3: 对比基准**

对比 Task 7 与重构前的数据。预期：
- DenseExisting: 减少 10-20%（消除了 Array.Copy 虚调用开销）
- CreateHeavy: 可能变化不大（新建 entity 迁移少）
- MixedScript: 介于两者之间

如果提升不显著，profiler 找下一个瓶颈。

---

## 风险与注意事项

1. **对齐**：`byte[]` 起始在 x64 .NET 上 8/16 字节对齐。各列 offset 需确保 `sizeof(T)` 对齐。方案：各列 offset 按 `sizeof(T)` 自然对齐（因为 cumulative sum of `elementSize * capacity`，如果 capacity 是 2 的幂且 elementSize 是 2 的幂，自然对齐）。如果不放心，加 padding。

2. **引用类型组件**：`byte[]` 不能直接存引用类型。当前 ECS 的 snapshot/clone 只支持 unmanaged 类型。boxed 路径（`_columns`）继续处理引用类型。`_columnRequiresClear` 逻辑不变。

3. **Span 对齐**：`MemoryMarshal.Cast<byte, T>` 要求 `byte[]` 中的 offset 按 `sizeof(T)` 对齐。同第 1 点。

4. **双写期**：Task 2-3 期间 typed 路径写 `_data`，boxed 路径写 `_columns`。两边会不一致。解决：**Task 2-3 中双写**（typed 写同时更新 `_data` 和 `_columns`），或者分阶段：先只改读路径，再改写路径。最终移除 `_columns` 的写路径。
