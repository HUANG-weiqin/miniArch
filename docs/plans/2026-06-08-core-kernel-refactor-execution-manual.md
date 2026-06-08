# Core Kernel Refactor Execution Manual

> **For Claude / other AI workers:** REQUIRED SUB-SKILL: Use `executing-plans` to implement this plan task-by-task. Before any code task, also use `karpathy-guidelines`; for bugfix-style tasks use `test-driven-development`; before claiming done use `verification-before-completion`.

**Goal:** 重构 `src/MiniArch/Core` 的代码形状和边界，让世界第一速度的热路径更难被改坏、更容易继续优化，同时不改变核心算法、不改变公开行为、不改业务/测试场景代码。

**Architecture:** 保留当前最快机制：`EntityRecord[]`、one-archetype-one-storage、flat `byte[]` SoA、`componentId -> columnIndex` direct map、query snapshot、edge cache、`CopySmall`。重构只做“文件边界/内部边界/生成边界”，禁止引入 runtime service、interface、virtual dispatch 或热路径对象层。

**Tech Stack:** C# / .NET, xUnit, `dotnet test`, `dotnet run -c Release --project perf/HeroComing.Perf`, MiniArch.Core unsafe flat storage.

---

## 0. 给低智能高速 AI 的总命令

你不是来“设计新 ECS”的。你是来保护一条已经最快的循环：

```csharp
foreach chunk in query.cached_chunks:
    resolve column refs once
    for row in chunk.count:
        ref component = ref Unsafe.Add(ref first, row)
        system(ref component)
```

任何让这条循环多一层间接、多一次分配、多一个 virtual/interface 调用、多一个 Dictionary 查找的改动，默认都是错的。

---

## 1. 绝对原则

### 1.1 只能改核心

允许修改：

- `src/MiniArch/Core/*.cs`
- `src/MiniArch/Ecs/*.cs`，仅当公共 facade 的薄转发或命名需要同步，且不得改行为
- `tests/MiniArch.Tests/Core/*.cs`，仅新增/调整核心回归测试
- `docs/plans/*.md`，计划文档
- `.knowledge/*.md`，只有真的学到新事实才更新

禁止修改：

- `tests/HeroPipeline.Tests/**` 的业务逻辑
- `perf/**` 的 benchmark 场景语义
- public API 语义
- package/project 文件，除非计划明确要求且获得人工确认

### 1.2 不许“聪明重构”

禁止：

- 把热路径抽成 interface/service
- 把 `Archetype` 存储改成多 chunk
- 把 flat `byte[]` 改成对象数组
- 把 `EntityRecord[]` 拆回 `_versions[] + _locations[]`
- 把 direct map 改成 Dictionary
- 把 query invalidation 改成复杂系统，除非有 benchmark 证明全局版本号是瓶颈
- 把 `CommandBuffer` 改成对象命令列表
- 为“未来可能需要”加抽象

允许：

- partial 文件拆分
- internal static helper，但必须能 inline，且不能引入状态对象
- source generator 生成重复泛型重载，但生成代码必须等价于当前手写热路径
- 纯测试和 benchmark 保护

### 1.3 性能门禁

凡是修改 `src/MiniArch/Core/`，必须跑：

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

如果改动也触及 query facade 或 public API，再跑：

```bash
dotnet test -c Release --project tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj
```

HeroComing.Perf 门槛：

- Movement ≥ 866 rounds/s
- Attack ≥ 200 rounds/s
- 无持续内存增长
- 无异常/崩溃

低于门槛：立刻回退本任务改动，不要解释。

### 1.4 Git 纪律

- 每个任务完成后先看 `git diff`。
- 不要提交，除非操作者明确要求 commit。
- 如果允许 commit，每个任务一个小 commit。
- 失败时不要堆补丁；回到最近通过状态。

---

## 2. 当前核心机制：必须背下来

### 2.1 One-line truth

MiniArch = `EntityRecord[]` 位置表 + `Signature -> Archetype` flat SoA 存储 + query chunk snapshot + deferred command replay。

### 2.2 核心状态

```csharp
EntityRecord[] records; // id -> { Archetype, RowIndex, Version }
Dictionary<Signature, Archetype> archetypes;
Archetype[] archetypeSnapshot;
int archetypeVersion;
```

```csharp
sealed class Archetype
{
    Entity[] _entities;
    byte[] _data;
    int[] _columnByteOffsets;
    int[] _elementSizes;
    int[] _componentIdToColumnIndex;
    int _count;
    int _capacity;
    Archetype?[] _addDestinationCache;
    Archetype?[] _removeDestinationCache;
}
```

