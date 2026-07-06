# Record-time 虚拟状态 Clone Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 重新设计 CommandStream.Clone，在 record 调用那一刻读取 source 的完整逻辑状态（组件+子树）深克隆，统一 materialized/pending source 语义为"调用时刻快照"。

**Architecture:** record-time 构造虚拟组件状态（materialized = archetype + component store overlay；pending = batch 链表）+ 虚拟 hierarchy view（world hierarchy + HierarchyByChild intent overlay），Clone 在虚拟视图上展开成标准命令。单一机制，零 wire format 变更，三条消费路径统一。

**Tech Stack:** C# .NET 8, MiniArch ECS, xUnit

---

## Task 1: 允许 pending source + 翻转现有测试

CloneCore 目前只接受 materialized source（`_world.TryGetLocation`，:269）。**即使 pending source（同批次 Create 后再 Clone）也应该允许**。同时翻转现有反向测试的预期。

### Step 1.1: 写测试 `Clone_reflects_pending_source_remove_in_same_buffer`（翻转旧测试）

**文件**: `tests/MiniArch.Tests/Core/EntityCloneTests.cs`，替换 `Clone_ignores_pending_source_remove_in_same_buffer`（:502）

旧测试名"ignores...remove"与实际语义相反。新预期：clone **反映** pending Remove，clone 后不含 Position。

```csharp
[Fact]
public void Clone_reflects_pending_source_remove_in_same_buffer()
{
    var world = new World();
    var source = world.Create(new Position(1, 2), new Velocity(3, 4));
    var buffer = new CommandStream(world);

    buffer.Remove<Position>(source);
    var clone = buffer.Clone(source);
    buffer.Submit();

    Assert.False(world.TryGet<Position>(source, out _));
    // CLONE BEHAVIOR FLIPPED: clone no longer has Position
    Assert.False(world.TryGet<Position>(clone, out _));
    Assert.True(world.TryGet(clone, out Velocity _));
}
```

### Step 1.2: 运行测试验证失败

```bash
dotnet test -c Release --filter "Clone_reflects_pending_source_remove_in_same_buffer"
```

预期：`Assert.False` 失败，因为 clone 仍包含 Position（从 archetype 读取）。

### Step 1.3: 实现 materialized overlay scan

**核心变更**: 在 `CloneComponents`（:307）中，读取 archetype base 后，**扫描所有 component store 找 source 的 overlay entry**，应用 last-wins 合并。

**思路**: 源实体是 materialized（`TryGetLocation` 成功）。archetype 提供 base 组件列表。但在同批次 record 中 `Remove<T>(source)` / `Set<T>(source)` 进了 `ComponentStore<T>`。Clone 时需要 overlay 这些 store entries。

**具体算法**（修改 `CloneComponents`）:

1. 从 archetype 枚举所有组件类型 → 读入一个临时数组 `(ComponentType, offset, size)`（现有代码片段 :313-324，但改为收集到临时列表而非直接 commit）
2. 遍历 `_frozen.Stores`（`ComponentStore?[]`），对每个非 null store：
   - 调用 store 的扫描方法找 source entity 对应的 entries
   - 对每个匹配的 entry：
     - `KindRemove` → 从临时列表移除该 ComponentType（找到并标记跳过）
     - `KindAdd` / `KindSet` → 替换临时列表中该 ComponentType 的 offset/size（如果列表中有），或新增（如果列表中没有）
3. 将合并后的临时列表 commit 到 clone 的 batch buffer
4. 调用 `CloneChildrenRecursive`

**需要新增的抽象方法**（在 `ComponentStore` 基类 :2047）：

```csharp
public abstract void ForEachEntityEntry(Entity entity, ref OverlayCollector collector);
```

其中 `OverlayCollector` 是 ref struct（也可用 `Span<Action>` 回调模式）。简单起见：使用 `List<OverlayEntry>` + 返回值。

**`ComponentStore<T>` 实现**:

```csharp
public override void ForEachEntityEntry(Entity entity, ref OverlayCollector collector)
{
    for (var i = 0; i < _count; i++)
    {
        ref var entry = ref _entries[i];
        if (entry.Entity == entity)
        {
            collector.Add(Component<T>.ComponentType, entry.Kind,
                MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref entry.Value)));
        }
    }
}
```

