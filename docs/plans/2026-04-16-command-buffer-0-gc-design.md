# CommandBuffer 0 GC 设计方案

## 1. 当前 GC 压力分析

### 1.1 Recording 阶段

| 来源 | 类型 | 严重程度 |
|------|------|----------|
| `RecordedComponentCommand(entity, typeof(T), component)` | `object?` 装箱 | 🔴 高 |
| `RecordedRemoveCommand(entity, typeof(T))` | 记录结构体（无装箱） | 🟡 中 |
| `RecordedHierarchyCommand` | 记录结构体（无装箱） | 🟡 中 |
| `ConcurrentDictionary<int, CommandBufferShard>` | 引用类型 | 🟡 中 |
| `CommandBufferShard` 列表动态增长 | `List<T>` 扩容 | 🟡 中 |

**核心问题**: `Add<T>` / `Set<T>` 的 `component` 参数是泛型值类型，但记录时装箱为 `object?`。

```csharp
// CommandBufferShard.cs - 当前
public void Add<T>(Entity entity, T component)
{
    GetShard().Adds.Add(new RecordedComponentCommand(entity, typeof(T), component)); // T 被装箱
}
```

### 1.2 Compile 阶段

| 来源 | GC 触发条件 | 严重程度 |
|------|-------------|----------|
| `EntityComponentKey(Entity, Type)` 字典键 | 每次 Add/Set/Remove | 🔴 高 |
| `CreatedEntityState._components` 字典 | create + component 操作 | 🔴 高 |
| `CompiledComponentValue` 持 `object?` | component 值装箱 | 🔴 高 |
| `CompiledCreatedEntity.Components` 数组 | created entity 有组件时 | 🔴 高 |
| `.ToArray()` / `.ToList()` | `ToFrameCommands()` | 🟡 中 |
| `Array.Sort` + comparer | `ToCompiledEntity` / `ToFrame` | 🟡 中 |
| `CompiledCommandBatch` 列表扩容 | 命令数超出容量 | 🟡 中 |

**关键瓶颈**: `EntityComponentKey` 是 `readonly record struct(Entity, Type)`，字典的 GetHashCode/Equals 每次都调用 `Type.GetHashCode()` 和 `Type.Equals`，Type 是引用类型。

### 1.3 Replay 阶段（Play() 短路径）

| 来源 | 严重程度 |
|------|----------|
| `World.Replay(CommandBuffer.CompiledCommandBatch)` 复用 `_compiledReplayComponentTypeScratch` | ✅ 已优化 |
| `World.Replay(in FrameCommands)` 每次 new `Dictionary<Type, ComponentType>` | 🟡 中 |
| `World.ReplayWithReverse(in FrameCommands)` + `CaptureReverseFrameCommands` 大量临时 List/Dictionary | 🔴 高 |
| `World.ApplyDelta` 每次 new `Dictionary<Type, ComponentType>` | 🟡 中 |

**关键发现**:
- `Replay(CommandBuffer.CompiledCommandBatch)` 已经复用 `World._compiledReplayComponentTypeScratch`，GC 友好
- `Replay(in FrameCommands)` 和 `ApplyDelta` 每次都 new Dictionary，是多余的

### 1.4 Playback() vs Play() 的 GC 对比

```
Playback(): Compile() -> ToFrameCommands() -> Clear()
  └── ToFrameCommands() 分配:
        - FrameCreatedEntity[]
        - FrameEntityComponentCommand[] (add)
        - FrameEntityComponentCommand[] (set)
        - FrameEntityRemoveCommand[]
        - FrameCommandsState + FrameCommands wrapper

Play(): Compile() -> Replay(CompiledCommandBatch) -> Clear()
  └── Replay() 复用 _compiledReplayComponentTypeScratch
  └── 不分配 FrameCommands
```

---

## 2. 0 GC 目标定义

### 2.1 目标范围

| 操作 | 目标 |
|------|------|
| `CommandBuffer.Add<T>` / `Set<T>` / `Remove<T>` | 0 装箱 |
| `CommandBuffer.Create()` / `Destroy()` / `Link` / `Unlink` | 0 装箱 |
| `CommandBuffer.Play()` | 0 GC（除外部 World mutation） |
| `CommandBuffer.Playback()` | 允许 `FrameCommands` 分配（用户主动获取 IR） |
| `CommandBuffer.PlayWithReverse()` | 可选优化，优先级低于 Play() |
| `World.ApplyDeltaForward/Backward()` | 允许 WorldDelta 分配（跨 world 同步语义） |

### 2.2 验收标准

- `Play()` 单次调用：Gen 0/1/2 GC 为 0
- 10000 次 `Play()` 循环：Gen 0/1/2 GC 为 0
- 录制 + Play 循环 10000 次：Gen 0 GC < 10（录制本身的 boxing 无法消除）