### 2.3 核心状态转移

Create:

```text
component types -> signature -> archetype -> append row -> write components -> records[id]
```

Set existing:

```text
records[id] -> archetype -> column index -> write in-place
```

Add/Remove:

```text
records[id] -> source archetype -> destination signature -> destination archetype
-> copy shared columns -> swap-remove source -> update moved record -> update current record
```

Query:

```text
description -> filter -> cached matching archetypes/chunks -> span iteration
```

---

## 3. 重构总路线

分 5 个阶段。每个阶段必须可以单独回滚。

1. **Baseline freeze**：记录当前测试和 perf 输出，不改代码。
2. **World 文件拆分**：只移动代码，建立状态机边界，不改逻辑。
3. **Archetype 文件拆分 / Storage Kernel 收束**：只移动或轻量改名，保护 flat storage 热路径。
4. **CommandBuffer 边界整理**：只整理录制、创建实体 materialize、direct apply 的文件边界。
5. **可选生成化**：只有前 4 阶段稳定后，才考虑 source generator 生成 Create 重载。

不要并行修改两个阶段。

---

## 4. Task 0: Baseline freeze

**Files:**

- Read: `.knowledge/INDEX.md`
- Read: `.knowledge/kb-core-ecs.md`
- Read: `.knowledge/kb-chunk-storage.md`
- Read: `.knowledge/kb-cache-optimization.md`
- Read: `.knowledge/kb-query-invalidation.md`
- Read: `.knowledge/kb-hero-pipeline-regression.md`
- No code changes

**Step 1: Inspect clean status**

Run:

```bash
git status --short
```

Expected:

- Know exactly what is already modified before starting.
- If dirty, do not overwrite user changes.

**Step 2: Run core tests**

Run:

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
```

Expected: PASS.

**Step 3: Run performance gate**

Run:

```bash
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected:

- Movement ≥ 866 rounds/s
- Attack ≥ 200 rounds/s
- No crash
- No sustained memory growth

**Step 4: Save baseline in task notes**

Do not edit `.knowledge` just to paste transient numbers. Only update knowledge if benchmark baseline mechanism already updates it or if the run reveals a durable new fact.

---

## 5. Task 1: Make `World` partial without moving methods

**Purpose:** 开最小口子，确认 partial 本身不影响性能。

**Files:**

- Modify: `src/MiniArch/Core/World.cs`

**Step 1: Minimal code change**

Change:

```csharp
public sealed class World : IDisposable
```

to:

```csharp
public sealed partial class World : IDisposable
```

No other edits.

**Step 2: Run tests**

Run:

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
```

Expected: PASS.

**Step 3: Run perf gate**

Run:

```bash
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: no regression below gate.

**Step 4: Review diff**

Run:

```bash
git diff -- src/MiniArch/Core/World.cs
```

Expected: one-line diff only.

---

## 6. Task 2: Split entity lifecycle from `World.cs`

**Purpose:** 把 entity id/version/free-list 生命周期从巨型文件中移出。只移动代码，不改行为。

**Files:**

- Modify: `src/MiniArch/Core/World.cs`
- Create: `src/MiniArch/Core/World.EntityLifecycle.cs`

**Move only these methods/regions if present:**

- `EnsureCapacity`
- `ReserveEntitySlot`
- `ReserveDeferredEntity`
- `ReleaseDeferredEntity`
- `CreateInArchetype`
- `CreateMany`
- `Destroy`
- `DestroySingle`
- `DestroySubtree` helpers
- `TryGetLocation`
- `GetRequiredLocation`
- `IsAlive`
- free-list helpers using `_freeIds`, `_freeIdCount`, `_entitySlotCount`, `_records`

**Do not move:**

- query cache methods
- `GetOrCreateArchetype`
- create generic overloads yet
- command replay methods yet

**Step 1: Create partial file skeleton**

```csharp
using System.Buffers;
using System.Runtime.CompilerServices;

namespace MiniArch;

public sealed partial class World
{
    // moved methods only
}
```

Add only the `using` directives required by moved code.

**Step 2: Move methods verbatim**

Rules:

- Do not rename methods.
- Do not reorder statements inside methods.
- Do not change attributes.
- Do not change comments unless they became false.
- Keep private helper visibility unchanged.

**Step 3: Build**

Run:

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
```

Expected: PASS.

If compile fails:

- Add missing `using`.
- Do not “fix” logic.

**Step 4: Perf gate**

Run:

```bash
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: pass thresholds.