**`OverlayCollector`** 放在 `CommandStreamCore` 中作为 `private` struct，持有 `(ComponentType type, int offset, int size, bool skip)[]` 列表。

**复用**: `ReserveBatchBufSpace`（:1380）、`CommitBatchComponent`（:1390）——现有方法可以直接用于写入合并结果。

### Step 1.4: CloneCore 允许 pending source

在 `CloneCore`（:269）中，在 `TryGetLocation` 之前加 `TryGetPendingBatch` 检查：

```csharp
protected Entity CloneCore(Entity source)
{
    // 1. Check if source is a pending entity (created in same buffer)
    if (TryGetPendingBatch(source, out var srcBatchIdx))
        return ClonePendingSource(source, srcBatchIdx);

    // 2. Fall through to materialized path
    if (!_world.TryGetLocation(source, out var location))
        throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

    return CloneImpl(source, location);
}
```

**`ClonePendingSource`**（新增方法）:

```csharp
private Entity ClonePendingSource(Entity source, int srcBatchIdx)
{
    // Create clone entity (same deferred mode as source)
    var clone = _deferredEntities ? CreateDeferredImpl() : CreateImpl();
    var cloneBatchIdx = clone.Id >= 0
        ? _frozen.PendingBatch[clone.Id]
        : _pendingBatchDeferredArr[clone.Version];

    // Copy components from source's batch chain into clone's batch
    CopyComponentsFromBatch(source, srcBatchIdx, clone, cloneBatchIdx);

    // TODO (Task 4/5): virtual hierarchy for children
    // For now, no hierarchy on pending source

    return clone;
}
```

**`CopyComponentsFromBatch`**（新增方法）:

复用 `MaterializeFromBatchBuffer`（:930）的链表遍历 + last-wins 去重 + `Removed` 跳过逻辑，但输出改为 **写入 clone 的 batch buffer**（而非 materialize 到 world）。

```csharp
private void CopyComponentsFromBatch(Entity source, int srcBatchIdx, Entity clone, int cloneBatchIdx)
{
    var head = _frozen.BatchHeads[srcBatchIdx];
    // Same bit-dedup + last-wins traversal as MaterializeFromBatchBuffer (lines 943-977)
    // but instead of MaterializeReservedEntityRaw, for each deduped component call:
    //   ReserveBatchBufSpace + CommitBatchComponent(cloneBatchIdx, ...)
}
```

### Step 1.5: 运行测试验证通过

```bash
dotnet test -c Release --filter "Clone_reflects_pending_source_remove_in_same_buffer"
```

预期：全部通过。

### Step 1.6: Commit

```bash
git add -A && git commit -m "feat(clone): allow pending source + materialized overlay scan

- Flip Clone_ignores_pending_source_remove_in_same_buffer expect clone reflects Remove
- Add ForEachEntityEntry abstract method to ComponentStore base
- Implement overlay scan in CloneComponents: archetype base + component store entries
- Allow pending source in CloneCore via TryGetPendingBatch + CopyComponentsFromBatch
- Add ClonePendingSource for pending-entity clone path"
```

---

## Task 2: 虚拟组件状态 — pending source 的组件合并

Task 1 添加了 `CopyComponentsFromBatch`，但 pending source 的 Clone 还缺少：
1. 正确处理 pending Remove（已在 `Removed` 标志跳过后覆盖）
2. 同类型多次操作（Add 后 Set 或 Remove 后 Add）的 last-wins 归约
3. 对 materialized source 的 pending Add/Set/Remove（source 是 existing entity，pending 操作也进了 store）—— Task 1 的 overlay scan 已覆盖

本 Task 聚焦为 pending source 增加更完整的组件合并测试。

### Step 2.1: 写测试 `Clone_pending_source_sees_last_write_win`

```csharp
[Fact]
public void Clone_pending_source_sees_last_write_win()
{
    var world = new World();
    var buffer = new CommandStream(world);

    var source = buffer.Create();
    buffer.Add(source, new Position(1, 2));
    buffer.Set(source, new Position(3, 4));  // overwrite

    var clone = buffer.Clone(source);
    buffer.Submit();

    Assert.True(world.TryGet(clone, out Position pos));
    Assert.Equal(new Position(3, 4), pos);  // last-wins
}
```

**文件**: `EntityCloneTests.cs`（接在 Task 1 翻转测试之后）

### Step 2.2: 运行测试验证失败

```bash
dotnet test -c Release --filter "Clone_pending_source_sees_last_write_win"
```

