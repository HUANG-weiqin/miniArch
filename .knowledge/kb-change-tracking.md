---
title: Change Tracking（变更追踪）
module: MiniArch.Core
description: Track() + Capture<T> 的轻量变更追踪——Transitions() 管结构变化，ValueChanges<T>() 管 lazy world-shared 单类型 Previous 值变化
updated: 2026-07-08 (RestoreState 保留观察者注册并清空 prediction-era 变更；shared tracker 仍保持 lazy/null 空状态)
---

# Change Tracking（变更追踪）

## 这个模块是干什么的

- 这个模块负责：
  - 给用户一个 `world.Track()` 返回的 `ChangeQuery` 游标对象，通过 `.Capture<T>()` 注册关注的组件类型。
  - `Transitions()`：追踪实体是否进入/离开 query filter（Create/Destroy/Add/Remove 导致的结构变化）。
  - `ValueChanges<T>()`：在 **恰好一个 `Capture<T>` + `Previous()` + 无 filter** 时，返回单类型 Old/New 变更对；这是一个 **value-only observer**，不会再收集 transitions。
  - `ClearChanges<T>()`：显式清空某个组件类型的 value changes；读取不再自动清空。
- 这个模块不负责：
  - 追踪通过 `Get<T>` 返回 ref 后直接改字段的变更——C# ref 无法拦截，想追踪值变更必须走 `Set<T>`。
  - 跨帧存储变更历史（transition log 是 ephemeral 渲染层状态，非确定 sim 状态）。
  - 多 capture / 带 filter 的值级 Old/New 组合结果；当前只保留单类型 typed fast path。

## 架构

- 核心组成：
  - **结构变更机制**：每个 `ChangeQuery` 持有自己的 `List<Transition>`。World 在 Create/Destroy/Add/Remove 时通过 `IChangeQuery.OnTransition` 分发到所有注册 query（`List<WeakReference<IChangeQuery>>`），query 自己做 filter 匹配并写入 log。
  - **typed 值变更机制**：满足 typed fast path 契约时，world lazy 创建 `SharedTrackerRegistry`，并在其中按 `ComponentType.Value` 持有唯一 `ChangeTracker<T>`。多个相同 `Track().Capture<T>().Previous()` query 共享同一个 tracker。tracker 由：
    - `SlotByEntityPlusOne[id]`：`0=clean`，`slot+1=dirty 且对应 ActiveLog 的槽位`
    - `ActiveLog` / `SpareLog`：双缓冲 `TypedChange<T>[]`
    - `DirtyCount`：当前 clear 周期内已记录的条目数
  - **生命周期管理**：
    - world 强拥有 shared tracker；query 只是读视图，不再拥有 per-query tracker
    - `SharedTrackerRegistry` 必须是 lazy/null：没有任何 `Previous()` value tracker 时，`World.SharedTrackers == null`，`CommandStream` 走 direct null no-track fast path；不要在 World 构造时常驻分配 registry
    - tracker 创建时按当前 `World.EntityCapacity` `PreSize`，后续 world 增长时在 `Set<T>` 补 `EnsureEntityCapacity`
    - query 配置变化（加 filter / 第二 capture）会让该 query 失去 typed value 视图；world 已创建的 shared tracker 可继续服务其它同类型 query
    - 纯 `ValueChanges<T>()` query 会从 `World._changeQueries` 退订；加 filter 后再重新订阅 transitions
    - `Track().Capture<T>()` 如果没有 `Previous()` 且没有 filter，是 inert cursor：不注册 transitions、不创建 tracker、不影响 Set/Submit 热路径
    - `RestoreState()` 后不会拆掉仍存活的观察者：transition query 立即清掉 prediction-era log 并保留注册；shared registry 只清 pending changes、保留已创建 tracker，避免 restore 后第一批 mutation 因“尚未读取 query 自愈”而丢失。`Dispose()` 才清 registry 并置 null
  - **边界修复**：
    - world 扩容后，`ApplyTypedSet` 先 `EnsureEntityCapacity(id)` 再按 `entity.Id` 直索引，避免创建 query 之后继续长大时越界
    - destroy 会清该 id 在所有 typed tracker 中的 slot；remove 某组件只清该组件对应 tracker 的 slot，避免 id 复用或 remove+add+set 把两段生命周期串成同一条 change
