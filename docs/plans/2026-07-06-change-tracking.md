# Native Change Tracking 实现计划

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 给 miniArch 增加原生的"变更追踪"能力——用户 `world.Track<T>()` 拿到一个游标对象，能枚举到该组件的值写入（`ModifiedChunks`）和成员进出（`Transitions`），供渲染层/UI 反应式消费，零新写入 API、零现有热路径退化。

**Architecture:** 两套机制按基数分工：
- **值变更（Set）**：per-(Archetype, componentColumn) 的 `long` 版本号，在 Archetype 的 3 个写入 chokepoint 处 bump。全局 `World._writeEpoch` 作为单调时钟，`long` 无回绕。
- **结构变更（Add/Remove/Create/Destroy/Clone）**：append-only 的 transition log，每条记录 `(Entity, OldArchetype?, NewArchetype?)`，消费者用自己的 query 对 old/new signature 各 match 一次推出 Entered/Exited。
两套都由 `ChangeQuery<T>` 游标对象统一暴露，游标全内化，用户不接触任何版本号。

**Tech Stack:** C# / .NET 8 / xUnit / `unsafe` + `Unsafe.*` / `[Conditional("DEBUG")]` 断言。

---

## 锁定的契约与决策（不可违反）

1. **Get = 读，永不标脏；Set = 写，标脏。** 不引入 `GetForWrite`。用户想要值追踪，写入必须走 `Set<T>`。
2. **自动 per-type opt-in**：没人 `Track<T>`，则追踪基础设施对热路径零成本（一条自字段 bool 读，预测不命中）。
3. **追踪关闭时零退化**：HeroComing.Perf baseline（Movement ≥1917 的 80% / Attack ≥1205 的 80%）必须不回归。这是门禁硬条件。
4. **per-archetype 列版本，非 per-segment**：HP 类组件的 archetype 是 flat 模式（实体少，<2MB 阈值），per-archetype = per-chunk = 最细粒度。chunked 模式下跨 segment 过报，文档注明，YAGNI 先不做 segment 级。
5. **transition log 不序列化、不 checksum**：它是 ephemeral 渲染层状态，不是确定 sim 状态。Snapshot 不含它；restore 后游标重置，消费者全量重同步。
6. **Replay/Submit 必须触发 observer 变更**：因为 instrument 在 Archetype 的 3 个写入方法（所有写入路径的公共底），replay 走 `WriteComponentRaw`、submit 走 `SetComponentAtFlat/Typed`，自动覆盖。
7. **概念唯一**：FrameDelta = 跨 host 持久化序列化日志；transition log = 本机单会话投递队列。不同生命周期，非重复。
8. **"Modified" = 写脏，不是"值不等"**：不比较相等性（更贵），dirty 语义足够。

## 既有约定（实现时遵守）

- 源码标识符与注释用英文；计划/文档用中文。
- Release 下零开销的安全检查用 `[Conditional("DEBUG")]`。
- `[SkipLocalsInit]` + `AggressiveInlining` + `Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(...), offset)` 消除边界检查。
- 性能测量永远 `-c Release`。
- 组件只能是 unmanaged（已有 fail-fast）。

---

## Hook 点参考地图（executor 勘查，实现时按此定位）

### 值写入的 3 个 Archetype chokepoint（全部要 instrument）
| 方法 | 文件 | 行 |
|---|---|---|
| `Archetype.SetComponentAtTyped<T>` | `src/MiniArch/Core/Archetype.Storage.cs` | 485-489 |
| `Archetype.SetComponentAtFlat<T>` | `src/MiniArch/Core/Archetype.Storage.cs` | 510-515 |
| `Archetype.WriteComponentRaw` (write 模式) | `src/MiniArch/Core/Archetype.Storage.cs` | 661-662 (经 `CopyComponentRaw` 第686行 `CopyBlockUnaligned`) |

这 3 个方法是**所有**写入路径的公共底：
- `World.Set<T>` → `ApplyTypedSet` (StructuralChange.cs:132→139) → `SetComponentAtTyped`
- `EntityAccessor.Set<T>` (StructuralChange.cs:48) → `SetComponentAtTyped`
- `ComponentStore<T>.ApplyToWorld` (CommandStreamCore.cs:2334/2367) → `SetComponentAtFlat`(flat) / `SetComponentAtTyped`(chunked)
- Replay `ApplyRawSet`/`ApplyRawAdd` (World.cs:165/178) → `WriteComponentRaw`