**Step 5: Diff audit**

Run:

```bash
git diff -- src/MiniArch/Core/World.cs src/MiniArch/Core/World.EntityLifecycle.cs
```

Expected:

- Mostly code movement.
- No algorithm change.

---

## 7. Task 3: Split structural change from `World.cs`

**Purpose:** 把 Add/Set/Remove/Move 的状态迁移集中到一个文件。

**Files:**

- Modify: `src/MiniArch/Core/World.cs`
- Create: `src/MiniArch/Core/World.StructuralChange.cs`

**Move only these methods/regions if present:**

- `MoveEntityCore`
- `FinishMoveEntity`
- `MoveEntity`
- `ApplyTypedAddOrSet`
- `ApplyRawAddOrSet`
- `MoveEntityFromBytes`
- `GetOrCreateAddDestinationArchetype`
- `RemoveBoxed`
- public `Add<T>` / `Set<T>` / `Remove<T>` methods if currently in same region
- raw replay helpers that directly mutate components

**Step 1: Create skeleton**

```csharp
using System.Runtime.CompilerServices;

namespace MiniArch;

public sealed partial class World
{
    // moved structural mutation methods only
}
```

If unsafe methods are moved, ensure file compiles with existing project unsafe settings; no extra project changes.

**Step 2: Move verbatim**

Preserve:

- `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
- `unsafe`
- null-forgiving usage
- current `Set` semantics: missing component means add

**Step 3: Add no tests yet**

This is mechanical movement. Existing tests should cover behavior.

**Step 4: Run core tests and perf**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: PASS and above thresholds.

---

## 8. Task 4: Split query ownership from `World.cs`

**Purpose:** 把 query/filter/cache 逻辑独立成文件，避免和 mutation 逻辑混杂。

**Files:**

- Modify: `src/MiniArch/Core/World.cs`
- Create: `src/MiniArch/Core/World.QueryCache.cs`

**Move only these methods/regions if present:**

- `Query(in QueryDescription description)`
- `GetAdvancedQuery`
- query filter cache helpers
- `PublishArchetypeSnapshot`
- properties/helpers directly related to `_queries`, `_queryFiltersByDescription`, `_archetypeSnapshot`, `_archetypeVersion`

**Do not change:**

- global `_archetypeVersion` invalidation strategy
- snapshot publication ordering
- volatile usage

**Step 1: Move verbatim**

Use skeleton:

```csharp
using System.Threading;

namespace MiniArch;

public sealed partial class World
{
    // moved query/cache methods only
}
```

**Step 2: Run query-focused tests**

Run:

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj --filter Query
```

Expected: PASS.

**Step 3: Run full core tests and perf**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: PASS and above thresholds.

---

## 9. Task 5: Split generic create overloads from `World.cs`

**Purpose:** 把 `Create<T1..T16>` 和 create-archetype generic cache 放到单独文件，为后续 source generation 做准备。

**Files:**

- Modify: `src/MiniArch/Core/World.cs`
- Create: `src/MiniArch/Core/World.Create.cs`

**Move:**

- public `Create<T...>` overloads
- `GetOrCreateCreateArchetype<T...>` overloads
- `CreateArchetypeCache<T...>` nested helpers if they exist in `World.cs`
- `SetCreatedComponent` helper if it is only used by create overloads

**Do not change:**

- overload count
- generic constraints
- `GetComponentType<T>()` usage unless existing helper already uses `Component<T>.ComponentType`
- exact component write order

**Step 1: Move verbatim**

Skeleton:

```csharp
using System.Runtime.CompilerServices;

namespace MiniArch;

public sealed partial class World
{
    // moved create overloads/cache only
}
```

**Step 2: Run create-focused tests**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj --filter Create
```

Expected: PASS.

**Step 3: Run full core tests and perf**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: PASS and above thresholds.

---

## 10. Task 6: Make `Archetype` partial without moving methods

**Files:**

- Modify: `src/MiniArch/Core/Archetype.cs`

**Step 1: Minimal code change**

Change:

```csharp
public sealed class Archetype
```

to:

```csharp
public sealed partial class Archetype
```

No other edits.

**Step 2: Run tests and perf**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: PASS and above thresholds.

---

## 11. Task 7: Split `Archetype` storage kernel

**Purpose:** 把 flat byte storage 热路径集中，不改变任何机器行为。

**Files:**

- Modify: `src/MiniArch/Core/Archetype.cs`
- Create: `src/MiniArch/Core/Archetype.Storage.cs`

**Move only:**

- `EnsureCapacity`
- `CreateStorage`
- `ThrowIfManagedComponent`
- `AlignUp`
- `GetByteOffset`
- `GetColumnBytes`
- `GetComponentRef<T>`
- `GetComponentRefAt<T>`
- `SetComponentAtTyped<T>`
- `GetComponentAt<T>`
- `GetComponent<T>`
- `GetComponentSpanAt<T>`
- `GetComponentSpan<T>`
- `WriteColumnTo<T>`
- `ReadColumnFrom<T>`
- `ReadComponentRaw`
- `WriteComponentRaw`

**Step 1: Create skeleton**

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public sealed partial class Archetype
{
    // moved storage methods only
}
```