预期：失败（现在 pending source clone 可能只复制了 Add 或 Set，或 last-wins 不正确）。

### Step 2.3: 确保 `CopyComponentsFromBatch` 正确 last-wins

**关键代码**: 确认 `CopyComponentsFromBatch` 的链表遍历逻辑与 `MaterializeFromBatchBuffer`（:943-977）一致：

- 使用位掩码（`b0`-`b7`）对小 id（<512）去重，对 >=512 的线性扫描 + 前一次写覆盖后一次
- 跳过 `comp.Removed == true` 的条目
- 链表顺序：`Next` 指针指向链表方向——**最新写入的在链表头部**（`CommitBatchComponent` 的 prepend 逻辑 :1398）。遍历是按链表顺序（head → ... → tail），所以 first-wins？不对。要确认遍历顺序下的 last-wins 语义。

**检查**: `MaterializeFromBatchBuffer` 的遍历方向是 `current = headIdx; while(current >= 0) { ... current = comp.Next; }`。因为 `CommitBatchComponent` 是 prepend（:1398 `Next = _frozen.BatchHeads[batchIdx]`），所以 **head 是最新条目**。遍历从 head 开始，遇到组件类型时位掩码 `TrySetBit`——**第一次遇到（最新的）保留，后续跳过**。因此语义是 last-wins。✅

**`CopyComponentsFromBatch` 必须使用同样的位掩码逻辑**。确保已实现。

### Step 2.4: 写测试 `Clone_pending_source_remove_component`

```csharp
[Fact]
public void Clone_pending_source_remove_component()
{
    var world = new World();
    var buffer = new CommandStream(world);

    var source = buffer.Create();
    buffer.Add(source, new Position(1, 2));
    buffer.Add(source, new Velocity(3, 4));
    buffer.Remove<Position>(source);

    var clone = buffer.Clone(source);
    buffer.Submit();

    Assert.False(world.TryGet<Position>(clone, out _));
    Assert.True(world.TryGet(clone, out Velocity _));
}
```

### Step 2.5: 运行测试验证通过

```bash
dotnet test -c Release --filter "Clone_pending_source_remove_component"
```

### Step 2.6: Commit

```bash
git add -A && git commit -m "feat(clone): pending source component merge with last-wins

- CopyComponentsFromBatch uses MaterializeFromBatchBuffer dedup+last-wins
- Tests: Clone_pending_source_sees_last_write_win
- Tests: Clone_pending_source_remove_component"
```

---

## Task 3: Destroy 检测 — 同批次先 Destroy 再 Clone 抛错

design doc 规定："同批次先 Destroy(source) 再 Clone(source) → 抛错"。

### Step 3.1: 写测试 `Clone_throws_after_destroy_in_same_buffer`

两种情况都需要覆盖：

```csharp
[Fact]
public void Clone_throws_after_destroy_in_same_buffer()
{
    var world = new World();
    var source = world.Create(new Position(1, 2));
    var buffer = new CommandStream(world);

    buffer.Destroy(source);
    Assert.Throws<InvalidOperationException>(() => buffer.Clone(source));
}

[Fact]
public void Clone_throws_after_destroy_pending_source_in_same_buffer()
{
    var world = new World();
    var buffer = new CommandStream(world);

    var source = buffer.Create();
    buffer.Add(source, new Position(1, 2));
    buffer.Destroy(source);
    Assert.Throws<InvalidOperationException>(() => buffer.Clone(source));
}
```

### Step 3.2: 运行测试验证失败

```bash
dotnet test -c Release --filter "Clone_throws_after_destroy_in_same_buffer|Clone_throws_after_destroy_pending_source_in_same_buffer"
```

预期：失败（当前 Clone 通过 `TryGetLocation` 但 source 在 DestroyEntities 中，或通过 `TryGetPendingBatch` 但 batch 已被 cancel）。

### Step 3.3: 实现 Destroy 检测

在 `CloneCore`（:269）中，在 TryGetPendingBatch 和 TryGetLocation 之前添加检测：

```csharp
protected Entity CloneCore(Entity source)
{
    // Destroy detection FIRST: if source is in DestroyEntities or its batch is canceled
    if (IsSourceDestroyedThisFrame(source))
        throw new InvalidOperationException(
            $"Cannot clone entity {source}: it was destroyed in the same batch.");

    // ... rest of CloneCore
}
```

