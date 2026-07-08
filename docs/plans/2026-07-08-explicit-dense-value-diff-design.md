# Explicit Dense Value Diff API 设计

## Goal

把 `ManualGenericTracker<T>`（Hero perf 中的手写 dense shadow diff）API 化，提供一个显式、高性能的 per-entity 值 diff 机制，性能目标 `NewExplicitDiff >= ManualDense * 0.95`。

## Non-goals

- **不替换 `TrackValueChanges<T>()`**。旧 API 继续保留，未来可 `[Obsolete]`，本次 spike 只新增 API。
- **不追踪结构变化**（Add/Remove/Destroy/Create）。结构变化走 `TrackTransitions`。
- **不承诺全局透明写入追踪**。不 hook `Set<T>`、`CommandStream.Set<T>`、`GetRef`、chunk span 写入；用户必须显式调用 `Capture()` 和 `Drain()`。
- **不为第一版支持复杂通用输出数组作为性能路径**。输出 sink 是 struct 泛型回调接口，而不是预分配的 `ValueChange<T>[]`。

## API 推荐形态

### IValueProjector<TComponent, TValue>

```csharp
public interface IValueProjector<TComponent, TValue>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
{
    TValue Project(in TComponent component);
}
```

从组件实例投影出要追踪的值。例如只追踪 `Position.X` 而忽略 `Position.Y`，或把 `struct HP { int Current; int Max }` 投影成 `int Current`。

### IValueChangeSink<TValue>

```csharp
public interface IValueChangeSink<TValue>
    where TValue : unmanaged, IEquatable<TValue>
{
    void OnChanged(Entity entity, TValue oldValue, TValue newValue);
}
```

Drain 调用输出——每一条值变化产生一次 `OnChanged`。struct 实现可 inline 到 hot path。

### DenseValueDiff<TComponent, TValue, TProjector>

```csharp
public sealed class DenseValueDiff<TComponent, TValue, TProjector>
    where TComponent : unmanaged
    where TValue : unmanaged, IEquatable<TValue>
    where TProjector : struct, IValueProjector<TComponent, TValue>
{
    // 工厂由 World 提供，不公开构造函数
    internal DenseValueDiff(QueryDescription query, TProjector projector);

    /// <summary>
    /// 扫描当前世界把 TComponent 的新值投影并存入 _oldValues。
    /// 记录 touched entities 供后续 Clear() 清零。
    /// </summary>
    public void Capture(World world);

    /// <summary>
    /// 再次扫描，与 _oldValues 比较，对每个变化的 entity 调用 sink.OnChanged。
    /// 不修改 _oldValues —— 多次 Drain 产生相同的回调序列。
    /// </summary>
    public void Drain<TDrain>(World world, ref TDrain sink)
        where TDrain : struct, IValueChangeSink<TValue>;

    /// <summary>
    /// 清空 touched entities 的 _oldValues 条目，重置 _touchedCount。
    /// 必须有对应的 Capture 在下次 Drain 前调用。
    /// </summary>
    public void Clear();
}
```

### 工厂方法（World 扩展）

```csharp
public partial class World
{
    /// <summary>
    /// 创建显式 dense value diff 追踪器。
    /// 如果未传 query，内部构造 new QueryDescription().With{TComponent}()。
    /// </summary>
    public DenseValueDiff<TComponent, TValue, TProjector> CreateDenseValueDiff<TComponent, TValue, TProjector>(
        QueryDescription? query = null,
        TProjector projector = default)
        where TComponent : unmanaged
        where TValue : unmanaged, IEquatable<TValue>
        where TProjector : struct, IValueProjector<TComponent, TValue>;
}
```

如果 `query` 是 `null`，工厂构造 `new QueryDescription().With<TComponent>()`。如果用户显式传入 `query`，第一版不自动改写；调用方必须确保 query 包含 `TComponent`，否则 `chunk.GetSpan<TComponent>()` 对不含该组件的 chunk 没有意义。Spike 测试要覆盖默认 query 的主路径，文档要把“显式 query 必须 With<TComponent>()”写清楚。

## State Model

```
DenseValueDiff<TComponent, TValue, TProjector>
├── TValue[] _oldValues          // entity.Id → old projected value
├── int[] _touchedEntities       // Capture 扫描时记录过的 entity id
├── int _touchedCount            // touched 数组有效长度
├── QueryDescription _query      // 预编译查询
├── TProjector _projector        // 投影器实例 (struct, 零开销)
└── bool _hasCaptured            // 首次 Capture 标记（可选项，用于检查）
```

- `_oldValues`: 长度 `≥ EntityCapacity`。扩容策略：`Array.Resize`，`Max(entityId + 1, _oldValues.Length * 2)`。初始值 `Array.Empty<TValue>()`。
- `_touchedEntities`: 记录 Capture 扫描时所有具有 `TComponent` 的 entity 的 id。Clear 时只遍历 touched 范围清零 `_oldValues`，避免遍历整个容量。
- 不使用 `Dictionary`、`ConditionalWeakTable`、`ConcurrentDictionary` 等非 dense 结构。
- 不使用 `HashSet<int>` 做去重——`_touchedEntities` 天然无重复（每 entity 在 Capture 时只出现一次）。