**读路径独立，不碰**：`World.Get<T>` (World.cs:304-311) → `GetComponentAt<T>` (Storage.cs:519) → `GetComponentRefAt<T>` (469)。

### 结构变更 hook 点（old/new archetype 已知位置）
| 操作 | 文件 | old arch 已知 | new arch 已知 |
|---|---|---|---|
| `Add<T>` | StructuralChange.cs ApplyTypedAdd 98-129 | 101 `info.Archetype` | 113 `destination` |
| `Remove<T>` | StructuralChange.cs RemoveBoxed 214-238 | 222 `archetype` | 232 `destination` |
| `Create()` | EntityLifecycle.cs 17-22 | null | 20 `archetype` |
| `Create<T...>` | Create.Generated.cs | null | builder 内 archetype |
| `Destroy` | EntityLifecycle.cs DestroySingle 205-227 | 208 `arch` | null |
| `Clone(Entity)` | World.cs CloneSingle 366-373 | 369 `archetype` | 同（新实体） |

### Archetype 结构（版本数组落位）
- 字段在 `src/MiniArch/Core/Archetype.cs` 23-72。已有 `_columnByteOffsets: int[]`、`_elementSizes: int[]`、`_componentIdToColumnIndex: int[]`、`_signature: Signature`。
- 列数 = `_elementSizes.Length`。
- `_componentIdToColumnIndex`（52行）是 componentId→列索引映射（`TryGetComponentIndex` 156行带检查 / `GetComponentIndexFast` 178行无检查）。
- `Segment` struct（148-153）：`{ Entity[] Entities, byte[] Data, int Count }`。

### ChunkView
- `src/MiniArch/Core/ChunkView.cs` 21-122。字段 `_archetype`、`_segmentIndex`(-1=flat)、`_startRow`、`_rowCount`。ChangeVersion 访问器自然挂在 ChunkView 上，委托 `_archetype._columnVersions[col]`。

### Query 枚举
- `MiniArch.Query` (Query.cs:11) 包 `Core.QueryCache`。
- `QueryCache.GetChunkViewSpan()` (Core/QueryCache.cs:83-87) → `EnsureRefreshed()` 后返 `_snapshotChunkViews.AsSpan(0,_chunkViewCount)`。
- ChunkView 在 `RebuildChunkViews`(152-189) / `AppendNewArchetypes`(191-261) 构造。

### World 字段（World.cs 38-89）
- 无现有 per-componentId 数组。新增 `_writeEpoch`、`_transitionLog`、追踪激活状态。
- Archetype 由 `GetOrCreateArchetype` 创建——此处注入 `_owner` backref。

### ComponentType
- `Component<T>.ComponentType` (ComponentRegistry.cs:119-121) 取 int id。`.Value` 取原始 int。

### 测试
- xUnit，`[Fact]`。命名 `{Desc}Tests.cs`，方法 PascalCase 如 `Set_updates_existing_component_in_place`。
- 相关：`tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`、`WorldLifecycleTests.cs`、`CommandStreamTests.cs`、`EntityAccessorTests.cs`、`Persistence/EntityCloneTests.cs`。

---

## Task 1: 核心数据结构与 Archetype backref

**Files:**
- Modify: `src/MiniArch/Core/Archetype.cs`（字段 + 构造）
- Modify: `src/MiniArch/Core/World.cs`（字段 + GetOrCreateArchetype 注入 backref）
- Test: `tests/MiniArch.Tests/Core/ChangeTrackingInfrastructureTests.cs`（新建）

**Step 1: 写失败测试——Archetype 持有 owner backref 且默认追踪关闭**

```csharp
using MiniArch.Core;
using Xunit;

namespace MiniArchTests.Core;

public class ChangeTrackingInfrastructureTests
{
    [Fact]
    public void Archetype_has_owner_backref_after_creation()
    {
        var world = new World();
        var e = world.Create<Position>();
        // 通过内部 hook 验证 backref 已注入（暴露 internal 测试访问器或反射）
        Assert.True(world.TryGetArchetypeOwnerBackref<Position>(out _));
    }

    [Fact]
    public void Tracking_is_inactive_by_default()
    {
        var world = new World();
        Assert.False(world.IsChangeTrackingActive);
    }
}
```
> 注：`TryGetArchetypeOwnerBackref`/`IsChangeTrackingActive` 是为测试暴露的 internal 诊断属性，放在 `InternalsVisibleTo` 下。若项目无 InternalsVisibleTo，改用 `[Conditional("DEBUG")]` 暴露的断言或世界级诊断快照（参考 `WorldStats` 模式）。