**`IsSourceDestroyedThisFrame`**:

复用 `IsDestroyedThisFrame`（:1121）的逻辑，加入 pending batch 检测：

```csharp
private bool IsSourceDestroyedThisFrame(Entity source)
{
    // Check DestroyEntities[] (materialized Destroy)
    for (var i = 0; i < _frozen.DestroyCount; i++)
        if (_frozen.DestroyEntities[i] == source) return true;

    // Check if pending batch was canceled (pending Destroy)
    if (TryGetPendingBatch(source, out var batchIdx))
        return _frozen.BatchCanceled[batchIdx];

    return false;
}
```

### Step 3.4: 运行测试验证通过

```bash
dotnet test -c Release --filter "Clone_throws_after_destroy_in_same_buffer|Clone_throws_after_destroy_pending_source_in_same_buffer"
```

### Step 3.5: Commit

```bash
git add -A && git commit -m "feat(clone): destroy detection rejects same-buffer Destroy+Clone

- Add IsSourceDestroyedThisFrame helper
- Reject Clone when source is in DestroyEntities or pending batch is canceled
- Tests: Clone_throws_after_destroy_in_same_buffer (materialized + pending)"
```

---

## Task 4: 虚拟 hierarchy view — 反向索引 + children 合并

当前 `CloneChildrenRecursive`（:1513）只读 world hierarchy（`_world.Hierarchy.EnumerateChildren`），忽略 `HierarchyByChild` 中的 pending AddChild/RemoveChild intent。

### Step 4.1: 写测试 — pending AddChild 在 Clone 中生效

```csharp
[Fact]
public void Clone_virtual_hierarchy_pending_AddChild_materialized_child()
{
    var world = new World();
    var father = world.Create(new Position(1, 2));       // pending source
    var son = world.Create(new Health(100));              // materialized existing
    var buffer = new CommandStream(world);

    buffer.AddChild(father, son);
    var cloneFather = buffer.Clone(father);               // record-time snapshot
    buffer.Submit();

    // cloneFather should have a child (the clone of son)
    var children = world.EnumerateChildren(cloneFather).ToChildList();
    Assert.Single(children);
    Assert.True(world.TryGet(children[0], out Health _));
    // original son still has father as parent (not moved)
    Assert.True(world.TryGetParent(son, out var origParent));
    Assert.Equal(father, origParent);
}
```

**设计 doc "死胡同化解"场景验证**: pending father + AddChild(father, materialized son) → Clone 后 son2 是 cloneFather 的孩子，son 仍是 father 的孩子。

### Step 4.2: 运行测试验证失败

```bash
dotnet test -c Release --filter "Clone_virtual_hierarchy_pending_AddChild_materialized_child"
```

预期：失败（当前 CloneChildrenRecursive 读不到 pending AddChild）。

### Step 4.3: 构建虚拟 hierarchy view + 替换 CloneChildrenRecursive

**核心变更**: 用 `VirtualHierarchyView` 替换 `CloneChildrenRecursive` 中的 `_world.Hierarchy.EnumerateChildren`。

**新增方法 `GetVirtualChildren`**:

```csharp
private List<Entity> GetVirtualChildren(Entity parent)
{
    // Build result on each call (no caching — hierarchy intent count is typically small)
    var children = new List<Entity>();

    // 1. World real children
    if (_world.Hierarchy.HasChildren(_world, parent))
    {
        foreach (var child in _world.Hierarchy.EnumerateChildren(_world, parent))
            children.Add(child);
    }

    // 2. pending AddChild: HierarchyByChild entries where IsAdd && Parent == parent
    foreach (var (child, intent) in _frozen.HierarchyByChild)
    {
        if (intent.IsAdd && intent.Parent == parent)
        {
            if (!children.Contains(child))  // avoid duplicate from world children
                children.Add(child);
        }
    }

    // 3. pending RemoveChild: HierarchyByChild entries where !IsAdd
    foreach (var (child, intent) in _frozen.HierarchyByChild)
    {
        if (!intent.IsAdd && children.Remove(child))
        {
            // removed
        }
    }

    return children;
}
```

**替换 `CloneChildrenRecursive`** 中的 `.EnumerateChildren` 调用点：

