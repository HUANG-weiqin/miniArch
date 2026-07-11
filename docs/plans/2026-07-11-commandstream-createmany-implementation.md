# CommandStream.CreateMany Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a complete `CommandStream.CreateMany` struct-writer API that supports deferred entities, `FrameDelta`, and async submit/snapshot paths.

**Architecture:** `CreateMany` is not a separate command type. It bulk-records into the existing pending-batch IR (`FrozenState.BatchEntities`, `BatchBuf`, `BatchComps`) so all existing Submit/Snapshot/Replay/Async logic remains the single source of truth. First implementation optimizes record-time API overhead only; bulk materialization is deferred until benchmark data justifies it.

**Tech Stack:** C#/.NET 8, xUnit, BenchmarkDotNet, MiniArch ECS core.

---

## Task 1: RED — Add single-component CreateMany behavior tests

**Files:**
- Modify: `tests/MiniArch.Tests/Core/CommandStreamTests.cs`

**Step 1: Add test writer type and tests**

Add near the existing small component records:

```csharp
private readonly record struct SelfRef(Entity Target);

private readonly struct PositionCreateManyWriter : ICreateManyWriter<Position>
{
    public void Write(int index, Entity entity, out Position component1)
    {
        component1 = new Position(index, index + 1);
    }
}

private readonly struct SelfRefCreateManyWriter : ICreateManyWriter<SelfRef>
{
    public void Write(int index, Entity entity, out SelfRef component1)
    {
        component1 = new SelfRef(entity);
    }
}
```

Add tests:

```csharp
[Fact]
public void CreateMany_submit_creates_entities_with_components()
{
    var world = new World();
    var stream = new CommandStream(world);
    var entities = new Entity[4];

    stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());

    for (var i = 0; i < entities.Length; i++)
        Assert.False(world.IsAlive(entities[i]));

    Assert.True(stream.Submit());

    for (var i = 0; i < entities.Length; i++)
    {
        Assert.True(world.IsAlive(entities[i]));
        Assert.True(world.TryGet(entities[i], out Position p));
        Assert.Equal(new Position(i, i + 1), p);
    }
}

[Fact]
public void CreateMany_snapshot_replays_created_entities()
{
    var source = new World();
    var stream = new CommandStream(source);
    var entities = new Entity[4];

    stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
    var delta = stream.Snapshot();

    for (var i = 0; i < entities.Length; i++)
        Assert.False(source.IsAlive(entities[i]));

    var replica = new World();
    new CommandStream(replica).Replay(delta);

    for (var i = 0; i < entities.Length; i++)
    {
        Assert.True(replica.IsAlive(entities[i]));
        Assert.True(replica.TryGet(entities[i], out Position p));
        Assert.Equal(new Position(i, i + 1), p);
    }
}

[Fact]
public async Task CreateMany_submit_and_snapshot_async_includes_created_entities()
{
    var source = new World();
    var stream = new CommandStream(source);
    var entities = new Entity[4];

    stream.CreateMany<Position, PositionCreateManyWriter>(entities, new PositionCreateManyWriter());
    var delta = await stream.SubmitAndSnapshotAsync();

    var replica = new World();
    new CommandStream(replica).Replay(delta);

    for (var i = 0; i < entities.Length; i++)
    {
        Assert.True(source.IsAlive(entities[i]));
        Assert.True(replica.IsAlive(entities[i]));
        Assert.True(replica.TryGet(entities[i], out Position p));
        Assert.Equal(new Position(i, i + 1), p);
    }
}
```

**Step 2: Run tests to verify RED**

Run:

```bash
dotnet test -c Release --filter "CreateMany_"
```

Expected: build fails because `ICreateManyWriter<>` / `CommandStream.CreateMany` do not exist.

---

## Task 2: GREEN — Implement 1-component CreateMany

**Files:**
- Modify: `src/MiniArch/Core/CommandStreamCore.cs`
- Modify: `src/MiniArch/Core/CommandStream.cs`
- Modify: `src/MiniArch/Core/ParallelCommandStream.cs`
- Create: `src/MiniArch/Core/CommandStream.CreateMany.cs`

**Step 1: Make command stream classes partial**

Change declarations:

```csharp
public abstract partial class CommandStreamCore
public sealed partial class CommandStream : CommandStreamCore
public sealed partial class ParallelCommandStream : CommandStreamCore
```

**Step 2: Add writer interface and single-component core helper**