**Step 2: 跑测试验证失败** — `dotnet test -c Release --filter ChangeTrackingInfrastructure` → 编译失败（方法不存在）。

**Step 3: 最小实现**

在 `Archetype.cs` 加字段（`internal`，紧跟现有字段）：
```csharp
internal World? _owner;                 // backref, set by World.GetOrCreateArchetype
internal bool _anyTrackingActive;       // gate: false = zero-cost skip on write path
internal long[]? _columnVersions;       // allocated when tracking activated; len = _elementSizes.Length
```

在 `World.cs` 加字段：
```csharp
private long _writeEpoch;                       // monotonic, never wraps (long)
private readonly List<TransitionEntry> _transitionLog = new();
private bool _anyTrackingActive;                // world-level gate (mirrored to archetypes on activate)
```

`TransitionEntry`（新建 `src/MiniArch/Core/TransitionEntry.cs`）：
```csharp
namespace MiniArch.Core;

internal readonly struct TransitionEntry
{
    public readonly Entity Entity;
    public readonly Archetype? OldArchetype;   // null = created
    public readonly Archetype? NewArchetype;   // null = destroyed
    public TransitionEntry(Entity e, Archetype? old, Archetype? @new)
        { Entity = e; OldArchetype = old; NewArchetype = @new; }
}
```

在 `GetOrCreateArchetype`（找到该方法）创建新 Archetype 后立即 `arch._owner = this;`。

加 internal 诊断属性 `IsChangeTrackingActive => _anyTrackingActive;`。

**Step 4: 跑测试验证通过。**

**Step 5: Commit** — `git add -A && git commit -m "feat(change-tracking): core fields — archetype backref, writeEpoch, transition log scaffolding"`

---

## Task 2: 值写入版本 bump（3 个 chokepoint）

**Files:**
- Modify: `src/MiniArch/Core/Archetype.Storage.cs`（SetComponentAtTyped 485、SetComponentAtFlat 510、WriteComponentRaw/CopyComponentRaw 661/686）
- Test: `tests/MiniArch.Tests/Core/ChangeTrackingInfrastructureTests.cs`

**Step 1: 写失败测试——Set 后列版本推进，Get 不推进；追踪关闭时版本不变**

```csharp
[Fact]
public void Set_advances_column_version_when_tracking_active()
{
    var world = new World();
    var tracker = world.Track<Position>();        // Track<T> 在 Task 4 实现；此处先假绿
    var e = world.Create<Position>();
    var v0 = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
    world.Set(e, new Position { X = 5 });
    var v1 = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
    Assert.True(v1 > v0);
}

[Fact]
public void Set_does_not_advance_version_when_tracking_inactive()
{
    var world = new World();                      // no Track<> call
    var e = world.Create<Position>();
    world.Set(e, new Position { X = 5 });
    Assert.Equal(0, world.DebugGetColumnVersion(e, Component<Position>.ComponentType));
}

[Fact]
public void Get_does_not_advance_version_even_when_tracking_active()
{
    var world = new World();
    var tracker = world.Track<Position>();
    var e = world.Create<Position>();
    world.Set(e, new Position { X = 1 });         // advance once
    var v = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
    _ = world.Get<Position>(e);                    // read
    Assert.Equal(v, world.DebugGetColumnVersion(e, Component<Position>.ComponentType));
}

[Fact]
public void Set_on_one_column_does_not_advance_other_column()
{
    var world = new World();
    var tracker = world.Track<Position>();
    var e = world.Create<Position, Velocity>();
    var posV = world.DebugGetColumnVersion(e, Component<Position>.ComponentType);
    world.Set(e, new Velocity { Dx = 1 });        // different component
    Assert.Equal(posV, world.DebugGetColumnVersion(e, Component<Position>.ComponentType));
}
```
> `DebugGetColumnVersion` 是 `[Conditional("DEBUG")]` 或 internal 测试 hook，解析 entity→archetype→column→version。

**Step 2: 跑测试验证失败**（Track / DebugGetColumnVersion 未实现）。

**Step 3: 实现**