- :1523 处：`foreach (var child in _world.Hierarchy.EnumerateChildren(_world, sourceRoot))` → `foreach (var child in GetVirtualChildren(sourceRoot))`
- :1564 处：`foreach (var grandChild in _world.Hierarchy.EnumerateChildren(_world, srcChild))` → `foreach (var grandChild in GetVirtualChildren(srcChild))`

**性能说明**: `GetVirtualChildren` 每次调用遍历 `HierarchyByChild`（典型大小：同批次 pending entity 数）。这是 O(帧内 hierarchy intent) 成本，只在 clone 路径触发，不影响 Add/Set/Remove/Submit。

### Step 4.4: 运行测试验证通过

```bash
dotnet test -c Release --filter "Clone_virtual_hierarchy_pending_AddChild_materialized_child"
```

### Step 4.5: 写 hierarchy 相关测试矩阵覆盖

```csharp
[Fact]
public void Clone_virtual_hierarchy_RemoveChild_excludes_from_children()
{
    var world = new World();
    var parent = world.Create(new Position(1, 2));
    var child = world.Create(new Health(100));
    world.AddChild(parent, child);
    var buffer = new CommandStream(world);

    buffer.RemoveChild(child);
    var clone = buffer.Clone(parent);
    buffer.Submit();

    // clone should have NO children (RemoveChild was pending)
    Assert.False(world.HasChildren(clone));
}

[Fact]
public void Clone_virtual_hierarchy_pending_AddChild_pending_child()
{
    var world = new World();
    var buffer = new CommandStream(world);

    var father = buffer.Create();
    var son = buffer.Create();
    buffer.Add(son, new Health(50));
    buffer.AddChild(father, son);

    var cloneFather = buffer.Clone(father);
    buffer.Submit();

    // cloneFather has a child (deep clone of son)
    var children = world.EnumerateChildren(cloneFather).ToChildList();
    Assert.Single(children);
    Assert.True(world.TryGet(children[0], out Health h));
    Assert.Equal(new Health(50), h);
}
```

### Step 4.6: 运行全部 hierarchy 测试

```bash
dotnet test -c Release --filter "Clone_virtual_hierarchy"
```

### Step 4.7: Commit

```bash
git add -A && git commit -m "feat(clone): virtual hierarchy view for pending AddChild/RemoveChild

- Replace CloneChildrenRecursive's world.EnumerateChildren with GetVirtualChildren
- Virtual children = world children + pending AddChild - pending RemoveChild
- Tests: pending AddChild (materialized child, pending child)
- Tests: RemoveChild exclusion"
```

---

## Task 5: 虚拟 hierarchy 防环检测 + 递归深克隆完善

`GetVirtualChildren` 可能构造出环（pending intent 在 submit 前不会被 world 拒绝）。需要防环。

### Step 5.1: 写测试 — 虚拟环检测

```csharp
[Fact]
public void Clone_virtual_hierarchy_cycle_throws()
{
    var world = new World();
    var buffer = new CommandStream(world);

    var a = buffer.Create();
    var b = buffer.Create();
    buffer.AddChild(a, b);
    // Create a cycle: b's parent = a (already), and a's parent = b
    buffer.AddChild(b, a);

    Assert.Throws<InvalidOperationException>(() => buffer.Clone(a));
}
```

### Step 5.2: 运行测试验证失败

```bash
dotnet test -c Release --filter "Clone_virtual_hierarchy_cycle_throws"
```

### Step 5.3: 实现防环检测

在 `CloneChildrenRecursive`（或新方法 `CloneVirtualRecursive`）中增加 `HashSet<Entity>`：

```csharp
private void CloneVirtualRecursive(Entity sourceRoot, Entity cloneRoot)
{
    var visited = new HashSet<Entity>();  // pool-friendly? Yes, see below
    var stack = ArrayPool<Entity>.Shared.Rent(32);
    var cloneStack = ArrayPool<Entity>.Shared.Rent(32);
    var stackCount = 0;

    try
    {
        foreach (var child in GetVirtualChildren(sourceRoot))
        {
            if (!visited.Add(child))
                throw new InvalidOperationException(
                    $"Clone detected a cycle in the virtual hierarchy at entity {child}.");
            // ... existing stack push logic from CloneChildrenRecursive
        }
        // ... existing BFS loop, with visited check before each push
    }
    finally { ... }
}
```

**性能注意**: `HashSet<Entity>` 在 clone 路径临时分配，避免用 pool（Entity 是小 struct，分配 cost 远小于 clone 整体开销）。如果需要优化，可用 `ArrayPool<int>` 按 id 做 cheap visited-set。

