---
title: Change Tracking（变更追踪）
module: MiniArch.Core
description: World.Watch pull-event 模型：ChangeWatch/TransitionWatch Snapshot+Diff 两阶段扫描；struct handler 回调；零 per-write 成本；TransitionWatch 使用 dense epoch marks（已选定）；旧 TrackValueChanges/TransitionLog/DenseValueDiff/IValueProjector/IValueChangeSink 已删除
updated: 2026-07-09
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

- **值变更**：`ChangeWatch` 内部持有 `TComponent[] _oldValues`（按 `entity.Id` 直索引的 dense array）和 `int[] _touchedIds`（记录上次 snapshot 触及的 id 列表）。
  - `Snapshot(World)`：查询 world → 遍历 chunk → 记录每个实体的当前值到 `_oldValues`，同时用 `_touchedIds` 标记哪些 id 有了 baseline。
  - `Diff(World)`：再次查询 world → 遍历 chunk → 对每个实体，比较当前值与 `_oldValues[id]`（若 id 未触及则 `default`）→ 差异收集到 `_buffer[]` → 缓冲区稳定后逐条回调 handler。
- **投影值变更**：`ChangeWatch<TComponent, TValue, THandler>` 与值变更结构相同，但 baseline 存储的是 `TValue[]`，Snapshot 时调用 `handler.Project(component)`，Diff 时再次调用 `Project()` 并比较 `TValue` 是否相等。
- **结构变更**：`TransitionWatch` 内部持有 `Entity[] _snapshotEntities` + `long[] _snapshotMarks`（按 `entity.Id` 索引的 dense epoch 标记）和 `long[] _currentMarks` + `Entity[] _currentEntities`（复用 buffer）。
  - `Snapshot(World)`：递增 64-bit `_snapshotEpoch`（不溢出，无 per-Diff 清除）→ 遍历 query → 对每个实体 `EnsureMarkCapacity` → `_snapshotMarks[id] = _snapshotEpoch` → 存储到 `_snapshotEntities`。
  - `Diff(World)`：递增 64-bit `_currentEpoch` → 遍历当前 query → `_currentMarks[id] = _currentEpoch` → 存储到 `_currentEntities` → Exited：`_currentMarks[id] != _currentEpoch` → Entered：`_snapshotMarks[id] != _snapshotEpoch` → 无 per-Diff 清除，两阶段回调。
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
- 两阶段安全：`Diff` 先把所有 diff 收集到内部 `_buffer[]`，再逐条回调。handler 可以在 `OnChange` 中安全地 mutate world（如 spawn entity），不会破坏 diff 迭代。
- **Stale slot 语义**：`Snapshot` 时未触及的 entity slot（从未出现在 query 中）在 `Diff` 中若匹配 query，oldValue 为 `default`。Entity 被 Destroy/Remove 后，`Diff` 不会报告（因为当前扫描找不到它）。
- **id-based 语义（TransitionWatch）**：Destroy 后同 id 新实体（LIFO 复用）若匹配 filter，视为同一实体，不报 Exited+Entered。此设计有意简化——需要精确结构语义的场景应使用跨帧的 id+version 追踪。
- 旧 `TrackValueChanges<T>()`、`TrackTransitions(QueryDescription)`、`SharedValueChanges<T>`、`TransitionLog`、`CreateDenseValueDiff`、`DenseValueDiff`、`IValueProjector`、`IValueChangeSink`、`ChangeTracker<T>`、`SharedTrackerRegistry`、`IChangeQuery` 已全部删除，无兼容 shim。

## 决策