在 3 个 Archetype 写入方法的**写入之后**加（每个方法尾部）：
```csharp
if (_anyTrackingActive)
    _columnVersions![columnIndex] = ++_owner!._writeEpoch;
```
注意：
- `SetComponentAtTyped<T>(int columnIndex, int row, in T value)` —— columnIndex 是参数。
- `SetComponentAtFlat<T>(int byteOffset, int row, in T value)` —— columnIndex 不是参数！需补传 columnIndex（改签名 internal），或在该方法的调用点（ComponentStore.ApplyToWorld）已知组件类型可反查。**决策**：给 `SetComponentAtFlat` 增加 `int columnIndex` 参数（internal 签名变更，调用点少且都在 Core 内）。
- `WriteComponentRaw(int columnIndex, int row, byte* source)` → `CopyComponentRaw(columnIndex, row, source, read: false)` —— 在 `CopyComponentRaw` 内 `if (!read && _anyTrackingActive) bump`。读路径（`read:true`）不 bump。

`Track<T>()` 与 `ActivateTracking` 在 Task 4 实现；Task 2 先把 bump 逻辑就位，用 Task 4 的激活路径驱动 `_anyTrackingActive`。

**Step 4: 跑测试验证通过**（待 Task 4 的 Track<T> 就绪后这批测试转绿；Task 2 可先用手动设置 `_anyTrackingActive=true` + 分配 `_columnVersions` 的临时测试 helper 跑绿，再在 Task 4 替换为正式 Track<T>）。

**Step 5: Commit** — `feat(change-tracking): bump per-column version on the 3 archetype write chokepoints`

---

## Task 3: 结构 transition 日志（5 个 hook 点）

**Files:**
- Modify: `src/MiniArch/Core/World.StructuralChange.cs`（ApplyTypedAdd 98-129、RemoveBoxed 214-238）
- Modify: `src/MiniArch/Core/World.EntityLifecycle.cs`（Create 17-22、CreateInArchetype 路径、DestroySingle 205-227）
- Modify: `src/MiniArch/Core/World.Create.Generated.cs`（Create<T...>）
- Modify: `src/MiniArch/Core/World.cs`（CloneSingle 366-373）
- Test: `tests/MiniArch.Tests/Core/ChangeTrackingInfrastructureTests.cs`

**Step 1: 写失败测试——各结构操作追加正确的 transition 条目**

```csharp
[Fact]
public void Create_appends_transition_with_null_old()
{
    var world = new World();
    world.Track<Position>();              // 激活
    world._transitionLog.ClearForTest();  // 清掉 Track 本身可能的噪声
    var e = world.Create<Position>();
    var log = world.DebugGetTransitions();
    Assert.Single(log);
    Assert.Null(log[0].OldArchetype);
    Assert.NotNull(log[0].NewArchetype);
    Assert.Equal(e, log[0].Entity);
}

[Fact]
public void Destroy_appends_transition_with_null_new()
{
    var world = new World();
    world.Track<Position>();
    var e = world.Create<Position>();
    world.DebugClearTransitionLog();
    world.Destroy(e);
    var log = world.DebugGetTransitions();
    Assert.Single(log);
    Assert.NotNull(log[0].OldArchetype);
    Assert.Null(log[0].NewArchetype);
}

[Fact]
public void Add_appends_migration_both_archetypes_present()
{
    var world = new World();
    world.Track<Position>();
    var e = world.Create<Position>();
    world.DebugClearTransitionLog();
    world.Add<Velocity>(e);
    var log = world.DebugGetTransitions();
    Assert.Single(log);
    Assert.NotNull(log[0].OldArchetype);
    Assert.NotNull(log[0].NewArchetype);
    Assert.NotSame(log[0].OldArchetype, log[0].NewArchetype);
}

[Fact]
public void Remove_appends_migration()
{ /* 同上对称，Remove<Velocity> */ }

[Fact]
public void Clone_appends_created_transition()
{ /* Clone(entity) → log 有 null-old 条目 */ }

[Fact]
public void No_transitions_when_tracking_inactive()
{
    var world = new World();              // no Track
    world.Create<Position>();
    Assert.Empty(world.DebugGetTransitions());
}
```

**Step 2: 跑测试验证失败。**

**Step 3: 实现**

加 World 内部 helper：
```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
private void AppendTransition(Entity e, Archetype? old, Archetype? @new)
{
    if (_anyTrackingActive) _transitionLog.Add(new TransitionEntry(e, old, @new));
}
```