## Hot Path Loop

### Capture

```
foreach (var chunk in Query.GetChunks)
{
    var span = chunk.GetSpan<TComponent>();
    var entities = chunk.GetEntities();
    for (int i = 0; i < chunk.Count; i++)
    {
        int entityId = entities[i].Id;
        TValue value = _projector.Project(span[i]);

        // 确保容量
        if (entityId >= _oldValues.Length)
            Array.Resize(ref _oldValues, Math.Max(entityId + 1, _oldValues.Length * 2));
        _oldValues[entityId] = value;

        // 记录 touched
        if (_touchedCount >= _touchedEntities.Length)
            Array.Resize(ref _touchedEntities, Math.Max(_touchedCount + 1, _touchedEntities.Length * 2));
        _touchedEntities[_touchedCount++] = entityId;
    }
}
```

### Drain

```
foreach (var chunk in Query.GetChunks)
{
    var span = chunk.GetSpan<TComponent>();
    var entities = chunk.GetEntities();
    for (int i = 0; i < chunk.Count; i++)
    {
        int entityId = entities[i].Id;
        TValue oldVal = entityId < _oldValues.Length ? _oldValues[entityId] : default;
        TValue newVal = _projector.Project(span[i]);

        if (!oldVal.Equals(newVal))
        {
            sink.OnChanged(entities[i], oldVal, newVal);
        }
    }
}
```

### Clear

```
for (int i = 0; i < _touchedCount; i++)
    _oldValues[_touchedEntities[i]] = default;
_touchedCount = 0;
```

## Semantics

- **Before/after diff 只对 Drain 的瞬间有效**：`Capture` 记录快照 A，`Drain` 扫描快照 B，只报告 `A != B` 的变化。`Capture` 后没有写入时，`Drain` 返回空。
- **`Capture` → 多次 `Drain` 产生相同回调**：Drain 不修改 `_oldValues`，所以第 2、3 次 Drain 仍报告同一组变化。调用者自行确保有意义的顺序。
- **`Capture` → `Drain` → `Clear` → `Capture` → `Drain`** 是推荐循环模式。
- **Add/Remove/Destroy 不作为结构事件报告**：如果一个 entity 在 `Capture` 时有 `TComponent`，在 `Drain` 时被 remove/destroy，则 Drain 不扫描到它——不报告变化。如果一个 entity 在 `Drain` 时刚刚 add 但 `Capture` 时不存在，Drain 会按 dense shadow-diff 语义读取 `_oldValues[entityId]`（通常是 `default`，也可能是 stale slot 值）并可能报告一次 value diff。这是 ManualDense 等价行为；需要严格结构语义时配合 `TrackTransitions`。
- **无版本检查**：不读 `Entity.Version`。`_oldValues[entityId]` 跨 frame 仍是同一 id 的旧值。如果 entity slot 被重用，`oldVal` 是新 entity 的 `default`——这是预期行为（等于 `A(default)→B` 报告一次）。用户如果要防范 id 重用必须在外部使用版本列表或 `TrackTransitions`。
- **`TValue == default` 是合法值**：`Capture` 会写入当前值，所以正常 `Capture`→无修改→`Drain` 不会因为默认值产生误报。只有未被 `Capture` 触碰但在 `Drain` 出现的新增 entity 才可能走 default/stale old 值路径。

## 性能约束

| 约束 | 说明 |
|------|------|
| **零稳态分配** | 暖机后不触发 GC 分配（`Array.Resize` 只在 entity 容量增长时发生，忽略不计） |
| **无 Dictionary** | 完全用 `int[]` dense 索引 |
| **无 EqualityComparer** | 内循环用 `TValue.Equals(TValue other)` 即 `IEquatable<TValue>` 直接调用。`EqualityComparer<TValue>.Default` 会额外引入 interface dispatch 和潜在的 boxing。必须确保 `TValue` 实现 `IEquatable<TValue>` |
| **无 ValueChange[] 输出数组** | 输出通过 struct sink 回调，不分配中间数组 |
| **无 Set 路径侵入** | 不修改 `Set<T>` / `CommandStream.Set<T>` |
| **Release 编译测量** | 所有 perf 跑测必须用 `-c Release` |

## 风险

### 1. 泛型 struct 接口调用的 JIT 内联能力

`_projector.Project(in component)` 和 `sink.OnChanged(entity, oldVal, newVal)` 是泛型 struct 接口上的调用。JIT 有约束：C# 的 `where TProjector : struct, IValueProjector<...>` 足以使 JIT 为每个 `TProjector` 单独特化——调用可以是完全 inline 的。但以下情况可能退化：