- 数据流 / 控制流：
  - 值路径：`World.Set<T>` → `World.SharedTrackers?.GetTracker<T>()` 直接按组件类型 O(1) 查 shared tracker（无 registry 或无人追踪则 null）→ `slot==0` 时写入第一份 `Old` + 当前 `New`，`slot!=0` 时仅更新最后一份 `New` → 用户 `ValueChanges<T>()` 只是读 `ActiveLog[..DirtyCount]` → 用户显式 `ClearChanges<T>()` 时 swap buffer、清 slot、`DirtyCount=0`
  - `CommandStream.Set<T>` 路径：`ComponentStore<T>.ApplyToWorld()` 在 **registry 存在且当前组件类型存在 shared tracker 时** 改走 tracked helper，保证 `Submit()` 落地的 Set 也能进入 `ValueChanges<T>()`；registry 为 null 或无人 tracking 该类型时保留内联 `*NoTrack` 快路径
  - 结构路径：仅对配置了 filter 的 query 生效。Create/Destroy/Add/Remove → `AppendTransition` → query 做 old/new archetype filter 匹配 → 记录 Entered/Exited
  - 用户调用 `Transitions()` 仍是 drain 语义；`ValueChanges<T>()` 是非破坏性读，必须由 `ClearChanges<T>()` 清空

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
valueChanges.ClearChanges<Position>();
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
- `Track().Capture<T>()` 本身不是订阅；没有 `Previous()`、也没有 `.With/.Without/.WithAny` filter 时，它不注册 transitions，也不创建 value tracker。
- `ValueChanges<T>()` 只读当前 dirty log，不清空；必须调用 `ChangeQuery.ClearChanges<T>()` 或 `World.ClearChanges<T>()` 显式清空。
- `ValueChanges<T>()` 是 **per-entity** 语义：同一 clear 周期内多次 `Set<T>` 只保留 first `Old` + last `New`。
- 多个相同类型的 value query 共享同一个 world-owned tracker；其中任意一个 query 的 `ClearChanges<T>()` 都会清空该组件类型，所有同类型 query 都看到空。
- `ChangeQuery.ValueChanges<T>()` / `ClearChanges<T>()` 只对该 query 唯一 captured 的 T 生效；想不依赖 query 句柄清空任意类型，用 `World.ClearChanges<T>()`。
- **单 capture + `Previous()` + 无 filter** 的 query 是 value-only：`Transitions()` 永远为空。想同时看结构变化，必须单独再建一个 query。

## 决策

1. **只保留两种公开枚举**：`Transitions()` 管结构变化，`ValueChanges<T>()` 管单类型值变化。
2. **Get=read / Set=write 契约**：值追踪只挂 `Set<T>`；`Get<T>` 拿 ref 后修改不追踪。
3. **typed fast path 只服务精确契约**：恰好一个 capture、开 `Previous()`、且无 filter；否则宁可返回空，也不做半兼容路径。
4. **直接索引数组替代 Dictionary**：按 `entity.Id` 直索引 `SlotByEntityPlusOne`，换取更低热路径成本。
5. **world-owned per-type shared tracker**：同一个 world + component type 只保留一个 `ChangeTracker<T>`，多个相同 value query 共享；`Set<T>` 不随 consumer 数 fanout。
6. **非破坏性读 + 显式 clear**：`ValueChanges<T>()` 只返回当前 span；`ClearChanges<T>()` 才清 slot / swap buffer。读取不是消费，清理才是消费。
7. **不做 per-query cursor**：当前选择全局 clear，保持概念唯一。需要多个消费者独立消费进度时再新增 cursor，不预留。
8. **类型匹配严格**：query captured `Position` 时，`ValueChanges<Velocity>()` 和 `ClearChanges<Velocity>()` 对该 query 都是 no-op / empty，即使 world 里已有 `Velocity` shared tracker。
9. **配置变化立即撤销 query 的 typed value 视图**：加 filter / 第二 capture 后，`ValueChanges<T>()` 直接返回空，避免语义漂移。
10. **显式切断生命周期边界**：destroy/remove 成功后清 slot，避免 id 复用和 remove+add 跨生命周期串脏。
11. **RestoreState 不要求手动 re-arm query**：旧 query 继续可用；restore 时立即丢弃 prediction-era transition/value-change，但保留 observer/tracker 注册，所以 restore 后第一批 mutation 也必须被观察到。
12. **value observer 与 transition observer 解耦**：纯 `ValueChanges<T>()` query 不再默认附带全量 structural observer，避免高 churn 场景下 transition log / `ToArray()` 噪音淹没真正的值变化成本。
13. **no-tracking 空状态必须是 null**：`SharedTrackerRegistry` 不能常驻在每个 World 上。实测常驻 registry 会让 HeroComing no-observer Movement 从接近 1950 掉到约 1836；改回 lazy/null 后恢复到约 1941。