各 hook 点在**操作完成后**调用（old/new archetype 此时确定）：
- `ApplyTypedAdd`（迁移分支，128行 FinishMoveEntity 后）：`AppendTransition(entity, sourceArch, destination)` —— 其中 sourceArch 在方法入口已知（`info.Archetype`）。注意 Add 已存在组件的原地分支（103-108）不产生迁移，但仍是"写入"——走 Task 2 的值版本路径，不进 transition 日志。
- `RemoveBoxed`（237行 MoveEntity 后）：`AppendTransition(entity, oldArch, destination)`。
- `Create()`/`Create<T...>`：`AppendTransition(entity, null, archetype)`（CreateInArchetype 返回后）。
- `DestroySingle`（RemoveAt 后）：`AppendTransition(entity, arch, null)`。
- `CloneSingle`（CreateInArchetype 后）：`AppendTransition(newEntity, null, archetype)`。

**Step 4: 跑测试验证通过。**

**Step 5: Commit** — `feat(change-tracking): append old→new archetype transitions on structural ops`

---

## Task 4: Track<T>() 激活路径 + ChangeQuery<T> 类型

**Files:**
- Create: `src/MiniArch/ChangeQuery.cs`（public，`MiniArch` namespace）
- Create: `src/MiniArch/Transition.cs`（public enum + struct）
- Modify: `src/MiniArch/Core/World.cs`（Track<T> + ActivateTracking）
- Test: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs`（新建）

**Step 1: 写失败测试——Track<T> 返回游标，激活追踪**

```csharp
using MiniArch;
using Xunit;

namespace MiniArchTests.UserApi;

public class ChangeQueryTests
{
    [Fact]
    public void Track_returns_cursor_and_activates_tracking()
    {
        var world = new World();
        var hp = world.Track<Position>();
        Assert.NotNull(hp);
        Assert.True(world.IsChangeTrackingActive);
    }
}
```

**Step 2: 跑测试验证失败。**

**Step 3: 实现**

`src/MiniArch/Transition.cs`：
```csharp
namespace MiniArch;

public enum TransitionKind { Entered, Exited }

public readonly struct Transition
{
    public readonly TransitionKind Kind;
    public readonly Entity Entity;
    public Transition(TransitionKind k, Entity e) { Kind = k; Entity = e; }
}
```

`src/MiniArch/ChangeQuery.cs`（public，引用 `MiniArch.Core`）：
```csharp
using MiniArch.Core;

namespace MiniArch;

public sealed class ChangeQuery<T> where T : unmanaged
{
    private readonly World _world;
    private readonly ComponentType _type;
    private long _valueCursor;      // last writeEpoch seen by ModifiedChunks
    private int _transitionCursor;  // last transition log index seen by Transitions

    internal ChangeQuery(World world)
    {
        _world = world;
        _type = Component<T>.ComponentType;
    }

    // 占位，Task 5/6 实现
    public IEnumerable<ChunkView> ModifiedChunks() => throw new NotImplementedException();
    public IEnumerable<Transition> Transitions() => throw new NotImplementedException();
}
```

World.Track<T> + ActivateTracking（Core）：
```csharp
public ChangeQuery<T> Track<T>() where T : unmanaged
{
    ActivateTracking(Component<T>.ComponentType);
    return new ChangeQuery<T>(this);
}

private void ActivateTracking(ComponentType type)
{
    if (!_anyTrackingActive)
    {
        _anyTrackingActive = true;
        foreach (var arch in _archetypes.Values)
            ActivateArchetypeTracking(arch);
    }
    else
    {
        // 已激活，确保含该组件的 archetype 也激活
        foreach (var arch in _archetypes.Values)
            if (arch.ContainsComponent(type)) ActivateArchetypeTracking(arch);
    }
}

private void ActivateArchetypeTracking(Archetype arch)
{
    if (arch._anyTrackingActive) return;
    arch._anyTrackingActive = true;
    arch._columnVersions = new long[arch._elementSizes.Length];   // zeroed
}
```
> `Archetype.ContainsComponent(ComponentType)` 走 `TryGetComponentIndex`（已有）。新 archetype（Track 之后创建的）在 `GetOrCreateArchetype` 末尾若 `_anyTrackingActive` 则立即 `ActivateArchetypeTracking`。

**Step 4: 跑测试验证通过。**

**Step 5: Commit** — `feat(change-tracking): Track<T> activation path + ChangeQuery<T> cursor type`

---

## Task 5: ModifiedChunks()

**Files:**
- Modify: `src/MiniArch/ChangeQuery.cs`
- Test: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs`

**Step 1: 写失败测试——ModifiedChunks 只吐被 Set 写过的 chunk，调用即推进游标**

