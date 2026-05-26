# CommandBuffer Submit 零 GC — 绕过 Signature 分配

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** CommandBuffer Submit 热路径（archetype 已存在时）实现零 GC 分配。

**Architecture:** 在 World 上新增 `GetOrCreateArchetype(CreateArchetypeKey)` 和 `MaterializeReservedEntityDirect` 入口。CommandBuffer Submit 改为：用 sorted `ComponentType[]` 构造 `CreateArchetypeKey`（struct，零分配）→ 查 `_createArchetypeCache` → 命中时拿到 Archetype，直接 materialize，跳过 `new Signature` + `new ComponentType[]`。未命中时走原有路径。

**Tech Stack:** C# / .NET / MiniArch ECS

**设计约束：**
- `CreateArchetypeKey` 已存在（World.cs:1988），支持 ≤16 个组件，readonly struct，可直接从 `Span<ComponentType>` 构造
- `_createArchetypeCache` 已存在（World.cs:30），`Dictionary<CreateArchetypeKey, Archetype>`
- `GetOrCreateCreateArchetype(Span<ComponentType>)` 已存在（World.cs:1139），但只服务于 Create<T> 路径
- Signature 是 sealed class，不能改 struct（影响面太大）
- `RawComponentValue[]` 也同步 ArrayPool 池化（省第 3 次分配）

---

### Task 1: World 新增 Materialize 入口

**Files:**
- Modify: `src/MiniArch/Core/World.cs` — 新增 `MaterializeReservedEntityDirect` 方法 + 公开 `GetOrCreateArchetype(CreateArchetypeKey)`

**Step 1: 在 World 中公开 archetype 缓存查找**

在 `GetOrCreateArchetype(Signature)` 方法旁（约 1124 行），新增：

```csharp
internal Archetype GetOrCreateArchetype(CreateArchetypeKey key)
{
    if (_createArchetypeCache.TryGetValue(key, out var archetype))
    {
        return archetype;
    }

    archetype = GetOrCreateArchetype(Signature.CreateNormalized(key.ToComponentArray()));
    _createArchetypeCache.TryAdd(key, archetype);
    return archetype;
}
```

注意：用 `TryAdd` 而非 `Add`，因为 `GetOrCreateArchetype(Signature)` 可能已通过 `_archetypes` 字典触发了相同 key 的缓存（如果别的路径先插入的话）。

**Step 2: 新增 `MaterializeReservedEntityDirect`**

在 `MaterializeReservedEntityTrusted` 旁（约 1760 行），新增：

```csharp
internal void MaterializeReservedEntityDirect(Entity entity, Archetype archetype, IReadOnlyList<RawComponentValue> components)
{
    var chunk = archetype.ReserveEntity(entity, out var chunkIndex, out var rowIndex);
    _locations[entity.Id] = new EntityLocation(archetype, chunkIndex, rowIndex);

    unsafe
    {
        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            var writer = ComponentWriterCache.GetColumnWriter(component.RuntimeType);
            fixed (byte* ptr = component.Data)
            {
                WriteComponentFromBytes(chunk, component.ComponentType, rowIndex, ptr + component.DataOffset, writer);
            }
        }
    }

    TouchQueryLayout();
}
```

与 `MaterializeReservedEntityCore` 的区别：直接接收 Archetype（跳过查找），且 `trustCompiledComponentTypes` 始终为 true（CommandBuffer 的组件类型是可信的）。

**Step 3: 运行测试验证没有破坏**

