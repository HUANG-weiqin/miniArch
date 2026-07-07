---
title: Change Tracking（变更追踪）
module: MiniArch.Core
description: Track() + Capture<T> 游标驱动的原生变更追踪——ModifiedChunks<T> 管值写入、Transitions 管成员进出，Changes()/DrainTypedChanges<T> 提供 Old/New 快照对
updated: 2026-07-08 (DrainTypedChanges<T> 双缓冲 TypedChange 日志；1%≈1.0x、10%≈1.6x、50/100%≈1.1x 手写 dirty-list)
---

# Change Tracking（变更追踪）

## 这个模块是干什么的

- 这个模块负责：
  - 给用户一个 `world.Track()` 返回的 `ChangeQuery` 游标对象，通过 `.Capture<T>()` 注册需要值快照的组件类型。三类枚举：`ModifiedChunks<T>()`（被 Set<T> 写过的 chunk，值变更）、`Transitions()`（进入/离开筛选集的实体，结构变更）、`Changes()`（+ `.Previous()` 后提供 Old/New 快照）。
  - 游标全内化，用户不接触任何版本号。不调用枚举方法的开销等于一条 bool 分支。
- 这个模块不负责：
  - 追踪通过 `Get<T>` 返回 ref 后直接改字段的变更——C# ref 无法拦截，想追踪值变更必须走 `Set<T>`。
  - 跨帧存储变更历史（transition log 是 ephemeral 渲染层状态，非确定 sim 状态）。

## 架构

- 核心组成：
  - **值变更机制**：per-(Archetype, componentColumn) 的 `long` 版本号，在 Archetype 的 3 个写入 chokepoint（`SetComponentAtTyped`、`SetComponentAtFlat`、`WriteComponentRaw`）处 bump。全局 `World._writeEpoch`（long，无回绕）作单调时钟。
  - **结构变更机制**：每个 `ChangeQuery` 持有自己的 `List<Transition>`（只存匹配的 Entered/Exited，预过滤）。World 在每个结构操作时通过 `IChangeQuery.OnTransition` 将变更 dispatch 到所有注册的 query（`List<WeakReference<IChangeQuery>>`），每个 query 独立 filter 后存入自己的 log。
  - **快照捕获机制**：当 `.Previous()` 启用时，`ChangeQuery` 实现 `IChangeQuery.OnBeforeWrite`/`OnAfterWrite`/`OnBeforeTransition` 来捕获 Old/New 快照。Set 路径通过 `World.DispatchBeforeWrite`/`DispatchAfterWrite`；结构路径通过 `World.DispatchBeforeTransition` + `AppendTransition`。快照数据存于 `_snapBuffer`（byte[]，分 Old/New 两半），在 `Changes()` 调用时物化为 `EntityChange[]`。5 个 hook 点：`ApplyTypedSet`、`ApplyRawSet`、`CommandStream.ApplyToWorld`（Set-only fast 和 mixed-kind 两条路径）、`ApplyTypedAdd`、`RemoveBoxed`、`DestroySingle`。
  - **ChangeQuery**（非泛型）：由 `.Capture<T>()` 注册组件类型列表。`_transitions`（自有 log）、`_snapBuffer` + `_snapEntities`（快照数据）、`_valueCursors`（per-type ModifiedChunks 游标）。
  - **Typed fast path**：单 `.Capture<T>()` + `.Previous()` + 无 filter 时启用 `ChangeTracker<T>`。Set 热路径直接追加到双缓冲 `TypedChange<T>[]` 日志；`DrainTypedChanges<T>()` 只做 buffer swap 并返回 `ReadOnlySpan<TypedChange<T>>`，不再物化 byte[] 或二次构造结果数组。
  - **全局 gate**：`Archetype._anyTrackingActive` / `World._anyTrackingActive`。无人 Track 时所有 hook 是一条预测不命中的 bool 分支，零成本。
- 数据流 / 控制流：
  - 写入路径（Set / CommandStream Submit / Replay）→ pre-hook `DispatchBeforeWrite` → Archetype 写入方法 bump 列版本号 → post-hook `DispatchAfterWrite` → `ChangeQuery.OnBeforeWrite`/`OnAfterWrite` 捕获 Old/New 快照
  - typed 单类型路径：`World.Set<T>` → `ChangeTracker<T>.ActiveLog[slot] = TypedChange<T>(entity, old, new)` → 用户 `DrainTypedChanges<T>()` → swap `ActiveLog/SpareLog` → 清 `SlotByEntityPlusOne[id]`。
  - 结构路径（Create/Destroy/Add/Remove）→ `DispatchBeforeTransition`（捕获 Old 快照）→ 实体迁移 → `AppendTransition`（`IChangeQuery.OnTransition` 检查 filter → 匹配的追加到 `_transitions` + 捕获 New 快照）
  - 用户调用 `Transitions()` drain（auto-clear）；`ModifiedChunks<T>()` 查询列版本游标；`Changes()` 物化快照数据

