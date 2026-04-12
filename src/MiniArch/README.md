# MiniArch API Guide

MiniArch 是一个小型的 archetype-based ECS runtime，面向 C# 使用。

它公开两层 API：

- `MiniArch.Ecs`：普通游戏开发者默认入口，复杂过滤也可以直接用 `QueryDescription`。
- `MiniArch.Core`：给需要控制底层查询、chunk、command buffer、snapshot 与 profiling 的 advanced 用户。

## MiniArch 当前能力

- versioned entity
- signature-based archetype
- dense chunk storage
- cached archetype transition
- archetype-filtered query 与 chunk snapshot
- runtime-owned hierarchy
- 支持 replay / rewind 的 command buffer
- binary world snapshot save/load

## 该选哪层 API？

### Start Here

- 写 gameplay：先用 `MiniArch.Ecs`
- 默认只需要：`Create`、`Add`、`Set`、`Remove`、`Destroy`、`Link`、`Unlink`、`TryGetParent`、`GetChildren`、`IsAlive`、`TryGet`、`Query<T>()`、`Query<T1, T2>()`
- `MiniArch.Ecs` 的 typed query façade 只覆盖 1~2 组件查询；复杂过滤可以直接转用 `QueryDescription`
- 只有你要 3+ 组件查询、`with/without/any` filters、chunk 级遍历、`CommandBuffer`、`WorldSnapshot` 时，再进入 `MiniArch.Core`

| 你在做什么                         | 推荐使用          | 原因                                                                                                                      |
| ---------------------------------- | ----------------- | ------------------------------------------------------------------------------------------------------------------------- |
| 普通 gameplay logic                | `MiniArch.Ecs`  | 概念更少，直接 `Create/Add/Set/Remove`，默认支持 `foreach` 查询                                                       |
| 系统里读 1~2 个组件                | `MiniArch.Ecs`  | `Query<T>()` 和 `Query<T1, T2>()` 就是默认路径                                                                        |
| 系统里读 3+ 个组件                 | `MiniArch.Core` | `MiniArch.Ecs` 不提供 3 组件以上 query façade                                                                          |
| 需要 `with/without/any` 组合过滤 | `MiniArch.Ecs`  | 普通层直接用 `QueryDescription`；更底层的 `QueryBuilder` 和 `MiniArch.Core.QueryDescription` 仍在 `MiniArch.Core` |
| 需要 chunk 级遍历或存储感知优化    | `MiniArch.Core` | 你会直接碰到 `Chunk`、`Archetype` 和 query chunk snapshot                                                             |
| 需要从工作线程录制结构变化         | `MiniArch.Core` | `CommandBuffer` 是并发录制入口                                                                                          |
| 需要存档/读档                      | `MiniArch.Core` | `WorldSnapshot` 属于 advanced persistence API                                                                           |

## 并发标记

- `MT-Read`：在 world 不发生写入，且相关组件类型已经完成注册后，支持并发只读使用。
- `MT-Record`：支持多线程并发录制命令。
- 当前没有任何公开 API 支持并发写 world。

## 关键边界

- `default(Entity)` 是非法句柄，真实 entity 从 `Version = 1` 起步。
- `World.IsAlive(entity)` 才是句柄是否仍然有效的权威判断。
- `Destroy()` 会级联销毁 runtime hierarchy 子树。
- query 的并发保证覆盖“world 无写入，且相关组件类型已注册后”的并发只读使用，包含冷 query materialize / cache publish；只有首次类型注册不在这个保证内。
- `CommandBuffer` 支持并发 recording，但 replay 仍然是在单个 world 上单线程 mutation。
- `MT-Record` 只表示多个线程可以同时向同一个 buffer 录制，不表示这些线程可以同时对同一个 `World` 做直接 mutation。
- 安全模型是：worker 线程只负责录制；owner 线程在所有 worker 结束后，再执行 `Playback()`、`Replay(...)`、`ReplayWithReverse(...)`、`Play()`、`PlayWithReverse()`、`Rewind()`。
- `CommandBuffer` 的消费阶段，也就是 `Playback()`、`Play()`、`PlayWithReverse()`，必须在所有 recording 线程结束后执行，不能与 recording 并发。
- `Playback()` 只负责编译命令，不会改 world；world 真正变化发生在 `Replay(...)`、`ReplayWithReverse(...)`、`Play()`、`PlayWithReverse()`。
- `Set<T>()` 在组件缺失时可能走“补组件 + 迁移”路径，不是严格的“只更新已有组件”。
- `WorldSnapshot` 只支持 snapshot-safe 组件类型；带托管引用的组件会被拒绝。
- 并发 recording 只保证线程安全收集命令：每个线程内保持本线程追加顺序，线程间不提供按录制时间线性的全局顺序保证。多个线程若对同一 entity/component 或同一 hierarchy 关系录制冲突命令，结果不应被依赖。