---

## 3. 详细设计方案

### 3.1 录制层：消除泛型装箱

#### 方案 A: Interface-based recording（推荐）

```csharp
internal interface IRecordedCommand
{
    Entity Entity { get; }
    void Replay(World world);
}

internal readonly struct RecordedAdd<T> : IRecordedCommand
{
    public Entity Entity { get; }
    public T Value { get; } // 不装箱！
    public RecordedAdd(Entity entity, T value) => (Entity, Value) = (entity, value);
    public void Replay(World world) => world.Add(Entity, Value); // 泛型 replay
}
```

**优点**: 消除所有 boxing
**缺点**:
- `CommandBufferShard.Adds` 变成 `List<IRecordedCommand>`，泛型 `T` 不能统一存储
- 需要运行时类型分发或分 shard

#### 方案 B: 结构化 ComponentStorage，消除 Type 作为字典键

当前问题：
```csharp
Dictionary<EntityComponentKey, CompiledComponentCommand>
// EntityComponentKey 每次 GetHashCode 都调用 Type.GetHashCode()
```

解决方案：用 `int` componentTypeId 替代 `Type` 作为键。

```csharp
// 用预分配的 int ID 替代 Type
internal readonly record struct EntityComponentKey(Entity Entity, int ComponentTypeId);

// ComponentRegistry 提供 int ID
public ComponentType GetOrCreate<T>(); // 返回的 ComponentType 携带 Id
```

### 3.2 编译层：消除 EntityComponentKey 字典开销

**核心问题**: `EntityComponentKey(Entity, Type)` 的 `Type` 作为字典键导致引用比较和装箱。

**解决方案**:
1. 在 `RecordedComponentCommand` 阶段就把 `Type` 解析成 `int componentTypeId`
2. `EntityComponentKey` 改成 `(Entity, int)` 组合

```csharp
// 录制时解析
public void Add<T>(Entity entity, T component)
{
    var componentTypeId = _world.Components.GetOrCreate<T>().Id;
    GetShard().Adds.Add(new RecordedComponentCommand(entity, componentTypeId, component)); // 不传 Type
}

// 编译时直接用 int 查找
compiledAdds[new EntityComponentKey(command.Entity, command.ComponentTypeId)] = ...;
```

### 3.3 编译层：消除 CreatedEntityState 字典

**当前**:
```csharp
Dictionary<Entity, CreatedEntityState>
CreatedEntityState {
    Dictionary<Type, CompiledComponentValue>? _components; // Type 键 + object? 值双重装箱
}
```

**解决方案**: 预分配固定大小数组，用 `int componentTypeId` 索引

```csharp
internal readonly struct CompactCreatedEntityState
{
    public Entity Entity { get; }
    private readonly int _componentCount;
    private readonly int[] _componentTypeIds;
    private readonly object?[] _values;

    public void Add(int componentTypeId, object? value)
    {
        // 内部数组直接追加，不装箱
    }
}
```

但更简单的方案：compile 阶段直接归约到最终输出，不中间存储。

### 3.4 Replay 层：消除 FrameCommands 到 CompiledCommandBatch 的转换

**当前 Play() 流程**:
```
Recording → Compile() → CompiledCommandBatch → ToFrameCommands() → Replay(in FrameCommands)
                                      ↓
                              Play() 走另一条路
```

**问题**: `Playback()` 走 `ToFrameCommands()` 再 Replay，而 `Play()` 直接用 `CompiledCommandBatch`。但两者共享 `Compile()` 逻辑。

**解决方案**: 统一到 `CompiledCommandBatch` 路径，`Playback()` 通过 `CompiledCommandBatch.ToFrameCommands()` 生成 `FrameCommands`。

### 3.5 Replay 层：消除 Dictionary<Type, ComponentType> 分配

**当前**:
```csharp
// World.Replay(in FrameCommands) - 每次调用
var componentTypeCache = new Dictionary<Type, ComponentType>();

// World.Replay(CommandBuffer.CompiledCommandBatch) - 已优化
var componentTypeCache = _compiledReplayComponentTypeScratch;
```

**解决方案**:
1. `World` 提供 `_replayComponentTypeScratch` 供 `Replay(in FrameCommands)` 复用
2. 或者提供内部 API 直接接受 `CompiledCommandBatch`

### 3.6 CaptureReverseFrameCommands：大量临时 List 分配

```csharp
var restoredEntities = new List<ReverseFrameEntity>();
var restoredEntitySet = new HashSet<Entity>();
var destroyedEntities = new List<Entity>(state.CreatedEntities.Length);
var linkCommands = new List<FrameLinkCommand>();
var unlinkCommands = new List<FrameUnlinkCommand>();
var addCommands = new List<FrameEntityComponentCommand>();
var setCommands = new List<FrameEntityComponentCommand>();
var removeCommands = new List<FrameEntityRemoveCommand>();
```