## 公共 API

```csharp
// ── 入口 ──
var q = world.Track()
    .Capture<Position>()       // 注册 Position 为快照追踪类型（可多次调用）
    .Capture<Velocity>()
    .With<Player>()            // 筛选举例：只追踪 With<Player>
    .Without<Dead>()           //                 且 Without<Dead>
    .Previous();               // 启用 Old/New 快照捕获

// ── 枚举 ──
foreach (var t in q.Transitions())         // Entered/Exited（auto-clear）
    if (t.Kind == TransitionKind.Entered) SpawnHealthBar(t.Entity);

foreach (var chunk in q.ModifiedChunks<Position>())  // 值写入脏 chunk（IEnumerable，有分配）
    { var span = chunk.GetSpan<Position>(); ... }

var span = q.DrainModifiedChunks<Position>();        // 值写入脏 chunk（ReadOnlySpan，零分配）
for (var i = 0; i < span.Length; i++)
    { var chunk = span[i]; var s = chunk.GetSpan<Position>(); ... }

foreach (var c in q.Changes())             // Old/New 快照对
{
    ref readonly var oldPos = ref c.Old.Get<Position>();
    ref readonly var newPos = ref c.New.Get<Position>();
    // c.Entity, c.Old.Get<HP>().Value, c.New.Get<Velocity>().Dx
}
```

### 类型

- `EntityChange`：Old/New 快照对。`.Old`/`.New` 返回 `EntitySnapshot`（ref struct）。
- `EntitySnapshot`：通过 `.Get<T>()` 读取捕获的组件值。`.Has<T>()` 检查是否包含。
- `Transition`：实体进入/退出 filter 事件。`.Kind`（Entered/Exited）、`.Cause`（Created/Destroyed/Added/Removed）。

### Fluent Filter API

`ChangeQuery` 支持链式筛选：`.With<T>()`、`.Without<T>()`、`.WithAny<T>()`。这些方法操作 `QueryDescription._filter`，通过 `QueryCache.Matches` 决定 Transitions 的成员集。

**语义要点**：
- `.Capture<T>()` 只注册组件类型做快照——**不**自动添加到 filter。需要显式调用 `.With<T>()` 做筛选。
- `ModifiedChunks<T>()` 要求 T 必须先被 `.Capture<T>()` 注册。
- `Transitions()` 只对 `._filter` 匹配的实体生效。
- `Changes()` 的 filter 检查在 `OnBeforeWrite`/`OnAfterWrite` 中进行——只捕获匹配 archetype 的写入。

## 决策

1. **Get=read/Set=write 契约**：值追踪只挂 Set；Get 拿 ref 不追踪（C# ref 无法拦截）。
2. **per-archetype 列版本非 per-segment**：同上。
3. **instrument 在 Archetype 3 个写入方法**：所有写入路径的公共底。
4. **Capture<T> 分解为独立注册**：不再隐式添加 With<T> 到 filter。Capture 和 With 职责分离。
5. **transition log 记 old→new signature 转移，Entered/Exited 在 dispatch 时确定**：同上。
6. **ModifiedChunks/Transitions/Changes 急切物化**：保证"调用即推进游标"语义。
7. **long epoch 无回绕**。
8. **transition log 不序列化、不 checksum**。
9. **Transitions() auto-clear**。
10. **fluent filter 复用 QueryDescription/QueryCache.Matches**。
11. **transition log 挂在 ChangeQuery 而非 World**。
12. **WeakReference 自动 prune**。
13. **Previous() 是 per-query 开关，非全局**：开销在 off 时为 0。On 时每个 Set 做快照捕获。
14. **Changes() 不保留 OldValue/NewValue 字段名**：改为 `Old.Get<T>()`/`New.Get<T>()` 支持多类型。
15. **OnBeforeWrite/OnAfterWrite 成对出现**：预/后钩子确保 Old 在写入前读取，New 在写入后读取。
16. **OnBeforeTransition 预捕获 Old**：结构变化前 Old 值已记录在 _snapBuffer，结构变化后 OnTransition 再写 New。Create/Destroy 跳过快照（无 Old 或 New）。
17. **per-entity 语义（非 per-Set-call）**：每个 entity 只记 first Old + last New。多次 Set 同一 entity 只产生一条 Change。
18. **inline capture 快速路径**：单 query + Previous 时，CaptureOld/CaptureNew 内联到 ApplyTypedSet，消除 dispatch 循环。
19. **直接索引替代 Dictionary**：用 stamp+version+slot 数组按 entity.Id 直接索引，消除 Dictionary 查找开销。
20. **CaptureNew 跳过 filter 检查**：Set 不改变 archetype，CaptureOld 已验证 filter，CaptureNew 无需重复检查。
21. **`DrainTypedChanges<T>()` 使用双缓冲 `TypedChange<T>[]` append log**：相比 entity-indexed Old/New + bitset，最终结果本来就要以 `TypedChange<T>` 暴露；在 Set 时直接写结果 log，drain 只 swap，避免 `DrainTypedChanges<T>()` 再构造一份中间数组。`SlotByEntityPlusOne[id]` 同时表示 dirty 与 slot，保持 first Old + last New 语义并减少随机写。

