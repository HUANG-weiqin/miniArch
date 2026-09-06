---
title: Change Tracking（变更追踪）
module: MiniArch.Core ChangeTracking
description: World.Watch pull-event 模型：ChangeWatch/TransitionWatch Snapshot+Diff 两阶段扫描；struct handler 回调；零 per-write 成本；TransitionWatch 使用 dense epoch marks。
updated: 2026-09-06
---

# Change Tracking（变更追踪）

> 2026-07-09: TransitionWatch membership 使用 dense epoch marks（int[] → long[]，64-bit epoch 消除溢出风险）。每个 id 存储最近一次被标记的 epoch 值；epoch bump 自动使旧标记失效，无需 per-Diff 清除，无 `Array.Clear` 尖峰。`WatchApi.Perf` 2s warmup + 5s measure 验证所有 Watch 场景稳态 `0 alloc/op`。

## 这个模块是干什么的

- `World.Watch<TComponent, THandler>(QueryDescription?)` → `ChangeWatch<TComponent, THandler>`：值变更追踪。`Snapshot(World)` 记录 baseline，`Diff(World)` 扫描当前 world、比较 baseline、回调 `IChangeHandler.OnChange`。
- `World.Watch<TComponent, TValue, THandler>(QueryDescription?)` → `ChangeWatch<TComponent, TValue, THandler>`：投影值变更追踪。handler 同时负责投影（`Project`）和消费（`OnChange`），比较在投影值上做。
- `World.Watch<THandler>(QueryDescription)` → `TransitionWatch<THandler>`：结构变更追踪。`Snapshot(World)` 记录当前 filter 成员，`Diff(World)` 对比前后成员集，回调 `ITransitionHandler.OnChange`（Entered/Exited）。
- Watch 是纯 pull-event 模型：不拦截 `Set`/`Add`/`Remove`，不写 dirty log，不维护 per-type 注册表。
- 这个模块不保存跨帧历史；它只基于 baseline 快照的 snapshot/diff 两阶段模式工作。

## 架构

- **值变更**：`ChangeWatch` 内部持有 `TComponent[] _oldValues`、同样按 `entity.Id` 直索引的 `int[] _baselineVersions` 和 `int[] _touchedIds`（记录上次 snapshot 触及的 id 列表）。
  - `Snapshot(World)`：查询 world → 遍历 chunk → 记录每个实体的当前值与 `Entity.Version`，同时用 `_touchedIds` 标记 baseline。
  - `Diff(World)`：仅当当前 `(Id, Version)` 匹配 baseline 时比较 `_oldValues[id]`；新 generation 的 oldValue 是 `default`。差异先收集到 `_buffer[]`，再逐条回调。
- **投影值变更**：`ChangeWatch<TComponent, TValue, THandler>` 与值变更结构相同，但 baseline 存储的是 `TValue[]`，Snapshot 时调用 `handler.Project(component)`，Diff 时再次调用 `Project()` 并比较 `TValue` 是否相等。
- **结构变更**：`TransitionWatch` 内部持有 snapshot/current `Entity[]`、`long[]` dense epoch marks 与 `int[]` version arrays。
  - `Snapshot(World)`：递增 64-bit `_snapshotEpoch`（不溢出，无 per-Diff 清除）→ 记录每个成员的 epoch 与 version。
  - `Diff(World)`：记录当前 epoch 与 version；只有 `(Id, Version)` 同时匹配才视为同一成员，否则先报告旧 generation Exited，再报告新 generation Entered。
- **生命周期**：Watch 不与 world 注册（无 SharedTrackerRegistry、无 IChangeQuery dispatch）。`Snapshot`/`Diff` 通过 `world.Query()` 读取当前状态。World dispose 后调用 `Snapshot`/`Diff` 抛 `ObjectDisposedException`。

## 公共 API

