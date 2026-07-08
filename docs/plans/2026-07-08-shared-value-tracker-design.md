# 共享 per-Component 值追踪器设计

> **Status 2026-07-08:** 历史中间设计。共享 per-component handle/API 保留，但这里的 dirty-log / slot 设计已被 boundary diff 取代：`ChangeTracker<T>` 保存 baseline，`.Changes` 扫描当前 world，`Set<T>` / `CommandStream.Set<T>` 不触达 value tracking。

## 现状问题分析

### 问题 1：每个 Query 单独持有 ChangeTracker，重复分配

当前每个 `ChangeQuery` 在满足 typed fast path 条件（单 capture + `Previous()` + 无 filter）时，通过 `TryActivateTypedTracker<T>()` 或 `ActivateTypedTrackerForCapturedType()` 独立创建自己的 `ChangeTracker<T>`。如果有 N 个 query 同时追踪同一组件类型（如 `Position`），则会创建 N 份 `SlotByEntityPlusOne[]`、`ActiveLog[]`、`SpareLog[]` 三件套。

```csharp
// 当前：三个 query → 三个 ChangeTracker<Position>
var q1 = world.Track().Capture<Position>().Previous();
var q2 = world.Track().Capture<Position>().Previous();
var q3 = world.Track().Capture<Position>().Previous();
// ApplyTypedSet<Position> → 3 次 RecordTypedChange
```

`PreSize` 按 `EntityCapacity` 分配，三倍的内存浪费。在 Hero 等已经很大 footprint 的场景里，每一份 `int[]` + `TypedChange<T>[]` × 2 都是可观的额外分配。

### 问题 2：Repeated Set fanout

`ApplyTypedSet<T>` 的 typed 路径（`World.StructuralChange.cs:178-195`）遍历 `_typedTrackers`（`List<WeakReference<IChangeTrackerControl>>`），对每个匹配的 `ChangeTracker<T>` 调用 `RecordTypedChange`。单次 `Set<Position>` 的成本随消费者数量线性增长：

- 1 个 query tracking `Position`：1 次 `RecordTypedChange`
- 8 个 query：8 次 `RecordTypedChange`
- 每次都要 `EnsureEntityCapacity`、`SlotByEntityPlusOne` 读/写、`ActiveLog` 写

`_singleTypedTracker` 优化只覆盖 N=1 的情况。N≥2 时退化到遍历列表。

### 问题 3：Drain 的破坏性副作用

`ValueChanges<T>()` 调 `tracker.Drain()`，做了三件事：

1. 把 `ActiveLog` 和 `SpareLog` 互换
2. 遍历 `DirtyCount` 个条目清 `SlotByEntityPlusOne`
3. 重置 `DirtyCount = 0`

这意味着 `ValueChanges<T>()` 是 **destructive read**：两个消费者不能各自读到同一组变更。第一次调用把 log 拿走了，第二次调用看到空 span。用户没有"我想看看但不消耗"的选项。

```csharp
var span1 = q.ValueChanges<Position>(); // drain: 3 个条目
var span2 = q.ValueChanges<Position>(); // 空 —— 第一遍已经 drain 了
```

### 问题 4：弱引用生命周期双向耦合

当前 `World._typedTrackers` 用 `List<WeakReference<IChangeTrackerControl>>` 持有 tracker。Query 是 tracker 的强引用持有者，World 是弱引用持有者。这套机制设计用来避免"被遗弃的 query 泄漏 tracker"。

但代价是：

- `ApplyTypedSet` 热路径每次都要 `TryGetTarget` 解弱引用
- `_singleTypedTracker` 用另一个 weak ref 缓存单 tracker 情况
- 每次 iterate 都要 prune dead refs（swap-remove）
- `AddTypedTracker` / `RemoveTypedTracker` / `UpdateTypedTrackerFastPath` / `PruneDeadTypedTrackers` / `ClearTypedTrackerSlots` 五个方法协调生命周期
- 新增复杂度：`IChangeTrackerControl` 接口 + 反射创建 `ChangeTracker<T>` + `IChangeTrackerControl.ClearSlot` 虚分派

---

## 批准模型

### 核心理念：World 持有共享 tracker

**World 按组件类型持有唯一的 `ChangeTracker<T>`。** 所有 tracking 同一类型的 query 共享此 tracker：

```
World
├── ComponentType A → ChangeTracker<A>  (shared)
│   ├── ChangeQuery q1 (read view)
│   └── ChangeQuery q2 (read view)
├── ComponentType B → ChangeTracker<B>  (shared)
│   └── ChangeQuery q3 (read view)
```