## 最小示例

### Quick Start：`MiniArch.Ecs`

如果你只是想开始写 gameplay system，先看这一段就够了。

`world.Query<T>()` / `world.Query<T1, T2>()` 返回的就是 `MiniArch.Ecs` 默认查询对象，并且默认是只读枚举。常见闭环是：`Create` -> `foreach` 读取 -> `Set` 写回；`TryGet` 用于按 entity 直接读取或校验组件。若你要复杂过滤，可以先构造 `QueryDescription`，再用 `world.Query(in description)`；若你要 3+ 组件查询、chunk 级遍历或更底层控制，再切到 `MiniArch.Core`。

```csharp
using MiniArch.Ecs;

readonly record struct Position(int X, int Y);
readonly record struct Velocity(int X, int Y);

var world = new World();
var enemy = world.Create(new Position(10, 20), new Velocity(-1, 0));

foreach (var item in world.Query<Position, Velocity>())
{
    var next = new Position(item.First.X + item.Second.X, item.First.Y + item.Second.Y);
    world.Set(item.Entity, next);
}

if (world.TryGet(enemy, out Position moved))
{
    Console.WriteLine($"enemy moved to {moved}");
}
```

再补一个最短读取例子：

```csharp
foreach (var item in world.Query<Position>())
{
    Console.WriteLine(item.Component);
}
```

### 最小 QueryDescription + foreach

```csharp
using MiniArch.Ecs;

readonly record struct Position(int X, int Y);
readonly record struct Sleeping();

var world = new World();
world.Create(new Position(1, 2));
world.Create(new Position(3, 4), new Sleeping());

var description = new QueryDescription()
    .With<Position>()
    .Without<Sleeping>();

foreach (var entity in world.Query(in description))
{
    Console.WriteLine(entity);
}
```

### Advanced：`MiniArch.Core`

下面这些例子更适合已经准备直接控制 ECS 底层形状的用户。

### 示例 1：可复用 QueryDescription `MT-Read`

```csharp
using MiniArch.Ecs;

readonly record struct Position(int X, int Y);
readonly record struct Velocity(int X, int Y);
readonly record struct Acceleration(int X, int Y);
readonly record struct Sleeping();

var description = new QueryDescription()
    .With<Position>()
    .Without<Sleeping>()
    .WithAny<Velocity>()
    .Or<Acceleration>();

var worldA = new World();
var worldB = new World();

var queryA = worldA.Query(in description);
// Same description can be reused in another world.
var queryB = worldB.Query(in description);

foreach (var chunk in queryA.GetChunkSpan())
{
    foreach (var entity in chunk.GetEntities())
    {
        Console.WriteLine(entity);
    }
}
```

### 示例 2：多线程 Command Recording `MT-Record`

```csharp
using MiniArch.Core;

readonly record struct Position(int X, int Y);

var world = new World();
var buffer = new CommandBuffer(world);

Parallel.For(0, 128, i =>
{
    var entity = buffer.Create();
    buffer.Add(entity, new Position(i, i));
});

// All recording threads must be finished before consuming the buffer.
buffer.Play();
```

### 示例 3：Snapshot Save / Load

```csharp
using MiniArch.Core;

readonly record struct Position(int X, int Y);

var world = new World();
world.Create(new Position(1, 2));

using var stream = File.Create("save.bin");
WorldSnapshot.Save(stream, world);

stream.Position = 0;
var loaded = WorldSnapshot.Load(stream);
```

## 公开 API 参考

### `MiniArch.Ecs`

这是普通游戏逻辑的推荐默认层。

边界可以直接这样记：`MiniArch.Ecs = Create/Add/Set/Remove/Destroy/Link/Unlink/TryGetParent/GetChildren/IsAlive/TryGet/Query<T>/Query<T1, T2>/QueryDescription/Query(in description)`。超过这条线的 3+ 组件查询、chunk、command buffer、snapshot 需求，再进入 `MiniArch.Core`。

#### `MiniArch.Ecs.World`

`MiniArch.Core.World` 的薄 façade。

普通层的核心常用路径可以直接记成：`Create/Add/Set/Remove/Destroy/Link/Unlink/TryGetParent/GetChildren/IsAlive/TryGet/Query<T>/Query<T1, T2>/QueryDescription/Query(in description)`。如果你要 3+ 组件查询、chunk 级访问、command buffer 或 snapshot，就切到 `MiniArch.Core`。

再强调一次：`MiniArch.Ecs` 继续只把 `Query<T>()` 和 `Query<T1, T2>()` 作为默认 typed query façade；另外也提供 `QueryDescription` 和 `Query(in description)` 给普通用户做复杂过滤查询。`Query<T1, T2, T3>()`、`QueryBuilder`、`MiniArch.Core.QueryDescription` 仍然属于 `MiniArch.Core`。

