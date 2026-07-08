---
title: Change Tracking（变更追踪）
module: MiniArch.Core
description: Track() + Capture<T> 的轻量变更追踪——Transitions() 管结构变化，ValueChanges<T>() 管 value-only 单类型 Previous 值变化
updated: 2026-07-08 (value-only query 与 transitions 正式拆分；CommandStream.Set 接回 typed tracking；补回 single-tracker Set<T> 快路)
---

# Change Tracking（变更追踪）

## 这个模块是干什么的

- 这个模块负责：
  - 给用户一个 `world.Track()` 返回的 `ChangeQuery` 游标对象，通过 `.Capture<T>()` 注册关注的组件类型。
  - `Transitions()`：追踪实体是否进入/离开 query filter（Create/Destroy/Add/Remove 导致的结构变化）。
  - `ValueChanges<T>()`：在 **恰好一个 `Capture<T>` + `Previous()` + 无 filter** 时，返回单类型 Old/New 变更对；这是一个 **value-only observer**，不会再收集 transitions。
- 这个模块不负责：
  - 追踪通过 `Get<T>` 返回 ref 后直接改字段的变更——C# ref 无法拦截，想追踪值变更必须走 `Set<T>`。
  - 跨帧存储变更历史（transition log 是 ephemeral 渲染层状态，非确定 sim 状态）。
  - 多 capture / 带 filter 的值级 Old/New 组合结果；当前只保留单类型 typed fast path。

## 架构

- 核心组成：
  - **结构变更机制**：每个 `ChangeQuery` 持有自己的 `List<Transition>`。World 在 Create/Destroy/Add/Remove 时通过 `IChangeQuery.OnTransition` 分发到所有注册 query（`List<WeakReference<IChangeQuery>>`），query 自己做 filter 匹配并写入 log。
  - **typed 值变更机制**：满足 typed fast path 契约时启用 `ChangeTracker<T>`。它由：
    - `SlotByEntityPlusOne[id]`：`0=clean`，`slot+1=dirty 且对应 ActiveLog 的槽位`
    - `ActiveLog` / `SpareLog`：双缓冲 `TypedChange<T>[]`
    - `DirtyCount`：当前 drain 周期内已记录的条目数
  - **生命周期管理**：
    - query 强引用自己的 `_typedTracker`
    - world 只用 `WeakReference<IChangeTrackerControl>` 持有 tracker，避免被遗弃 query 泄漏成幽灵 tracker
    - 当且仅当 live typed tracker 恰好 1 个时，world 额外缓存一个 `_singleTypedTracker` weak ref，`Set<T>` 可跳过 `List` 遍历直接命中该 tracker
    - query 配置变化（加 filter / 第二 capture）会撤销 typed fast path
    - 纯 `ValueChanges<T>()` query 会从 `World._changeQueries` 退订；加 filter / 第二 capture 后再重新订阅 transitions
    - `RestoreState()` 后旧 query 通过 `_trackingGeneration` 自愈：清 stale 状态并按当前配置重建 tracker
  - **边界修复**：
    - world 扩容后，`ApplyTypedSet` 先 `EnsureEntityCapacity(id)` 再按 `entity.Id` 直索引，避免创建 query 之后继续长大时越界
    - destroy 会清该 id 在所有 typed tracker 中的 slot；remove 某组件只清该组件对应 tracker 的 slot，避免 id 复用或 remove+add+set 把两段生命周期串成同一条 change
- 数据流 / 控制流：
  - 值路径：`World.Set<T>` → 若只有 1 个 live typed tracker 则直达 `_singleTypedTracker`，否则遍历 live typed trackers → `slot==0` 时写入第一份 `Old` + 当前 `New`，`slot!=0` 时仅更新最后一份 `New` → 用户 `ValueChanges<T>()` drain 时 swap `ActiveLog/SpareLog`
  - `CommandStream.Set<T>` 路径：`ComponentStore<T>.ApplyToWorld()` 在 **存在 typed tracker 时** 改走 `World.ApplyTypedSet<T>`，保证 `Submit()` 落地的 Set 也能进入 `ValueChanges<T>()`；无人 tracking 时仍保留 `*NoTrack` 快路径
  - 结构路径：仅对已订阅 transitions 的 query 生效。Create/Destroy/Add/Remove → `AppendTransition` → query 做 old/new archetype filter 匹配 → 记录 Entered/Exited
  - 用户调用 `Transitions()` / `ValueChanges<T>()` 都是 drain 语义；返回后旧累积被清空

## 公共 API

