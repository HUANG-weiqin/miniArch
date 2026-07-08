# Explicit Dense Value Diff Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.
> **Requires another agent session** — this is a separate spike that builds on the shared-value-tracker worktree but does NOT modify existing source/tests/knowledge until implementation starts.

**Goal:** 实现 `World.CreateDenseValueDiff<TComponent, TValue, TProjector>()` API，语义等价于 `ManualGenericTracker<T>` 手写 shadow diff，性能达到 `NewExplicitDiff >= ManualDense * 0.95`。

**Architecture:**
- 新增三个 public 接口：`IValueProjector<TComponent, TValue>`（投影）、`IValueChangeSink<TValue>`（输出回调）
- 新增 sealed class `DenseValueDiff<TComponent, TValue, TProjector>`：独立的 per-instance state（`_oldValues[]`, `_touchedEntities[]`, `_touchedCount`），不共享、不写入 world 注册表、不 hook Set 路径
- 工厂方法 `World.CreateDenseValueDiff<...>()` 接受可选的 `QueryDescription` 和投影器，默认自动 `.With<TComponent>()`
- Hot path：`chunk.GetEntities` + `chunk.GetSpan<TComponent>` + `_projector.Project()` + `IEquatable<TValue>.Equals()`→`sink.OnChanged()`
- 不使用 `Dictionary`、`EqualityComparer<T>.Default`、`ValueChange<T>[]` 输出数组

**Tech Stack:** C# unmanaged generics (`where T : unmanaged, IEquatable<TValue>`), struct constrained interface, `MethodImpl(MethodImplOptions.AggressiveInlining)`, xUnit, HeroComing.Perf benchmark harness.

**Status:** Not started. This plan describes the implementation spike AFTER boundary-value-diffs checkpoint.

---

### Task 1: RED — Add API semantics tests

**Files:**
- Create: `tests/MiniArch.Tests/UserApi/ExplicitDenseValueDiffTests.cs`

**Test list (failing — APIs don't exist yet):**

1. **`Capture_Drain_reports_old_new`**: Create 2 entities with `Position(1,2)` and `Position(3,4)`; `Capture`; set both to new values; `Drain` counts exactly 2 callbacks with correct old/new values.

2. **`Capture_then_drain_without_changes_reports_nothing`**: Create 1 entity; `Capture`; `Drain` without any write — no callbacks. `Capture` records the current projected value, so this is not a `default->Value` initial dump API.

3. **`Second_drain_without_capture_returns_same_data`**: Capture then drain twice — same entity changes reported both times.

4. **`Clear_resets_old_values`**: Capture → Set → Drain (count changes) → Clear → Capture → Set different → Drain reports new changes.

5. **`Add_entity_after_capture_uses_dense_shadow_semantics`**: Capture, then add a new entity with Position, Drain — the new entity is scanned during Drain even though it was not present during Capture. `_oldValues[newEntityId]` is `default` or stale dense slot value, so it may report a value diff. The regression test should use a fresh id and non-default value to assert `default->Value`.

   **Clarify**: `Capture` records old values for all entities that EXIST at Capture time. If a new entity appears between Capture and Drain, Drain will find it, read `_oldValues[newId]` = `default`, and report a change. This matches ManualGenericTracker's behavior.

   Actually, wait — in ManualGenericTracker's Drain, it reads `_oldValues[entityId]`. If the entity was never in `_oldValues` (because it didn't exist during `BeforeRound`), `_oldValues[entityId]` would be `default(int) = 0`. If the new entity's value is non-zero, it reports a change. If zero, no change. So yes, it DOES report new entities — this is a known ManualGenericTracker behavior.

   **Decision: explicitly match ManualGenericTracker semantics.** New entity between Capture and Drain: reported as dense-slot old value → current value. Entity removed between Capture and Drain: not scanned (no chunk entry) → not reported.

6. **`Remove_entity_after_capture_not_reported_in_drain`**: Capture, remove entity, Drain — entity removed, not scanned, no report.

7. **`Destroy_entity_after_capture_not_reported`**: Same as remove.

8. **`Projector_only_projects_selected_field`**: Use a test component with multiple fields, project only one, verify changes in other fields don't trigger callback.

9. **`Multiple_instances_independent`**: Two `DenseValueDiff<Position,int,PX>` instances; Capture/Drain each independently produce same results.

10. **`Sink_struct_is_called_per_change`**: Use a struct sink that counts calls and accumulates old/new values.

11. **`Query_filter_works`**: Create with `With<Alive>()` etc, verify only matching entities are scanned.

