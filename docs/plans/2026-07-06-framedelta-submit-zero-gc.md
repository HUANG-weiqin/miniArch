# FrameDelta Deserialize and Submit Zero GC Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make hot `FrameDelta` receive/replay setup and create-heavy snapshot/submit paths steady-state zero GC.

**Architecture:** `FrameDelta` gets an instance `Deserialize(ReadOnlySpan<byte>)` that reuses its owned `_buffer`; one-shot allocation is renamed to explicit `FromWire`. Submit/snapshot create emission uses pooled `RawComponentValue` buffers and span-based `AddCreate` so temporary arrays and resize copies disappear.

**Tech Stack:** C#/.NET 8, xUnit tests, MiniArch.Core, `ArrayPool<T>`, `Span<T>`.

---

### Task 1: Add API and allocation behavior tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/FrameDeltaTests.cs` or closest existing FrameDelta test file
- Modify: `tests/MiniArch.Tests/Core/CommandStreamTests.cs` or closest existing allocation/perf-ish test file

**Step 1: Write failing tests**

Add tests for:

1. `FrameDelta.Deserialize_reuses_existing_buffer_when_capacity_is_sufficient`
   - Build a small valid delta.
   - Serialize to `wire`.
   - Create `var reusable = new FrameDelta();`.
   - Call `reusable.Deserialize(wire)` once to allocate capacity.
   - Measure allocated bytes around a second `reusable.Deserialize(wire)`.
   - Assert zero allocation for the second call.
   - Assert replay/checksum behavior still matches `FromWire`.

2. `FrameDelta.FromWire_returns_independent_delta`
   - Build a delta and copy `wire` to a mutable byte array.
   - `var delta = FrameDelta.FromWire(wireArray);`
   - Mutate the source `wireArray` after construction.
   - Assert `delta.AsSpan()` still has original bytes.

3. `Snapshot_create_batch_emission_is_zero_alloc_after_warmup`
   - Use a persistent `World` and `CommandStream`.
   - Warm up one create+snapshot path enough to size `FrameDelta` and pools.
   - Measure `stream.Snapshot()` allocation on a create-heavy frame where capacity is already stable.
   - Assert allocation is zero or at least no per-create managed arrays remain. If exact zero is too brittle because test scaffolding allocates, assert a tight threshold and document why.

**Step 2: Run focused tests and verify RED**

Run:

```powershell
dotnet test -c Release --filter "Deserialize_reuses_existing_buffer_when_capacity_is_sufficient|FromWire_returns_independent_delta|Snapshot_create_batch_emission_is_zero_alloc_after_warmup"
```

Expected: fail because instance `Deserialize`/`FromWire` do not exist and/or snapshot still allocates temporary arrays.

---

### Task 2: Implement reusable FrameDelta deserialization API

**Files:**
- Modify: `src/MiniArch/Core/FrameDelta.cs`
- Modify call sites found by `rg "FrameDelta\.Deserialize"`

**Step 1: Add instance API**

Implement:

```csharp
public void Deserialize(ReadOnlySpan<byte> wire)
```

Behavior:
- Throws when `wire.Length > MaxFrameBytes`.
- Ensures `_buffer.Length >= wire.Length` without shrinking.
- Copies `wire` into `_buffer.AsSpan(0, wire.Length)`.
- Sets `_length = wire.Length` and `_opCount = 0` before validation scan.
- Reuses the existing decoder scan to recompute `_opCount` and validate truncation/malformed varints.
- Leaves `FrameDelta` owning its data; no external span is retained.

**Step 2: Add explicit allocation factory**

Implement:

```csharp
public static FrameDelta FromWire(ReadOnlySpan<byte> wire)
{
    var delta = new FrameDelta();
    delta.Deserialize(wire);
    return delta;
}
```

Remove or rename the old static `Deserialize(ReadOnlySpan<byte>)`, because C# cannot expose static and instance members with the same signature. Update XML docs so allocation semantics are honest.

**Step 3: Update call sites**

- Simple one-shot call sites become `FrameDelta.FromWire(wire)`.
- Hot/repeated call sites in tools/tests should prefer a reused `FrameDelta` plus `.Deserialize(wire)` where natural.

**Step 4: Verify focused tests GREEN**

Run the focused tests from Task 1.

---

### Task 3: Remove Submit/Snapshot create temporary arrays

**Files:**
- Modify: `src/MiniArch/Core/CommandStreamCore.cs:848-888`
- Modify: `src/MiniArch/Core/FrameDelta.cs:377-402`

**Step 1: Change AddCreate to span**

Change:

```csharp
internal void AddCreate(Entity e, RawComponentValue[] components)
```

to:

```csharp
internal void AddCreate(Entity e, ReadOnlySpan<RawComponentValue> components)
```

Use `components.Length` and `ref readonly var c = ref components[i]` where appropriate.

**Step 2: Pool RawComponentValue buffers in EmitCreateFromBatch**

Change `EmitCreateFromBatch` to:
- Keep `Array.Empty<RawComponentValue>()` for zero-component creates.
- Rent `RawComponentValue[]` from `ArrayPool<RawComponentValue>.Shared` for `rawCount > 0`.
- Fill only `outIdx` entries.
- If `outIdx == 0`, emit empty create and return.
- Sort/dedup only `comps.AsSpan(0, outIdx)`.
- Pass `comps.AsSpan(0, outIdx)` to `delta.AddCreate`.
- Return the rented array in `finally`.
- Delete all `Array.Resize(ref comps, ...)` calls.

**Step 3: Verify focused tests GREEN**

Run Task 1 focused tests.

---

### Task 4: Update docs and knowledge

**Files:**
- Modify: `.knowledge/kb-command-stream.md`
- Modify: `.knowledge/kb-snapshot-persistence.md` if FrameDelta receive API is documented there; otherwise do not touch.
- Modify: `.knowledge/INDEX.md` only if keywords/modules change; likely not needed.

**Step 1: Document API change**

Update CommandStream/FrameDelta knowledge:
- `delta.Deserialize(wire)` is the reusable steady-state zero-GC receive API.
- `FrameDelta.FromWire(wire)` is the explicit one-shot allocation API.
- Submit/Snapshot create emission now pools `RawComponentValue` and does not resize temporary arrays.

**Step 2: Keep front matter valid**

Set `updated: 2026-07-06 (...)`.

---

### Task 5: Full verification and perf gates

**Files:** none unless failures require fixes.

**Step 1: Run full tests**

```powershell
dotnet test -c Release
```

Expected: all tests pass.

**Step 2: Run architecture perf gate**

```powershell
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

Expected: Movement ≥1210 rounds/s, Attack ≥767 rounds/s, memory stable.

**Step 3: Optional targeted profiling**

```powershell
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario snapshot-only
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-duplicates
```

Expected: no recurring Gen0 caused by `EmitCreateFromBatch` temporary arrays.

---

### Task 6: Review

**Files:** all modified files.

Review checklist:
- No public API keeps ambiguous allocating `Deserialize` static name.
- Reusable `Deserialize` is still owning-copy, not external-buffer borrowing.
- `ArrayPool<RawComponentValue>` arrays are always returned on all paths.
- Span lengths, not rented array lengths, drive sorting/dedup/writing.
- No new per-frame containers in hot paths.
- Knowledge docs match code.
