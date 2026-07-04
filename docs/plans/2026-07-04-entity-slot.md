# EntitySlot Implementation Plan

> **⚠️ 设计已变更：** 最初使用 `FrameDelta.OriginStream`（内部引用）自动检测自己的 delta。2026-07-04 重构为显式 `stream.Replay(delta, resolveSlots: true)` 参数——删除了 `OriginStream`，用户为自己的 delta 传 `true`，其他传 `false`。当前设计见 `.knowledge/kb-deferred-create-design.md`。以下内容反映的是旧设计方案。

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add an `EntitySlot` API that automatically tracks deferred entity resolution —users call `stream.Track(placeholder)`, hold the returned `EntitySlot` across frames, and `.Value` auto-updates to the real Entity ID after Submit or Replay.

**Architecture:** CommandStream owns the tracking (seq is stream-local, no multi-stream collision). `EntitySlot` wraps either an inline `Entity` (non-deferred mode, zero alloc) or a heap-allocated `Slot` object (deferred mode, one alloc per Track). During resolution, CommandStream iterates a seq-indexed registration array, writes real IDs into each Slot, then clears the array. `stream.Replay(delta)` wraps `world.Replay(delta)` and auto-resolves tracked slots when it detects the delta is its own (via `FrameDelta.OriginStream` reference).

**Tech Stack:** C# .NET 8, xUnit, MiniArch ECS

---

## Background Knowledge (READ BEFORE STARTING)

### How deferred entities work

When `CommandStream.DeferredEntities = true`:
- `stream.Create()` returns placeholder `Entity(-1, seq)` where seq starts from 0 and increments.
- seq resets to 0 after `ResolveDeferredCreates()` runs (inside Submit/Snapshot/SwapOutState).
- `stream.Snapshot()` produces a placeholder-id `FrameDelta` for lockstep.
- `stream.Submit()` resolves placeholders to real IDs locally.
- `world.Replay(delta)` resolves placeholders during replay (populates `_replayPlaceholderMap`).
- `world.TryResolvePlaceholder(Entity(-1, seq), out Entity real)` queries the replay map after Replay.

### Key constraint this feature solves

Without EntitySlot, users must manually call `TryResolvePlaceholder` after Replay to get the real ID. EntitySlot automates this.

### Files you will touch

| File | Action |
|---|---|
| `src/MiniArch/Core/FrameDelta.cs` | Add 1 internal field |
| `src/MiniArch/Core/CommandStream.cs` | Add EntitySlot struct, Slot class, Track/Replay methods, _trackedBySeq, ResolveTrackedSlots, hook into Snapshot/ResolveDeferredCreates |
| `src/MiniArch/Core/World.cs` | **ZERO CHANGES** |
| `tests/MiniArch.Tests/Core/EntitySlotTests.cs` | **Create** new test file |
| `.knowledge/kb-deferred-create-design.md` | Update docs |

### Critical rules

1. **NEVER** modify `World.cs`. All tracking logic goes in `CommandStream.cs`.
2. **NEVER** change the FrameDelta wire format. `OriginStream` is internal, not serialized.
3. **ALWAYS** use `-c Release` for performance tests.
4. **Commit after each task** —small commits, clear messages.
5. Entity and Component structs use `LayoutKind.Sequential` (C# default). Don't add `LayoutKind.Auto`.

---

## Task 1: Add OriginStream field to FrameDelta

**Files:**
- Modify: `src/MiniArch/Core/FrameDelta.cs` (around line 69-71, near `_buffer`/`_length`/`_opCount`)

**Step 1: Add the field**

Open `src/MiniArch/Core/FrameDelta.cs`. Find the internal fields block (around line 69-71):

```csharp
internal byte[] _buffer = Array.Empty<byte>();
internal int _length;
internal int _opCount;
```

Add after `_opCount`:

```csharp
    /// <summary>
    /// Set by <see cref="CommandStream.Snapshot"/> to the producing stream.
    /// Used by <see cref="CommandStream.Replay(FrameDelta)"/> to auto-detect
    /// the stream's own delta and resolve tracked EntitySlots.
    /// Not serialized —deserialized deltas have this set to <c>null</c>.
    /// </summary>
    internal CommandStream? OriginStream;
```

**Step 2: Build to verify no compile errors**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/MiniArch/Core/FrameDelta.cs
git commit -m "feat: add OriginStream field to FrameDelta for EntitySlot support"
```

---

## Task 2: Add Slot class and EntitySlot struct

**Files:**
- Modify: `src/MiniArch/Core/CommandStream.cs` (add at end of file, before the closing `}`)

**Step 1: Add the types**

Scroll to the very end of `CommandStream.cs` (line ~1907). Find the closing `}` of `class CommandStream`. 

Add these types **after** the class closing brace, still inside the `namespace MiniArch.Core;` block (they can be at the bottom of the file):

```csharp
/// <summary>
/// Internal mutable state shared between all copies of an <see cref="EntitySlot"/>.
/// One instance is allocated per <see cref="CommandStream.Track"/> call on a placeholder entity.
/// </summary>
internal sealed class Slot
{
    /// <summary>The current entity value: placeholder before resolution, real after.</summary>
    internal Entity Entity;

