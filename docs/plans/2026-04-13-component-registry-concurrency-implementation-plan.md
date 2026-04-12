# Component Registry Concurrency Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make component-type registration safe for concurrent lazy access so parallel query setup can call `GetOrCreate` without a data race or a manual warmup step.

**Architecture:** Replace the mutable shared registry state with a lock-protected copy-on-write snapshot that publishes both the forward `Type -> ComponentType` map and the reverse `ComponentType -> Type` view together. Reads stay lock-free by using the latest immutable snapshot; writes take a small lock only on the first registration of a new type.

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet

---

### Task 1: Add a concurrency regression test

**Files:**
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ComponentRegistryTests.cs`

**Step 1: Write the failing test**

- Add a test that spawns several tasks calling `GetOrCreate<Position>()` at the same time.
- Assert all tasks observe the same `ComponentType` value.
- Assert `TryGetType` still resolves the returned id back to the original type after the concurrent registration finishes.

**Step 2: Run the targeted test to verify it fails**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~ComponentRegistryTests -v minimal
```

Expected: the new concurrency test fails or is flaky until the registry is fixed.

**Step 3: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ComponentRegistryTests.cs
git commit -m "test: cover concurrent component registration"
```

### Task 2: Make `ComponentRegistry` lock-free for reads

**Files:**
- Modify: `E:/godot/arch/miniArch/src/MiniArch/Core/ComponentRegistry.cs`
- Modify: `E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ComponentRegistryTests.cs`

**Step 1: Write the minimal implementation**

- Replace the mutable shared registry fields with a published snapshot that contains both mappings.
- Keep `GetOrCreate` lazy.
- Keep read paths (`TryGetId`, `TryGetType`, `GetType`, `RegisteredTypes`) lock-free.
- Use a tiny write lock only around first-time registration of a type.

**Step 2: Re-run the targeted tests**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~ComponentRegistryTests -v minimal
```

Expected: all component registry tests pass, including the concurrent registration regression.

**Step 3: Commit**

Run:
```bash
git add E:/godot/arch/miniArch/src/MiniArch/Core/ComponentRegistry.cs E:/godot/arch/miniArch/tests/MiniArch.Tests/Core/ComponentRegistryTests.cs
git commit -m "fix: make component registry concurrent-safe"
```

### Task 3: Verify benchmark behavior

**Files:**
- Modify: none

**Step 1: Run a focused benchmark**

Run:
```powershell
dotnet run --project E:/godot/arch/miniArch/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release --filter *MiniArch_WithAll_Execute*
```

Expected: benchmark completes successfully and the MiniArch query path does not show a large regression versus the existing results.

**Step 2: If regression appears, inspect the query build path**

- Check whether the new snapshot/copy-on-write registry is adding measurable startup cost.
- Compare warm and cold query creation separately before changing anything else.