变化一览：

| 方面 | 当前 | 新模型 |
|------|------|--------|
| 创建时机 | 每个 query 独立创建 | 首次 `Capture<T>().Previous()` 时 world 按类型创建 |
| 复用策略 | 不可能（每个 query 都有自己的） | 后续同类型 query 自动共享 |
| Set 写入 | O(N_consumers) | O(1) |
| ValueChanges 语义 | destructive drain | 只读快照 |
| 显式清空 | 无 | `ClearChanges<T>()` |
| 生命周期 | query 强拥有、world 弱引用 | world 强拥有、query 只持有读视图 |

### 改动范围

- **ChangeTracker<T>**: 新增 `Read()` / `Clear()`（或等价），取代 `Drain()` 的破坏性语义
- **World**: 新增 `Dictionary<ComponentType, object>`（或 `TrackerById` 数组）持有共享 tracker
- **ChangeQuery**: 移除 `_typedTracker` 强引用；改为从 World 获取共享 tracker 的读视图
- **ApplyTypedSet**: 移除 `_typedTrackers` 列表遍历；改为按类型直接定位共享 tracker
- **IChangeTrackerControl**: 可删除（如果不再需要泛型擦除的控制接口）

---

## 提案 API 形状

### 核心 API

```csharp
public sealed class ChangeQuery
{
    // 当前（保持不变）
    ReadOnlySpan<TypedChange<T>> ValueChanges<T>() where T : unmanaged;

    // 新增——显式清空该类型的变更累积
    void ClearChanges<T>() where T : unmanaged;

    // 当前（保持不变）
    IEnumerable<Transition> Transitions();
    IEnumerable<ChunkView> ModifiedChunks<T>() where T : unmanaged;
}

public sealed partial class World
{
    // 新增——世界级快捷清空（无需持有 ChangeQuery 引用）
    void ClearChanges<T>() where T : unmanaged;
}
```

### 行为契约

```csharp
// 1. 非消耗性读取
var c1 = q.ValueChanges<Position>();  // 读当前变更
var c2 = q.ValueChanges<Position>();  // 还是读到同一组（没有 drain）
Assert.True(c1.Length == c2.Length);

// 2. 显式清空
q.ClearChanges<Position>();
var c3 = q.ValueChanges<Position>();  // 空
Assert.True(c3.Length == 0);

// 3. 多 query 共享
var qA = world.Track().Capture<Position>().Previous();
var qB = world.Track().Capture<Position>().Previous();
world.Set(e, new Position(1, 2));
var spanA = qA.ValueChanges<Position>();
var spanB = qB.ValueChanges<Position>();
Assert.Equal(spanA.Length, spanB.Length);  // 看到相同的变更

// 4. 清空是全局的：清空一个 query 会清空该组件类型的共享 tracker
qA.ClearChanges<Position>();
var afterA = qA.ValueChanges<Position>();
var afterB = qB.ValueChanges<Position>();
Assert.Equal(0, afterA.Length);   // qA 看到的被清空了
Assert.Equal(0, afterB.Length);   // qB 也看到清空，因为底层 tracker 共享
```

### 名称决策

`ClearChanges<T>()` 是当前推荐名称。备选方案：

| 名称 | 评价 |
|------|------|
| `ClearChanges<T>()` | **采用**。对称于 `ValueChanges<T>()`，自动表达"清除变更累积" |
| `ResetChanges<T>()` | 有点技术感，不够直观 |
| `DismissChanges<T>()` | 不太口语化 |
| `FlushChanges<T>()` | 含有"写入"的歧义 |
| `ConsumeChanges<T>()` | 但这已经不是 consumption 语义了 |

`World.ClearChanges<T>()` 用于不需要 query 句柄的场景（如帧末统一清所有 tracker）。

---

## 数据流与控制流

### 正常 Set 路径

```
World.Set<T>(entity, value)
  → ApplyTypedSet<T>
    → world._sharedTrackerRegistry.TryGetTracker<T>(out var tracker)
    → tracker 不存在（无人追踪）：快速跳过，直接写 cell
    → tracker 存在：
      1. 读 old 值
      2. 写 new 值到 archetype cell
      3. RecordTypedChange(tracker, entity, old, new)
        → SlotByEntityPlusOne[entity.Id] = 0 → 新建条目（Old+New）
        → SlotByEntityPlusOne[entity.Id] != 0 → 更新同一条目 New
```

**变化点**：`RecordTypedChange` 不再遍历列表，直接写入唯一的共享 tracker。

### CommandStream.Set 路径

