# FrameDelta Optimal Squash Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement `FrameDelta.Squash()` that eliminates all redundant commands across appended deltas, including folding component commands into CreatedEntity.Components.

**Architecture:** Per-entity, per-component-type state machine that folds sequential commands into net effects. Rebuilds all 9 lists from surviving commands. O(N) where N = total commands.

**Tech Stack:** C# 12, .NET 9, xUnit

---

## Squash Rules

### Component commands (per Entity × ComponentTypeId)

| Current | New | Result |
|---|---|---|
| None | Add(data) | Add(data) |
| None | Set(data) | Set(data) |
| None | Remove | Remove |
| Add(data1) | Set(data2) | Add(data2) |
| Add(data1) | Remove | **cancel** |
| Set(data1) | Set(data2) | Set(data2) |
| Set(data1) | Remove | Remove |
| Remove | Add(data) | Set(data) |
| Remove | Set(data) | Set(data) |

### Entity lifecycle

- Create(E) + Destroy(E) → Release(E) only (entity never existed in world)
- Reserve(E) + Release(E) → **cancel** (no-op)
- Reserve(E) + Create(E) → keep both (Create needs Reserve in replay)

### Hierarchy

- AddChild(P,C) + RemoveChild(C) → **cancel**
- RemoveChild(C) + AddChild(P,C) → keep both (parent may differ)

### Created component folding

For created entities, fold subsequent Add/Set/Remove into `RawCreatedEntity.Components`:
- Set/X adds to Components → update value
- Add/Y not in Components → add to Components
- Remove/X in Components → remove from Components
- Rebuild Signature from final Components (can pass null, Replay rebuilds)

---

## Files

- **Modify:** `src/MiniArch/Core/FrameDelta.cs` — add `Squash()`, `Merge()`, internal types
- **Modify:** `tests/MiniArch.Tests/Core/CommandBufferTests.cs` — add squash tests
- **Reference:** `src/MiniArch/Core/ComponentWriterCache.cs` — `GetSize(Type)` for RawComponentValue

---

### Task 1: Write failing squash tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Write failing tests**

Add these test methods after the existing Append tests (after line 1167):