Run: `pwsh scripts/test.ps1`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add src/MiniArch/Core/World.cs
git commit -m "feat: add MaterializeReservedEntityDirect and GetOrCreateArchetype(CreateArchetypeKey)"
```

---

### Task 2: 重构 Submit 路径 — BuildCreatedEntityComponents 返回 Archetype

**Files:**
- Modify: `src/MiniArch/Core/CommandBuffer.cs` — `BuildCreatedEntityComponents` 和 Submit 循环

**Step 1: 修改 `BuildCreatedEntityComponents` 签名和实现**

当前签名：
```csharp
private (Signature Signature, RawComponentValue[] Components) BuildCreatedEntityComponents(in CreatedState state)
```

改为：

```csharp
private (Archetype Archetype, RawComponentValue[] Components) BuildCreatedEntityComponents(in CreatedState state)
{
    _tempComponents.Clear();
    state.CopyTo(_tempComponents);
    var componentCount = _tempComponents.Count;

    var components = ArrayPool<ComponentType>.Shared.Rent(componentCount);
    var sourceComponents = ArrayPool<CreatedComponent>.Shared.Rent(componentCount);
    var rawComponents = ArrayPool<RawComponentValue>.Shared.Rent(componentCount);
    try
    {
        for (var i = 0; i < componentCount; i++)
        {
            sourceComponents[i] = _tempComponents[i].Component;
            components[i] = _tempComponents[i].Component.ComponentType;
        }

        Array.Sort(components, 0, componentCount);

        var key = new CreateArchetypeKey(components.AsSpan(0, componentCount));
        var archetype = _world.GetOrCreateArchetype(key);

        for (var i = 0; i < componentCount; i++)
        {
            var sc = sourceComponents[i];
            rawComponents[i] = new RawComponentValue(
                ComponentsTypeToId(sc.ComponentType), sc.RuntimeType, sc.ComponentType,
                _slabs[sc.SlabIndex], sc.DataOffset, sc.DataSize);
        }

        return (archetype, rawComponents);
    }
    finally
    {
        ArrayPool<CreatedComponent>.Shared.Return(sourceComponents);
        ArrayPool<ComponentType>.Shared.Return(components);
    }
}
```

关键变化：
1. `ComponentType[]` 不再 `new`，用 ArrayPool Rent
2. 排序后直接构造 `CreateArchetypeKey`（struct，零 GC）
3. `RawComponentValue[]` 用 ArrayPool Rent
4. 返回 `Archetype` 而非 `Signature`
5. `CreateArchetypeKey` 要求组件数 1-16，0 组件的实体在外层已走 `Array.Empty<RawComponentValue>()` 分支，不受影响

**Step 2: 修改 Submit 循环调用点**

```csharp
// 旧代码
var (signature, components) = BuildCreatedEntityComponents(in state);
_world.MaterializeReservedEntityTrusted(entity, signature, components);

// 新代码
var (archetype, components) = BuildCreatedEntityComponents(in state);
try
{
    _world.MaterializeReservedEntityDirect(entity, archetype, components);
}
finally
{
    ArrayPool<RawComponentValue>.Shared.Return(components);
}
```

注意：`components` 是 ArrayPool 租的，用完必须 Return。

**Step 3: 修改 SubmitFromFrozen 内联版本**

同样模式改造 `SubmitFromFrozen` 中 440-473 行的内联组件构建逻辑：
- `new RawComponentValue[componentCount]` → `ArrayPool<RawComponentValue>.Shared.Rent(componentCount)`
- 排序后用 `CreateArchetypeKey` 查找 archetype
- 调用 `MaterializeReservedEntityDirect`
- finally 里 Return `rawComponents`

注意 `CreateArchetypeKey` 限制 ≤16 组件。对于 >16 组件的实体（极罕见），保持原有 `Signature` 路径作为 fallback。

实际上 CreatedState 内联 4 槽 + Overflow Dictionary，理论上组件数无上限。需要在代码里加判断：

```csharp
if (componentCount > 0 && componentCount <= 16)
{
    // 零 GC 路径：CreateArchetypeKey
}
else if (componentCount > 16)
{
    // Fallback: 旧 Signature 路径
}
```

但 SubmitFromFrozen 中的逻辑是内联展开而非调用 `BuildCreatedEntityComponents`，需要在内联处同样处理。

**Step 4: 运行测试**

Run: `pwsh scripts/test.ps1`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/MiniArch/Core/CommandBuffer.cs
git commit -m "feat: CommandBuffer Submit zero-GC path via CreateArchetypeKey"
```

---

### Task 3: 验证零 GC — Benchmark

**Files:**
- Modify: 现有 benchmark 或新建 ad-hoc 测试

**Step 1: 确认现有 benchmark**

查找项目中已有的 CommandBuffer benchmark，在其基础上添加 GC 测量。

**Step 2: 运行 benchmark 对比**

Run: `dotnet run -c Release --project benchmarks/...`

验证：稳态 Submit 每实体 GC 次数从 3 降到 0。

**Step 3: Commit**

如果有 benchmark 变更：
```bash
git add benchmarks/...
git commit -m "bench: add GC measurement for CommandBuffer Submit"
```

---

## 总预期效果

| 路径 | 改前每实体 GC | 改后每实体 GC |
|---|---|---|
| Submit（热路径，archetype 已存在） | 3 | **0** |
| Submit（冷路径，新 archetype） | 3 | 3（不变，首次才触发） |
| SubmitFromFrozen（热路径） | 3 | **0** |
| BuildDelta（FrameDelta 路径） | 3 | 3（不改，rawComponents 被 FrameDelta 持有） |