```csharp
var transitions = world.Track()
    .Capture<Position>()
    .With<Position>();

var valueChanges = world.Track()
    .Capture<Position>()
    .Previous();

foreach (var t in transitions.Transitions())
    if (t.Kind == TransitionKind.Entered) SpawnHealthBar(t.Entity);

var span = valueChanges.ValueChanges<Position>();
for (var i = 0; i < span.Length; i++)
{
    ref readonly var c = ref span[i];
    UpdatePosition(c.Entity, c.Old, c.New);
}
```

### 类型

- `TypedChange<T>`：单组件 Old/New 快照对，包含 `Entity`、`Old`、`New`。
- `Transition`：实体进入/退出 filter 事件。`.Kind`（Entered/Exited）、`.Cause`（Created/Destroyed/Added/Removed）。

### Fluent Filter API

`ChangeQuery` 支持链式筛选：`.With<T>()`、`.Without<T>()`、`.WithAny<T>()`。这些方法操作 `QueryDescription._filter`，通过 `QueryCache.Matches` 决定 Transitions 的成员集。

**语义要点**：
- `.Capture<T>()` 只注册组件类型做快照——**不**自动添加到 filter。需要显式调用 `.With<T>()` 做筛选。
- `Transitions()` 只对 `._filter` 匹配的实体生效。
- `ValueChanges<T>()` 只有在 **单 capture + `Previous()` + 无 filter** 时才有值；其余配置直接返回空 span。
- `ValueChanges<T>()` 是 **per-entity** 语义：同一 drain 周期内多次 `Set<T>` 只保留 first `Old` + last `New`。
- **单 capture + `Previous()` + 无 filter** 的 query 是 value-only：`Transitions()` 永远为空。想同时看结构变化，必须单独再建一个 query。

## 决策

1. **只保留两种公开枚举**：`Transitions()` 管结构变化，`ValueChanges<T>()` 管单类型值变化。
2. **Get=read / Set=write 契约**：值追踪只挂 `Set<T>`；`Get<T>` 拿 ref 后修改不追踪。
3. **typed fast path 只服务精确契约**：恰好一个 capture、开 `Previous()`、且无 filter；否则宁可返回空，也不做半兼容路径。
4. **直接索引数组替代 Dictionary**：按 `entity.Id` 直索引 `SlotByEntityPlusOne`，换取更低热路径成本。
5. **双缓冲 `TypedChange<T>[]`**：Set 时直接写最终暴露格式；drain 只做 buffer swap，不二次构造结果。
6. **world 弱引用持有 tracker**：query 拥有 tracker 生命周期，world 只负责热路径分发与 dead-ref prune。
7. **单 tracker 恢复直达快路**：保留弱引用语义，但在 live tracker 恰好 1 个时缓存一个 weak ref，避免每次 `Set<T>` 都走 `List<WeakReference<...>>` 分发。
8. **配置变化立即撤销 fast path**：加 filter / 第二 capture 后，typed tracker 直接下线，避免语义漂移。
9. **显式切断生命周期边界**：destroy/remove 成功后清 slot，避免 id 复用和 remove+add 跨生命周期串脏。
10. **RestoreState 自愈重建，不复用旧 buffer**：旧 query 继续可用，但必须丢弃 prediction-era 脏数据并重新挂回 world。
11. **value observer 与 transition observer 解耦**：纯 `ValueChanges<T>()` query 不再默认附带全量 structural observer，避免高 churn 场景下 transition log / `ToArray()` 噪音淹没真正的值变化成本。

## 认知模型

`Track()` 返回一个游标：`Capture<T>()` 决定关注的组件，`With/Without/WithAny` 决定结构 filter，`Previous()` 尝试打开 typed fast path。结构变化写 `Transition`；值变化在满足契约时直接写 `TypedChange<T>[]`。

常见误解：
- **误解**：`Get<T>` 后改字段会触发变更追踪。→ 实际不会，C# ref 无法拦截，必须走 `Set<T>`。
- **误解**：`ModifiedChunks<T>` 通知"值变了"。→ 实际通知的是"被写脏了"，写相同值也算"变更"。
- **误解**：`.Capture<T>()` 隐式做了 `.With<T>()`。→ 实际不会，Capture 和 With 是正交的。

## 性能特征