**Step 2: Preserve attributes exactly**

Especially preserve:

- `[SkipLocalsInit]`
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]`

**Step 3: Run storage-focused tests**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "Chunk|Archetype|Component"
```

Expected: PASS.

**Step 4: Run full gate**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: PASS and above thresholds.

---

## 12. Task 8: Split `Archetype` row movement/copy kernel

**Purpose:** 把迁移复制和 swap-remove 的字节搬运集中，方便未来只替换 copy kernel。

**Files:**

- Modify: `src/MiniArch/Core/Archetype.cs`
- Create: `src/MiniArch/Core/Archetype.Copy.cs`

**Move only:**

- `CopySharedComponentsFrom`
- `CopyAllColumnsFrom`
- `CopyComponent`
- `CopyColumnsFrom`
- `CopyColumnFrom`
- `CopyRemovedRow`
- `CopySmall`

**Step 1: Move verbatim**

Skeleton:

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public sealed partial class Archetype
{
    // moved copy methods only
}
```

**Step 2: Do not optimize yet**

Do not sort copy columns. Do not add SIMD. Do not change alignment. This task is movement only.

**Step 3: Run tests**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "Structural|Add|Remove|Clone|Chunk"
```

Expected: PASS.

**Step 4: Run full gate**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: PASS and above thresholds.

---

## 13. Task 9: Split `CommandBuffer` by behavior, movement only

**Purpose:** 降低 `CommandBuffer.cs` 认知负担，但不碰 submit 语义。

**Files:**

- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Create: `src/MiniArch/Core/CommandBuffer.Record.cs`
- Create: `src/MiniArch/Core/CommandBuffer.Submit.cs`
- Create: `src/MiniArch/Core/CommandBuffer.Storage.cs`

**Step 1: Make class partial**

Change:

```csharp
public sealed class CommandBuffer
```

to:

```csharp
public sealed partial class CommandBuffer
```

Run tests before moving anything.

**Step 2: Move record API to `CommandBuffer.Record.cs`**

Move:

- `Create`
- `Add<T>`
- `Set<T>`
- `Remove<T>`
- `Destroy`
- `Clone`
- hierarchy record methods
- per-entity op allocation helpers

**Step 3: Move submit/replay to `CommandBuffer.Submit.cs`**

Move:

- `Submit`
- direct apply helpers
- `BuildDelta`
- `EmitOp`
- `ApplyOpDirect`
- created entity materialization helpers
- archetype cache helpers used during submit

**Step 4: Move slab/type-info storage to `CommandBuffer.Storage.cs`**

Move:

- `EnsureSlabSpace`
- `CopyData`
- `CopyComponentFromArchetype`
- `ResolveTypeInfo`
- `GetComponentTypeId<T>`
- `Clear`

**Step 5: Preserve all internals**

Do not change:

- `InlineMap`
- `OverflowPool`
- ThreadStatic buffers
- slab size
- archetype cache size
- created/existing command ordering

**Step 6: Run tests and perf**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj --filter CommandBuffer
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected: PASS and above thresholds.

---

## 14. Task 10: Add hot-path regression tests only if missing

**Purpose:** 用测试锁住“重构不改行为”的关键不变量。

**Files:**

- Modify or create tests under: `tests/MiniArch.Tests/Core/`

**Add tests only for behavior gaps. Do not benchmark in unit tests.**

Candidate tests:

### 10.1 Structural move updates moved entity record

Test shape:

```csharp
[Fact]
public void Remove_UsesSwapRemove_UpdatesMovedEntityLocation()
{
    using var world = new World();
    var a = world.Create(new Position(1), new Velocity(1));
    var b = world.Create(new Position(2), new Velocity(2));

    world.Remove<Velocity>(a);

    Assert.True(world.TryGetLocation(b, out var info));
    Assert.Equal(0, info.RowIndex);
    Assert.Equal(2, world.Get<Position>(b).Value);
}
```