**更轻量的 visited 检测**: 利用 `pending batch` 数量少的特点，用 `List<Entity>` + 线性扫描（同 GetVirtualChildren 风格）。典型场景下 pending entity 数 < 64，线性扫描足够。

选用方案：用 `List<Entity> _visited`（方法局部，复用 `ArrayPool<Entity>` 租用），线性 `Contains` 检查。

### Step 5.4: 运行测试验证通过

```bash
dotnet test -c Release --filter "Clone_virtual_hierarchy_cycle_throws"
```

### Step 5.5: ParallelCommandStream.Clone — destroy 检测 + pending source 限制

`ParallelCommandStream.Clone`（:172）当前在 lock 外验证 `TryGetLocation`，在 lock 内调 `CloneImpl`。

**Design doc 明确**：第一版只实现单线程 CommandStream 的完整虚拟状态语义。ParallelCommandStream 的 component store 用 ThreadLocal 锁外 append，clone-time snapshot 不保证看到并发写。因此 **ParallelCommandStream.Clone 不支持 pending source**，只保留 materialized source 路径 + 加 destroy 检测。

```csharp
public Entity Clone(Entity source)
{
    // Destroy detection (read-only, safe outside lock)
    if (IsSourceDestroyedThisFrame(source))
        throw new InvalidOperationException(
            $"Cannot clone entity {source}: it was destroyed in the same batch.");

    // pending source NOT supported in parallel mode — component store uses
    // ThreadLocal append, clone-time snapshot cannot see concurrent writes reliably.
    if (TryGetPendingBatch(source, out _))
        throw new NotSupportedException(
            "ParallelCommandStream.Clone does not support pending source. " +
            "Use single-threaded CommandStream for pending-entity cloning, " +
            "or Submit first then clone the materialized result.");

    if (!_world.TryGetLocation(source, out var location))
        throw new InvalidOperationException($"Cannot clone entity {source}: it is no longer alive.");

    lock (_storeCreateLock)
    {
        return CloneImpl(source, location);
    }
}
```

**注意**：materialized source 的 overlay scan（Task 1 的 ComponentStore 扫描）在 parallel 模式下同样有 ThreadLocal 可见性问题。保守起见，ParallelCommandStream.Clone 的 materialized 路径**也只读 archetype storage**（不扫 component store overlay），保持与旧行为一致。虚拟状态语义只在单线程 CommandStream 生效。此限制写入 XML doc 注释。

### Step 5.6: Commit

```bash
git add -A && git commit -m "feat(clone): cycle detection in virtual hierarchy

- Add visited-set cycle detection in CloneVirtualRecursive
- Update ParallelCommandStream.Clone for pending source
- Test: Clone_virtual_hierarchy_cycle_throws"
```

---

## Task 6: 跨路径收敛测试 + 快照语义验证

Submit vs Snapshot+Replay 必须收敛。Clone 是标准命令展开，不应该分歧。

### Step 6.1: 写 Submit vs Replay 收敛测试

```csharp
[Fact]
public void Clone_pending_source_submit_equals_replay()
{
    var world1 = new World();
    var buffer1 = new CommandStream(world1);

    var src = buffer1.Create();
    buffer1.Add(src, new Position(1, 2));
    buffer1.Clone(src);
    buffer1.Submit();

    // Replay path
    var world2 = new World();
    var buffer2 = new CommandStream(world2) { DeferredEntities = true };
    var src2 = buffer2.Create();
    buffer2.Add(src2, new Position(1, 2));
    buffer2.Clone(src2);
    var delta = buffer2.Snapshot();
    buffer2.Clear();

    world2.Replay(delta);

    // Both worlds should have exactly 2 entities with Position
    var q1 = world1.CountEntities(new QueryDescription().With<Position>());
    var q2 = world2.CountEntities(new QueryDescription().With<Position>());
    Assert.Equal(2, q1);
    Assert.Equal(q1, q2);
}
```

### Step 6.2: 验证

```bash
dotnet test -c Release --filter "Clone_pending_source_submit_equals_replay"
```

### Step 6.3: 写 materialized overlay 的 Snapshot+Replay 收敛