    /// <summary>Linked-list pointer for registration in <c>_trackedBySeq</c>. Nulled after resolution.</summary>
    internal Slot? Next;
}

/// <summary>
/// A tracked entity handle that auto-updates when a deferred placeholder is resolved.
/// </summary>
/// <remarks>
/// <para>
/// Obtain an EntitySlot via <see cref="CommandStream.Track"/>. The <see cref="Value"/>
/// property returns the placeholder entity before resolution and the real entity after
/// <see cref="CommandStream.Submit"/> or <see cref="CommandStream.Replay(FrameDelta)"/>.
/// </para>
/// <para>
/// <b>EntitySlot cannot be stored in ECS components</b> (it contains reference types and
/// is not <c>unmanaged</c>). Store <see cref="Entity"/> (via <c>slot.Value</c>) in
/// component fields instead —the existing <c>EntityFieldResolver</c> handles auto-resolution
/// of component fields independently.
/// </para>
/// </remarks>
public readonly struct EntitySlot
{
    private readonly Entity _entity;
    private readonly Slot? _slot;

    /// <summary>Creates an EntitySlot wrapping an inline real entity (non-deferred mode).</summary>
    internal EntitySlot(Entity entity)
    {
        _entity = entity;
        _slot = null;
    }

    /// <summary>Creates an EntitySlot wrapping a mutable Slot (deferred mode).</summary>
    internal EntitySlot(Slot slot)
    {
        _entity = default;
        _slot = slot;
    }

    /// <summary>
    /// The current entity. Returns the placeholder before resolution,
    /// the real entity after Submit/Replay.
    /// </summary>
    public Entity Value => _slot is not null ? _slot.Entity : _entity;

    /// <summary>Whether this slot holds a non-default entity handle.</summary>
    public bool HasValue => Value != default;
}
```

**Step 2: Build to verify**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/MiniArch/Core/CommandStream.cs
git commit -m "feat: add Slot class and EntitySlot struct"
```

---

## Task 3: Add _trackedBySeq field and Track method to CommandStream

**Files:**
- Modify: `src/MiniArch/Core/CommandStream.cs`

**Step 1: Add the registration fields**

Find the private fields block near the top of the class (around line 31-48). Add after `_lastCreatedBatch`:

```csharp
    // ── EntitySlot tracking ──────────────────────────────────────────
    // Registration array indexed by placeholder seq. Each entry is a linked
    // list of Slot objects that want to be notified when this seq is resolved.
    // Cleared (Array.Clear + _trackedMaxSeq=0) after each resolution pass.
    private Slot?[] _trackedBySeq = [];
    private int _trackedMaxSeq;
```

Find the exact location by looking for this code (around line 47-48):
```csharp
    private Entity _lastCreated;
    private int _lastCreatedBatch = -1;
```

Add the new fields right after those two lines.

**Step 2: Add the Track method**

Find the `// ── Record API ──` section (around line 111). Add the Track method after the `Create()` method block (after line 121, before the `CreateImpl` method). Insert:

