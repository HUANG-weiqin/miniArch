---
title: Change Tracking（变更追踪）
module: MiniArch.Core
description: TrackValueChanges<T>() 使用 boundary diff（baseline-to-current 扫描）返回 SharedValueChanges<T>；Set/CommandStream.Set 热路径不触达 value tracking；TrackTransitions(QueryDescription) 返回 TransitionLog 结构成员变更日志
updated: 2026-07-08 (value tracking 改为 boundary diff；Set 热路径移除 tracker 分支；GetRef/chunk span 直接写可追踪)
---

# Change Tracking（变更追踪）

## 这个模块是干什么的

- `World.TrackValueChanges<T>()` → `SharedValueChanges<T>`：对比上次 baseline 与当前 world 中所有 `T` 值，返回 `ReadOnlySpan<ValueChange<T>>` 的 Old/New net diff。
- `World.TrackTransitions(QueryDescription filter)` → `TransitionLog`：追踪实体因 Create/Destroy/Add/Remove 导致的 filter 成员进入/退出事件。
- `.Changes` / `.Transitions` 都是非破坏性读；必须显式调用 `.ClearAll()` / `.Clear()` 推进消费边界。
- 这个模块不保存跨帧历史；它是渲染层/观察层状态，不是确定性 sim 状态。

## 架构

- **值变更机制**：world lazy 创建 `SharedTrackerRegistry`，按 `ComponentType.Value` 持有唯一 `ChangeTracker<T>`。同一组件类型的所有 `SharedValueChanges<T>` handle 共享同一个 baseline。
- **ChangeTracker<T> 状态**：
  - `BaselineValues[entity.Id]`：上次 arm / `ClearAll()` / structural add 时记录的值。
  - `BaselineVersions[entity.Id]`：baseline 对应的 entity version；`0` 表示无 baseline。
  - `ChangesBuffer[]`：`.Changes` 读取时扫描 world 后临时填充的 `ValueChange<T>` 输出。
- **值路径控制流**：
  - `TrackValueChanges<T>()` 首次创建 tracker 时扫描当前 world，建立 baseline，不回溯之前的写入。
  - `SharedValueChanges<T>.Changes` 扫描当前 world 的 `T`，与 baseline 比较，生成 net diff。
  - `SharedValueChanges<T>.ClearAll()` 扫描当前 world，把当前值设为新 baseline，并清空输出。
  - `World.Set<T>()` 和 `CommandStream.Set<T>()` 完全不查询 tracker、不写 log、不读 baseline。
- **结构路径控制流**：Create/Destroy/Add/Remove → `AppendTransition` → 每个 `TransitionLog` 做 old/new archetype filter 匹配 → 记录 Entered/Exited。
- **生命周期边界**：Destroy 清该 entity 在所有 value tracker 中的 baseline；Remove<T> 清该组件 tracker 的 baseline；Add<T>/Create<T...> 若 tracker 已存在，会把新组件初始值作为 baseline（不记录 value change）；Replay raw Add 也会为已有 tracker 捕获 Add 初值 baseline。
- **RestoreState**：value tracker 保留注册但把 baseline 移到恢复后的当前 world；transition log 清 stale 数据但保持注册。

## 公共 API

```csharp
var positions = world.TrackValueChanges<Position>();

foreach (ref readonly var c in positions.Changes)
    UpdatePosition(c.Entity, c.Old, c.New);

positions.ClearAll();

var visible = world.TrackTransitions(
    new QueryDescription()
        .With<Renderable>()
        .Without<Hidden>());

foreach (var t in visible.Transitions)
{
    if (t.Kind == TransitionKind.Entered) SpawnHealthBar(t.Entity);
    else DestroyHealthBar(t.Entity);
}

visible.Clear();
```

## 语义要点

- `TrackValueChanges<T>()` 是 **baseline-to-current net value diff**，不是 `Set<T>` 调用日志。
- `.Changes` 会看见任何让当前 `T` 值不同于 baseline 的写法：
  - `World.Set<T>()`
  - `CommandStream.Set<T>()`
  - `world.GetRef<T>()` 返回 ref 后直接赋值
  - `chunk.GetSpan<T>()` / `GetComponentSpanAt<T>()` 直接写