```csharp
[Fact]
public void Clone_materialized_overlay_submit_equals_replay()
{
    var world1 = new World();
    var src1 = world1.Create(new Position(1, 2), new Velocity(3, 4));
    var buffer1 = new CommandStream(world1);
    buffer1.Remove<Position>(src1);
    buffer1.Clone(src1);
    buffer1.Submit();

    var world2 = new World();
    var src2 = world2.Create(new Position(1, 2), new Velocity(3, 4));
    var buffer2 = new CommandStream(world2) { DeferredEntities = true };
    buffer2.Remove<Position>(src2);
    buffer2.Clone(src2);
    var delta = buffer2.Snapshot();
    buffer2.Clear();
    world2.Replay(delta);

    // Both worlds: source with Velocity only, clone with Velocity only
    var q1v = world1.CountEntities(new QueryDescription().With<Velocity>());
    var q2v = world2.CountEntities(new QueryDescription().With<Velocity>());
    Assert.Equal(2, q1v);
    Assert.Equal(q1v, q2v);

    var q1p = world1.CountEntities(new QueryDescription().With<Position>());
    var q2p = world2.CountEntities(new QueryDescription().With<Position>());
    Assert.Equal(0, q1p);
    Assert.Equal(q1p, q2p);
}
```

**注意**: 需要 `CountEntities` helper——在 EntityCloneTests.cs 中已定义（:550）。

### Step 6.4: 验证

```bash
dotnet test -c Release --filter "Clone_materialized_overlay_submit_equals_replay"
```

### Step 6.5: 添加 `KnownLimitationTests` — ParallelCommandStream 限制文档

在 ParallelCommandStream.xml doc 注释和知识页中明确：

> **Parallel recording 中同一 source 的 Clone 与并发组件写冲突由用户避免**。第一版只实现单线程 CommandStream 的完整语义。

写测试（预期行为由用户保证，但记录当前限制）：

```csharp
[Fact]
public void Parallel_clone_pending_source_limitation_documented()
{
    // NOTE: ParallelCommandStream 不支持 pending source 的 Clone。
    // 当前 ParallelCommandStream.Clone 只接受 materialized source。
    // 原因是 pending batch 链表在并行模式下没有 ThreadLocal 保护，
    // 多线程同时 append 到同一 batch 会导致不可预测的 last-wins。
    // 此限制记录在 ParallelCommandStream 文档中。
    var world = new World();
    var buffer = new ParallelCommandStream(world);
    var source = world.Create(new Position(1, 2));
    var clone = buffer.Clone(source);  // materialized, should work
    buffer.Submit();
    Assert.True(world.IsAlive(clone));
}
```

### Step 6.6: 运行所有 Clone 测试确保无回归

```bash
dotnet test -c Release --filter "EntityCloneTests|CommandBufferCloneTests"
```

全部通过。

### Step 6.7: Commit

```bash
git add -A && git commit -m "test(clone): cross-path convergence (Submit=Replay) + parallel doc

- Clone_pending_source_submit_equals_replay
- Clone_materialized_overlay_submit_equals_replay
- Parallel_clone_pending_source_limitation_documented
- Document parallel clone limitation in XML doc"
```

---

## Task 7: 回归测试 + 知识页更新 + 门禁

### Step 7.1: 全量单元测试

```bash
dotnet test -c Release
```

预期：全部通过（含现有的 674+ 测试）。

### Step 7.2: 性能门禁

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

预期：Movement ≥1642 rounds/s, Attack ≥997 rounds/s，内存无持续增长。

### Step 7.3: 更新知识页 `kb-command-stream.md`

在**坑点**段修改 `Clone()` 相关条目：

**修改** (:156):
```
- `Clone()` 是完整深拷贝，包含所有 slab 数据，大 buffer 克隆成本高
```
→
```
- `Clone()` 是完整深拷贝，包含所有 slab 数据，大 buffer 克隆成本高
- `Clone()` 语义：record-time 快照。读取 source 在调用时刻的虚拟状态（materialized = archetype +
  component store overlay；pending = batch 链表），不受后续命令影响。hierarchy 为虚拟视图
  （world children + pending AddChild - pending RemoveChild），包含防环检测。
- 同批次 Destroy(source) 后 Clone(source) 抛错；Clone(source) 后 Destroy(source) 不影响 clone
```

### Step 7.4: 更新 `kb-code-review-findings.md`

如果实现过程中发现新的 bug/非 bug 猜想，按 `kb-code-review-findings.md` 的格式 append。