```csharp
[Fact]
public void ModifiedChunks_yields_only_chunks_written_since_last_call()
{
    var world = new World();
    var hp = world.Track<Position>();
    var e1 = world.Create<Position>();
    var e2 = world.Create<Position>();

    _ = hp.ModifiedChunks();              // consume initial (create 不走 Set，无值版本推进 → 空)
    Assert.Empty(hp.ModifiedChunks());    // 二次调用无新写入 → 空

    world.Set(e1, new Position { X = 1 });

    var modified = hp.ModifiedChunks().ToList();
    Assert.Single(modified);              // e1 和 e2 同 archetype → 同一 chunk
}

[Fact]
public void ModifiedChunks_skips_unchanged_after_consume()
{
    var world = new World();
    var hp = world.Track<Position>();
    var e = world.Create<Position>();
    world.Set(e, new Position { X = 1 });
    Assert.Single(hp.ModifiedChunks());   // 第一次有
    Assert.Empty(hp.ModifiedChunks());    // 已消费 → 空
}
```

**Step 2: 跑测试验证失败（NotImplemented）。**

**Step 3: 实现**

```csharp
public IEnumerable<ChunkView> ModifiedChunks()
{
    var query = _world.Query(new QueryDescription().With<T>());
    var colType = _type;
    var snapshotEpoch = _world.CurrentWriteEpoch;   // 暴露 internal long
    foreach (var chunk in query.GetChunks())
    {
        var arch = chunk.Advanced;                   // 取 Core.Archetype（暴露 internal）
        if (!arch.TryGetComponentIndex(colType, out var col)) continue;
        var versions = arch._columnVersions;
        if (versions != null && versions[col] > _valueCursor)
            yield return chunk;
    }
    _valueCursor = snapshotEpoch;
}
```
> 需暴露：`World.CurrentWriteEpoch`（internal）、`ChunkView` 到 `Core.Archetype` 的 internal 访问器（已有 `_archetype` 字段，加 internal property）、`Archetype._columnVersions`/`TryGetComponentIndex`（internal，已满足）。`yield return` + IEnumerable 可接受（ModifiedChunks 是冷路径，渲染消费）。

**Step 4: 跑测试验证通过。**

**Step 5: Commit** — `feat(change-tracking): ModifiedChunks yields dirty chunks since last call`

---

## Task 6: Transitions()

**Files:**
- Modify: `src/MiniArch/ChangeQuery.cs`
- Test: `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs`

**Step 1: 写失败测试——Transitions 按序 yield Entered/Exited，复合 query 形态**

```csharp
[Fact]
public void Transitions_yields_entered_on_create()
{
    var world = new World();
    var hp = world.Track<Position>();
    var e = world.Create<Position>();
    var ts = hp.Transitions().ToList();
    Assert.Single(ts);
    Assert.Equal(TransitionKind.Entered, ts[0].Kind);
    Assert.Equal(e, ts[0].Entity);
}

[Fact]
public void Transitions_yields_exited_on_destroy()
{
    var world = new World();
    var hp = world.Track<Position>();
    var e = world.Create<Position>();
    _ = hp.Transitions();                 // consume create
    world.Destroy(e);
    var ts = hp.Transitions().ToList();
    Assert.Single(ts);
    Assert.Equal(TransitionKind.Exited, ts[0].Kind);
}

[Fact]
public void Transitions_preserves_remove_then_add_order()
{
    var world = new World();
    var hp = world.Track<Velocity>();
    var e = world.Create<Velocity>();
    _ = hp.Transitions();
    world.Remove<Velocity>(e);            // Exited
    world.Add<Velocity>(e);               // Entered
    var ts = hp.Transitions().ToList();
    Assert.Equal(2, ts.Count);
    Assert.Equal(TransitionKind.Exited, ts[0].Kind);
    Assert.Equal(TransitionKind.Entered, ts[1].Kind);
}

[Fact]
public void Destroy_emits_exit_with_old_entity_version()
{
    // 验证 Exited 带的是被销毁实体的 version，slot 复用后新实体是独立的 Entered
    var world = new World();
    var hp = world.Track<Position>();
    var e1 = world.Create<Position>();
    _ = hp.Transitions();
    world.Destroy(e1);
    var e2 = world.Create<Position>();
    var ts = hp.Transitions().ToList();
    Assert.Equal(TransitionKind.Exited, ts[0].Kind);
    Assert.Equal(e1, ts[0].Entity);
    Assert.Equal(TransitionKind.Entered, ts[1].Kind);
    Assert.Equal(e2, ts[1].Entity);
    Assert.NotEqual(e1, e2);   // version 不同
}
```

**Step 2: 跑测试验证失败。**

**Step 3: 实现**