Create `src/MiniArch/Core/CommandStream.CreateMany.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace MiniArch.Core;

public interface ICreateManyWriter<T1> where T1 : unmanaged
{
    void Write(int index, Entity entity, out T1 component1);
}

public abstract partial class CommandStreamCore
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void CreateManyCore<T1, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1>
    {
        for (var i = 0; i < entities.Length; i++)
        {
            var entity = CreateCore();
            entities[i] = entity;
            var batchIdx = GetCreatedBatchIndex(entity);
            writer.Write(i, entity, out T1 c1);
            WritePendingComponent(batchIdx, c1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected int GetCreatedBatchIndex(Entity entity)
        => entity.Id >= 0 ? _frozen.PendingBatch[entity.Id] : _pendingBatchDeferredArr[entity.Version];
}

public sealed partial class CommandStream
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CreateMany<T1, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1>
        => CreateManyCore<T1, TWriter>(entities, writer);
}

public sealed partial class ParallelCommandStream
{
    public void CreateMany<T1, TWriter>(Span<Entity> entities, TWriter writer)
        where T1 : unmanaged
        where TWriter : struct, ICreateManyWriter<T1>
    {
        lock (_storeCreateLock)
            CreateManyCore<T1, TWriter>(entities, writer);
    }
}
```

**Step 3: Run tests to verify GREEN**

Run:

```bash
dotnet test -c Release --filter "CreateMany_"
```

Expected: three tests pass.

**Step 4: Commit**

```bash
git add src/MiniArch/Core/CommandStreamCore.cs src/MiniArch/Core/CommandStream.cs src/MiniArch/Core/ParallelCommandStream.cs src/MiniArch/Core/CommandStream.CreateMany.cs tests/MiniArch.Tests/Core/CommandStreamTests.cs
git commit -m "feat: add basic commandstream createmany"
```

---

## Task 3: RED/GREEN — Add deferred placeholder resolution coverage

**Files:**
- Modify: `tests/MiniArch.Tests/Core/CommandStreamTests.cs`

**Step 1: Add tests**

```csharp
[Fact]
public void CreateMany_deferred_submit_resolves_self_references()
{
    var world = new World();
    var stream = new CommandStream(world) { DeferredEntities = true };
    var placeholders = new Entity[4];

    stream.CreateMany<SelfRef, SelfRefCreateManyWriter>(placeholders, new SelfRefCreateManyWriter());

    for (var i = 0; i < placeholders.Length; i++)
        Assert.True(placeholders[i].IsPlaceholder);

    Assert.True(stream.Submit());

    var count = 0;
    foreach (var chunk in world.Query(new QueryDescription().With<SelfRef>()).GetChunks())
    {
        var entities = chunk.GetEntities();
        var refs = chunk.GetSpan<SelfRef>();
        for (var i = 0; i < chunk.Count; i++)
        {
            Assert.Equal(entities[i], refs[i].Target);
            count++;
        }
    }
    Assert.Equal(4, count);
}

[Fact]
public void CreateMany_deferred_snapshot_replay_resolves_self_references()
{
    var source = new World();
    var stream = new CommandStream(source) { DeferredEntities = true };
    var placeholders = new Entity[4];

    stream.CreateMany<SelfRef, SelfRefCreateManyWriter>(placeholders, new SelfRefCreateManyWriter());
    var delta = stream.Snapshot();
    stream.Clear();

    var replica = new World();
    new CommandStream(replica).Replay(delta);

    var count = 0;
    foreach (var chunk in replica.Query(new QueryDescription().With<SelfRef>()).GetChunks())
    {
        var entities = chunk.GetEntities();
        var refs = chunk.GetSpan<SelfRef>();
        for (var i = 0; i < chunk.Count; i++)
        {
            Assert.Equal(entities[i], refs[i].Target);
            count++;
        }
    }
    Assert.Equal(4, count);
}
```

**Step 2: Run tests**

Run:

```bash
dotnet test -c Release --filter "CreateMany_deferred"
```

Expected: tests pass if Task 2 reused pending-batch IR correctly. If not, fix only the minimal failing path.

**Step 3: Commit**

```bash
git add tests/MiniArch.Tests/Core/CommandStreamTests.cs src/MiniArch/Core/CommandStream.CreateMany.cs
git commit -m "test: cover createmany deferred resolution"
```

---

## Task 4: RED/GREEN — Add 2..8 component overloads

**Files:**
- Modify: `src/MiniArch/Core/CommandStream.CreateMany.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandStreamTests.cs`

**Step 1: Add test components and writer**

Add small component records if needed:

```csharp
private readonly record struct C4(int Value);
private readonly record struct C5(int Value);
private readonly record struct C6(int Value);
private readonly record struct C7(int Value);
private readonly record struct C8(int Value);

private readonly struct EightComponentCreateManyWriter : ICreateManyWriter<Position, Velocity, Health, SignalPayloadField, C4, C5, C6, C7>
{
    public void Write(int index, Entity entity, out Position c1, out Velocity c2, out Health c3, out SignalPayloadField c4, out C4 c5, out C5 c6, out C6 c7, out C7 c8)
    {
        c1 = new Position(index, index + 1);
        c2 = new Velocity(index + 2, index + 3);
        c3 = new Health(index + 4);
        c4 = new SignalPayloadField(index, index + 1, index + 2);
        c5 = new C4(index + 5);
        c6 = new C5(index + 6);
        c7 = new C6(index + 7);
        c8 = new C7(index + 8);
    }
}
```