### Step 7.5: 最终提交

```bash
git add -A && git commit -m "docs(clone): knowledge update for virtual-state clone

- Update kb-command-stream.md Clone semantics and pitfall
- All tests pass, perf gate passes"
```

---

## 实现注意事项（review 发现）

以下问题需要实现者在写测试/实现时确认或调整：

- **`.ToChildList()` 不存在**：`World.EnumerateChildren`（World.cs:243）返回 `ChildrenEnumerable`，无 `ToChildList()` 扩展。Task 4.1/4.5 测试代码里改用 `foreach` 手动收集到 `List<Entity>`。建议在 EntityCloneTests.cs 加 helper：`static List<Entity> CollectChildren(World w, Entity p) { var l = new List<Entity>(); foreach (var c in w.EnumerateChildren(p)) l.Add(c); return l; }`（注意：若 ChildrenEnumerable 是 ref struct 则不能用 LINQ `.ToList()`）。
- **`CountEntities` helper**：计划引用 EntityCloneTests.cs:550 附近的 `CountEntities`，实现时确认其签名是否匹配 `CountEntities(QueryDescription)`。
- **GetVirtualChildren 性能**：当前实现每次调用遍历 `HierarchyByChild` 两次（Add + Remove）。design doc 建议一次性构建 parent→children 反向索引。第一版可先用简单遍历，perf 验证后再优化。
- **ComponentStore abstract method**：`ComponentStore` 是 `private protected abstract class`（:2047），`ComponentStore<T>` 是 sealed（:2069），加 `ForEachEntityEntry` abstract method 可行。`OverlayCollector` 若用 ref struct 注意不能装箱。
- **ParallelCommandStream 限制**：第一版 ParallelCommandStream.Clone **不支持 pending source、不扫 overlay**（materialized 路径也只读 archetype storage，保持旧行为）。虚拟状态语义只在单线程 CommandStream 生效（见 Task 5.5 修正）。

---

## 文件变更汇总

| File | 变更 |
|------|------|
| `src/MiniArch/Core/CommandStreamCore.cs` | CloneCore (:269) — pending source + destroy detection; CloneComponents (:307) — overlay scan; 新增 ClonePendingSource, CopyComponentsFromBatch, GetVirtualChildren, IsSourceDestroyedThisFrame; CloneChildrenRecursive (:1513) — 虚拟 hierarchy + 防环 |
| `src/MiniArch/Core/CommandStreamCore.cs` | ComponentStore (:2047) — 新增 abstract ForEachEntityEntry |
| `src/MiniArch/Core/CommandStreamCore.cs` | ComponentStore<T> (:2069) — 实现 ForEachEntityEntry |
| `src/MiniArch/Core/ParallelCommandStream.cs` | Clone (:172) — pending source 支持 + destroy 检测 |
| `tests/MiniArch.Tests/Core/EntityCloneTests.cs` | 翻转 Clone_ignores_pending_source_remove_in_same_buffer → Clone_reflects_pending_source_remove_in_same_buffer; 新增 6+ 测试 |
| `docs/plans/2026-07-06-clone-virtual-state.md` | 本文件 |
| `.knowledge/kb-command-stream.md` | Clone 语义/坑点更新 |

---

## 附录：关键方法签名

### 新增方法签名

```csharp
// ComponentStore (abstract base)
public abstract void ForEachEntityEntry(Entity entity, ref OverlayCollector collector);

// ComponentStore<T>
public override void ForEachEntityEntry(Entity entity, ref OverlayCollector collector);

// CommandStreamCore
private Entity ClonePendingSource(Entity source, int srcBatchIdx);
private void CopyComponentsFromBatch(Entity source, int srcBatchIdx, Entity clone, int cloneBatchIdx);
private List<Entity> GetVirtualChildren(Entity parent);
private bool IsSourceDestroyedThisFrame(Entity source);
private void CloneVirtualRecursive(Entity sourceRoot, Entity cloneRoot);
```

### 修改方法签名

```csharp
// CommandStreamCore.CloneCore — add pending + destroy check
protected Entity CloneCore(Entity source);

// CommandStreamCore.CloneComponents — add overlay scan
private void CloneComponents(Entity source, EntityInfo info, Entity clone, int batchIdx);

// ParallelCommandStream.Clone — add pending + destroy check
public Entity Clone(Entity source);
```