```csharp
// ── Value change watch ────────────────────────────────────────────────
struct HpHandler : IChangeHandler<Health>
{
    public int Count;
    public void OnChange(World world, Entity entity, in Health oldValue, in Health newValue)
    {
        Count++;
        UpdateHealthBar(entity, oldValue, newValue);
    }
}

var watch = world.Watch<Health, HpHandler>(
    new QueryDescription().With<Health>().With<EnemyTag>());
watch.Snapshot(world);
// ... mutate ...
watch.Diff(world);

// ── Transition watch ──────────────────────────────────────────────────
struct SpawnHandler : ITransitionHandler
{
    public void OnChange(World world, Entity entity, TransitionKind kind)
    {
        if (kind == TransitionKind.Entered) SpawnHealthBar(entity);
        else DestroyHealthBar(entity);
    }
}

var tWatch = world.Watch<SpawnHandler>(
    new QueryDescription().With<Renderable>().Without<Hidden>());
tWatch.Snapshot(world);
// ... mutate ...
tWatch.Diff(world);

// ── Handler mutation via ref ──────────────────────────────────────────
ref var handler = ref watch.Handler;  // mutate struct in-place
handler.Count = 0;
```

## 语义要点

- `Snapshot` 记录的是 **当时** 的 world 状态快照。`Snapshot` 后任何改变（Set/Add/Remove/Destroy）在下一次 `Diff` 中被发现。
- `Diff` 是 **非破坏性读**：同一 baseline 可多次调用 `Diff`，每次产生相同回调（除非 world 继续变化）。
- `Snapshot` 再次调用推进 baseline：旧 baseline 被丢弃，新 baseline 在当前 world 状态建立。
- 两阶段安全：`Diff` 先把所有 diff 收集到内部 `_buffer[]`，再逐条回调。handler 可以在 `OnChange` 中安全地 mutate world（如 spawn entity），不会破坏 diff 迭代；但不能在回调/投影期间对**同一个 watch** 嵌套调用 `Snapshot` 或 `Diff`，否则会 fast-fail。
- Snapshot 异常安全：收集开始前旧 baseline 即失效；若 query 扫描或投影抛异常，partial baseline 不可观察，后续 `Diff` 会要求先成功 `Snapshot`。所有操作 guard 均在 `finally` 中释放，异常后 watch 可重试。
- **generation 语义**：baseline 和 membership 使用完整 `(Entity.Id, Entity.Version)`。`Snapshot` 后新建或复用 id 的实体没有旧值 baseline，因此 oldValue 为 `default`；旧实体 Destroy/Remove 后值 Watch 不报告，因为当前扫描找不到它。TransitionWatch 对复用 id 报告旧 generation Exited 与新 generation Entered。
- 旧 `TrackValueChanges<T>()`、`TrackTransitions(QueryDescription)`、`SharedValueChanges<T>`、`TransitionLog`、`CreateDenseValueDiff`、`DenseValueDiff`、`IValueProjector`、`IValueChangeSink`、`ChangeTracker<T>`、`SharedTrackerRegistry`、`IChangeQuery` 已全部删除，无兼容 shim。

## 决策