## 认知模型

`Track()` 返回一个游标：`Capture<T>()` 决定关注的组件，`With/Without/WithAny` 决定结构 filter，`Previous()` 尝试打开 typed fast path。结构变化写 query 自己的 `Transition` log；值变化写 world 共享的 per-type `TypedChange<T>[]` log。

常见误解：
- **误解**：`Get<T>` 后改字段会触发变更追踪。→ 实际不会，C# ref 无法拦截，必须走 `Set<T>`。
- **误解**：`ModifiedChunks<T>` 通知"值变了"。→ 实际通知的是"被写脏了"，写相同值也算"变更"。
- **误解**：`.Capture<T>()` 隐式做了 `.With<T>()`。→ 实际不会，Capture 和 With 是正交的。

## 性能特征

实测，Ryzen 7 5700X3D / .NET 8 Release：
- **tracking OFF（默认）**：任何 `src/MiniArch/` 架构改动仍必须跑 `HeroComing.Perf --check-baseline`。
- **tracking OFF 空状态**：`World.SharedTrackers == null`。这是性能契约，不只是实现细节；no-observer `CommandStream` 必须绕开 registry/tracker lookup，直接走 no-track write path。
- **单类型无 filter 的 `ValueChanges<T>()` 路径**：使用双缓冲 `TypedChange<T>[]` append log；稳态 `0.00 KB/tick`。
- **GameTickSim.Perf（100K entities, 500 ticks；Manual=手写 shadow+dirty-list，Changes=`Capture<Position>().Previous().ValueChanges<Position>()` + 每 tick `ClearChanges<Position>()`）**：
  | 更新密度 | Manual | Changes | C/M | Alloc M | Alloc C |
  |----------|--------|---------|-----|---------|---------|
  | 1%       | 13639  | 11284   | 0.83x | 0.00 KB | 0.00 KB |
  | 10%      | 4571   | 4970    | 1.09x | 0.00 KB | 0.00 KB |
  | 50%      | 1538   | 1591    | 1.03x | 0.00 KB | 0.00 KB |
  | 100%     | 764    | 808     | 1.06x | 0.00 KB | 0.00 KB |

  **同类型 consumer scaling（100K entities, 50% updates, 500 ticks；只读第一个 query、每 tick clear 一次，用于隔离 Set fanout）**：
  | Consumers | ops/s | KB/tick |
  |-----------|------:|--------:|
  | 1 | 1468 | 0.00 |
  | 2 | 1619 | 0.00 |
  | 8 | 1649 | 0.00 |

  **关键原因**：
  1. `SlotByEntityPlusOne[id]` 直接索引，避免 Dictionary/hash
  2. `TypedChange<T>[]` 双缓冲，clear 时 swap，不二次构造结果
  3. `SharedTrackerRegistry.GetTracker<T>()` 按 `ComponentType.Value` 直索引，内部用 invariant + `Unsafe.As` 避开热路径 type check
  4. 同类型多 query 共享 tracker，`Set<T>` 只记录一次
  5. `PreSize` + `EnsureEntityCapacity` 让 world 扩容后仍保持安全
  6. `destroy/remove` 清 slot，保证 first `Old` + last `New` 只在同一生命周期内成立

- **HeroComing.Perf 旁路 observer 实验（`--track-observer`，2026-07-08）**：
  - 旧实现（拆分前）：挂 `Track().Capture<T>().Previous()` observer 后，Movement `1338.1 rounds/s`，Attack `981.2 rounds/s`；`transitions` 极大且会带来额外堆分配/保留噪音
  - shared tracker + 显式 clear 后：Movement `1664.9 rounds/s`，Attack `1023.8 rounds/s`；`transitions=0`，`Heap delta` 稳定，Gen0/1/2 为 `3/3/3`
  - Hero perf 实际不需要 old value；`--track-observer` 改为 capture-only/no `Previous()` 后不创建 tracker、不记录 old/new。lazy registry + inert capture 后实测 no-observer Movement `1941.4` / Attack `1200.6`，capture-only observer Movement `1999.0` / Attack `1204.0`（单次样本，均为 Release，波动范围内）
  - 结论：Hero 场景里先前的“大堆分配”主因不是 `TypedChange<T>[]`，而是 value observer 被强行捆绑了 transitions；后续 no-observer 基线下跌的主因是 `SharedTrackerRegistry` 常驻破坏 no-track null fast path。拆分 transitions + lazy registry 后，关掉 `Previous()` 的 observer 与 baseline 等价。