```csharp
    // ── EntitySlot API ───────────────────────────────────────────────

    /// <summary>
    /// Creates a tracked handle for <paramref name="entity"/> that auto-updates
    /// when a deferred placeholder is resolved during Submit or Replay.
    /// </summary>
    /// <param name="entity">A placeholder from <see cref="Create"/> (deferred mode)
    /// or any real entity (non-deferred mode).</param>
    /// <returns>
    /// An <see cref="EntitySlot"/> whose <see cref="EntitySlot.Value"/> returns
    /// the placeholder before resolution and the real entity after.
    /// </returns>
    /// <remarks>
    /// <para>
    /// In deferred mode, one small heap object is allocated per call (the internal
    /// <c>Slot</c>). In non-deferred mode (when <paramref name="entity"/> is already
    /// a real entity), no allocation occurs —the entity is stored inline.
    /// </para>
    /// <para>
    /// Track the entity in the same frame you create it. Call before Submit/Snapshot.
    /// </para>
    /// </remarks>
    public EntitySlot Track(Entity entity)
    {
        if (!entity.IsPlaceholder)
            return new EntitySlot(entity);

        var slot = new Slot { Entity = entity };
        var seq = entity.Version;

        EnsureCapacity(ref _trackedBySeq, seq, 16);
        slot.Next = _trackedBySeq[seq];
        _trackedBySeq[seq] = slot;
        if (seq >= _trackedMaxSeq) _trackedMaxSeq = seq + 1;

        return new EntitySlot(slot);
    }
```

**Step 3: Build to verify**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/MiniArch/Core/CommandStream.cs
git commit -m "feat: add Track method and _trackedBySeq to CommandStream"
```

---

## Task 4: Add ResolveTrackedSlots methods

**Files:**
- Modify: `src/MiniArch/Core/CommandStream.cs`

**Step 1: Add the two resolution methods**

Find the `// ── Deferred entity resolution ──` section (around line 1488). Add these methods right before `ResolveDeferredCreates()`:

```csharp
    // ── EntitySlot resolution ────────────────────────────────────────

    /// <summary>
    /// Resolves all tracked slots using a Submit-path resolve map.
    /// Called at the end of <see cref="ResolveDeferredCreates"/>.
    /// </summary>
    private void ResolveTrackedSlots(Entity[] resolveMap, int mapLen)
    {
        if (_trackedMaxSeq == 0) return;

        var max = Math.Min(_trackedMaxSeq, _trackedBySeq.Length);
        for (var seq = 0; seq < max; seq++)
        {
            var s = _trackedBySeq[seq];
            if (s is null) continue;

            var hasReal = (uint)seq < (uint)mapLen && resolveMap[seq].Id >= 0;

            while (s is not null)
            {
                var next = s.Next;
                if (hasReal)
                    s.Entity = resolveMap[seq];
                s.Next = null;  // break chain so Slot doesn't retain linked list
                s = next;
            }

            _trackedBySeq[seq] = null;
        }

        _trackedMaxSeq = 0;
    }

    /// <summary>
    /// Resolves all tracked slots using the World's replay placeholder map.
    /// Called after <see cref="Replay(FrameDelta)"/> when the delta is this
    /// stream's own (detected via <see cref="FrameDelta.OriginStream"/>).
    /// </summary>
    private void ResolveTrackedSlotsFromReplay()
    {
        if (_trackedMaxSeq == 0) return;

        var max = Math.Min(_trackedMaxSeq, _trackedBySeq.Length);
        for (var seq = 0; seq < max; seq++)
        {
            var s = _trackedBySeq[seq];
            if (s is null) continue;

            if (_world.TryResolvePlaceholder(new Entity(-1, seq), out var real))
            {
                while (s is not null)
                {
                    var next = s.Next;
                    s.Entity = real;
                    s.Next = null;
                    s = next;
                }
            }

            _trackedBySeq[seq] = null;
        }

        _trackedMaxSeq = 0;
    }
```

**Step 2: Build to verify**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/MiniArch/Core/CommandStream.cs
git commit -m "feat: add ResolveTrackedSlots methods for Submit and Replay paths"
```

---

## Task 5: Hook ResolveTrackedSlots into ResolveDeferredCreates

**Files:**
- Modify: `src/MiniArch/Core/CommandStream.cs` (in `ResolveDeferredCreates`, around line 1555)

**Step 1: Add the call**

Find the end of `ResolveDeferredCreates()`. It currently ends with (around line 1555):

```csharp
        _resolveMapPool = resolveMap;
    }
```

Change to:

```csharp
        _resolveMapPool = resolveMap;

        // Resolve tracked EntitySlots using this resolve map.
        ResolveTrackedSlots(resolveMap, _deferredSeq);
    }
