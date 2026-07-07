# Change Tracking 统一设计

## 现状

```csharp
World.Track<T>() → ChangeQuery<T>
  .With<TU>() / .Without<TU>() / .WithAny<TU>()
  .WithPreviousValues() → .Changes<T>()  ← ValueChange<T>[]
  .Transitions()                          ← Transition[]
  .ModifiedChunks()                       ← ChunkView[]
```

- 入口带泛型 `<T>`，一个 query 只能追踪一个组件
- `Changes<T>()` 只返回单个组件的 old/new，没有跨组件 entity snapshot
- `.Previous()` 跨帧不分离——要么全部开要么全不开

## 目标

```csharp
World.Track() → ChangeQuery
  .Capture<T>()                     // 声明要追踪的组件（可多个）
  .With<TU>() / .Without<TU>()      // 过滤语义（用于 Transitions）
  .Previous()                       // 当前 query 的开关，开启 Set 旧值记录
  .Transitions()                    ← Transition[]（不变）
  .ModifiedChunks<T>()              ← ChunkView[]（T 移到方法级）
  .Changes()                        ← EntityChange[]（新增：多组件 snapshot）
```

## 改动清单

### 1. 去掉 `ChangeQuery<T>` 的泛型

`ChangeQuery<T>` → `ChangeQuery`（无泛型）。

之前依赖 class 泛型的地方：
- `ModifiedChunks()` 默认用 T 检查版本 → 改为 `ModifiedChunks<T>()`，T 在方法参数指定
- `IValueChangeSink<T>.OnValueChange<T>()` → 通过 Handler 内部类隔离

### 2. 内部 Handler 模式

每个 `.Capture<T>()` 在内部创建一个 `TypeHandler<T>`，实现 `IValueChangeSink<T>`，持有自己的 `_valueCursor`（用于 ModifiedChunks 版本比对）：

```csharp
class ChangeQuery : IChangeQuery
{
    // ── Handler 结构 ──
    interface IHandler
    {
        ComponentType Type { get; }
        long ValueCursor { get; set; }
        void OnBeforeSet(Entity entity, Archetype arch, int row);
    }

    class Handler<T> : IHandler, IValueChangeSink<T> where T : unmanaged
    {
        public ComponentType Type => Component<T>.ComponentType;
        public long ValueCursor { get; set; }
        
        // pre-hook：Previous 启用时被 World 调用
        public void OnBeforeSet(Entity entity, Archetype arch, int row)
        {
            // 读所有 Captured 组件的旧值
            owner.CaptureSnapshot(entity, arch, row);
        }
        
        // IValueChangeSink<T> —— post-hook，记录 per-T old/new（给未来用）
        void IValueChangeSink<T>.OnValueChange(Entity entity, in T oldValue, in T newValue)
        {
            // 暂不处理，旧值已在 pre-hook 捕获
        }
    }
    
    List<IHandler> _handlers;       // 每个 Capture<T> 一个
    bool _hasPrevious;              // Previous()
    List<EntityChangeEntry> _entries; // Changes() 的输出缓存
    
    // Handlers 索引，用于 ModifiedChunks<T> 和 pre-hook 查找
    Dictionary<ComponentType, IHandler> _handlerMap;
}
```

为什么不直接在这个类放多个 List？因为 `IValueChangeSink<T>` 需要实现一个 per-T 的 sink 接口，C# 无法在一个 class 上实现多个泛型接口，必须通过内部 Handler 类。

### 3. World.Track() 新入口

```csharp
public ChangeQuery Track()
{
    var query = new ChangeQuery(this);
    RegisterChangeQuery(query);
    return query;
}
```

不再自动 `ActivateTracking`（没有 T 了）。改为 `.Capture<T>()` 里调 `ActivateTracking`。

### 4. World 新 pre-hook

在 `ApplyTypedSet<T>` 里写入前加 dispatch：

```csharp
internal static void ApplyTypedSet<T>(..., in T component)
{
    // ... TryGetComponentIndex ...
    
    // [NEW] Pre-write hook: 通知 ChangeQuery 读旧值
    if (world is not null)
        world.DispatchBeforeSet(entity, archetype, info.RowIndex, componentType);
    
    // ... 写入 ...
}
```

`DispatchBeforeSet` 遍历 `_changeQueries`，对每个 `IChangeQuery` 调一个新的 `OnBeforeWrite(entity, arch, row)` 方法：

```csharp
internal interface IChangeQuery
{
    void OnTransition(Entity entity, Archetype? old, Archetype? @new);  // 已有
    void OnBeforeWrite(Entity entity, Archetype arch, int row) { }    // 新增，默认空
}
```

只在 `_hasPrevious` 的 query 上有实际内容。

### 5. 结构变更 pre-hook

同理在 `ApplyTypedAdd`、`RemoveBoxed`、`PlaceEntityInArchetype` 的 MoveEntity 前加：

```csharp
// ApplyTypedAdd 里，MoveEntityCore 之前
if (_anyTrackingActive) DispatchBeforeTransition(entity, sourceArchetype, info.RowIndex);
```

`IChangeQuery.OnBeforeTransition(entity, Archetype oldArch, int oldRow)` — 新增方法，默认空。ChangeQuery 在这里读 oldArch 上所有 Captured 组件的值。

### 6. Changes() 输出

```csharp
public EntityChange[] Changes()
{
    if (!_hasPrevious)
        return Array.Empty<EntityChange>();
    
    var result = _entries.ToArray();
    _entries.Clear();
    return result;
}
```

`EntityChange` 是 public struct：

```csharp
public readonly struct EntityChange
{
    public readonly Entity Entity;
    // internal: byte[] _data, int _oldOffset, int _newOffset, int _snapshotStride
    // internal: ChangeQuery _owner (for Get<T> offset lookup)
    
    public EntitySnapshot Old => new(...old portion...);
    public EntitySnapshot New => new(...new portion...);
}

public readonly ref struct EntitySnapshot
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly ChangeQuery _owner;
    
    public bool Has<T>() where T : unmanaged;
    public ref readonly T Get<T>() where T : unmanaged;  // 不存在则抛
}
```

存储格式：一个 `byte[]` 平铺所有 entries 的 Old → New 数据。

### 7. 删掉

| 删除 | 替代 |
|---|---|
| `ChangeQuery<T>` 泛型 | `ChangeQuery` 无泛型 |
| `.WithPreviousValues()` | `.Previous()` |
| `.Changes<T>()`（返回 ValueChange<T>[]） | `.Changes()`（返回 EntityChange[]） |
| `ValueChange<T>` | EntityChange 替代 |

保留：
- `Transition`, `TransitionKind`, `TransitionCause`
- `ChunkView`
- `With<T>()`, `Without<T>()`, `WithAny<T>()`
- `Transitions()`, `ModifiedChunks<T>()`

## 实现顺序

1. `ChangeQuery.cs` — 去掉 `<T>` 泛型，改为 Capture + Handler 模式
2. `World.cs` — `Track()` 新入口 + `DispatchBeforeWrite` + `DispatchBeforeTransition`
3. `World.StructuralChange.cs` — 两个结构变更入口加 pre-hook 调用
4. `IChangeQuery.cs` — 加 `OnBeforeWrite` / `OnBeforeTransition`
5. `IValueChangeSink.cs` — 现有接口不变，Handler 实现它用
6. `EntityChange.cs` / `EntitySnapshot.cs` — 新 public 类型
7. 更新测试
8. 更新知识库