- `A -> B -> C` 只返回 `Old=A, New=C`。
- `A -> B -> A` 返回空。
- no-op `A -> A` 返回空。
- Create/Add/Remove/Destroy 本身不是 value change；它们由 `TrackTransitions` 表达。Add/Create 后如果组件又被改值，baseline 是 Add/Create 的初始值。
- Existing entity 的 Replay Add→Set 与 Submit Add→Set 对齐：Add 初值是 baseline，后续 Set 终值不同则输出一条 value diff。
- 多个同类型 `SharedValueChanges<T>` handle 共享同一个 baseline；任意一个调用 `.ClearAll()` 会影响所有同类型 handle。
- `default(SharedValueChanges<T>)` 是 inert：`.Changes` 返回空，`.ClearAll()` 是 no-op。

## 决策

1. **不影响 Set 热路径**：value tracking 从 write-time tracker 改为 read-time/boundary diff。`Set<T>` 不再有 `SharedTrackers` null check、type lookup、baseline compare、dirty slot 写入。
2. **接受语义升级**：因为不再拦截写入点，value tracking 追踪的是“值是否变了”，不是“谁调用了 Set”。因此 ref/span 直接写现在也会被捕获。
3. **保持 shared per-type baseline**：同一 world + component type 只有一个 `ChangeTracker<T>`，多 handle 不 fanout。
4. **结构和值分离**：Add/Remove/Create/Destroy 用 `TransitionLog`；value tracker 只在组件存在于 baseline 与当前世界时输出 Old/New。
5. **不做 per-consumer cursor**：`.ClearAll()` 是该类型的全局 baseline 推进。需要独立消费进度时再新增能力。
6. **no-tracking 空状态仍为 null**：无人调用 `TrackValueChanges<T>()` 时 `World.SharedTrackers == null`，默认 world 不持有 registry。

## 认知模型

- `TrackValueChanges<T>()` 像“拍一张 T 组件快照”。
- `.Changes` 像“现在再拍一张，和上次快照做 diff”。
- `.ClearAll()` 像“把当前画面设为新快照”。
- `TrackTransitions(filter)` 像“在 filter 集合门口装一个门禁”，只记录实体进出集合。

## 性能特征

- **Set 热路径**：tracking on/off 对 `World.Set<T>()` 与 `CommandStream.Set<T>()` 的写入路径没有 value-tracking 分支差异。
- **读取成本**：`.Changes` 是 O(当前拥有 T 的实体数) 扫描；`.ClearAll()` 也是 O(当前拥有 T 的实体数) baseline 重建。
- **空间成本**：每个 tracked component type 持有按 `entity.Id` 直索引的 baseline arrays 和一个复用的 `ValueChange<T>[]` 输出 buffer。
- **多消费者**：同类型多 handle 共享 baseline 和输出 buffer，不增加 Set 成本。
- **HeroComing.Perf**：最新三路对比见 `kb-hero-pipeline-regression.md` 的 `--compare-old-value-tracking` 段。

## 入口

- `src/MiniArch/ChangeTracker.cs`：baseline-to-current 扫描与输出 buffer。
- `src/MiniArch/SharedValueChanges.cs`：public value diff handle。
- `src/MiniArch/Core/SharedTrackerRegistry.cs`：world-owned per-component tracker 注册表。
- `src/MiniArch/Core/World.cs`：`TrackValueChanges<T>()`、`TrackTransitions(...)`、tracker baseline 生命周期。
- `src/MiniArch/Core/World.StructuralChange.cs`：Add/Remove 生命周期 baseline 处理；Set 不触达 value tracking。
- `src/MiniArch/Core/World.Create.Generated.cs`：Create 初始组件 baseline capture。
- `src/MiniArch/Core/World.EntityLifecycle.cs`：Destroy baseline 清理。
- `src/MiniArch/TransitionLog.cs`：结构变更 log。

## 坑点

- `.Changes` 每次读取都会重建输出 buffer；需要长期保存结果时自行 copy。
- `ClearAll()` 是全局 baseline 推进，不是当前 handle 私有消费。
- Pending entity 的 batch 内中间 Add/Set/Remove 仍按 CommandStream 最终状态契约折叠；它们不是独立 value/transition 事件。
- `TrackTransitions(QueryDescription)` 要求 filter 非空；空 filter 抛 `ArgumentException`。
- `CaptureState`/`RestoreState` 后旧 handle/log 自动自愈；用户无需重新 arm。
- `World.Dispose` 后旧 handle/log 会抛 `ObjectDisposedException`。