## 入口

- `src/MiniArch/ChangeQuery.cs`：`ChangeQuery` 公共 API（非泛型 + 泛型 `ChangeQuery<T>` 已移除）
- `src/MiniArch/Transition.cs`：`Transition` / `TransitionKind` 类型定义
- `World.Track()`：入口方法
- `Core/World.cs`：query 注册、lazy `SharedTrackers` registry、`World.ClearChanges<T>()`、`RestoreState()` 自愈 generation
- `Core/SharedTrackerRegistry.cs`：world-owned per-component `ChangeTracker<T>` 注册表
- `Core/IChangeQuery.cs`：仅保留 `OnTransition`
- `Core/World.StructuralChange.cs`：`Set<T>` 的 typed fast path、`RemoveBoxed()` 的 slot 清理
- `Core/World.EntityLifecycle.cs`：`DestroySingle()` 的 slot 清理
- `src/MiniArch/ChangeTracker.cs`：单类型无 filter 的 `ValueChanges<T>()` typed fast path（双缓冲 `TypedChange<T>[]`）

最小示例：
```csharp
var hpValues = world.Track().Capture<HP>().Previous();
foreach (var c in hpValues.ValueChanges<HP>())
    UpdateHealthBar(c.Entity, c.Old.Value, c.New.Value);
hpValues.ClearChanges<HP>();

var hpTransitions = world.Track().Capture<HP>().With<HP>();
foreach (var t in hpTransitions.Transitions())
    if (t.Kind == TransitionKind.Entered) SpawnHealthBar(t.Entity);
    else DestroyHealthBar(t.Entity);
```

## 坑点

- **Pending entity 的中间操作不产生 Changes() 条目**：通过 `CommandStream.Create()`/`Clone()` 创建的 pending entity，其所有 `Add`/`Set`/`Remove` 操作在 `Submit()`/`Snapshot()`/`Replay()` 时折叠为最终组件状态。这意味着：
  - `ValueChanges<T>()` 不包含这些中间操作对应的 Old/New 快照。
  - `Transitions()` 只反映最终 filter 匹配结果（Create+Add+Remove → 从未进入 filter）。
  - Existing entity 的 `Add`/`Set`/`Remove`（通过 component store 路径）不受此影响，它们仍然产生独立的 Changes() 条目。
  - 详见 `kb-command-stream.md`「Pending entity 最终状态契约」段。

- `Get<T>` 返回 ref 后直接改字段**不追踪**（C# ref 无法拦截）。
- 批量 `chunk.GetSpan<T>()` 后改 span **不追踪**（同上）。
- `Transitions()` 在 drain 时自动清零；`ValueChanges<T>()` 不自动清零，必须显式 `ClearChanges<T>()`。
- capture-only/no `Previous()`/no filter query 是 inert cursor；如果想追踪结构进入/退出，必须加 `.With<T>()` 或其它 filter。
- `CaptureState`/`RestoreState` 后旧 cursor **自动自愈**（self-heal）：内部累积的 transition/value-change 清空，cursor 推进到当前 epoch，重新注册 dispatch 路径。用户无需重新 `Track()`。World.Dispose 后旧 cursor 会抛 `ObjectDisposedException`。
- `.Capture<T>()` 必须在使用 `.With<T>()` 之前或之后调用——顺序无关。但必须赶在第一次枚举之前。
- `ValueChanges<T>()` 返回的是 shared tracker `ActiveLog` 的只读 span：**直到下一次 `ClearChanges<T>()` 前有效**，但它是当前 dirty log 的 live view；clear 前同一实体再次 `Set<T>` 会更新已返回 span 中对应 entry 的 `New`。需要稳定快照时用户自行 copy。
- world 在 query 创建后继续扩容是允许的：typed tracker 会在 `Set<T>` 时按 `entity.Id` 补扩容；但为了保护上一帧返回的 span，增长时只会扩当前写 buffer，不会立刻动 `SpareLog`。
- 加了 filter 或第二个 capture 后，typed fast path 会被撤销，`ValueChanges<T>()` 直接返回空 span。这不是退化成慢路径，而是当前 API 契约本身。
- 纯 `ValueChanges<T>()` query **不再收到 transitions**。如果你既想看值变化又想看结构变化，要建两个 query，而不是复用同一个 query。