Match 逻辑：`ChangeQuery<T>` 的隐式 query = `With<T>`。对每条 transition entry：
- old=null,new≠null（Created）：若 new 含 T → Entered。
- old≠null,new=null（Destroyed）：若 old 含 T → Exited。
- old≠null,new≠null（Migrated）：`oldHas = old.Contains(T)`, `newHas = new.Contains(T)`；`!oldHas && newHas` → Entered；`oldHas && !newHas` → Exited；都含或都不含 → 无（成员未变）。

```csharp
public IEnumerable<Transition> Transitions()
{
    var log = _world.GetTransitionLogInternal();      // internal IReadOnlyList<TransitionEntry>
    var end = log.Count;
    for (int i = _transitionCursor; i < end; i++)
    {
        var entry = log[i];
        var oldHas = entry.OldArchetype is { } o && o.ContainsComponent(_type);
        var newHas = entry.NewArchetype is { } n && n.ContainsComponent(_type);
        if (!oldHas && newHas) yield return new Transition(TransitionKind.Entered, entry.Entity);
        else if (oldHas && !newHas) yield return new Transition(TransitionKind.Exited, entry.Entity);
    }
    _transitionCursor = end;
}
```

**Step 4: 跑测试验证通过。**

**Step 5: Commit** — `feat(change-tracking): Transitions yields ordered Entered/Exited membership changes`

---

## Task 7: Replay / Submit observer 覆盖验证

**Files:**
- Test: `tests/MiniArch.Tests/Core/ChangeTrackingReplayTests.cs`（新建）

**Step 1: 写测试——Replay 后 observer 看到等价变更；Submit 后看到变更**

```csharp
[Fact]
public void Replay_set_produces_modified_chunk()
{
    // host A 录 delta，host B replay → B 的 Track<T>.ModifiedChunks 看到写入
    var hostA = new World();
    var cs = new CommandStream(hostA);
    var e = hostA.Create<Position>();
    var deltaA = cs.Submit();
    var trackerB = ... // 在 hostB 上先 Track<Position> 再 replay
    // 构造：hostB Track<Position>，replay deltaA（含 Set），断言 ModifiedChunks 非空
}

[Fact]
public void Replay_add_produces_entered_transition()
{ /* replay Add<T> → Transitions 含 Entered */ }

[Fact]
public void Submit_set_produces_modified_chunk()
{ /* CommandStream Submit 后 observer 看到 ModifiedChunks */ }
```
> 细节按 `CommandStreamTests.cs` 现有 replay 测试模式构造。关键断言：observer 变更在**应用时**（replay/submit apply）产生，不是录制时。

**Step 2: 跑测试——预期全绿**（因为 instrument 在 Archetype 3 个 chokepoint，Replay 走 WriteComponentRaw、Submit 走 SetComponentAtFlat/Typed，自动覆盖）。**若有红**：说明某条路径绕过了 chokepoint（如新增的写入捷径），补 instrument。

**Step 3: 无需新实现（验证性 task）；仅当红时补 instrument。**

**Step 4: Commit** — `test(change-tracking): verify replay/submit produce observer changes`

---

## Task 8: Snapshot restore 语义

**Files:**
- Test: `tests/MiniArch.Tests/Persistence/ChangeTrackingSnapshotTests.cs`（新建）
- Modify（仅当测试要红）：`src/MiniArch/Core/World.cs` snapshot restore 路径

**Step 1: 写测试——restore 后游标重置、log 清空、不进序列化**

```csharp
[Fact]
public void Snapshot_does_not_serialize_transition_log()
{
    // 构造 world + Track + 几次变更 → snapshot bytes → 断言不含 transition log
    // （通过 snapshot 大小或反序列化后 log 为空间接验证）
}

[Fact]
public void Restore_resets_cursors_and_clears_log()
{
    var world = new World();
    var hp = world.Track<Position>();
    world.Create<Position>();
    _ = hp.Transitions();
    // snapshot + restore 到新 world / 或同 world restore
    // 断言：新 world 的 transition log 空、ChangeQuery 游标重置（下一次 Transitions 不吐历史）
}
```

**Step 2: 跑测试。** snapshot 序列化不含 log 应自然成立（log 是 World 实例字段，不在 snapshot 序列化路径）。restore 重置需在 restore 路径加 `_transitionLog.Clear(); _writeEpoch = 0;` 并让现有 ChangeQuery 失效（dispose 或标记 stale）。

**Step 3: 若红，在 snapshot restore 路径加 log 清空 + 游标失效。**

