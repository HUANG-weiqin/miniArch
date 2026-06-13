# Plan: InlineMap Zero-Allocation Overflow

## Goal

Eliminate per-map heap allocation from `InlineMap<TKey, TValue>` by replacing the per-map `(TKey, TValue)[]? Overflow` array with a shared linked-list node pool owned by `CommandBuffer`.

## Design

### 1. New struct: `OverflowPool<TKey, TValue>`

File: `src/MiniArch/Core/OverflowPool.cs` (new file)

A shared node pool using three parallel arrays (keys, values, next pointers) backed by `ArrayPool<T>`. Nodes form singly-linked lists.

```csharp
internal struct OverflowPool<TKey, TValue> where TKey : unmanaged
{
    private TKey[] _keys;
    private TValue[] _values;
    private int[] _next;
    private int _count;

    // Add node, return index. currentHead = previous head index.
    public int Add(TKey key, TValue value, int currentHead);

    // Walk list from head, return node index or -1.
    public int FindIndex(int head, TKey key);

    // Unlink node from list. Returns true if found.
    public bool Remove(ref int head, TKey key);

    // Direct access by node index.
    public ref TValue GetValue(int index);
    public ref readonly TValue GetValueReadonly(int index);
    public int GetNext(int index);

    // Reset count to 0 (does NOT return arrays).
    public void Clear();

    // Return arrays to ArrayPool (for frozen state cleanup).
    public void ReturnArrays();

    private void Grow(); // Rent larger arrays, copy, return old
}
```

Key decisions:
- Three parallel arrays instead of `(TKey, TValue, int)[]` to avoid tuple struct overhead
- `ArrayPool<T>` for internal arrays — enables proper cleanup in async path
- `Clear()` only resets `_count = 0` — arrays are reused across frames
- `ReturnArrays()` returns to ArrayPool — used when frozen state is cleaned up

### 2. Modify `InlineMap<TKey, TValue>`

File: `src/MiniArch/Core/InlineMap.cs`

Changes:
- **Remove**: `(TKey, TValue)[]? Overflow` field
- **Add**: `int OverflowHead` field (meaningless when `OverflowCount == 0`)
- **Keep**: `int OverflowCount` field
- **Remove**: `GetValueRef(ref map, index)` and `GetValueRefReadonly(in map, index)` static methods
- **Add**: `GetValueRefInline(ref map, index)` and `GetValueRefReadonlyInline(in map, index)` — inline-only access by index (0..7)
- **Modify**: `Set`, `TryGetValue`, `Remove`, `CopyTo` — add `ref OverflowPool<TKey, TValue> pool` parameter
- **Modify**: `Clear()` — reset `OverflowHead = 0`, `OverflowCount = 0` (no pool needed)
- **Keep**: `IsEmpty`, `TotalCount` properties unchanged

Iteration pattern change:
- Old: `for (j = 0; j < TotalCount; j++) GetValueRef(ref map, j)`
- New: inline loop `for (j = 0; j < Count; j++) GetValueRefInline(ref map, j)` + overflow walk `for (nodeIdx = OverflowHead; nodeIdx >= 0; nodeIdx = pool.GetNext(nodeIdx)) pool.GetValue(nodeIdx)`

### 3. Modify `CommandBuffer`

File: `src/MiniArch/Core/CommandBuffer.cs`

New fields:
```csharp
private OverflowPool<int, EntityOpSlot> _opsOverflow;
private OverflowPool<int, CreatedComponent> _createdOverflow;
```

Call site changes (all ~20 InlineMap method calls):
- `ops.Set(key, slot)` → `ops.Set(key, slot, ref _opsOverflow)`
- `state.Map.Set(key, val)` → `state.Map.Set(key, val, ref _createdOverflow)`
- `ops.Remove(key)` → `ops.Remove(key, ref _opsOverflow)`
- `state.Map.Remove(key)` → `state.Map.Remove(key, ref _createdOverflow)`
- `state.Map.CopyTo(list)` → `state.Map.CopyTo(list, ref _createdOverflow)`

Iteration changes (5 loop sites):
1. **Submit** (line ~591): split into inline + overflow loops
2. **SubmitFromFrozen** (line ~784): split into inline + overflow loops, use `frozen.OpsOverflow`
3. **BuildFromFrozen** (line ~880): split into inline + overflow loops, use `frozen.OpsOverflow`
4. **ExtractAndSortComponents** (line ~902): split into inline + overflow loops, accept pool parameter
5. **BuildDelta** (line ~1134): split into inline + overflow loops

Clear changes:
- Add `_opsOverflow.Clear()` and `_createdOverflow.Clear()` in `Clear()`

### 4. FrozenBufferState changes

New fields:
```csharp
public OverflowPool<int, EntityOpSlot> OpsOverflow;
public OverflowPool<int, CreatedComponent> CreatedOverflow;
```

SwapOutState:
- Capture current pools: `frozen.OpsOverflow = _opsOverflow;`
- Reset main buffer pools: `_opsOverflow = default;`

Async cleanup (ContinueWith in SubmitAndSnapshotAsync):
- Add `frozen.OpsOverflow.ReturnArrays();` and `frozen.CreatedOverflow.ReturnArrays();`

## Verification

1. `dotnet build` — compile clean
2. `dotnet test` — all existing tests pass
3. LeakRepro — verify zero GC growth from overflow
4. Benchmark — compare with baseline