```

**IMPORTANT**: This call must be BEFORE `_resolveMapPool = resolveMap` is reassigned, because `ResolveTrackedSlots` reads from `resolveMap`. Actually, `resolveMap` is a local variable that's valid until the method returns, so the order doesn't matter for correctness. But placing it at the end (after `_resolveMapPool = resolveMap`) is fine since `resolveMap` and `_resolveMapPool` point to the same array. Put it after `_resolveMapPool = resolveMap;` for clarity.

Wait, actually `_deferredSeq` is set to 0 at line 1520 (`_deferredSeq = 0`). So we can't use `_deferredSeq` as `mapLen` —it's already been reset. Let me check...

Looking at the code flow:
- Line 1497: `EnsureCapacity(ref resolveMap, _deferredSeq - 1, 64)` — sizes resolveMap to `_deferredSeq` entries
- Line 1500: `for (var seq = 0; seq < _deferredSeq; seq++)` — fills resolveMap
- Line 1520: `_deferredSeq = 0;` — **resets seq counter**
- Line 1555: `_resolveMapPool = resolveMap;` — end of method

So at the point where we add the call, `_deferredSeq` is already 0. We need to use `resolveMap.Length` or save the original value. The simplest fix: use `resolveMap.Length` as mapLen, since `ResolveTrackedSlots` already bounds by `_trackedMaxSeq`.

Revise the call to:

```csharp
        _resolveMapPool = resolveMap;

        // Resolve tracked EntitySlots using this resolve map.
        // _deferredSeq was reset to 0 above, so use resolveMap.Length as the bound.
        ResolveTrackedSlots(resolveMap, resolveMap.Length);
    }