| 成员                                                                | 说明                                                                             |
| ------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| `World(int chunkCapacity = 128, int entityCapacity = 64)`         | 创建用户层 world façade。                                                       |
| `Advanced`                                                        | 逃生口，拿到底层 `MiniArch.Core.World`。                                       |
| `Create()`                                                        | 在空 archetype 中创建实体。                                                      |
| `Create<T1>(T1 component1)`                                       | 创建带 1 个组件的实体。                                                          |
| `Create<T1, T2>(T1 component1, T2 component2)`                    | 创建带 2 个组件的实体。                                                          |
| `Create<T1, T2, T3>(T1 component1, T2 component2, T3 component3)` | 创建带 3 个组件的实体。更高 arity 请用 `Advanced.Create<T1...>()`。            |
| `Add<T>(Entity entity, T component)`                              | 添加组件；必要时会迁移 archetype。                                               |
| `Set<T>(Entity entity, T component)`                              | 更新组件；若缺失则可能走结构变化路径。                                           |
| `Remove<T>(Entity entity)`                                        | 若组件存在则移除。                                                               |
| `Destroy(Entity entity)`                                          | 销毁实体，并级联销毁 runtime hierarchy 子节点。                                  |
| `Link(Entity parent, Entity child)`                               | 建立 parent-child runtime hierarchy。                                            |
| `Unlink(Entity child)`                                            | 移除子节点当前 parent。                                                          |
| `TryGetParent(Entity child, out Entity parent)`                   | 若已链接则解析 parent。                                                          |
| `GetChildren(Entity parent)`                                      | 返回当前子节点列表，类型为 `List<Entity>`。                                    |
| `IsAlive(Entity entity)`                                          | 判断句柄是否仍然匹配当前 world 中的 live entity。                                |
| `TryGet<T>(Entity entity, out T component)`                       | 按 entity 直接读取组件，不必先 query。                                           |
| `Query<T>()`                                                      | 返回可直接 `foreach` 的单组件只读查询。`MT-Read`，但首次类型注册不在保证内。 |
| `Query<T1, T2>()`                                                 | 返回可直接 `foreach` 的双组件只读查询。`MT-Read`，但首次类型注册不在保证内。 |
| `Query(in QueryDescription)`                                      | 返回可直接 `foreach` 的 entity 查询。`MT-Read`，但首次类型注册不在保证内。   |

#### `MiniArch.Ecs.Entity`

用户层 entity 句柄。

| 成员                               | 说明                                      |
| ---------------------------------- | ----------------------------------------- |
| `Id`                             | entity slot id。                          |
| `Version`                        | 用于拒绝 stale handle 的版本号。          |
| `IsValid`                        | 当 `Id >= 0 && Version > 0` 时为真。    |
| `MatchesVersion(int version)`    | 只检查 version 部分是否相等。             |
| `ToString()`                     | 返回便于调试的字符串。                    |
| `==` / `!=` / equality members | entity 身份由 `Id + Version` 共同决定。 |

#### `MiniArch.Ecs.QueryDescription`

普通层可复用查询描述。

| 成员              | 说明                                          |
| ----------------- | --------------------------------------------- |
| `Advanced`      | 访问底层 `MiniArch.Core.QueryDescription`。 |
| `With<T>()`     | 添加 required 条件。                          |
| `Without<T>()`  | 添加 excluded 条件。                          |
| `WithAny<T>()`  | 添加 any 条件。                               |
| `Or<T>()`       | `WithAny<T>()` 的别名。                     |
| `RequiredTypes` | 只读 required 类型视图。                      |
| `ExcludedTypes` | 只读 excluded 类型视图。                      |
| `AnyTypes`      | 只读 any 类型视图。                           |

#### `MiniArch.Ecs.Query<T>`

为直接 `foreach` 设计的单组件查询 façade。

| 成员                | 说明                                |
| ------------------- | ----------------------------------- |
| `Advanced`        | 访问底层 `MiniArch.Core.Query`。  |
| `GetEnumerator()` | 返回 struct enumerator。`MT-Read` |

#### `MiniArch.Ecs.Query<T1, T2>`

为直接 `foreach` 设计的双组件查询 façade。

| 成员                | 说明                                |
| ------------------- | ----------------------------------- |
| `Advanced`        | 访问底层 `MiniArch.Core.Query`。  |
| `GetEnumerator()` | 返回 struct enumerator。`MT-Read` |

#### `MiniArch.Ecs.QueryItem<T>`

单组件查询中的一行视图。

| 成员          | 说明                                                    |
| ------------- | ------------------------------------------------------- |
| `Entity`    | 当前 entity。                                           |
| `Component` | 当前组件，类型为 `ref readonly`，适合读多写少的遍历。 |

#### `MiniArch.Ecs.QueryItem<T1, T2>`

双组件查询中的一行视图。