```csharp
[Fact]
public void Squash_empty_delta_remains_empty()
{
    var delta = new FrameDelta();
    delta.Squash();
    Assert.True(delta.IsEmpty);
}

[Fact]
public void Squash_Set_then_Set_same_component_keeps_last()
{
    var world = new World();
    var entity = world.Create(new Position(10, 20));

    var buffer1 = new CommandBuffer(world);
    buffer1.Set(entity, new Position(30, 40));
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Set(entity, new Position(50, 60));
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Empty(merged.AddCommands);
    Assert.Single(merged.SetCommands);
    Assert.Empty(merged.RemoveCommands);

    world.Replay(merged);
    Assert.True(world.TryGet(entity, out Position p));
    Assert.Equal(new Position(50, 60), p);
}

[Fact]
public void Squash_Add_then_Remove_same_component_cancels()
{
    var world = new World();
    var entity = world.Create();

    var buffer1 = new CommandBuffer(world);
    buffer1.Add(entity, new Position(1, 2));
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Remove<Position>(entity);
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Empty(merged.AddCommands);
    Assert.Empty(merged.SetCommands);
    Assert.Empty(merged.RemoveCommands);

    world.Replay(merged);
    Assert.False(world.TryGet<Position>(entity, out _));
}

[Fact]
public void Squash_Set_then_Remove_keeps_Remove()
{
    var world = new World();
    var entity = world.Create(new Position(10, 20));

    var buffer1 = new CommandBuffer(world);
    buffer1.Set(entity, new Position(30, 40));
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Remove<Position>(entity);
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Empty(merged.AddCommands);
    Assert.Empty(merged.SetCommands);
    Assert.Single(merged.RemoveCommands);

    world.Replay(merged);
    Assert.False(world.TryGet<Position>(entity, out _));
}

[Fact]
public void Squash_Remove_then_Add_becomes_Set()
{
    var world = new World();
    var entity = world.Create(new Position(10, 20));

    var buffer1 = new CommandBuffer(world);
    buffer1.Remove<Position>(entity);
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Add(entity, new Position(30, 40));
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Empty(merged.AddCommands);
    Assert.Single(merged.SetCommands);
    Assert.Empty(merged.RemoveCommands);

    world.Replay(merged);
    Assert.True(world.TryGet(entity, out Position p));
    Assert.Equal(new Position(30, 40), p);
}

[Fact]
public void Squash_Add_then_Set_same_component_keeps_Add_with_latest_data()
{
    var world = new World();
    var entity = world.Create();

    var buffer1 = new CommandBuffer(world);
    buffer1.Add(entity, new Position(1, 2));
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Set(entity, new Position(3, 4));
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Single(merged.AddCommands);
    Assert.Empty(merged.SetCommands);
    Assert.Empty(merged.RemoveCommands);

    world.Replay(merged);
    Assert.True(world.TryGet(entity, out Position p));
    Assert.Equal(new Position(3, 4), p);
}

[Fact]
public void Squash_Create_then_Destroy_becomes_Release()
{
    var world = new World();
    var buffer = new CommandBuffer(world);
    var e = buffer.Create();
    buffer.Add(e, new Position(1, 2));
    var delta1 = buffer.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Destroy(e);
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Empty(merged.CreatedEntities);
    Assert.Empty(merged.DestroyedEntities);
    Assert.Empty(merged.AddCommands);
    Assert.Single(merged.ReleasedEntities);

    world.Replay(merged);
    Assert.False(world.IsAlive(e));
}

[Fact]
public void Squash_Link_then_Unlink_same_child_cancels()
{
    var world = new World();
    var parent = world.Create();
    var child = world.Create();

    var buffer1 = new CommandBuffer(world);
    buffer1.AddChild(parent, child);
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.RemoveChild(child);
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Empty(merged.AddChildCommands);
    Assert.Empty(merged.UnAddChildCommands);
}

[Fact]
public void Squash_folds_component_commands_into_CreatedEntity()
{
    var world = new World();
    var buffer1 = new CommandBuffer(world);
    var e = buffer1.Create();
    buffer1.Add(e, new Position(1, 2));
    buffer1.Add(e, new Velocity(3, 4));
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Set(e, new Position(5, 6));
    buffer2.Remove<Velocity>(e);
    buffer2.Add(e, new Health(100));
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Single(merged.CreatedEntities);
    Assert.Empty(merged.AddCommands);
    Assert.Empty(merged.SetCommands);
    Assert.Empty(merged.RemoveCommands);

    world.Replay(merged);
    Assert.True(world.IsAlive(e));
    Assert.True(world.TryGet(e, out Position p));
    Assert.Equal(new Position(5, 6), p);
    Assert.False(world.TryGet<Velocity>(e, out _));
    Assert.True(world.TryGet(e, out Health h));
    Assert.Equal(new Health(100), h);
}

[Fact]
public void Squash_multiple_entities_independent()
{
    var world = new World();
    var e1 = world.Create(new Position(1, 1));
    var e2 = world.Create(new Position(2, 2));

    var buffer1 = new CommandBuffer(world);
    buffer1.Set(e1, new Position(10, 10));
    buffer1.Add(e2, new Velocity(5, 5));
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Set(e1, new Position(20, 20));
    buffer2.Remove<Velocity>(e2);
    var delta2 = buffer2.Compile();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Append(delta2);
    merged.Squash();

    Assert.Single(merged.SetCommands);
    Assert.Empty(merged.AddCommands);
    Assert.Empty(merged.RemoveCommands);

    world.Replay(merged);
    Assert.True(world.TryGet(e1, out Position p1));
    Assert.Equal(new Position(20, 20), p1);
    Assert.False(world.TryGet<Velocity>(e2, out _));
}

[Fact]
public void Merge_is_equivalent_to_append_then_squash()
{
    var world = new World();
    var entity = world.Create(new Position(10, 20));

    var buffer1 = new CommandBuffer(world);
    buffer1.Set(entity, new Position(30, 40));
    buffer1.Add(entity, new Velocity(5, 6));
    var delta1 = buffer1.Compile();

    var buffer2 = new CommandBuffer(world);
    buffer2.Set(entity, new Position(50, 60));
    buffer2.Remove<Velocity>(entity);
    var delta2 = buffer2.Compile();

    var appendSquash = new FrameDelta();
    appendSquash.Append(delta1);
    appendSquash.Append(delta2);
    appendSquash.Squash();

    var merged = new FrameDelta();
    merged.Append(delta1);
    merged.Merge(delta2);

    Assert.Equal(appendSquash.CreatedEntities.Count, merged.CreatedEntities.Count);
    Assert.Equal(appendSquash.AddCommands.Count, merged.AddCommands.Count);
    Assert.Equal(appendSquash.SetCommands.Count, merged.SetCommands.Count);
    Assert.Equal(appendSquash.RemoveCommands.Count, merged.RemoveCommands.Count);
    Assert.Equal(appendSquash.DestroyedEntities.Count, merged.DestroyedEntities.Count);
    Assert.Equal(appendSquash.ReleasedEntities.Count, merged.ReleasedEntities.Count);
    Assert.Equal(appendSquash.AddChildCommands.Count, merged.AddChildCommands.Count);
    Assert.Equal(appendSquash.UnAddChildCommands.Count, merged.UnAddChildCommands.Count);

    var replayWorld = new World();
    var replayEntity = replayWorld.Create(new Position(10, 20));
    replayWorld.Replay(merged);
    Assert.True(replayWorld.TryGet(replayEntity, out Position p));
    Assert.Equal(new Position(50, 60), p);
    Assert.False(replayWorld.TryGet<Velocity>(replayEntity, out _));
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/MiniArch.Tests --filter "Squash_|Merge_is" -v n`
Expected: FAIL (Squash/Merge methods don't exist)

**Step 3: Commit**

```bash
git add tests/MiniArch.Tests/Core/CommandBufferTests.cs
git commit -m "test: add FrameDelta squash tests (failing)"
```

---

### Task 2: Implement Squash() and Merge()

**Files:**
- Modify: `src/MiniArch/Core/FrameDelta.cs`

**Step 1: Add internal types after the FrameDelta class (before RawComponentValue)**

Add these types inside the `FrameDelta` class or as file-internal types:

```csharp
private enum ComponentNetKind : byte { None = 0, Add, Set, Remove }

private struct ComponentNetAction
{
    public ComponentNetKind Kind;
    public RawComponentCommand Command; // data for Add/Set
    public RawRemoveCommand RemoveCmd; // data for Remove
}

private class SquashEntityState
{
    public bool IsReserved;
    public bool IsReleased;
    public bool IsCreated;
    public RawCreatedEntity CreatedEntity;
    public bool IsDestroyed;
    public Dictionary<int, RawComponentValue>? CreatedComponents;
    public readonly Dictionary<int, ComponentNetAction> ComponentActions = new();
    public bool HasHierarchyChange;
    public bool NetIsAdd;
    public Entity NetLinkParent;
}
```

**Step 2: Implement Squash() method on FrameDelta**

The algorithm:

1. Build `Dictionary<Entity, SquashEntityState>` by iterating all 9 lists
2. For ReservedEntities/ReleasedEntities: set flags
3. For CreatedEntities: copy Components into mutable dictionary
4. For DestroyedEntities: set flag
5. For AddCommands/SetCommands/RemoveCommands: fold into ComponentActions per (Entity, ComponentTypeId) using the state machine rules
6. For AddChildCommands/UnAddChildCommands: last-write-wins per child entity
7. Clear all lists, then rebuild from net state:
   - Create+Destroy → emit Reserved+Released only
   - Reserve+Release (no Create) → skip
   - IsCreated: fold ComponentActions into CreatedComponents, rebuild CreatedEntity with `null` Signature
   - !IsCreated: emit component commands from ComponentActions (respecting kind: Add→AddCommands, Set→SetCommands, Remove→RemoveCommands)
   - Hierarchy: emit AddChild/RemoveChild from net state
   - Emit DestroyedEntities for non-created destroyed entities

Key folding logic:
```csharp
private static void FoldComponent(Dictionary<int, ComponentNetAction> actions, ComponentNetKind kind, RawComponentCommand? cmd, RawRemoveCommand? removeCmd)
{
    var typeId = kind == ComponentNetKind.Remove ? removeCmd.Value.ComponentTypeId : cmd.Value.ComponentTypeId;
    ref var current = ref CollectionsMarshal.GetValueRefOrAddDefault(actions, typeId, out _);

    switch (current.Kind)
    {
        case ComponentNetKind.None:
            current.Kind = kind;
            if (kind != ComponentNetKind.Remove) current.Command = cmd.Value;
            else current.RemoveCmd = removeCmd.Value;
            break;
        case ComponentNetKind.Add:
            if (kind == ComponentNetKind.Set) { current.Command = cmd.Value; }
            else if (kind == ComponentNetKind.Remove) { current = default; }
            else { current.Command = cmd.Value; } // Add+Add
            break;
        case ComponentNetKind.Set:
            if (kind == ComponentNetKind.Set || kind == ComponentNetKind.Add) { current.Command = cmd.Value; }
            else if (kind == ComponentNetKind.Remove) { current = default; current.Kind = ComponentNetKind.Remove; current.RemoveCmd = removeCmd.Value; }
            break;
        case ComponentNetKind.Remove:
            if (kind == ComponentNetKind.Add || kind == ComponentNetKind.Set) { current.Kind = ComponentNetKind.Set; current.Command = cmd.Value; }
            break;
    }
}
```

For rebuilding CreatedEntity with folded components:
```csharp
// After folding ComponentActions into CreatedComponents dictionary:
var componentArray = new RawComponentValue[components.Count];
var index = 0;
foreach (var kvp in components)
    componentArray[index++] = kvp.Value;
var rebuilt = new RawCreatedEntity(created.Entity, null, componentArray);
```

Signature is passed as `null` — Replay will rebuild it via `BuildReplaySignature`.

**Step 3: Implement Merge() method**

```csharp
public void Merge(FrameDelta other)
{
    Append(other);
    Squash();
}
```

**Step 4: Run tests**

Run: `dotnet test tests/MiniArch.Tests --filter "Squash_|Merge_is" -v n`
Expected: ALL PASS

**Step 5: Run all tests**

Run: `dotnet test tests/MiniArch.Tests -v n`
Expected: ALL PASS (173 existing + 11 new = 184)

**Step 6: Commit**

```bash
git add src/MiniArch/Core/FrameDelta.cs tests/MiniArch.Tests/Core/CommandBufferTests.cs
git commit -m "feat: implement FrameDelta.Squash() and Merge() with optimal command folding"
```

---

### Task 3: Update knowledge base

**Files:**
- Modify: `.knowledge/kb-command-buffer-feasibility.md`
- Modify: `.knowledge/INDEX.md` if needed

Update the knowledge base with information about Squash/Merge operations.

---

## Verification

```bash
dotnet build src/MiniArch
dotnet test tests/MiniArch.Tests -v n
```

All 184 tests pass (173 existing + 11 new squash tests).