实测，Ryzen 7 5700X3D / .NET 8 Release：
- **tracking OFF（默认）**：任何 `src/MiniArch/` 架构改动仍必须跑 `HeroComing.Perf --check-baseline`。
- **单类型无 filter 的 `ValueChanges<T>()` 路径**：使用双缓冲 `TypedChange<T>[]` append log；稳态 `0.00 KB/tick`。
- **GameTickSim.Perf（100K entities, 500 ticks；Manual=手写 shadow+dirty-list，Changes=`Capture<Position>().Previous().ValueChanges<Position>()`）**：
  | 更新密度 | Manual | Changes | C/M | Alloc M | Alloc C |
  |----------|--------|---------|-----|---------|---------|
  | 1%       | 13146  | 10466   | 0.80x | 0.00 KB | 0.00 KB |
  | 10%      | 5411   | 6288    | 1.16x | 0.00 KB | 0.00 KB |
  | 50%      | 1523   | 1758    | 1.15x | 0.00 KB | 0.00 KB |
  | 100%     | 748    | 879     | 1.18x | 0.00 KB | 0.00 KB |

  **关键原因**：
  1. `SlotByEntityPlusOne[id]` 直接索引，避免 Dictionary/hash
  2. `TypedChange<T>[]` 双缓冲，drain 不二次分配
  3. `World._singleTypedTracker` 让单 observer 场景避开 weak-ref 列表遍历
  4. `PreSize` + `EnsureEntityCapacity` 让 world 扩容后仍保持安全
  5. `destroy/remove` 清 slot，保证 first `Old` + last `New` 只在同一生命周期内成立

- **HeroComing.Perf 旁路 observer 实验（`--track-observer`，2026-07-08）**：
  - 旧实现（拆分前）：挂 `Track().Capture<T>().Previous()` observer 后，Movement `1338.1 rounds/s`，Attack `981.2 rounds/s`；`transitions` 极大且会带来额外堆分配/保留噪音
  - 新实现（value-only 拆分后）：Movement `1829.3 rounds/s`，Attack `1167.7 rounds/s`；`transitions=0`，`Heap delta` 恢复稳定，Gen0/1/2 回到 `3/3/3`
  - 结论：Hero 场景里先前的“大堆分配”主因不是 `TypedChange<T>[]`，而是 value observer 被强行捆绑了 transitions。拆分后，剩下的成本才主要反映真实的 value tracking 开销

## 入口

- `src/MiniArch/ChangeQuery.cs`：`ChangeQuery` 公共 API（非泛型 + 泛型 `ChangeQuery<T>` 已移除）
- `src/MiniArch/Transition.cs`：`Transition` / `TransitionKind` 类型定义
- `World.Track()`：入口方法
- `Core/World.cs`：query / tracker 注册、弱引用 prune、`RestoreState()` 自愈 generation
- `Core/IChangeQuery.cs`：仅保留 `OnTransition`
- `Core/World.StructuralChange.cs`：`Set<T>` 的 typed fast path、`RemoveBoxed()` 的 slot 清理
- `Core/World.EntityLifecycle.cs`：`DestroySingle()` 的 slot 清理
- `src/MiniArch/ChangeTracker.cs`：单类型无 filter 的 `ValueChanges<T>()` typed fast path（双缓冲 `TypedChange<T>[]`）

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
  - `ValueChanges<T>()` 不包含这些中间操作对应的 Old/New 快照。
  - `Transitions()` 只反映最终 filter 匹配结果（Create+Add+Remove → 从未进入 filter）。
  - Existing entity 的 `Add`/`Set`/`Remove`（通过 component store 路径）不受此影响，它们仍然产生独立的 Changes() 条目。
  - 详见 `kb-command-stream.md`「Pending entity 最终状态契约」段。

- `Get<T>` 返回 ref 后直接改字段**不追踪**（C# ref 无法拦截）。
- 批量 `chunk.GetSpan<T>()` 后改 span **不追踪**（同上）。
- `Transitions()` 在 drain 时自动清零；typed tracker 由 query 强持有、world 弱引用持有，丢弃 query 后会在后续写入时自动 prune。
- `CaptureState`/`RestoreState` 后旧 cursor **自动自愈**（self-heal）：内部累积的 transition/value-change 清空，cursor 推进到当前 epoch，重新注册 dispatch 路径。用户无需重新 `Track()`。World.Dispose 后旧 cursor 会抛 `ObjectDisposedException`。
- `.Capture<T>()` 必须在使用 `.With<T>()` 之前或之后调用——顺序无关。但必须赶在第一次枚举之前。
- `ValueChanges<T>()` 返回的是双缓冲 `TypedChange<T>[]` 的 span：drain 后下一次 `Set<T>` 不会覆盖刚返回的 span，但**下一次 drain 会让旧 span 失效**。不要改成单缓冲。
- world 在 query 创建后继续扩容是允许的：typed tracker 会在 `Set<T>` 时按 `entity.Id` 补扩容；但为了保护上一帧返回的 span，增长时只会扩当前写 buffer，不会立刻动 `SpareLog`。
- 加了 filter 或第二个 capture 后，typed fast path 会被撤销，`ValueChanges<T>()` 直接返回空 span。这不是退化成慢路径，而是当前 API 契约本身。
- 纯 `ValueChanges<T>()` query **不再收到 transitions**。如果你既想看值变化又想看结构变化，要建两个 query，而不是复用同一个 query。