| 成员       | 说明                                  |
| ---------- | ------------------------------------- |
| `Entity` | 当前 entity。                         |
| `First`  | 第一个组件，类型为 `ref readonly`。 |
| `Second` | 第二个组件，类型为 `ref readonly`。 |

#### `MiniArch.Ecs.QueryEnumerator<T>`

`Query<T>` 背后的公开 struct enumerator。

| 成员           | 说明                            |
| -------------- | ------------------------------- |
| `Current`    | 当前 `QueryItem<T>`。         |
| `MoveNext()` | 移动到下一条匹配行。`MT-Read` |

#### `MiniArch.Ecs.QueryEnumerator<T1, T2>`

`Query<T1, T2>` 背后的公开 struct enumerator。

| 成员           | 说明                            |
| -------------- | ------------------------------- |
| `Current`    | 当前 `QueryItem<T1, T2>`。    |
| `MoveNext()` | 移动到下一条匹配行。`MT-Read` |

### `MiniArch.Core`

这一层面向需要控制 query 形状、chunk 遍历、replay、rewind、snapshot 和 runtime 细节的 advanced 用户。

#### `MiniArch.Core.World`

核心 runtime 入口。

| 成员                                                        | 说明                                                                                                                                |
| ----------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| `World(int chunkCapacity = 128, int entityCapacity = 64)` | 创建 core world。`new World()` 会走默认 chunk 策略；当前实现里只有传入非默认值时，chunk 边界才稳定表现为调用方显式指定的固定值。  |
| `Components`                                              | 公开的 `ComponentRegistry`，用于 runtime type 注册和查询。                                                                        |
| `EntityCapacity`                                          | 当前 entity metadata capacity。                                                                                                     |
| `EnsureCapacity(int entityCapacity)`                      | 提前扩好 entity metadata 容量。                                                                                                     |
| `Create()`                                                | 创建空实体。                                                                                                                        |
| `Create<T1>()` ... `Create<T1, ..., T16>()`             | 最高支持 16 个组件的高性能 direct-create 重载族。                                                                                   |
| `CreateMany(Span<Entity> entities)`                       | 批量创建实体并把句柄写入调用方提供的 span。                                                                                         |
| `Add<T>(Entity entity, T component)`                      | 添加组件，必要时触发 archetype 迁移。                                                                                               |
| `Set<T>(Entity entity, T component)`                      | 更新已有组件；若缺失则可能走结构变化路径。                                                                                          |
| `Remove<T>(Entity entity)`                                | 若存在则移除组件。                                                                                                                  |
| `Destroy(Entity entity)`                                  | 销毁实体及其 runtime-owned 子树。                                                                                                   |
| `Link(Entity parent, Entity child)`                       | 建立 runtime hierarchy link。                                                                                                       |
| `Unlink(Entity child)`                                    | 移除 child 的 parent link。                                                                                                         |
| `TryGetParent(Entity child, out Entity parent)`           | 若已链接则返回 parent。                                                                                                             |
| `GetChildren(Entity parent)`                              | 返回当前 live children。                                                                                                            |
| `TryGetLocation(Entity entity, out EntityInfo info)`      | 解析 live entity 当前的 archetype/chunk/row 位置。                                                                                  |
| `IsAlive(Entity entity)`                                  | 校验句柄是否仍匹配当前 world 状态。                                                                                                 |
| `Query()`                                                 | 返回可继续 `With/Without/Any` 扩展的空 query builder。`MT-Read`，但首次类型注册不在保证内。                                     |
| `Query<T1>()`                                             | 返回要求 1 个组件的 query。`MT-Read`，但首次类型注册不在保证内。                                                                  |
| `Query<T1, T2>()`                                         | 返回要求 2 个组件的 query。`MT-Read`，但首次类型注册不在保证内。                                                                  |
| `Query<T1, T2, T3>()`                                     | 返回要求 3 个组件的 query。`MT-Read`，但首次类型注册不在保证内。                                                                  |
| `Query(in QueryDescription description)`                  | 物化可跨 world 复用的 world-agnostic query description。`MT-Read`，包含冷 materialize / cache publish，但首次类型注册不在保证内。 |
| `Replay(in FrameCommands frame)`                          | 以固定顺序 `create -> link/unlink -> add -> set -> remove -> destroy` 回放一帧命令。                                              |
| `ReplayWithReverse(in FrameCommands frame)`               | 回放并捕获 reverse commands，供后续 rewind。reverse frame 只对捕获它的同一个 `World` 实例有效。                                   |
| `Rewind(in ReverseFrameCommands reverse)`                 | 用 reverse commands 把公开可观察状态回退。设计目标是相邻 frame 的栈式 `LIFO` 回退。                                               |

#### `MiniArch.Core.Entity`

底层 entity 句柄值类型。