1. **纯 pull-event，不拦截写入**：Watch 不注册到 World，不拦截 `Set`/`Add`/`Remove`。写入热路径零额外分支。代价是 `Diff` 做全量扫描——这是 pull 模型的固有成本。
2. **两阶段回调安全**：所有 diff 先收集到 buffer，再回调 handler。允许 handler 在 `OnChange` 中 mutate world（如 spawn entity），不破坏迭代稳定性。
3. **dense array 直索引 + version**：值与 version 均由 `entity.Id` O(1) 定位，再以 `Entity.Version` 确认 identity。ID 密集时空间局部性极好；稀疏时用额外 dense version array 换回完整实体身份。
4. **无世界级注册表**：旧架构的 `SharedTrackerRegistry`、`IChangeQuery dispatch`、`ChangeTracker<T>` 全部删除。每个 Watch 独立管理自己的 dense arrays，互不干扰，多 watch 不会 fanout 写入成本。
5. **struct handler 零分配回调**：`IChangeHandler`/`ITransitionHandler` 是 struct 接口约束，JIT 去虚化，回调零分配。`ref THandler Handler` 属性支持外部 mutate handler 字段。
6. **无 per-consumer cursor 管理**：Watch 不维护消费游标，不自动推进 baseline。消费端完全控制何时 `Snapshot`（推进 baseline）。
7. **删除旧 API，无兼容层**：旧 `TrackValueChanges`/`TrackTransitions`/`CreateDenseValueDiff`/`SharedValueChanges`/`TransitionLog`/`DenseValueDiff` 全部删除。旧 consumer 须迁移到 Watch API。
8. **默认 query vs 显式 query**：`ChangeWatch` 的 `query` 参数可选；无论是否显式传入，Watch 都会把 `.With<TComponent>()` 合并为 required 条件，保证内部读取的组件列存在，调用方传入的其他过滤条件保持不变。`TransitionWatch` 的 filter 必填，空时抛 `ArgumentException`。
9. **Dense epoch marks 替代 bitset**：`TransitionWatch` 的 membership 使用 `long[]` dense array（按 `entity.Id` 直索引）。每个 id 存储最后被标记的 epoch 值；Snapshot/Diff 时递增对应 epoch 并写入，比较 mark == epoch 即可判断成员资格。**不**需要 per-Diff 清除——epoch bump 自动使旧标记失效。Epoch 计数器为 64-bit（`long`），无限寿命——服务器运行几十年不会溢出，无 `Array.Clear` 尖峰。稳态 Diff 零 heap allocation。
10. **同 watch 不可重入**：Snapshot、扫描状态和 callback buffer 都是实例级复用内存；嵌套调用会覆写外层操作的状态，因此统一以实例级 operation guard 拒绝。guard 使用 `try/finally` 恢复，不增加稳态分配。不同 watch 仍可互相调用。
11. **失败 Snapshot 使 baseline 失效**：不为罕见异常路径保留双份 dense arrays；投影或扫描失败后明确要求重新 Snapshot，避免暴露半写 baseline，同时保持正常路径的内存规模和零分配特征。

## 认知模型

- 把 `ChangeWatch` 看作**手动拍照对比**：拍一张（`Snapshot`），再拍一张（`Diff`），看哪里不一样。
- 把 `TransitionWatch` 看作**集合进出日志**：记录集合当前成员（`Snapshot`），下次查看谁进来谁出去（`Diff`）。
- 与旧模型（world 注册、intercept 写入、自动消费）的核心区别是**显式两阶段**：baseline 推进、diff 触发、回调消费全部由用户显式控制。

## 性能特征

- **热路径零成本**：`Watch` 创建不做任何 world 注册（无 registry、无 type lookup、无数组预分配 fallocate）。写入路径无任何 watch 分支。
- **`Snapshot`**：O(当前匹配 query 的实体数) 扫描 + baseline 存储。每个实体一次 `_oldValues[id] = value`（或 `handler.Project(component)`）。
- **`Diff`**：O(当前匹配 query 的实体数) 扫描 + O(entities) 值比较 + O(diffs) 回调。
- **空间**：每个 `ChangeWatch` 持有 `_oldValues`、`_baselineVersions`（均按 `entity.Id` 索引）、`_touchedIds` 和 `_buffer`。`TransitionWatch` 持有 snapshot/current entities、epoch marks 与 version arrays、以及 `_buffer`。版本数组只在 Watch 实例内增长；World 写入路径仍无 Watch 状态。
- **稳态 GC**：内部数组按需增长，增长后不再缩小；稳态 `Snapshot`+`Diff` 循环零堆分配。Dense epoch `long[]` 在 warmup 后不再 reallocate（max entity id 稳定）。
- **多 watch 同组件**：互不干扰，各自持有独立的 baseline arrays。不共享状态，不 fanout。

### WatchApi.Perf 发布验证（2026-07-09）

命令：

```bash
dotnet run -c Release --project tools/perf/WatchApi.Perf -- --entity-count 10000 --warmup-seconds 2 --duration-seconds 5
```

结果（10k entities，2s warmup + 5s measure）：