**解决方案**: 预分配到 `World` 级别的 scratch 列表

```csharp
internal class WorldReplayScratch
{
    public List<ReverseFrameEntity> RestoredEntities = new(4);
    public HashSet<Entity> RestoredEntitySet = new(4);
    public List<Entity> DestroyedEntities = new(4);
    public List<FrameLinkCommand> LinkCommands = new(4);
    public List<FrameUnlinkCommand> UnlinkCommands = new(4);
    public List<FrameEntityComponentCommand> AddCommands = new(4);
    public List<FrameEntityComponentCommand> SetCommands = new(4);
    public List<FrameEntityRemoveCommand> RemoveCommands = new(4);
}
```

---

## 4. 实施计划

### Phase 1: Recording 层优化（高优先级）
1. 把 `RecordedComponentCommand.ComponentType` 从 `Type` 改成 `int componentTypeId`
2. 在 `CommandBuffer.Add<T>/Set<T>` 时直接解析 `componentTypeId`
3. 验证: 录制阶段泛型路径不再装箱 `T`

### Phase 2: Compile 层优化
1. `EntityComponentKey` 改成 `(Entity, int)`
2. `CreatedEntityState` 内部用 `int[]` + `object?[]` 替代 `Dictionary<Type, ...>`
3. 验证: `Play()` 循环 10000 次 GC 为 0

### Phase 3: Replay 层优化
1. `World.Replay(in FrameCommands)` 复用 `Dictionary<Type, ComponentType>` scratch
2. `CaptureReverseFrameCommands` 复用 `WorldReplayScratch`
3. 验证: `PlayWithReverse()` + `Rewind()` 循环 GC 明显下降

### Phase 4: 验证与基准
1. 集成 BenchmarkDotNet GC 模式
2. 对比 Arch Engine 同类 API
3. 建立 0 GC 达标门禁

---

## 5. 关键技术决策

### 5.1 为什么 `Type` 要改成 `int componentTypeId`？

`Type` 是引用类型，作为字典键时：
- `GetHashCode()` 基于字符串比较，慢
- 每次 `.Equals()` 都有引用比较开销
- `Type` 对象本身可能未装箱但仍造成 GC 压力

`int` 是值类型：
- `GetHashCode()` 就是自身，O(1)
- 比较是原生整数比较
- 无 GC 压力

### 5.2 ComponentType.Id 已经是 int，为什么不用？

当前录制 API:
```csharp
public void Add<T>(Entity entity, T component)
{
    GetShard().Adds.Add(new RecordedComponentCommand(entity, typeof(T), component));
}
```

录制时没解析 `ComponentType`，留到 compile 阶段才解析。如果录制时就解析好 `int componentTypeId`，compile 阶段就无需再查表。

### 5.3 录制时解析 componentTypeId 的代价

```csharp
public void Add<T>(Entity entity, T component)
{
    var componentTypeId = _world.Components.GetOrCreate<T>().Id; // 每 Add 一次就查表
}
```

这相当于把 GC 压力从"运行时"移到"录制时"，且录制时通常是多线程的，字典查询有锁竞争。

**更好的方案**: 录制时不解析，compile 时统一解析一次到 scratch 字典。

---

## 6. 当前代码中已优化的部分

| 优化点 | 位置 |
|--------|------|
| `_compiledReplayComponentTypeScratch` 复用 | World.cs:49, 1505 |
| `CompiledCommandBatch` 预分配 | CommandBuffer.cs:164-172 |
| `EnsureCapacity` 预分配 | CommandBuffer.cs:164-204 |
| `Play()` 不物化 FrameCommands | CommandBuffer.cs:120-137 |
| 录制层 shard 本地化，无锁 | CommandBufferShard.cs |

---

## 7. 下一步行动

建议先做 **Phase 1 的第一步**：把 `RecordedComponentCommand.ComponentType` 从 `Type` 改成 `int`，这样可以量化每一步的 GC 改善。

具体改动：
1. `FrameCommands.cs`: `RecordedComponentCommand` 改成 `(Entity Entity, int ComponentTypeId, object? Value)`
2. `FrameCommands.cs`: `RecordedRemoveCommand` 改成 `(Entity Entity, int ComponentTypeId)`
3. `CommandBufferShard.cs`: `Adds`/`Sets`/`Removes` 列表元素类型同步修改
4. `CommandBuffer.cs`: `Add<T>/Set<T>/Remove<T>` 录制时把 `typeof(T)` 换成 `componentTypeId`
5. `CommandBuffer.cs Compile()`: 相应调整 `ResolveComponentType` 调用方式