| 成员                            | 说明                             |
| ------------------------------- | -------------------------------- |
| `Entity(int Id, int Version)` | 公开值构造器。                   |
| `Id`                          | entity slot id。                 |
| `Version`                     | 用于失效 stale handle 的版本号。 |
| `IsValid`                     | 句柄形状是否合法。               |
| `MatchesVersion(int version)` | version-only 比较辅助。          |
| `ToString()`                  | 调试字符串。                     |

#### `MiniArch.Core.ComponentType`

runtime component id。

| 成员                                    | 说明                            |
| --------------------------------------- | ------------------------------- |
| `ComponentType(int value)`            | id wrapper 的公开构造器。       |
| `Value`                               | 数字 runtime id。               |
| `IsValid`                             | id 形状是否合法。               |
| `CompareTo(...)` and equality members | 值语义。                        |
| `int` conversions                     | 供底层流程使用的显式/隐式转换。 |

#### `MiniArch.Core.ComponentRegistry`

把 CLR type 映射到 runtime component id。

| 成员                                            | 说明                                           |
| ----------------------------------------------- | ---------------------------------------------- |
| `GetOrCreate<T>()`                            | 按泛型类型注册或解析 component id。            |
| `GetOrCreate(Type type)`                      | 按运行时 `Type` 注册或解析 component id。    |
| `TryGetId(Type type, out ComponentType id)`   | 查询已存在的 id，但不强制注册。                |
| `TryGetType(ComponentType id, out Type type)` | 按 component id 反向查类型。                   |
| `GetType(ComponentType id)`                   | 按 component id 反向查类型；无效 id 会抛异常。 |
| `RegisteredTypes`                             | 当前注册表的只读视图。                         |

#### `MiniArch.Core.Signature`

表示组件集合的值对象。

| 成员                                                 | 说明                             |
| ---------------------------------------------------- | -------------------------------- |
| `Empty`                                            | 空 signature 常量。              |
| `Signature(params ComponentType[] components)`     | 从 component id 构造 signature。 |
| `Signature(IEnumerable<ComponentType> components)` | 从序列构造 signature。           |
| `Count`                                            | 集合中的组件数量。               |
| `AsSpan()`                                         | 以 span 形式返回组件 id。        |
| `Contains(ComponentType component)`                | 判断是否包含某组件。             |
| `Add(ComponentType component)`                     | 返回包含该组件的新 signature。   |
| `Remove(ComponentType component)`                  | 返回移除该组件后的新 signature。 |
| `GetEnumerator()`                                  | 枚举组件 id。                    |
| equality members /`ToString()`                     | 值语义与调试输出。               |

#### `MiniArch.Core.Archetype`

管理一个 signature 对应的 chunk 集合。

| 成员                                                                                                                       | 说明                                                                         |
| -------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| `Archetype(Signature signature, int chunkCapacity = 4)`                                                                  | 公开 archetype 构造器，主要用于测试和 advanced 场景。                        |
| `Signature`                                                                                                              | 该 archetype 的组件集合。                                                    |
| `EntityCount`                                                                                                            | 当前 archetype 中的 live entity 总数。                                       |
| `Chunks`                                                                                                                 | 当前 chunk 列表。                                                            |
| `Edges`                                                                                                                  | add/remove 转移缓存。                                                        |
| `AddEntity(Entity entity, IReadOnlyDictionary<ComponentType, object?> components, out int chunkIndex, out int rowIndex)` | 添加实体与初始组件负载，并返回落位位置。                                     |
| `ReserveEntity(Entity entity, out int chunkIndex, out int rowIndex)`                                                     | 为 live entity 预留一行，并返回所在 chunk/row。                              |
| `RemoveEntity(int chunkIndex, int rowIndex, out Entity movedEntity)`                                                     | 删除一行；若发生 dense compaction，会通过 `movedEntity` 返回被搬迁的实体。 |
| `GetChunk(int index)`                                                                                                    | 按索引返回一个 chunk。                                                       |

#### `MiniArch.Core.ArchetypeEdges`

缓存 add/remove 组件时的 archetype 跳转结果。

| 成员                                                               | 说明                     |
| ------------------------------------------------------------------ | ------------------------ |
| `TryGetAdd(ComponentType component, out Archetype archetype)`    | 查找缓存的 add 转移。    |
| `TryGetRemove(ComponentType component, out Archetype archetype)` | 查找缓存的 remove 转移。 |
| `CacheAdd(ComponentType component, Archetype archetype)`         | 缓存 add 转移。          |
| `CacheRemove(ComponentType component, Archetype archetype)`      | 缓存 remove 转移。       |

#### `MiniArch.Core.Chunk`

实体与组件列的 dense storage block。