| Scenario | ops/s | alloc/op |
|---|---:|---:|
| change-quick-nochange | 15,973.8 | 0 B |
| change-quick-allchanged | 4,206.6 | 0 B |
| change-projected-nochange | 15,768.7 | 0 B |
| change-projected-allchanged | 4,013.7 | 0 B |
| transition-nochange | 11,137.5 | 0 B |
| transition-all-entered | 1,838.2 | 0 B |
| transition-all-exited | 1,698.4 | 0 B |
| transition-churn-1pct | 7,447.8 | 0 B |

**决策**：TransitionWatch 使用 dense epoch marks（long[] 按 entity.Id 索引）作为 membership 判定。空间换时间：long 标记比 bitset 多 32× 内存，但 epoch bump 避免 per-Diff 清除，64-bit epoch 保证服务器无限运行不溢出，稳态零分配，在当前 ECS dense-id 模型下性能最优。

### Version identity A/B（2026-09-06）

同机连续运行、10k entities、2s warmup + 5s measure；下表是一组未修改 main 与 version-aware worktree 的配对结果，方向性证据而非统计基线。所有场景仍为 `0 B/op`。

| Scenario | main ops/s | version-aware ops/s | Δ |
|---|---:|---:|---:|
| change-quick-nochange | 17,575.6 | 15,366.7 | -12.6% |
| change-quick-allchanged | 6,310.4 | 6,076.6 | -3.7% |
| change-projected-nochange | 14,436.8 | 12,080.2 | -16.3% |
| change-projected-allchanged | 5,552.5 | 5,495.7 | -1.0% |
| transition-nochange | 9,754.6 | 8,913.7 | -8.6% |
| transition-all-entered | 1,866.6 | 1,824.0 | -2.3% |
| transition-all-exited | 1,816.5 | 1,856.3 | +2.2% |
| transition-churn-1pct | 9,433.8 | 7,972.2 | -15.5% |

新增 dense version metadata 使 cold-start 增长后保持零分配；完整 entity identity 的读取/写入成本主要落在无变化扫描。性能门禁 `HeroComing.Perf --check-baseline` 通过（Movement 2209.7、Attack 1166.5 rounds/s）。

## 入口

- `src/MiniArch/ChangeWatch.cs`：值变更 watch 实现（Snapshot/Diff/两阶段 buffer）。
- `src/MiniArch/ChangeWatch.Projected.cs`：投影值变更 watch 实现。
- `src/MiniArch/TransitionWatch.cs`：结构变更 watch 实现。
- `src/MiniArch/IChangeHandler.cs`：`IChangeHandler<TComponent>` 和 `IChangeHandler<TComponent, TValue>` 接口。
- `src/MiniArch/ITransitionHandler.cs`：`ITransitionHandler` 接口 + `TransitionKind` 枚举。
- `src/MiniArch/Core/World.cs`：`World.Watch<TComponent, THandler>()`、`World.Watch<TComponent, TValue, THandler>()`、`World.Watch<THandler>(QueryDescription)` 入口。

## 坑点

- `Diff` 前必须先调用 `Snapshot`，否则抛 `InvalidOperationException`。
- 成功 `Snapshot` 推进 baseline 后，旧 baseline 永久丢失（无法回退）；失败 Snapshot 会使 baseline 失效，必须重试成功后才能 Diff。
- `oldValue` 只属于完整 `(Id, Version)` baseline：该 identity 未在 Snapshot 时匹配时为 `default`；Destroy+Create 复用同 id 不会继承前一 generation 的值。
- TransitionWatch 使用完整 entity identity：Destroy+Create 同 id 依次报告旧 generation Exited 和新 generation Entered。
- TransitionWatch 的 Entered 和 Exited 扫描均为 O(n)（使用 `_snapshotMarks` 和 `_currentMarks` dense epoch 标记进行 O(1) 成员检测）。Warmup 后无 per-Diff 分配。
- 同一 watch 的 `Snapshot`/`Diff` 不可嵌套；handler 需要组合其他追踪时使用另一个 watch。该 guard 解决单线程重入，不承诺 World 或 Watch 的并发线程安全。
- `World` dispose 后调用 `Snapshot`/`Diff` 抛 `ObjectDisposedException`。