1. **纯 pull-event，不拦截写入**：Watch 不注册到 World，不拦截 `Set`/`Add`/`Remove`。写入热路径零额外分支。代价是 `Diff` 做全量扫描——这是 pull 模型的固有成本。
2. **两阶段回调安全**：所有 diff 先收集到 buffer，再回调 handler。允许 handler 在 `OnChange` 中 mutate world（如 spawn entity），不破坏迭代稳定性。
3. **dense array 直索引**：`_oldValues[id]` 是 O(1) 直访问。ID 密集时空间局部性极好；稀疏时浪费少量内存但性能仍可接受（已压缩到 touched slot 清理）。
4. **无世界级注册表**：旧架构的 `SharedTrackerRegistry`、`IChangeQuery dispatch`、`ChangeTracker<T>` 全部删除。每个 Watch 独立管理自己的 dense arrays，互不干扰，多 watch 不会 fanout 写入成本。
5. **struct handler 零分配回调**：`IChangeHandler`/`ITransitionHandler` 是 struct 接口约束，JIT 去虚化，回调零分配。`ref THandler Handler` 属性支持外部 mutate handler 字段。
6. **无 per-consumer cursor 管理**：Watch 不维护消费游标，不自动推进 baseline。消费端完全控制何时 `Snapshot`（推进 baseline）。
7. **删除旧 API，无兼容层**：旧 `TrackValueChanges`/`TrackTransitions`/`CreateDenseValueDiff`/`SharedValueChanges`/`TransitionLog`/`DenseValueDiff` 全部删除。旧 consumer 须迁移到 Watch API。
8. **默认 query vs 显式 query**：`ChangeWatch` 的 `query` 参数可选，`null` 时自动 `.With<TComponent>()`。`TransitionWatch` 的 filter 必填，空时抛 `ArgumentException`。
9. **Dense epoch marks 替代 bitset**：`TransitionWatch` 的 membership 使用 `long[]` dense array（按 `entity.Id` 直索引）。每个 id 存储最后被标记的 epoch 值；Snapshot/Diff 时递增对应 epoch 并写入，比较 mark == epoch 即可判断成员资格。**不**需要 per-Diff 清除——epoch bump 自动使旧标记失效。Epoch 计数器为 64-bit（`long`），无限寿命——服务器运行几十年不会溢出，无 `Array.Clear` 尖峰。稳态 Diff 零 heap allocation。

## 认知模型

- 把 `ChangeWatch` 看作**手动拍照对比**：拍一张（`Snapshot`），再拍一张（`Diff`），看哪里不一样。
- 把 `TransitionWatch` 看作**集合进出日志**：记录集合当前成员（`Snapshot`），下次查看谁进来谁出去（`Diff`）。
- 与旧模型（world 注册、intercept 写入、自动消费）的核心区别是**显式两阶段**：baseline 推进、diff 触发、回调消费全部由用户显式控制。

## 性能特征

- **热路径零成本**：`Watch` 创建不做任何 world 注册（无 registry、无 type lookup、无数组预分配 fallocate）。写入路径无任何 watch 分支。
- **`Snapshot`**：O(当前匹配 query 的实体数) 扫描 + baseline 存储。每个实体一次 `_oldValues[id] = value`（或 `handler.Project(component)`）。
- **`Diff`**：O(当前匹配 query 的实体数) 扫描 + O(entities) 值比较 + O(diffs) 回调。
- **空间**：每个 `ChangeWatch` 持有 `_oldValues`（按 `entity.Id` 索引）、`_touchedIds`（上次触及 id）、`_buffer`（diff buffer）。`TransitionWatch` 持有 `_snapshotEntities[]` + `_snapshotMarks` (long[]) + `_currentMarks` (long[]) + `_currentEntities[]` + `_buffer`。Dense epoch 比 bitset 内存多 32×（long vs bit），但 64-bit epoch 消除溢出风险且无 `Array.Clear` 尖峰。
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

## 入口

- `src/MiniArch/ChangeWatch.cs`：值变更 watch 实现（Snapshot/Diff/两阶段 buffer）。
- `src/MiniArch/ChangeWatch.Projected.cs`：投影值变更 watch 实现。
- `src/MiniArch/TransitionWatch.cs`：结构变更 watch 实现。
- `src/MiniArch/IChangeHandler.cs`：`IChangeHandler<TComponent>` 和 `IChangeHandler<TComponent, TValue>` 接口。
- `src/MiniArch/ITransitionHandler.cs`：`ITransitionHandler` 接口 + `TransitionKind` 枚举。
- `src/MiniArch/Core/World.cs`：`World.Watch<TComponent, THandler>()`、`World.Watch<TComponent, TValue, THandler>()`、`World.Watch<THandler>(QueryDescription)` 入口。

## 坑点

- `Diff` 前必须先调用 `Snapshot`，否则抛 `InvalidOperationException`。
- `Snapshot` 推进 baseline 后，旧 baseline 永久丢失（无法回退）。
- Stale slot 的 `oldValue` 来自 dense slot：该 id 在 Snapshot 时未触及时是 `default`；若 Snapshot 时曾匹配、之后 Destroy+Create 复用同 id，则可能是前一实体的 snapshot 值。
- TransitionWatch 是 id-based：Destroy+Create 同 id 复用不报 Exited+Entered。需要精确 version 语义时需自行记录。
- TransitionWatch 的 Entered 和 Exited 扫描均为 O(n)（使用 `_snapshotMarks` 和 `_currentMarks` dense epoch 标记进行 O(1) 成员检测）。Warmup 后无 per-Diff 分配。
- `Handler` 是 ref 返回的 struct 引用，不要缓存到局部变量后跨 `Diff`/`Snapshot` 使用（可能因数组 resize 变为 dangling ref）。
- `World` dispose 后调用 `Snapshot`/`Diff` 抛 `ObjectDisposedException`。