Add test:

```csharp
[Fact]
public void CreateMany_supports_eight_components()
{
    var world = new World();
    var stream = new CommandStream(world);
    var entities = new Entity[3];

    stream.CreateMany<Position, Velocity, Health, SignalPayloadField, C4, C5, C6, C7, EightComponentCreateManyWriter>(entities, new EightComponentCreateManyWriter());
    Assert.True(stream.Submit());

    for (var i = 0; i < entities.Length; i++)
    {
        Assert.True(world.TryGet(entities[i], out Position p));
        Assert.Equal(new Position(i, i + 1), p);
        Assert.True(world.TryGet(entities[i], out C7 c7));
        Assert.Equal(new C7(i + 8), c7);
    }
}
```

**Step 2: Run to verify RED**

Run:

```bash
dotnet test -c Release --filter "CreateMany_supports_eight_components"
```

Expected: build fails because 8-component interface/overload does not exist.

**Step 3: Implement interfaces and overloads for 2..8**

Extend `CommandStream.CreateMany.cs` with generated-looking explicit overloads. Each overload does:

```csharp
var entity = CreateCore();
entities[i] = entity;
var batchIdx = GetCreatedBatchIndex(entity);
writer.Write(i, entity, out T1 c1, out T2 c2, ...);
WritePendingComponent(batchIdx, c1);
WritePendingComponent(batchIdx, c2);
...
```

Do not introduce delegate overloads.

**Step 4: Run tests**

Run:

```bash
dotnet test -c Release --filter "CreateMany_"
```

Expected: all CreateMany tests pass.

**Step 5: Commit**

```bash
git add src/MiniArch/Core/CommandStream.CreateMany.cs tests/MiniArch.Tests/Core/CommandStreamTests.cs
git commit -m "feat: support createmany component arities"
```

---

## Task 5: Add benchmark and performance decision data

**Files:**
- Create: `tests/MiniArch.Benchmarks/CreateManyBenchmarks.cs`

**Step 1: Add benchmark**

Create benchmark comparing:

- per-entity `Create()+Add<T>...+Submit`
- `CreateMany<T1..T8,TWriter>+Submit`
- `CreateMany<T1..T8,TWriter>+SubmitAndSnapshotAsync().GetAwaiter().GetResult()`

Use `[Params(1000, 10000, 30000)]` and keep world setup out of measured path except the unavoidable target world creation if the compared method also creates it. Prefer per-iteration new `World` for all methods so measurement scopes match.

**Step 2: Run benchmark smoke**

Run:

```bash
dotnet run -c Release --project tests/MiniArch.Benchmarks -- --filter "*CreateMany*" --job short
```

Expected: benchmark runs and prints all methods.

**Step 3: Commit**

```bash
git add tests/MiniArch.Benchmarks/CreateManyBenchmarks.cs
git commit -m "perf: add createmany benchmark"
```

---

## Task 6: Public API baseline and docs/knowledge update

**Files:**
- Modify: `tests/MiniArch.Tests/PublicApiSentinelTests.cs`
- Modify: `.knowledge/kb-command-stream.md`
- Optionally modify: `.knowledge/kb-test-workflow.md`

**Step 1: Run sentinel to capture intended diff**

Run:

```bash
dotnet test -c Release --filter "PublicApiSentinel"
```

Expected: failure showing new public API.

**Step 2: Regenerate baseline**

Run with environment variable per project instructions, then paste generated baseline into `PublicApiSentinelTests.cs`:

```bash
GENERATE_API_BASELINE=1 dotnet test -c Release --filter "PublicApiSentinel"
```

**Step 3: Update knowledge**

Update `.knowledge/kb-command-stream.md` with a concise section:

- `CreateMany` is pending-batch IR, not `Action<World>`.
- Struct-writer only, 1..8 components.
- Full support for Submit/Snapshot/Async because it reuses FrozenState.
- Benchmark result and keep/delete decision.

Update `updated: 2026-07-11` if changed.

**Step 4: Commit**

```bash
git add tests/MiniArch.Tests/PublicApiSentinelTests.cs .knowledge/kb-command-stream.md .knowledge/kb-test-workflow.md
git commit -m "docs: document commandstream createmany"
```

---

## Task 7: Final verification gate

**Files:**
- No edits expected.

**Step 1: Run full tests**

```bash
dotnet test -c Release
```

Expected: all tests pass.

**Step 2: Run architecture perf gate**

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

Expected: Movement ≥1642 rounds/s, Attack ≥997 rounds/s, memory stable.

**Step 3: Run CreateMany benchmark**

```bash
dotnet run -c Release --project tests/MiniArch.Benchmarks -- --filter "*CreateMany*" --job short
```

Expected: data sufficient to decide whether API has retention value.

**Step 4: Summarize decision**

If CreateMany improves record+submit materially, keep it. If not, revert implementation commits and keep only design/benchmark report if useful.