**Step 4: Commit** — `feat(change-tracking): reset observer state on snapshot restore`

---

## Task 9: 性能回归门禁 + benchmark

**Files:**
- Run: `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
- Modify（可选）: `tools/perf/HeroComing.Perf` 增加一个 tracked-type 场景

**Step 1: 跑门禁（追踪未在 HeroComing 中激活，预期零回归）**
```
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```
预期：Movement ≥ 1642 rounds/s（baseline 1917 的 80%），Attack ≥ 997 rounds/s（baseline 1205 的 80%），内存稳定，无崩溃。**若回归 → 回退，分析**（很可能某处 bump 未被 `_anyTrackingActive` 正确门控）。

**Step 2: 增加追踪激活的 perf 场景**（可选，测 Set 加 bump 的开销）：在 HeroComing.Perf 加一个变体，Track 某组件后测 Movement/Attack，记录数据，写进 `kb-change-tracking.md`。不作为门禁阈值（只观测）。

**Step 3: Commit** — `perf(change-tracking): gate green with tracking off; measured overhead with tracking on`

---

## Task 10: 知识库更新

**Files:**
- Modify: `.knowledge/kb-design-rationale.md`（§2 新增子系统决策 "Change Tracking"；§3 新增误判 "push 式 event 回调"）
- Create: `.knowledge/kb-change-tracking.md`（按 `_template.md`）
- Modify: `.knowledge/INDEX.md`（挂新页 + 模块地图）
- Modify: `.knowledge/kb-changelog.md`（变更记录）

**内容要点：**
- `kb-design-rationale.md` §2 新条目：机制选择（per-archetype 列版本 + signature transition log）、为什么不是 push 回调（三约束 + 概念唯一）、为什么不是 GetForWrite（Get=read 契约）、为什么 per-archetype 非 per-segment（HP archetype flat 模式，YAGNI）。
- §3 新误判条目："push 式 event 回调 / inline observer"——记录拒绝理由，指回本页。
- `kb-change-tracking.md`：这个模块是干什么的、架构、决策、认知模型（"Track<T> 是一个游标，值写入 bump 列版本、结构变更进 transition log，消费者各自推进游标"）、坑点（Get-ref-mutate 不追踪、批量 span 写不追踪、chunked 模式跨 segment 过报、transition log 无 compaction 的长会话内存特征）、入口。
- `updated` frontmatter 全部设为 `2026-07-06`。

**Commit** — `docs(change-tracking): knowledge base — rationale, new kb page, index`

---

## 风险清单（实现时盯紧）

1. **`SetComponentAtFlat` 签名变更**：加 `int columnIndex` 参数，更新所有 Core 内调用点（ComponentStore.ApplyToWorld 等）。漏一个 → 该路径不追踪（Replay/Submit 漏报）。
2. **`WriteComponentRaw` 的 read/write 区分**：`CopyComponentRaw(.., read: false)` 才 bump；`read:true`（读快照）绝不 bump。混了 → 读路径被污染。
3. **新 archetype 的激活时机**：Track 之后 `GetOrCreateArchetype` 新建的 archetype 必须立即 `ActivateArchetypeTracking`，否则后创建的实体写入不追踪。
4. **transition log 无 compaction**：长会话（1h+）结构性操作累积内存。MVP 接受，文档注明，soak test（`kb-soak-test.md`）观测。若 soak 内存增长 → 补 compaction（min-cursor shift）。
5. **ChangeQuery 是 sealed reference 类型**，不可拷贝；持有它的系统负责其生命周期。restore 后旧 ChangeQuery 失效。
6. **observer 变更在 apply 时产生**：Replay/Submit 必须经过 Archetype chokepoint（已验证路径），不能新增绕过 chokepoint 的写入捷径。

## 不在本次范围（YAGNI，记录待真需求）

- per-segment 列版本（chunked 模式更细粒度）
- transition log compaction
- 复合 query 的 `Track(QueryDescription)`（Transitions 已天然支持，ModifiedChunks 需指定 watched column）
- 批量 span 写入追踪（`GetWriteSpan<T>`）
- 多 consumer 引用计数回收

## 完成门禁（合并前全绿）

- [ ] `dotnet build -c Release` 0 错误
- [ ] `dotnet test -c Release` 全绿（含新增 ChangeTracking 测试）
- [ ] `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` 绿（tracking off 零回归）
- [ ] Soak test（`kb-soak-test.md`）跑一轮，observer 状态无分歧、内存稳定
- [ ] `.knowledge/` 更新齐备，INDEX 准确