```
ComponentStore<T>.ApplyToWorld
  → 检查 world._sharedTrackerRegistry 是否有 tracker
  → 有：走 ApplyTypedSet（自动走共享 tracker 路径）
  → 无：走 SetComponentAtFlatNoTrack/TypedNoTrack（零额外开销）
```

**变化点**：CommandStream 的 `_typedTrackers is not null` 检查替换为 `_sharedTrackerRegistry.HasTracker`。

### ValueChanges 路径

```
ChangeQuery.ValueChanges<T>()
  → world._sharedTrackerRegistry.GetTrackerOrNull<T>()
  → tracker 不存在或当前 clean：返回 empty
  → tracker 存在且有 dirty：
    → return ActiveLog.AsSpan(0, DirtyCount)
    → **不 swap、不清零、不修改 tracker 任何状态**
```

**变化点**：`Drain()` 逻辑替换为 `ReadOnlySpan` 上的纯读操作。tracker 保留 `ActiveLog` 和 `DirtyCount` 不变。

### ClearChanges 路径

```
ChangeQuery.ClearChanges<T>()
  → world._sharedTrackerRegistry.GetTrackerOrNull<T>()
  → tracker 不存在：return
  → tracker 存在：
    → 遍历 DirtyCount 个条目清 SlotByEntityPlusOne
    → DirtyCount = 0
    → （可选：swap 释放旧 buffer 引用）

World.ClearChanges<T>()  // 世界级
  → 同上，不需要 query
```

### 多 query 共享语义细节

关键设计问题：当一个 query 调用 `ClearChanges<T>()`，其他 query 还能不能看到 same-epoch 的变更？

**策略：每个 query 维护自己的消费游标。**

```
ChangeTracker<T>
├── ActiveLog, DirtyCount    (共享写状态)
├── SlotByEntityPlusOne      (共享写入索引)

ChangeQuery                  (每个 query 独立)
├── _lastConsumedVersion     (上次读/清空的 DirtyCount 版本)
```

- `ValueChanges<T>()`: 返回 `ActiveLog.AsSpan(_lastConsumedVersion, DirtyCount - _lastConsumedVersion)`
- `ClearChanges<T>()`: 设置 `_lastConsumedVersion = DirtyCount`

这样 `qA.ClearChanges<Position>()` 后 `qB` 仍能读到自己的未消费部分。当所有 query 的游标都追到 `DirtyCount` 时，tracker 可以执行物理清理。

**简化版本（也是推荐实现）**：不做 per-query 游标。`ClearChanges<T>()` 全局清空共享 tracker。理由是：

- 当前 drain 语义已经是全局的（调用者就会清空所有人）
- per-query 游标增加复杂度、内存占用（每个 query 一个 long）
- 真实场景多 consumer 通常在同一帧消费，不会一个消费一个不消费
- YAGNI：先做最简单的，有明确需求再加

**最终决策：全局清空。** `ClearChanges<T>()` 作用于共享 tracker，所有 query 都看到清空。这和当前 `ValueChanges<T>()` 的 drain 破坏性是同级别的"一个消费者会影响到另一个"，但这次影响在可控范围内且用户有心理预期（"我调了 clear，它清空了"）。

---

## 生命周期

### Tracker 创建

- 时机：第一次 `world.Track().Capture<T>().Previous()` 被调用
- World 检查 `_sharedTrackerRegistry` 里有没有 `ComponentType` 的 tracker
- 没有 → 创建 `ChangeTracker<T>` 并注册
- 有 → 复用现有 tracker

### Tracker 销毁

- **World.Dispose**: `_sharedTrackerRegistry` 整体释放
- **RestoreState**: `_trackingGeneration++` + 清空 registry + query 自愈
- **Tracker 空闲清理（可选）**: 如果没有任何 query 再引用某个类型的 tracker，world 可以在合适时（如 RestoreState 或显式 prune）释放它。不做 GC 自动回收——tracker 是 world 拥有的，不是 query 拥有的。

### Destroy / Remove 时的 Slot 清理

当前 `ClearTypedTrackerSlots` 遍历所有 typed trackers 清 `SlotByEntityPlusOne[entityId]`。在共享模型下：

```csharp
internal void ClearTypedTrackerSlots(int entityId, ComponentType? componentType = null)
{
    if (componentType is not null)
    {
        // 精准清除：只清该组件类型对应的 tracker
        _sharedTrackerRegistry.TryGetTracker(componentType, out var control);
        control?.ClearSlot(entityId);
    }
    else
    {
        // Destroy 清所有 tracker 中该 id 的 slot
        _sharedTrackerRegistry.ClearAllSlots(entityId);
    }
}
```