## 认知模型

Track() 返回一个游标：Capture 注册追踪组件类型、With/Without 设筛选、Previous 开快照。值写入 bump 列版本、结构变更进 transition log、快照成对写入 byte buffer。

常见误解：
- **误解**：`Get<T>` 后改字段会触发变更追踪。→ 实际不会，C# ref 无法拦截，必须走 `Set<T>`。
- **误解**：`ModifiedChunks<T>` 通知"值变了"。→ 实际通知的是"被写脏了"，写相同值也算"变更"。
- **误解**：`.Capture<T>()` 隐式做了 `.With<T>()`。→ 实际不会，Capture 和 With 是正交的。

## 性能特征

实测，Ryzen 7 5700X3D / .NET 8 Release：
- **tracking OFF（默认）**：HeroComing.Perf 门禁应零回归；任何 `src/MiniArch/` 架构改动仍必须跑 `HeroComing.Perf --check-baseline`。
- **普通 `Changes()` 多类型/带 filter 路径**：仍使用 byte[] shadow + Lazy New，优先保证兼容结构变更与多 capture。
- **单类型无 filter 的 `DrainTypedChanges<T>()` 路径**：使用双缓冲 `TypedChange<T>[]` append log，热路径不写 byte[]、不构造 drain buffer。
- **GameTickSim.Perf `--modified-chunks`（100K entities, 500 ticks；Manual=手写 shadow+dirty-list，Changes=`Capture<Position>().Previous().DrainTypedChanges<Position>()`）**：
  | 更新密度 | C/M 典型范围 | 结论 |
  |----------|--------------|------|
  | 1%       | 1.00x–1.12x  | 与手写持平或略快 |
  | 10%      | 1.57x–1.86x  | 明显快于手写 |
  | 50%      | 1.09x–1.14x  | 快于手写 |
  | 100%     | 1.09x–1.15x  | 快于手写 |

  **已优化**：
  1. `_anyPreviousTrackingActive` 门控：无 Previous 时 dispatch 完全跳过
  2. `_singlePreviousQuery` 快速路径：单 query 时内联 CaptureOld，消除 dispatch 循环
  3. 直接索引数组替代 Dictionary<Entity, int>（stamp+version+slot 数组）
  4. **Lazy New**：热路径不读 New，Changes() 时从 live storage 读。结构变更时 materialize-on-escape
  5. CaptureNew 跳过 filter/archetype 检查（Set 不改变 archetype）
  6. `DrainModifiedChunks<T>()` 零分配 span API
  7. per-entity 语义：每个 entity 只记 first Old + last New（非 per-Set-call）
  8. Previous() 时预分配 entity 索引数组到 world 容量
  9. `DrainTypedChanges<T>()` 直接返回双缓冲 `TypedChange<T>[]`；`SlotByEntityPlusOne[id]` 合并 dirty flag 与 slot map，减少随机写

## 入口