```

**Step 2: Also handle the early-return case in ResolveDeferredCreates**

The method has an early return at the top (line 1492-1493):

```csharp
    private void ResolveDeferredCreates()
    {
        if (_deferredSeq == 0)
            return;
```

When `_deferredSeq == 0` (non-deferred mode or no deferred creates), tracked slots are still pending. They might be real entities (non-deferred Track returns inline Entity, no Slot registration). But if a user tracked a placeholder from a PREVIOUS frame that wasn't resolved... that's a usage error.

For correctness, we should still clear `_trackedBySeq` even when `_deferredSeq == 0`, because otherwise stale registrations from this frame would persist. Add clearing logic:

```csharp
    private void ResolveDeferredCreates()
    {
        if (_deferredSeq == 0)
        {
            // No deferred creates to resolve, but clear any stale tracked slots.
            if (_trackedMaxSeq > 0)
            {
                Array.Clear(_trackedBySeq, 0, _trackedMaxSeq);
                _trackedMaxSeq = 0;
            }
            return;
        }
```

**Step 3: Build to verify**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/MiniArch/Core/CommandStream.cs
git commit -m "feat: hook ResolveTrackedSlots into ResolveDeferredCreates"
```

---

## Task 6: Set OriginStream in Snapshot

**Files:**
- Modify: `src/MiniArch/Core/CommandStream.cs` (in `Snapshot()`, around line 428-441)

**Step 1: Add OriginStream assignment**

Find the `Snapshot()` method (line 428):

```csharp
    public FrameDelta Snapshot()
    {
        if (!_deferredEntities)
        {
            ResolveDeferredCreates();
            var delta = new FrameDelta();
            BuildDelta(delta);
            return delta;
        }
        ThrowIfSnapshotHasImmediateEntities();
        var d = new FrameDelta();
        BuildDelta(d);
        return d;
    }
```

Change to:

```csharp
    public FrameDelta Snapshot()
    {
        if (!_deferredEntities)
        {
            ResolveDeferredCreates();
            var delta = new FrameDelta();
            BuildDelta(delta);
            delta.OriginStream = this;
            return delta;
        }
        ThrowIfSnapshotHasImmediateEntities();
        var d = new FrameDelta();
        BuildDelta(d);
        d.OriginStream = this;
        return d;
    }
```

**Step 2: Build to verify**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/MiniArch/Core/CommandStream.cs
git commit -m "feat: set OriginStream in Snapshot for EntitySlot auto-detection"
```

---

## Task 7: Add CommandStream.Replay method

**Files:**
- Modify: `src/MiniArch/Core/CommandStream.cs`

**Step 1: Add the Replay method**

Find the `// ── Submit ──` section (around line 330). Add the Replay method **before** the Submit section (or after the Snapshot section, around line 442). Insert after the `Snapshot()` method's closing brace:

```csharp
    // ── Replay ───────────────────────────────────────────────────────

    /// <summary>
    /// Replays a <see cref="FrameDelta"/> into the underlying <see cref="World"/>
    /// and, if the delta was produced by this stream's <see cref="Snapshot"/>,
    /// automatically resolves all tracked <see cref="EntitySlot"/>s.
    /// </summary>
    /// <param name="delta">The delta to replay. Can be from any source —only
    /// deltas produced by this stream's <see cref="Snapshot"/> trigger slot
    /// resolution (detected via internal <see cref="FrameDelta.OriginStream"/>).</param>
    /// <remarks>
    /// <para>
    /// In a lockstep setup, replay all peer deltas and your own delta through
    /// this method. The stream automatically recognizes its own delta and
    /// resolves tracked slots at the right time.
    /// </para>
    /// <para>
    /// The underlying <see cref="World.Replay(FrameDelta)"/> is also available
    /// for direct use, but does not resolve tracked EntitySlots.
    /// </para>
    /// </remarks>
    public void Replay(FrameDelta delta)
    {
        _world.Replay(delta);

        // Only resolve tracked slots when replaying our own delta.
        // Peer deltas (deserialized, OriginStream == null) are skipped.
        if (ReferenceEquals(delta.OriginStream, this))
            ResolveTrackedSlotsFromReplay();
    }
```

**Step 2: Build to verify**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add src/MiniArch/Core/CommandStream.cs
git commit -m "feat: add CommandStream.Replay with EntitySlot auto-resolution"
```

---

## Task 8: Mark World.Replay as obsolete

**Files:**
- Modify: `src/MiniArch/Core/World.cs` (around line 497)

**Step 1: Add Obsolete attribute**

Find the `Replay` method on World (line 497):

```csharp
    public void Replay(FrameDelta delta)
    {
        ReplayCore(delta);
    }
```

Add the `[Obsolete]` attribute and update the doc comment:

```csharp
    /// <summary>
    /// Replays a <see cref="FrameDelta"/> into this world.
    /// </summary>
    /// <remarks>
    /// For tracked entity support (<see cref="CommandStream.Track"/>), use
    /// <see cref="CommandStream.Replay(FrameDelta)"/> instead, which wraps
    /// this method and auto-resolves tracked EntitySlots.
    /// This method will be removed in a future version.
    /// </remarks>
    [Obsolete("Use CommandStream.Replay(delta) for EntitySlot support. This method will be removed in a future version.")]
    public void Replay(FrameDelta delta)
    {
        ReplayCore(delta);
    }
```

**Step 2: Build to verify (expect warnings, not errors)**

Run: `dotnet build src/MiniArch/MiniArch.csproj -c Release`
Expected: Build succeeded, 0 errors. May show obsolete warnings (OK).

**Step 3: Build the test project to see what warnings appear**

Run: `dotnet build tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release`
Expected: Build succeeded. Many obsolete warnings from existing test code. This is expected —existing tests still call `world.Replay()` directly.

**IMPORTANT**: Do NOT fix the warnings in existing tests. They still work correctly. Migration to `stream.Replay()` is optional and can be done later.

**Step 4: Commit**

```bash
git add src/MiniArch/Core/World.cs
git commit -m "chore: mark World.Replay as obsolete, prefer CommandStream.Replay"
```

---

## Task 9: Write EntitySlot tests —Submit path

**Files:**
- Create: `tests/MiniArch.Tests/Core/EntitySlotTests.cs`

**Step 1: Create the test file with Submit-path tests**

```csharp
using MiniArch.Core;

namespace MiniArchTests.Core;

/// <summary>
/// Tests for <see cref="CommandStream.Track"/> and <see cref="EntitySlot"/>
/// resolution via the Submit path.
/// </summary>
public sealed class EntitySlotTests
{
    private readonly record struct Health(int Value);
    private readonly record struct Linked(int Id, Entity Target);

    private static CommandStream MakeStream(World world, bool deferred = true)
    {
        return new CommandStream(world) { DeferredEntities = deferred };
    }

    // ── Submit path ───────────────────────────────────────────────

    [Fact]
    public void Track_Submit_resolves_placeholder_to_real_entity()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        Assert.True(slot.Value.IsPlaceholder);  // before Submit

        stream.Submit();

        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
    }

    [Fact]
    public void Track_Submit_slot_value_usable_for_component_access()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        stream.Add(slot.Value, new Health(42));
        stream.Submit();

        Assert.True(world.TryGet(slot.Value, out Health hp));
        Assert.Equal(42, hp.Value);
    }

    [Fact]
    public void Track_multiple_placeholders_all_resolve_on_Submit()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slotA = stream.Track(stream.Create());
        var slotB = stream.Track(stream.Create());
        var slotC = stream.Track(stream.Create());

        stream.Submit();

        Assert.True(world.IsAlive(slotA.Value));
        Assert.True(world.IsAlive(slotB.Value));
        Assert.True(world.IsAlive(slotC.Value));
        Assert.NotEqual(slotA.Value, slotB.Value);
        Assert.NotEqual(slotB.Value, slotC.Value);
    }

    [Fact]
    public void Track_multiple_trackers_on_same_placeholder_all_resolve()
    {
        var world = new World();
        var stream = MakeStream(world);

        var placeholder = stream.Create();
        var slot1 = stream.Track(placeholder);
        var slot2 = stream.Track(placeholder);
        var slot3 = stream.Track(placeholder);

        stream.Submit();

        Assert.Equal(slot1.Value, slot2.Value);
        Assert.Equal(slot2.Value, slot3.Value);
        Assert.True(world.IsAlive(slot1.Value));
    }

    [Fact]
    public void Track_non_deferred_mode_returns_real_entity_immediately()
    {
        var world = new World();
        var stream = MakeStream(world, deferred: false);

        var slot = stream.Track(stream.Create());
        // Non-deferred: Create returns real entity, slot stores it inline.
        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(slot.Value.Id >= 0);

        stream.Submit();
        Assert.True(world.IsAlive(slot.Value));
    }

    [Fact]
    public void Track_slot_value_survives_across_frames()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        stream.Submit();

        var realEntity = slot.Value;
        Assert.True(world.IsAlive(realEntity));

        // Simulate next frame: create and submit more entities.
        stream.Clear();
        var other = stream.Create();
        stream.Submit();

        // Original slot still holds the same entity.
        Assert.Equal(realEntity, slot.Value);
        Assert.True(world.IsAlive(slot.Value));
    }

    [Fact]
    public void Track_cancelled_entity_slot_stays_placeholder()
    {
        var world = new World();
        var stream = MakeStream(world);

        var parent = stream.Create();
        var child = stream.Create();
        stream.AddChild(child, parent);
        stream.Destroy(parent);  // cascade cancels child
        var slot = stream.Track(child);

        stream.Submit();

        // Child was cancelled —slot stays as placeholder (not resolved).
        Assert.True(slot.Value.IsPlaceholder);
        Assert.False(slot.Value.IsValid);
    }

    [Fact]
    public void Track_default_EntitySlot_returns_default_entity()
    {
        EntitySlot slot = default;
        Assert.Equal(default(Entity), slot.Value);
        Assert.False(slot.HasValue);
    }
}
```

**Step 2: Build and run the tests**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~EntitySlotTests"`
Expected: All tests PASS.