复杂度不变，但从"遍历 N 个 tracker"降为"直接索引 1 个"。

### 预分配 / 扩容

当前 `PreSize` 和 `EnsureEntityCapacity` 逻辑不变。唯一变化：只调用一次（创建 tracker 时），而非每个 query 各一次。

```csharp
// 创建时预分配到当前容量
tracker.PreSize(world.EntityCapacity - 1);
// 后续自动扩容（ApplyTypedSet 中调用 EnsureEntityCapacity）
```

---

## RestoreState

`RestoreState` 当前做：

```csharp
_trackingGeneration++;                     // 自愈 generation
_changeQueries.Clear();                    // 清空 transition observer
_typedTrackers = null;                     // 清空 typed tracker 弱引用
_singleTypedTracker = null;                // 清空单 tracker 缓存
```

在共享模型下：

```csharp
_trackingGeneration++;                     // 保持
_changeQueries.Clear();                    // 保持
_sharedTrackerRegistry.Clear();            // 代替 _typedTrackers = null
```

`_trackingGeneration++` 触发所有现存 query 的自愈（`EnsureUsable` 检查 `_worldGen == _world._trackingGeneration`），query 会在下一次操作时重新从 world 获取共享 tracker。

---

## 性能目标与热路径期望

### 目标数据

| 场景 | N consumers | 当前 | 目标 |
|------|------------|------|------|
| Set on tracked type | 1 | 1 RecordTypedChange | 1 RecordTypedChange（不变） |
| Set on tracked type | 2 | 2 RecordTypedChange | 1 RecordTypedChange |
| Set on tracked type | 8 | 8 RecordTypedChange | 1 RecordTypedChange |
| ValueChanges 读取 | 1 | swap + clear O(DirtyCount) | 零拷贝 span O(1) |
| ClearChanges | 1 | 不存在 | O(DirtyCount) 清 slot |
| Alloc per Set | 任意 | 0 | 0 |
| Alloc per ValueChanges | 1 | 0 | 0 |

### 热路径

**`ApplyTypedSet` 的 `_sharedTrackerRegistry.TryGetTracker<T>` 路径**：

- 期望实现为 `Component<T>.ComponentType.Value` 索引的数组或 `Unsafe.As` 的字典
- 预期成本：一次数组边界检查读 + null check
- 优于当前 `_singleTypedTracker` weak ref 的 `TryGetTarget` + `is ChangeTracker<T>` 匹配

**`ValueChanges<T>()` 路径**：

- 只需要读 `ActiveLog` 引用 + `DirtyCount`
- 零分配、零写
- 当前 `Drain()` 做的 buffer swap 和 slot clear 移到 `ClearChanges<T>()`

### 不变假设

- 无人 tracking 时：`_sharedTrackerRegistry` 为空 → 直接在 `ApplyTypedSet` 的 `if (world is not null)` 块前检查 → **零退化**（和当前一样）
- 类型检查：`TryGetTracker<T>` 应为 O(1) 泛型专用索引，不使用 `is` 类型匹配

---

## 实现风险

1. **`SharedTrackerRegistry` 的线程安全**：当前 ECS 非线程安全，不需要锁；struct 设计按 `Component<T>.ComponentType.Value` 索引数组或 growable list。
2. **`RestoreState` 后 query 自愈时序**：`_trackingGeneration++` 后 query 的 `EnsureUsable` 会清 `_transitions` 和 `_consumed`。但 query 的 `_lastConsumedVersion` 在共享 tracker 被清后也需要归零（否则读到负值）。
3. **现有的 `TypedChange<T>[]` 返回 span 的生命周期**：当前 `Drain()` 返回 `SpareLog` swap 后的旧 `ActiveLog`。在新模型中 `ValueChanges<T>()` 返回的 span 指向 `ActiveLog`，调用 `ClearChanges<T>()` 后才能 reuse。需要文档注明"clear 之前返回的 span 有效"。
4. **CommandStream 的 `*NoTrack` 快路径**：当前 `_typedTrackers is null` 时走无跟踪快路径（直接 `SetComponentAtFlatNoTrack`）。共享模型也需要等价快路径——查 registry 需要足够快。

---

## 不在本次范围

- Per-query 独立游标（多 consumer 独立消费进度）
- Push event 回调（否决方案，见 `kb-design-rationale.md` §3.10）
- 支持 filter / 多 capture / WithAny 的值变更（仍返回空 span）
- Transition log 持久化（永远不回）
- 批量 span 写入追踪（`GetWriteSpan<T>`）