| 成员                                                                           | 说明                                                                              |
| ------------------------------------------------------------------------------ | --------------------------------------------------------------------------------- |
| `Chunk(Signature signature, int capacity = 4)`                               | 公开兼容构造器。手工创建的 chunk 与 world 创建出的 typed chunk 不是同一语义层级。 |
| `Signature`                                                                  | chunk 的组件集合。                                                                |
| `Capacity`                                                                   | 最大行数。                                                                        |
| `Count`                                                                      | 当前 live 行数。                                                                  |
| `GetEntities()`                                                              | 返回 live entity 的 span，适合快速读循环。                                        |
| `GetEntity(int row)`                                                         | 按行读取一个 entity。                                                             |
| `Add(Entity entity)`                                                         | 追加一行 entity，不带 payload。                                                   |
| `Add(Entity entity, IReadOnlyDictionary<ComponentType, object?> components)` | 追加一行 entity 和 payload。                                                      |
| `GetComponent(ComponentType component, int row)`                             | boxed 组件读取。                                                                  |
| `GetComponent<T>(ComponentType component, int row)`                          | typed 行读取。                                                                    |
| `GetComponentSpan<T>(ComponentType component)`                               | 返回一个 typed 列的 span。typed chunk 上这是推荐读路径。`MT-Read`               |
| `SetComponent(ComponentType component, int row, object? value)`              | boxed 组件写入。                                                                  |
| `SetComponent<T>(ComponentType component, int row, in T value)`              | typed 组件写入。                                                                  |
| `CaptureRow(int row)`                                                        | 捕获一行 payload，适合 restore/transfer 场景。                                    |
| `RemoveAt(int row)`                                                          | 删除一行并保持 chunk dense。                                                      |

#### `MiniArch.Core.QueryBuilder`

builder 风格的 query 组合 API。

命名约定：`QueryBuilder` / `Query` 用 `Any<T>()`，`QueryDescription` 用 `WithAny<T>()`，二者的 `Or<T>()` 都只是别名。

| 成员                  | 说明                                                                                                        |
| --------------------- | ----------------------------------------------------------------------------------------------------------- |
| `RequiredSignature` | 当前 required 组件集合。                                                                                    |
| `ExcludedSignature` | 当前 excluded 组件集合。                                                                                    |
| `AnySignature`      | 当前 any-of 组件集合。                                                                                      |
| `With<T>()`         | 添加 required 组件。                                                                                        |
| `Without<T>()`      | 添加 excluded 组件。                                                                                        |
| `Any<T>()`          | 添加 any-of 组件。                                                                                          |
| `Or<T>()`           | `Any<T>()` 的别名。                                                                                       |
| `Build()`           | 物化 query。`MT-Read` 覆盖 world 无写入且相关组件类型已注册后的路径，包括冷 materialize / cache publish。 |
| `MatchedArchetypes` | 来自已构建 query 的匹配 archetype 视图。`MT-Read`                                                         |
| `Chunks`            | 来自已构建 query 的 chunk 枚举。`MT-Read`                                                                 |

#### `MiniArch.Core.QueryDescription`

可跨 world 复用的 world-agnostic query description。

| 成员              | 说明                                                                            |
| ----------------- | ------------------------------------------------------------------------------- |
| `RequiredTypes` | 返回 required CLR types 的防御性拷贝；修改返回值不会影响 description 内部状态。 |
| `ExcludedTypes` | 返回 excluded CLR types 的防御性拷贝；修改返回值不会影响 description 内部状态。 |
| `AnyTypes`      | 返回 any-of CLR types 的防御性拷贝；修改返回值不会影响 description 内部状态。   |
| `With<T>()`     | 返回加入 required type 后的新 description。                                     |
| `Without<T>()`  | 返回加入 excluded type 后的新 description。                                     |
| `WithAny<T>()`  | 返回加入 any-of type 后的新 description。                                       |
| `Or<T>()`       | `WithAny<T>()` 的别名。                                                       |
| equality members  | 值语义，便于安全复用与缓存。                                                    |

#### `MiniArch.Core.Query`

带匹配缓存快照的 advanced query 对象。

| 成员                  | 说明                                                                                                 |
| --------------------- | ---------------------------------------------------------------------------------------------------- |
| `RequiredSignature` | 以 signature 形式暴露 required filter。                                                              |
| `ExcludedSignature` | 以 signature 形式暴露 excluded filter。                                                              |
| `AnySignature`      | 以 signature 形式暴露 any-of filter。                                                                |
| `With<T>()`         | 返回收窄后的 query。                                                                                 |
| `Without<T>()`      | 返回收窄后的 query。                                                                                 |
| `Any<T>()`          | 返回扩展后的 any-of query。                                                                          |
| `Or<T>()`           | `Any<T>()` 的别名。                                                                                |
| `RefreshCount`      | 该 query 实例观测到的缓存刷新次数。                                                                  |
| `MatchedArchetypes` | 当前匹配到的 archetype。`MT-Read`，包含冷 materialize / cache publish，但不覆盖首次类型注册。      |
| `MatchedChunks`     | 当前扁平化 chunk snapshot。`MT-Read`，包含冷 materialize / cache publish，但不覆盖首次类型注册。   |
| `GetChunkSpan()`    | span-first chunk 遍历 API。`MT-Read`，包含冷 materialize / cache publish，但不覆盖首次类型注册。   |
| `Chunks`            | 基于枚举器的 chunk 遍历 API。`MT-Read`，包含冷 materialize / cache publish，但不覆盖首次类型注册。 |