**Step 3: Commit**

```bash
git add tests/MiniArch.Tests/Core/EntitySlotTests.cs
git commit -m "test: add EntitySlot Submit-path tests"
```

---

## Task 10: Write EntitySlot tests —Replay path

**Files:**
- Modify: `tests/MiniArch.Tests/Core/EntitySlotTests.cs`

**Step 1: Add Replay-path tests to the EntitySlotTests class**

Add these tests inside the `EntitySlotTests` class (before the closing `}`):

```csharp
    // ── Replay path (lockstep relay) ──────────────────────────────

    [Fact]
    public void Track_Replay_own_delta_resolves_slot()
    {
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        stream.Add(slot.Value, new Health(99));

        var delta = stream.Snapshot();
        stream.Clear();

        // Replay own delta —should auto-resolve slot.
        stream.Replay(delta);

        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
        Assert.True(world.TryGet(slot.Value, out Health hp));
        Assert.Equal(99, hp.Value);
    }

    [Fact]
    public void Track_Replay_peer_delta_does_not_resolve_slot()
    {
        // Host A records and tracks.
        var hostA = new World();
        var streamA = MakeStream(hostA);

        var slotA = streamA.Track(streamA.Create());
        var deltaA = streamA.Snapshot();
        streamA.Clear();

        // Host B: has its own stream, replays host A's delta.
        var hostB = new World();
        var streamB = MakeStream(hostB);
        var slotB = streamB.Track(streamB.Create());
        var deltaB = streamB.Snapshot();
        streamB.Clear();

        // Host B replays both deltas. deltaA is NOT streamB's own (OriginStream != streamB).
        streamB.Replay(deltaA);
        // slotB should NOT be resolved yet —deltaB hasn't been replayed.
        Assert.True(slotB.Value.IsPlaceholder);

        streamB.Replay(deltaB);
        // NOW slotB resolves —deltaB is streamB's own.
        Assert.False(slotB.Value.IsPlaceholder);
        Assert.True(hostB.IsAlive(slotB.Value));
    }

    [Fact]
    public void Track_Replay_multiple_deltas_only_resolves_own()
    {
        // Two hosts, each with own stream and tracked entity.
        var worldA = new World();
        var worldB = new World();
        var streamA = MakeStream(worldA);
        var streamB = MakeStream(worldB);

        var slotA = streamA.Track(streamA.Create());
        var slotB = streamB.Track(streamB.Create());

        var deltaA = streamA.Snapshot();
        var deltaB = streamB.Snapshot();
        streamA.Clear();
        streamB.Clear();

        // Each host replays both deltas in order.
        // Host A:
        streamA.Replay(deltaA);  // own —resolves slotA
        streamA.Replay(deltaB);  // peer —no effect on slotA
        Assert.True(worldA.IsAlive(slotA.Value));

        // Host B:
        streamB.Replay(deltaA);  // peer —no effect on slotB
        streamB.Replay(deltaB);  // own —resolves slotB
        Assert.True(worldB.IsAlive(slotB.Value));

        // Slots resolved to different entities (different hosts' entities).
        Assert.NotEqual(slotA.Value, slotB.Value);
    }

    [Fact]
    public void Track_Replay_slot_survives_across_frames()
    {
        var world = new World();
        var stream = MakeStream(world);

        // Frame 1: create + track + snapshot + clear + replay
        var slot = stream.Track(stream.Create());
        stream.Add(slot.Value, new Health(1));
        var delta1 = stream.Snapshot();
        stream.Clear();
        stream.Replay(delta1);

        var realEntity = slot.Value;
        Assert.True(world.IsAlive(realEntity));

        // Frame 2: new create + track + snapshot + clear + replay
        var slot2 = stream.Track(stream.Create());
        stream.Add(slot2.Value, new Health(2));
        var delta2 = stream.Snapshot();
        stream.Clear();
        stream.Replay(delta2);

        // Original slot still valid.
        Assert.Equal(realEntity, slot.Value);
        Assert.True(world.IsAlive(slot.Value));
        Assert.True(world.IsAlive(slot2.Value));
    }

    [Fact]
    public void Track_Replay_deserialized_delta_does_not_trigger_resolution()
    {
        // Simulate receiving own delta back via network (deserialized).
        var world = new World();
        var stream = MakeStream(world);

        var slot = stream.Track(stream.Create());
        var delta = stream.Snapshot();
        stream.Clear();

        // Serialize and deserialize —loses OriginStream.
        var bytes = delta.AsSpan().ToArray();
        var deserialized = FrameDelta.Deserialize(bytes);

        stream.Replay(deserialized);
        // Deserialized delta has OriginStream == null —not recognized as own.
        // Slot is NOT resolved (placeholder still).
        Assert.True(slot.Value.IsPlaceholder);

        // Now replay the ORIGINAL delta —resolves correctly.
        stream.Replay(delta);
        Assert.False(slot.Value.IsPlaceholder);
        Assert.True(world.IsAlive(slot.Value));
    }
```