- 用了 `interface` 默认方法或虚方法——struct 接口方法默认是 `callvirt`。需要确认 .NET JIT 能在 constrained call + struct 上 devirtualize。
- 输出 sink 的 `OnChanged` 逻辑复杂时可能不 inline——但 sink 是用户提供的 struct，JIT 和 R2R 行为因版本而异。

**缓解**：在 hot path 上加 `[MethodImpl(MethodImplOptions.AggressiveInlining)]` 提示 JIT。

### 2. TValue generic equality

`IEquatable<TValue>` 约束确保 `TValue.Equals` 是强类型方法，避免 boxing。但对很小的 struct（如 `int`），JIT 能拆成 `cmp` 指令。对更复杂的 struct（如 `Vector3`），JIT 可能产生 memcmp 调用或不内联的 `Equals`。这是用户选择 `TValue` 的性能责任。

### 3. Entity id capacity 增长

`_oldValues` 按 `entity.Id` 索引——如果某些 entity id 很大（如 200k 实体但 id 分布导致 max id=500k），数组会有 300k 未使用槽。但这是 O(maxEntityId) 的空间，不是 O(entityCount)。大多数 ECS 使用连续 id 分配，问题不大。如果 id 跳跃过大，ManualDict 是更合适的备选——但本 API 是 dense 方案，不提供 dict fallback。

### 4. Structural churn（高频 Add/Remove）

如果世界经历了高频 Add/Remove TComponent，则：

- `Capture` 扫描到的 entities 在 `Drain` 时可能不存在（remove/destroy）→ 不报告变化（正确）。
- Drain 时大量 entity 有 `_oldValues` 但本帧被 remove → 不被扫描到 → 正确。但该 entity 的 `_oldValues` 残留到下次 `Capture`。
- Clear 时只清 `_touchedEntities` 记录的 slot——remove 的 entity 如果不在 `_touchedEntities` 则不清，但下次 `Capture` 会覆盖它。不会泄漏。

### 5. QueryDescription default 的工厂 ergonomics

用户最自然的调用：

```csharp
var diff = world.CreateDenseValueDiff<Position, int, PosXProjector>();
```

但 `CreateDenseValueDiff` 的参数列表决定了默认 query 怎么表达。如果反冲 null 为 "auto-with-TComponent"：

```csharp
public DenseValueDiff<TComponent, TValue, TProjector> CreateDenseValueDiff<...>(
    QueryDescription? query = null, TProjector projector = default)
{
    var q = query ?? new QueryDescription().With<TComponent>();
    return new DenseValueDiff<TComponent, TValue, TProjector>(q, projector);
}
```

但 `With<TComponent>()` 是否多余？用户传入的 query 如果没 include `TComponent` 会导致 Drain 扫描不到该组件。这是一个 FAQ 级别的坑。两种方案：

- **方案 A（推荐）**：工厂自动 `.With<TComponent>()` 到用户 query 上。如果用户 query 已有 `With<TComponent>()`，去重。
- **方案 B**：要求用户在 query 中显式包括 `TComponent`，工厂不做自动添加。报错（如果不是世界有该组件的 entity 而是 query 不匹配，Drain 走零 chunk 但不会报错——静默空结果，更难调试）。

**首选方案：默认 query 自动 `.With<TComponent>()`，显式 query 不改写但文档要求包含 `TComponent`**。这样主路径零样板，显式 filter 仍保持“用户传什么就扫什么”的可预测性。未来如果真实用户频繁踩坑，再加 `query.With<TComponent>()` 自动合并或 debug 断言。

## 与 TrackValueChanges<T> 的关系

| 维度 | `TrackValueChanges<T>` | `CreateDenseValueDiff<...>` |
|------|-----------------------|---------------------------|
| 写入点 hook | ✅ 所有写入方式（Set/GetRef/Span） | ❌ 仅显式 Capture/Drain |
| 结构变化 | ❌ 不追踪（走 Transitions） | ❌ 不追踪 |
| 输出形态 | `ValueChange<T>[]` span | struct callback `OnChanged` |
| 投影 | 只能全组件对比 | 任意 `IValueProjector` |
| 性能模型 | O(entityCount) 每 .Changes 扫描 + baseline compare | O(entityCount) 每 Drain 扫描 + value compare |
| 多消费者 | 全局共享 baseline | 每个 DenseValueDiff 实例独立 |
| 使用场景 | "我现在想知道我改变了什么" | "我需要最高性能的 pre/post diff" |
| 稳态分配 | `.Changes` 每次输出分配新 `ValueChange<T>[]` | 零分配（struct sink） |

`CreateDenseValueDiff` 的存在不强制用户迁移——它只为需要 `ManualDense` 级别性能的路径提供选项。`TrackValueChanges<T>()` 将在未来版本标记 `[Obsolete("Use CreateDenseValueDiff + TrackTransitions instead")]`。