#### `MiniArch.Core.ChunkEnumerable` and `MiniArch.Core.ChunkEnumerator`

`Query.Chunks` 背后的公开枚举辅助类型。

| 类型                | 成员                | 说明                                  |
| ------------------- | ------------------- | ------------------------------------- |
| `ChunkEnumerable` | `GetEnumerator()` | 返回 `ChunkEnumerator`。`MT-Read` |
| `ChunkEnumerator` | `Current`         | 当前 chunk。                          |
| `ChunkEnumerator` | `MoveNext()`      | 移动到下一个 chunk。`MT-Read`       |

#### `MiniArch.Core.EntityInfo`

`TryGetLocation(...)` 返回的位置结果。

| 成员                                                                           | 说明                            |
| ------------------------------------------------------------------------------ | ------------------------------- |
| `EntityInfo(int Version, Archetype Archetype, int ChunkIndex, int RowIndex)` | 公开值构造器。                  |
| `Version`                                                                    | 位置结果携带的 entity version。 |
| `Archetype`                                                                  | 当前 archetype。                |
| `ChunkIndex`                                                                 | archetype 内的 chunk 索引。     |
| `RowIndex`                                                                   | chunk 内的行索引。              |

#### `MiniArch.Core.CommandBuffer` `MT-Record`

延迟结构变化与 hierarchy 变化的命令录制器。

| 成员                                   | 说明                                                                                                           |
| -------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| `CommandBuffer(World world)`         | 创建一个绑定到单个 world 的 buffer。                                                                           |
| `Create()`                           | 为延迟创建预留一个真实 entity handle。`MT-Record`                                                            |
| `Add<T>(Entity entity, T component)` | 录制 add command。`MT-Record`                                                                                |
| `Set<T>(Entity entity, T component)` | 录制 set command。`MT-Record`                                                                                |
| `Remove<T>(Entity entity)`           | 录制 remove command。`MT-Record`                                                                             |
| `Destroy(Entity entity)`             | 录制 destroy command。`MT-Record`                                                                            |
| `Link(Entity parent, Entity child)`  | 录制 hierarchy link command。`MT-Record`                                                                     |
| `Unlink(Entity child)`               | 录制 hierarchy unlink command。`MT-Record`                                                                   |
| `Playback()`                         | 编译出 `FrameCommands`，但不改 world。只能在 recording 完成后单线程消费；buffer 也只能消费一次。             |
| `Play()`                             | 直接编译并回放到 owning world。用于不需要跨 world replay 的低分配路径；同样必须在 recording 完成后单线程消费。 |
| `PlayWithReverse()`                  | 编译、回放并返回 reverse commands，供后续 rewind；同样必须在 recording 完成后单线程消费。                      |

并发契约补充：线程安全只覆盖“向同一个 buffer 录制命令”。它不保证线程间存在全局录制顺序，也不允许在录制期间并发调用同一个 `World` 的直接 mutation API。

#### `MiniArch.Core.FrameCommands`

可回放到另一个同步 world 的编译后 frame。

| 成员                  | 说明                                              |
| --------------------- | ------------------------------------------------- |
| `CreatedEntities`   | 存活下来的 created entity 及其最终 payload。      |
| `LinkCommands`      | 录制得到的 parent-child link。                    |
| `UnlinkCommands`    | 录制得到的 unlink。                               |
| `AddCommands`       | 编译后的 add commands。                           |
| `SetCommands`       | 编译后的 set commands。                           |
| `RemoveCommands`    | 编译后的 remove commands。                        |
| `DestroyedEntities` | replay 时需要销毁的 existing entity。             |
| `ReleasedEntities`  | 同帧 create+destroy 后在 replay 时释放的 handle。 |

#### `MiniArch.Core.ReverseFrameCommands`

`ReplayWithReverse(...)` 捕获出的 reverse frame。

| 成员                  | 说明                                               |
| --------------------- | -------------------------------------------------- |
| `RestoredEntities`  | 需要恢复的 existing entities 及其状态。            |
| `DestroyedEntities` | 在 rewind 期间需要移除的 replay-created entities。 |
| `LinkCommands`      | 反向 link 操作。                                   |
| `UnlinkCommands`    | 反向 unlink 操作。                                 |
| `AddCommands`       | 反向 add 操作。                                    |
| `SetCommands`       | 反向 set 操作。                                    |
| `RemoveCommands`    | 反向 remove 操作。                                 |