**Step 2: Build and run tests**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release --filter "FullyQualifiedName~EntitySlotTests"`
Expected: All tests PASS (both Submit and Replay groups).

**Step 3: Commit**

```bash
git add tests/MiniArch.Tests/Core/EntitySlotTests.cs
git commit -m "test: add EntitySlot Replay-path tests including multi-host lockstep"
```

---

## Task 11: Add World.Replay suppression for obsolete warnings in tests

**Files:**
- Modify: `tests/MiniArch.Tests/MiniArch.Tests.csproj`

**Step 1: Add GlobalSuppressions or pragma to suppress obsolete warnings**

The `[Obsolete]` on `World.Replay` will generate many warnings across existing tests. We need to suppress these to keep the build clean without migrating every test.

**Option A (preferred):** Add `NoWarn` to the test project file.

Open `tests/MiniArch.Tests/MiniArch.Tests.csproj`. Find a `<PropertyGroup>` and add:

```xml
<NoWarn>$(NoWarn);CS0618</NoWarn>
```

This suppresses the obsolete warning CS0618 project-wide for tests.

**Step 2: Build to verify warnings are gone**

Run: `dotnet build tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release`
Expected: Build succeeded, 0 errors, 0 warnings (or at least no CS0618 warnings).

**Step 3: Commit**

```bash
git add tests/MiniArch.Tests/MiniArch.Tests.csproj
git commit -m "chore: suppress CS0618 obsolete warnings in test project"
```

---

## Task 12: Run full test suite and verify

**Step 1: Run all tests**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -c Release`
Expected: ALL tests pass (existing + new EntitySlot tests). 0 failures.