**Run tests**:
```bash
dotnet test -c Release --filter "ExplicitDenseValueDiff" --nologo
```
Expected: FAIL (types don't exist)

---

### Task 2: RED — Run focused initial tests

```bash
dotnet test -c Release --filter "ExplicitDenseValueDiff_*|Capture_Drain*" --nologo
```
Expected: Compilation failure (missing types). If compiler halts before test discovery, there's no RED to observe — this is expected. The test file won't even load.

**Gate**: Compiler error count = however many missing type references.

---

### Task 3: Implement interfaces, class, and factory

**Files:**
- Create: `src/MiniArch/ChangeTracking/IValueProjector.cs`
- Create: `src/MiniArch/ChangeTracking/IValueChangeSink.cs`
- Create: `src/MiniArch/ChangeTracking/DenseValueDiff.cs`
- Modify: `src/MiniArch/Core/World.cs` — add `CreateDenseValueDiff<TComponent, TValue, TProjector>()`

**Step 1: IValueProjector**

```csharp
// src/MiniArch/ChangeTracking/IValueProjector.cs
namespace MiniArch;

public interface IValueProjector<TComponent, TValue>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
{
    TValue Project(in TComponent component);
}
```

**Step 2: IValueChangeSink**

```csharp
// src/MiniArch/ChangeTracking/IValueChangeSink.cs
namespace MiniArch;

public interface IValueChangeSink<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    void OnChanged(Entity entity, TValue oldValue, TValue newValue);
}
```

**Step 3: DenseValueDiff**

```csharp
// src/MiniArch/ChangeTracking/DenseValueDiff.cs
using System.Runtime.CompilerServices;
using MiniArch.Core;

namespace MiniArch;

public sealed class DenseValueDiff<TComponent, TValue, TProjector>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where TProjector : struct, IValueProjector<TComponent, TValue>
{
    private TValue[] _oldValues = Array.Empty<TValue>();
    private int[] _touchedEntities = Array.Empty<int>();
    private int _touchedCount;
    private readonly QueryDescription _query;
    private readonly TProjector _projector;
    private bool _hasCaptured;

    internal DenseValueDiff(QueryDescription query, TProjector projector)
    {
        _query = query;
        _projector = projector;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Capture(World world)
    {
        _touchedCount = 0;
        _hasCaptured = true;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var span = chunk.GetSpan<TComponent>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int entityId = entities[i].Id;

                // Resize _oldValues if needed
                if ((uint)entityId >= (uint)_oldValues.Length)
                    Array.Resize(ref _oldValues, Math.Max(entityId + 1, _oldValues.Length * 2));

                _oldValues[entityId] = _projector.Project(span[i]);

                // Track touched entities
                if (_touchedCount >= _touchedEntities.Length)
                    Array.Resize(ref _touchedEntities, Math.Max(_touchedCount + 1, _touchedEntities.Length * 2));
                _touchedEntities[_touchedCount++] = entityId;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Drain<TDrain>(World world, ref TDrain sink)
        where TDrain : struct, IValueChangeSink<TValue>
    {
        // If never captured, drain yields nothing (no baseline)
        if (!_hasCaptured) return;

        foreach (var chunk in world.Query(in _query).GetChunks())
        {
            var span = chunk.GetSpan<TComponent>();
            var entities = chunk.GetEntities();
            for (int i = 0; i < chunk.Count; i++)
            {
                int entityId = entities[i].Id;
                TValue oldVal = (uint)entityId < (uint)_oldValues.Length
                    ? _oldValues[entityId]
                    : default;
                TValue newVal = _projector.Project(span[i]);

                if (!oldVal.Equals(newVal))
                {
                    sink.OnChanged(entities[i], oldVal, newVal);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        if (!_hasCaptured) return;
        for (int i = 0; i < _touchedCount; i++)
            _oldValues[_touchedEntities[i]] = default;
        _touchedCount = 0;
        _hasCaptured = false;
    }
}
```

**Step 4: World factory**

```csharp
// World.cs — add (in the World partial class)
public DenseValueDiff<TComponent, TValue, TProjector> CreateDenseValueDiff<TComponent, TValue, TProjector>(
    QueryDescription? query = null,
    TProjector projector = default)
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where TProjector : struct, IValueProjector<TComponent, TValue>
{
    var q = query ?? new QueryDescription().With<TComponent>();
    return new DenseValueDiff<TComponent, TValue, TProjector>(q, projector);
}
```

Note: if `query` is provided but missing `With<TComponent>`, the factory does NOT auto-add — user must include it. This is simpler and avoids surprising behavior where the factory modifies user's query. The test should document this: if query doesn't include TComponent, drain scans nothing.

**Step 5: Build**

```bash
dotnet build -c Release --nologo src/MiniArch
```
Expected: success.

---

### Task 4: GREEN — Run tests

```bash
dotnet test -c Release --filter "ExplicitDenseValueDiff" --nologo
```
Expected: All tests PASS.

Fix any test failures — most likely around semantics of first Capture→Drain (old=default) and add-entity-between-Capture-and-Drain behavior.

---

### Task 5: Add HeroComing.Perf — NewExplicitDiff strategy

**Files:**
- Modify: `tools/perf/HeroComing.Perf/Program.cs`

**Step 1: Add projector/sink types (file-scoped, in Program.cs)**

```csharp
file readonly struct PositionQProjector : IValueProjector<PositionQValue, int>
{
    public int Project(in PositionQValue component) => component.Value;
}

file readonly struct PositionRProjector : IValueProjector<PositionRValue, int>
{
    public int Project(in PositionRValue component) => component.Value;
}

file readonly struct HpProjector : IValueProjector<CurrentHpValue, int>
{
    public int Project(in CurrentHpValue component) => component.Value;
}

file struct ChecksumSink : IValueChangeSink<int>
{
    public int TotalChanges;
    public int Checksum;

    public void OnChanged(Entity entity, int oldValue, int newValue)
    {
        TotalChanges++;
        Checksum = HashCode.Combine(Checksum, entity.Id, oldValue, newValue);
    }
}
```

**Step 2: Observer factories**

```csharp
// CreateExplicitDenseMovementObserver
static TrackObserver CreateExplicitDenseMovementObserver(MiniArchRuntime runtime)
{
    var world = runtime.World;
    var posQDiff = world.CreateDenseValueDiff<PositionQValue, int, PositionQProjector>();
    var posRDiff = world.CreateDenseValueDiff<PositionRValue, int, PositionRProjector>();

    return TrackObserver.Create(
        "Explicit Dense Diff (PositionQValue+PositionRValue)",
        obs =>
        {
            var sinkQ = new ChecksumSink();
            posQDiff.Drain(world, ref sinkQ);
            obs.TotalChanges += sinkQ.TotalChanges;
            obs.Checksum = HashCode.Combine(obs.Checksum, sinkQ.Checksum);

            var sinkR = new ChecksumSink();
            posRDiff.Drain(world, ref sinkR);
            obs.TotalChanges += sinkR.TotalChanges;
            obs.Checksum = HashCode.Combine(obs.Checksum, sinkR.Checksum);

            posQDiff.Clear();
            posRDiff.Clear();
        },
        () =>
        {
            posQDiff.Capture(world);
            posRDiff.Capture(world);
        });
}

static TrackObserver CreateExplicitDenseAttackObserver(MiniArchRuntime runtime)
{
    var world = runtime.World;
    var hpDiff = world.CreateDenseValueDiff<CurrentHpValue, int, HpProjector>();

    return TrackObserver.Create(
        "Explicit Dense Diff (CurrentHpValue)",
        obs =>
        {
            var sink = new ChecksumSink();
            hpDiff.Drain(world, ref sink);
            obs.TotalChanges += sink.TotalChanges;
            obs.Checksum = HashCode.Combine(obs.Checksum, sink.Checksum);
            hpDiff.Clear();
        },
        () => hpDiff.Capture(world));
}
```

**Step 3: Register in comparison table**

In `CompareOldValueTracking` or a new `--compare-dense-diff` mode, add columns:

```
Movement   | ExplicitDiff | <rounds/s> | <ms/round> | <ch/rd> | <chk/rd> | <total_ch> | <ch/rd_ratio> | <checksum>
```

Compare `NewExplicitDiff` vs `ManualDense` throughput ratio. Print warning if `< 0.95`.

**Step 4: Run comparison in Release**

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --compare-old-value-tracking
```
Expected: NewExplicitDiff throughput >= ManualDense * 0.95. If not, investigate and optimize.

**Output should print ratios** like:
```
  Movement   | ExplicitDiff |     1860.5 |     0.537 |   55815 |    -3019.4 | 27907500 |  500.00 |   123456789
  Movement   | ManualDense  |     1958.7 |     0.511 |   58762 |    -3019.4 | 29381000 |  500.00 |  -1093226894
  Ratio: ExplicitDiff/ManualDense = 0.95 ✅
```

If ratio < 0.95, the task is NOT complete — must optimize or document reason.

---

### Task 6: Run compare in Release and verify >= 95%

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --compare-old-value-tracking
```

Analyze output. If throughput ratio >= 0.95 for both Movement and Attack: PASS.

If ratio < 0.95:
1. Check if Issue is in `Capture` vs `BeforeRound` — Capture is the full scan, same cost.
2. Check if `Drain` has extra overhead — the `sink` is a struct method, verify inline via disassembly or flamegraph.
3. Check `_oldValues` bounds check — `(uint)entityId < (uint)_oldValues.Length` avoids double bounds check but JIT may still emit one. Use `Unsafe.Add` if needed.
4. Check `TValue.Equals` vs `ManualDense`'s `int != int` — JIT should devirtualize `Int32.Equals` to `cmp`. Verify.

If all fail to close gap, document reason and set lower target. But the target is 95% which should be achievable given the hot path is nearly identical.

---

### Task 7: Run full Release tests + soak + Hero --check-baseline

Since `src/MiniArch/` changed (new files added to ChangeTracking/), this is NOT a pure-documentation change. The architecture regression gate (`§5` in AGENTS.md) applies.

**Step 1: Full test suite**

```bash
dotnet test -c Release --nologo
```
Expected: all pass.

**Step 2: Soak test**

```bash
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 16 --frames 50000 --quiet
```
Expected: 16/16 PASS.

**Step 3: HeroComing regression gate**

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```
Expected: Movement >= 1642, Attack >= 997, no memory growth, no crashes.

Note: `CreateDenseValueDiff` is NOT wired into `--track-observer` mode (which still uses `TrackValueChanges`). The new API is standalone and doesn't affect existing tracking paths. So regression should trivially pass.

**Step 4: Perf soak**

```bash
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 16 --frames 50000 --track-observer --quiet
```
Expected: 16/16 PASS.

---

### Task 8: Update knowledge pages after implementation

**Files:**
- Modify: `.knowledge/kb-change-tracking.md` — add `CreateDenseValueDiff` API, add comparison with `TrackValueChanges`, describe semantics and use cases.
- Modify: `.knowledge/kb-hero-pipeline-regression.md` — add `ExplicitDiff` row to baseline comparison table if implementing `--check-baseline` update (this plan does NOT update baseline; only comparison mode).
- Modify: `.knowledge/kb-changelog.md` — add entry for `CreateDenseValueDiff<TComponent, TValue, TProjector>`.
- Verify: `.knowledge/INDEX.md` — if `dense-value-diff` or `explicit-diff` becomes a new module reference, add.

**Do NOT create new `.knowledge/kb-*.md` files unless the module is sufficiently distinct.** Likely just update `kb-change-tracking.md` with a section like "§ DenseValueDiff explicit API" and update `kb-changelog.md`.

---

### Task 9: Commit

```bash
git add -A
git commit -m "feat: add CreateDenseValueDiff explicit dense shadow-diff API

- New interfaces: IValueProjector<TComponent,TValue>, IValueChangeSink<TValue>
- New sealed class DenseValueDiff<TComponent,TValue,TProjector> with
  Capture/Drain/Clear hot path matching ManualGenericTracker semantics
- World.CreateDenseValueDiff factory with optional query and projector
- Zero steady-state allocation, struct sink callbacks, dense int[] state
- Exhaustive semantics tests in ExplicitDenseValueDiffTests
- HeroComing.Perf ExplicitDiff strategy with 95% ManualDense throughput gate"
```

---

### Verification (final)

After all tasks:

1. `dotnet build -c Release` → clean
2. `dotnet test -c Release --nologo` → all tests pass
3. `dotnet run -c Release --project tools/perf/HeroComing.Perf --compare-old-value-tracking` → NewExplicitDiff >= ManualDense * 0.95
4. `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` → no regression
5. `dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 16 --frames 50000 --quiet` → 16/16 PASS

---

## 关键设计决策

### 1. `Drain` 在 `!hasCaptured` 时返回空

如果没有调用过 `Capture`，Drain 不会有任何输出（不扫描、不回调）。这在首次使用场景中避免了无 baseline 状态下的误报。用户必须显式 `Capture` 来建立 baseline。

### 2. `Clear` 后 `_hasCaptured = false`

所以 `Clear` 之后的下一次 `Drain`（没有中间的 `Capture`）也是空。这是故意的——意思是"你看过的基线已经被清除，需要重新 Capture"。

### 3. 工厂的 `query` 不自动 `.With<TComponent>()`

因为用户传入的 query 可能是精心构造的（带 filter），自动加 `.With<TComponent>()` 不会破坏 filter——但是很神秘。文档强调"Drain 只扫描 query 匹配的 chunk"；如果 query 没 include TComponent，Drain 得到零 chunk 是用户责任。

**但是**看具体场景：大多数用户只是想对所有拥有 `TComponent` 的 entity 做 diff，不需要额外 filter。如果默认 query 是 `null` → 工厂构造 `new QueryDescription().With<TComponent>()` 是完美默认。如果用户传了 query，信任用户的 filter，不自动加——因为用户可能故意 exclude 掉某个 archetype 来限制 diff 范围。如果用户不小心忘记加 `.With<TComponent>()`，Drain 会静默空——这是少量 ergonomic 亏损，但符合 YAGNI 和 predictability。

**最终决策**：`query == null` → `new QueryDescription().With<TComponent>()`；`query != null` → 直接使用，不修改。
