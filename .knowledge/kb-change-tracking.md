---
title: Change Tracking（变更追踪）
module: MiniArch.Core
description: Track() + Capture<T> 游标驱动的原生变更追踪——ModifiedChunks<T> 管值写入、Transitions 管成员进出，Changes() 提供 Old/New 快照对
updated: 2026-07-09
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
  - **全局 gate**：`Archetype._anyTrackingActive` / `World._anyTrackingActive`。无人 Track 时所有 hook 是一条预测不命中的 bool 分支，零成本。
- 数据流 / 控制流：
  - 写入路径（Set / CommandStream Submit / Replay）→ pre-hook `DispatchBeforeWrite` → Archetype 写入方法 bump 列版本号 → post-hook `DispatchAfterWrite` → `ChangeQuery.OnBeforeWrite`/`OnAfterWrite` 捕获 Old/New 快照
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

foreach (var chunk in q.ModifiedChunks<Position>())  // 值写入脏 chunk
    { var span = chunk.GetSpan<Position>(); ... }

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
13. **Previous() 是 per-query 开关，非全局**：开销在 off 时为 0。On 时每个 Set 或结构操作做 `ComponentType.Count * Unsafe.SizeOf<T>()` 次 memcpy。
14. **Changes() 不保留 OldValue/NewValue 字段名**：改为 `Old.Get<T>()`/`New.Get<T>()` 支持多类型。
15. **OnBeforeWrite/OnAfterWrite 成对出现**：预/后钩子确保 Old 在写入前读取，New 在写入后读取。
16. **OnBeforeTransition 预捕获 Old**：结构变化前 Old 值已记录在 _snapBuffer，结构变化后 OnTransition 再写 New。Create/Destroy 跳过快照（无 Old 或 New）。

## 认知模型

Track() 返回一个游标：Capture 注册追踪组件类型、With/Without 设筛选、Previous 开快照。值写入 bump 列版本、结构变更进 transition log、快照成对写入 byte buffer。

常见误解：
- **误解**：`Get<T>` 后改字段会触发变更追踪。→ 实际不会，C# ref 无法拦截，必须走 `Set<T>`。
- **误解**：`ModifiedChunks<T>` 通知"值变了"。→ 实际通知的是"被写脏了"，写相同值也算"变更"。
- **误解**：`.Capture<T>()` 隐式做了 `.With<T>()`。→ 实际不会，Capture 和 With 是正交的。

## 性能特征

实测，BenchmarkDotNet，Ryzen 7 5700X3D / .NET 8 Release：
- **Set<T> of tracked type**：+~1.65 ns/op（~16%），来自一条预测中的 bool 分支 + 一个 long store。
- **Previous() 启用**：额外 ~20-30 ns/op（一次 byte[] 拷贝做快照）。
- **tracking OFF（默认）**：HeroComing.Perf 门禁零回归（Movement 1973 / Attack 1195，均高于 baseline 1642/997）。
- **结构 ops（Create+Destroy）**：+~16% median。

## 入口

- `src/MiniArch/ChangeQuery.cs`：`ChangeQuery` 公共 API（非泛型 + 泛型 `ChangeQuery<T>` 已移除）
- `src/MiniArch/EntityChange.cs`：`EntityChange` / `EntitySnapshot` 类型定义
- `src/MiniArch/Transition.cs`：`Transition` / `TransitionKind` 类型定义
- `World.Track()`：入口方法
- `Core/World.cs`：`DispatchBeforeWrite`/`DispatchAfterWrite`/`DispatchBeforeTransition` 实现
- `Core/IChangeQuery.cs`：`OnBeforeWrite`/`OnAfterWrite`/`OnBeforeTransition` 默认方法
- `Core/World.StructuralChange.cs`：pre-hook 调用点（ApplyTypedAdd、RemoveBoxed、ApplyTypedSet/ApplyRawSet）

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

- `Get<T>` 返回 ref 后直接改字段**不追踪**（C# ref 无法拦截）。
- 批量 `chunk.GetSpan<T>()` 后改 span **不追踪**（同上）。
- chunked 模式（>2MB archetype）下版本是 per-archetype，跨 segment 过报。
- transition log / _snapBuffer 在 drain 时自动清零。不调用的 query 会增长——丢弃的 query 由 WeakReference 自动 prune。
- `CaptureState`/`RestoreState` 后旧 cursor **自动自愈**（self-heal）：内部累积的 transition/value-change 清空，cursor 推进到当前 epoch，重新注册 dispatch 路径。用户无需重新 `Track()`。World.Dispose 后旧 cursor 会抛 `ObjectDisposedException`。
- `.Capture<T>()` 必须在使用 `.With<T>()` 之前或之后调用——顺序无关。但必须赶在第一次枚举之前。
- `Changes()` 返回的 `EntityChange[]` 内的 byte 数据是独立副本（从 `_snapBuffer` 克隆），可跨帧持有。