**Step 2: If any existing test fails, debug and fix**

Most likely cause: something in the `ResolveDeferredCreates` hook interfering. Check that the `ResolveTrackedSlots` call is correctly placed and doesn't affect existing behavior when `_trackedMaxSeq == 0`.

**Step 3: Commit if any fixes were needed**

```bash
git add -A
git commit -m "fix: resolve test failures from EntitySlot integration"
```

---

## Task 13: Run HeroComing performance regression gate

**Step 1: Run the perf gate**

Run: `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
Expected: Throughput meets threshold (Movement >= 1210 rounds/s, Attack >= 767 rounds/s). No memory growth. No crash.

**Step 2: If the gate FAILS**

- Check if the `ResolveTrackedSlots` call adds overhead to the Submit path when no slots are tracked (it should early-return when `_trackedMaxSeq == 0`).
- The added code should be zero-cost when EntitySlot is not used.
- If there's a regression, investigate the hot path and optimize.

**Step 3: Do NOT update the baseline** unless the user explicitly asks.

---

## Task 14: Update knowledge base documentation

**Files:**
- Modify: `.knowledge/kb-deferred-create-design.md`

**Step 1: Add EntitySlot section**

Open `.knowledge/kb-deferred-create-design.md`. Find the "用户指南：组件 Entity 字段自动解析" section. Add a new section AFTER it:

```markdown
## 用户指南：EntitySlot —跨帧追踪 deferred entity

当你在 deferred mode 下 `Create()` 拿到 placeholder 后，除了塞进组件字段（自动解析）外，还可以用 `EntitySlot` 追踪解析结果：

```csharp
var slot = stream.Track(stream.Create());
stream.Add(slot.Value, new Health(100));  // slot.Value 是 placeholder，组件自动解析
stream.Submit();
// slot.Value 现在是 real Entity ✅，跨帧持有也有效
world.Get<Health>(slot.Value);
```

### 锁步中继模式

```csharp
var slot = stream.Track(stream.Create());
var myDelta = stream.Snapshot();
stream.Clear();
// ... 网络交换 ...
foreach (var delta in allDeltas)
    stream.Replay(delta);   // 自动识别自己的 delta，解析 slots
slot.Value  // real Entity ✅
```

`stream.Replay(delta)` 包装了 `world.Replay(delta)`，通过 `FrameDelta.OriginStream` 自动检测哪个 delta 是自己产的，只在自己的 delta replay 后解析 tracked slots。

### 约束

- EntitySlot 不能当组件用（含引用类型，不是 unmanaged）。组件里用 `Entity`（`slot.Value`）。
- 非延迟模式下 Track 零分配（Entity 内联存储）。
- 延迟模式下每个 Track 分配一个小的 Slot 对象（opt-in）。
```

**Step 2: Update the front matter `updated` date**

Change `updated: 2026-07-04` to today's date if different.

**Step 3: Commit**

```bash
git add .knowledge/kb-deferred-create-design.md
git commit -m "docs: add EntitySlot usage guide to knowledge base"
```

---

## Task 15: Final verification and push

**Step 1: Full build**

Run: `dotnet build -c Release`
Expected: 0 errors.

**Step 2: Full test suite**

Run: `dotnet test -c Release`
Expected: All tests pass.

**Step 3: Perf gate**

Run: `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
Expected: Within thresholds.

**Step 4: Review git log**

Run: `git log --oneline -15`
Expected: Clean history of feature commits.

**Step 5: Push**

```bash
git push
```

---

## Summary of changes

| File | Lines changed | What |
|---|---|---|
| `src/MiniArch/Core/FrameDelta.cs` | +7 | `internal CommandStream? OriginStream` field |
| `src/MiniArch/Core/CommandStream.cs` | +~120 | `EntitySlot` struct, `Slot` class, `Track()`, `Replay()`, `_trackedBySeq`, `ResolveTrackedSlots`, hooks |
| `src/MiniArch/Core/World.cs` | +3 | `[Obsolete]` attribute on `Replay` |
| `tests/MiniArch.Tests/Core/EntitySlotTests.cs` | +~200 | New test file |
| `tests/MiniArch.Tests/MiniArch.Tests.csproj` | +1 | `NoWarn CS0618` |
| `.knowledge/kb-deferred-create-design.md` | +~35 | EntitySlot usage guide |

**World.cs core logic: ZERO changes.** Wire format: ZERO changes.