Use existing test component types. If names differ, reuse existing components from current test files instead of adding duplicate structs.

### 10.2 Set existing component does not migrate

Test shape:

```csharp
[Fact]
public void Set_ExistingComponent_WritesInPlace()
{
    using var world = new World();
    var e = world.Create(new Position(1));
    Assert.True(world.TryGetLocation(e, out var before));

    world.Set(e, new Position(2));

    Assert.True(world.TryGetLocation(e, out var after));

    Assert.Same(before.Archetype, after.Archetype);
    Assert.Equal(before.RowIndex, after.RowIndex);
    Assert.Equal(2, world.Get<Position>(e).Value);
}
```

### 10.3 Query refresh only after new archetype creation

Test shape:

```csharp
[Fact]
public void Query_ReusesSnapshot_WhenNoNewArchetypeCreated()
{
    using var world = new World();
    world.Create(new Position(1));
    var query = world.Query(new QueryDescription().With<Position>());

    _ = query.RefreshCount;
    foreach (var _ in query) { }
    var afterFirst = query.RefreshCount;

    world.Create(new Position(2)); // same archetype
    foreach (var _ in query) { }

    Assert.Equal(afterFirst, query.RefreshCount);
}
```

Only add if current tests do not already cover this.

**Run:**

```bash
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

---

## 15. Task 11: Optional source generation feasibility spike

**Do this only after Tasks 0-10 pass. Ask human before starting.**

**Purpose:** 生成 `Create<T1..T16>` 重复代码，减少维护成本，但不得改变生成产物的热路径形状。

**Preferred low-risk approach:** 不引入 Roslyn generator 项目。先用 repo-local script 或 template 生成 checked-in `.cs` 文件。只有证明无性能回归后再考虑真正 Source Generator。

**Files candidate:**

- Create: `tools/GenerateWorldCreate.ps1` or similar only if tools are acceptable
- Generate/modify: `src/MiniArch/Core/World.Create.Generated.cs`
- Modify: `src/MiniArch/Core/World.Create.cs`

**Hard constraints:**

- Generated code must be committed as source if commits are allowed.
- Generated code must include `[MethodImpl(MethodImplOptions.AggressiveInlining)]` where current handwritten code has it.
- No reflection in hot path.
- No params array in hot path.
- No `Span<ComponentType>` allocation in hot path.
- No `Dictionary` lookup after generic static cache is warm.

**Abort conditions:**

- Any perf regression > noise.
- Generated code becomes harder to inspect than handwritten code.
- Project file changes become necessary without human approval.

---

## 16. Task 12: Optional micro-optimization experiments

**Do not mix these with refactor tasks. One experiment per branch/worktree.**

Candidate experiments from knowledge base:

1. CommandBuffer 按 archetype 批量 materialize
2. Query 快照失效拆分
3. `EntityRecord` 去引用化/压缩
4. NativeMemory + 64B alignment
5. CopySharedComponentsFrom source-column ordering

Rules:

- Each experiment starts from a clean post-refactor baseline.
- Each experiment needs one hypothesis and one measurement command.
- If not faster under `HeroComing.Perf`, revert.
- Never merge multiple experiments before measuring individually.

---

## 17. Final verification checklist

Before saying “done”, run:

```bash
git status --short
dotnet test -c Release --project tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet test -c Release --project tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Then inspect:

```bash
git diff --stat
git diff -- src/MiniArch/Core src/MiniArch/Ecs tests/MiniArch.Tests/Core docs/plans .knowledge
```

Completion requires:

- Core tests PASS
- HeroPipeline tests PASS if any public/facade behavior touched
- HeroComing.Perf above thresholds
- No sustained memory growth
- No unrelated files modified
- `.knowledge` updated only if new durable facts were learned
- `.knowledge/INDEX.md` still accurate if knowledge pages changed

---

## 18. Human review prompts after each phase

After each phase, report exactly:

```text
Phase: <name>
Changed files: <list>
Behavior changes intended: none / list
Tests run: <commands>
Perf result: Movement X, Attack Y
Risk: <one sentence>
Next recommended task: <task number>
```

If the worker cannot fill this, the phase is not complete.

---

## 19. The brutal rule

The current implementation is fast because the machine does little work.

So the refactor is successful only if, after cleanup, the machine still does little work.

Do not make the code “prettier” by making the CPU busier.