- `src/MiniArch/ChangeQuery.cs`：`ChangeQuery` 公共 API（非泛型 + 泛型 `ChangeQuery<T>` 已移除）
- `src/MiniArch/EntityChange.cs`：`EntityChange` / `EntitySnapshot` 类型定义
- `src/MiniArch/Transition.cs`：`Transition` / `TransitionKind` 类型定义
- `World.Track()`：入口方法
- `Core/World.cs`：`DispatchBeforeWrite`/`DispatchAfterWrite`/`DispatchBeforeTransition` 实现
- `Core/IChangeQuery.cs`：`OnBeforeWrite`/`OnAfterWrite`/`OnBeforeTransition` 默认方法
- `Core/World.StructuralChange.cs`：pre-hook 调用点（ApplyTypedAdd、RemoveBoxed、ApplyTypedSet/ApplyRawSet）
- `src/MiniArch/ChangeTracker.cs`：单类型无 filter 的 `DrainTypedChanges<T>()` typed fast path（双缓冲 `TypedChange<T>[]`）

最小示例：
```csharp
var q = world.Track().Capture<HP>().With<HP>().Previous();
foreach (var t in q.Transitions())
    if (t.Kind == TransitionKind.Entered) SpawnHealthBar(t.Entity);
    else DestroyHealthBar(t.Entity);
foreach (var chunk in q.ModifiedChunks<HP>())
    { var span = chunk.GetSpan<HP>(); ... }
foreach (var c in q.Changes())
    UpdateHealthBar(c.Entity, c.Old.Get<HP>().Value, c.New.Get<HP>().Value);
```

## 坑点

- **Pending entity 的中间操作不产生 Changes() 条目**：通过 `CommandStream.Create()`/`Clone()` 创建的 pending entity，其所有 `Add`/`Set`/`Remove` 操作在 `Submit()`/`Snapshot()`/`Replay()` 时折叠为最终组件状态。这意味着：
  - `Changes()` 不包含这些中间操作对应的 Old/New 快照。
  - `Transitions()` 只反映最终 filter 匹配结果（Create+Add+Remove → 从未进入 filter）。
  - Existing entity 的 `Add`/`Set`/`Remove`（通过 component store 路径）不受此影响，它们仍然产生独立的 Changes() 条目。
  - 详见 `kb-command-stream.md`「Pending entity 最终状态契约」段。

- `Get<T>` 返回 ref 后直接改字段**不追踪**（C# ref 无法拦截）。
- 批量 `chunk.GetSpan<T>()` 后改 span **不追踪**（同上）。
- chunked 模式（>2MB archetype）下版本是 per-archetype，跨 segment 过报。
- transition log / _snapBuffer 在 drain 时自动清零。不调用的 query 会增长——丢弃的 query 由 WeakReference 自动 prune。
- `CaptureState`/`RestoreState` 后旧 cursor **自动自愈**（self-heal）：内部累积的 transition/value-change 清空，cursor 推进到当前 epoch，重新注册 dispatch 路径。用户无需重新 `Track()`。World.Dispose 后旧 cursor 会抛 `ObjectDisposedException`。
- `.Capture<T>()` 必须在使用 `.With<T>()` 之前或之后调用——顺序无关。但必须赶在第一次枚举之前。
- `Changes()` 返回的 `EntityChange[]` 内的 byte 数据是独立副本（从 `_snapBuffer` 克隆），可跨帧持有。
- `DrainTypedChanges<T>()` 返回的是双缓冲 `TypedChange<T>[]` 的 span：drain 后下一次 Set 不会覆盖刚返回的 span，但下一次 drain 会让旧 span 失效。不要改成单缓冲；否则用户在 drain 后、下一次 drain 前继续读 span 会被新 Set 覆盖。
- typed fast path 也必须保持 `Changes()` 兼容：无 filter 单 Capture 会启用 `ChangeTracker<T>`，因此 `Changes()` 不能只看 byte[] shadow 路径；通过 `ComponentRegistry.Shared.TryGetType` + `MakeGenericMethod` 调度到 `DrainTypedChanges<T>()` 再遍历构造 `EntityChange[]`（删除了 `IChangeTrackerDrain` 接口）。
- `Previous()` 启用时，若某个 `.Capture<T>()` 注册的类型在当前 archetype 中不存在，快照的 Old/New 相应的 byte 范围保持为零值（0-initialized）。不会崩溃或读取错数据。这是通过 `TryGetComponentIndex` 的守卫实现的——所有 4 个快照捕获点（`OnBeforeWrite`、`OnAfterWrite`、`OnBeforeTransition`、`WriteNewTransitionSnapshot`）在读取前先检查组件是否存在，缺失则跳过拷贝。**注意**：`getComponentIndexFast` 不在这些守卫中，若在其他路径使用需自行保障。