补充说明：`ReverseFrameCommands` 只对捕获它的同一个 `World` 实例有效。除了公开可观察状态外，它还会携带 replay reservation 对齐所需的 reserved-handle 轨迹，这样 rewind 后同一批 deferred handle 才能再次 replay 成功。

#### Frame DTO Record Types

这些是 `FrameCommands` 与 `ReverseFrameCommands` 使用的公开传输记录类型。

| 类型                            | 作用                                          |
| ------------------------------- | --------------------------------------------- |
| `FrameCreatedEntity`          | 编译后 frame 中一个 created entity 的最终状态 |
| `FrameComponentValue`         | frame 数据中的一个 boxed 组件 payload         |
| `FrameLinkCommand`            | 一条 parent-child link command                |
| `FrameUnlinkCommand`          | 一条 unlink command                           |
| `FrameEntityComponentCommand` | 一条 entity + component payload mutation      |
| `FrameEntityRemoveCommand`    | 一条 entity + component type removal          |
| `ReverseFrameEntity`          | rewind 使用的一条 restored entity 记录        |

#### `MiniArch.Core.WorldSnapshot`

whole-world save/load 的二进制持久化辅助类型。

| 成员                                 | 说明                                  |
| ------------------------------------ | ------------------------------------- |
| `Save(Stream stream, World world)` | 把 world 写入二进制 snapshot。        |
| `Load(Stream stream)`              | 从 snapshot stream 重建一个新 world。 |

## 并发总结

| API                                                                  | 是否允许并发使用   | 说明                                                                                                                 |
| -------------------------------------------------------------------- | ------------------ | -------------------------------------------------------------------------------------------------------------------- |
| `MiniArch.Ecs.Query<T>` / `Query<T1, T2>`                        | 是，`MT-Read`    | 仅限 world 不发生 mutation，且相关组件类型已注册后                                                                   |
| `MiniArch.Core.Query` / `QueryBuilder` 结果 / `GetChunkSpan()` | 是，`MT-Read`    | 覆盖 query 的只读消费与冷 materialize / cache publish；首次类型注册不在保证内                                        |
| `MiniArch.Core.QueryDescription`                                   | 是                 | 值对象风格，可安全共享                                                                                               |
| `MiniArch.Core.CommandBuffer` 的 recording 方法                    | 是，`MT-Record`  | 多线程可以同时向同一个 buffer 录制，但 recording 期间不要并发调用同一个 world 的直接 mutation API                    |
| `Playback()` / `Play()` / `PlayWithReverse()`                  | 否                 | 必须等 recording 结束后再由单线程消费，不能和 recording 并发                                                         |
| `MiniArch.Core.World` mutation API                                 | 否                 | `Create`、`Add`、`Set`、`Remove`、`Destroy`、`Replay`、`Rewind`、`Link`、`Unlink` 都不是并发写 API |
| `ComponentRegistry` 首次注册                                       | 按单线程初始化对待 | 不要假设首次注册可自由并发写                                                                                         |

## 常见坑点

### 给普通 gameplay 用户

- `MiniArch.Ecs.World` 只包装到 3 组件 `Create`；更高 arity 请改用 `world.Advanced.Create<T1...>()`。
- `GetChildren()` 当前返回 `List<Entity>`，它不是热循环遍历 API。

### 给 advanced 用户

- `Chunk.GetComponentSpan<T>()` 是 typed chunk 的推荐读路径，但它针对的是 world 创建出的 typed chunk，不保证手工兼容 chunk 也可用。
- `MatchedChunks` 与 `GetChunkSpan()` 返回的是 snapshot，不是可变 live view。
- 跨 world replay `FrameCommands` 的前提是起始状态同步，且 frame 推进顺序同步。
- `QueryBuilder/Query` 用 `Any<T>()`，`QueryDescription` 用 `WithAny<T>()`；两边的 `Or<T>()` 都只是别名，不是另一套能力。

### 给 command buffer 用户

- `CommandBuffer` 只能消费一次。
- `buffer.Create()` 会立刻返回 reserved handle，但在 replay materialize 之前，`world.IsAlive(entity)` 仍然是 `false`。
- `Play()` 与 `Playback()+Replay()` 语义一致；`Play()` 只是 owning-world 的低分配快捷路径。
- `ReverseFrameCommands` 只属于捕获它的同一个 `World`，不要把它当成跨 world undo 数据。
- `Rewind` 是栈式 `LIFO` 回退，不是任意顺序 undo 系统，也不是完整内部状态 mirror rollback 机制。
- 多线程 recording 不提供跨线程全局顺序保证；同一目标上的冲突命令不要跨线程依赖结果。

### 给 snapshot 用户

- snapshot persistence 只面向受支持的组件布局。
- 如果组件类型包含托管引用，`WorldSnapshot.Save/Load` 就不是正确 API。
