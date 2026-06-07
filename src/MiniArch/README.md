# MiniArch API Guide

MiniArch 是一个小型 archetype ECS runtime，公开两层 API：

- `MiniArch`
  默认用户入口。这里只有一份 `World`、`Entity`、`QueryDescription`。
- `MiniArch.Core`
  advanced 类型集合，面向 chunk/query internals、command buffer、snapshot、profiling。

## 当前边界

### 默认层：`MiniArch`

- `World`
- `Entity`
- `QueryDescription`
- `Query`

默认查询只保留一种写法：

```csharp
using MiniArch;

readonly record struct Position(int X, int Y);
readonly record struct Velocity(int X, int Y);

var world = new World();
world.Create(new Position(1, 2), new Velocity(3, 4));

var description = new QueryDescription()
    .With<Position>()
    .With<Velocity>();

foreach (var entity in world.Query(in description))
{
    if (world.TryGet(entity, out Position position) &&
        world.TryGet(entity, out Velocity velocity))
    {
        world.Set(entity, new Position(position.X + velocity.X, position.Y + velocity.Y));
    }
}
```

说明：

- 不再公开 `Query<T>`、`Query<T1, T2>`、`QueryItem<...>`、`QueryEnumerator<...>`。
- 不再公开 `World.Query<T...>()` 和 builder 风格 `World.Query()...Build()`。
- “查什么”统一由 `QueryDescription` 表达。

### advanced 层：`MiniArch.Core`

- `Query`
- `Chunk`
- `ChunkEnumerable` / `ChunkEnumerator`
- `ChunkView<T1..T4>` / `ChunkViewEnumerable<T1..T4>` — typed chunk 列视图
- `Archetype`
- `Signature`
- `EntityInfo`
- `ComponentRegistry`
- `ComponentType`
- `CommandBuffer`
- `FrameDelta` — `DeltaCount`（增量总量）、`HasEntity`（实体查询）、`Merge`（支持序列叠加）
- `WorldSnapshot`

advanced query 统一显式创建：

```csharp
using MiniArch;
using MiniArch.Core;

readonly record struct Position(int X, int Y);

var world = new World();
world.Create(new Position(1, 2));

var description = new QueryDescription().With<Position>();
var query = Query.Create(world, in description);

foreach (var chunk in query.Chunks)
{
    foreach (var entity in chunk.GetEntities())
    {
        Console.WriteLine(entity);
    }
}
```

这层仍然保留 chunk/span 级遍历与缓存可见性：

- `MatchedArchetypes`
- `MatchedChunks`
- `GetChunkSpan()`
- `Chunks`

## 关键约束

- `default(Entity)` 是非法句柄；真实 entity 从 `Version = 1` 起步。
- `World.IsAlive(entity)` 是句柄有效性的权威判断。
- `Destroy()` 会级联销毁 runtime hierarchy 子树。
- `Set<T>()` 在组件缺失时可能走“补组件 + 迁移”路径。
- query 的并发保证覆盖：world 无写入，且相关组件类型已注册后的并发只读。
- `CommandBuffer` 支持并发 recording，但 `Submit()` 只能在 recording 结束后单线程消费。
- `SubmitAndSnapshotAsync()` 换出 buffer 状态后，主线程 Submit 与后台线程 BuildDelta 并行执行，返回 `Task<FrameDelta>`。
- `WorldSnapshot` 只支持 snapshot-safe 组件类型；带托管引用的组件会被拒绝。

## 常用类型

### `MiniArch.World`

默认和 advanced 都共享同一个 `World`：

- `Create`
- `CreateMany`
- `EnsureCapacity`
- `Add`
- `Set`
- `Remove`
- `Destroy`
- `Link`
- `Unlink`
- `TryGetParent`
- `GetChildren`
- `IsAlive`
- `TryGet`
- `TryGetLocation`
- `Query(in QueryDescription)`
- `Replay`

### `MiniArch.QueryDescription`

唯一的查询描述语言：

- `With<T>()`
- `Without<T>()`
- `WithAny<T>()`
- `Or<T>()`
- `RequiredTypes`
- `ExcludedTypes`
- `AnyTypes`

### `MiniArch.Query`

默认层 entity-only 查询结果：

- `GetEnumerator()`
- `OrderBy(IComparer<Entity>)`
- `OrderBy(Comparison<Entity>)`
- `Advanced`

`Advanced` 会暴露对应的 `MiniArch.Core.Query`，用于必要时下沉到 chunk 级遍历。

`OrderBy(...)` 会在每次枚举时用内部池化 buffer materialize 当前 query 结果并排序。它不改变 `QueryDescription`，也不缓存排序结果；同一个 ordered query 可以并发枚举，但 comparer 自身也必须只做并发安全的读取。

### `MiniArch.Core.CommandBuffer`

延迟录制 + 批量提交器，per-entity per-component-type 去重：

- `Create` — 预留 entity handle
- `Add` / `Set` / `Remove` — 组件操作（录时去重）
- `Destroy` — 销毁实体
- `Link` / `Unlink` — hierarchy 操作
- `Submit()` — 同步提交到 world，`true` if any ops were submitted
- `Snapshot()` — 生成自包含 `FrameDelta`（不影响 world）
- `SubmitAndSnapshotAsync()` — 换出 buffer 状态，主线程 Submit 与后台线程构建 FrameDelta 并行执行；返回 `Task<FrameDelta>`，调用返回时 Submit 已完成

### `MiniArch.Core.FrameDelta`

帧快照 IR，可跨 world replay：

- `DeltaCount` — 增量总量
- `HasEntity` — 实体查询
- `Merge` — 两个 delta 合并为一个
- `IsEmpty` — 是否为空

## 并发总结

| API | 并发语义 | 说明 |
| --- | --- | --- |
| `MiniArch.Query` | `MT-Read` | 仅限 world 无 mutation，且相关组件类型已注册 |
| `MiniArch.OrderedQuery` | `MT-Read` | 每次枚举独立租用内部 buffer；comparer 必须可并发读 |
| `MiniArch.Core.Query` | `MT-Read` | 覆盖冷 materialize / cache publish，不覆盖首次类型注册 |
| `CommandBuffer` recording | `MT-Record` | 多线程可同时录制到独立 buffer 实例 |
| `CommandBuffer.Submit()` | 否 | 单线程消费 |
| `CommandBuffer.SubmitAndSnapshotAsync()` | 否（主线程）| 主线程 Submit，后台线程构建 FrameDelta，两者并行读同一份换出状态 |
| `World` mutation API | 否 | 不支持并发写 |

## 迁移提示

### 旧写法

```csharp
using MiniArch.Ecs;

foreach (var item in world.Query<Position, Velocity>())
{
    world.Set(item.Entity, new Position(item.First.X + item.Second.X, item.First.Y + item.Second.Y));
}
```

### 新写法

```csharp
using MiniArch;

var description = new QueryDescription()
    .With<Position>()
    .With<Velocity>();

foreach (var entity in world.Query(in description))
{
    if (world.TryGet(entity, out Position position) &&
        world.TryGet(entity, out Velocity velocity))
    {
        world.Set(entity, new Position(position.X + velocity.X, position.Y + velocity.Y));
    }
}
```

### advanced 迁移

```csharp
using MiniArch;
using MiniArch.Core;

var description = new QueryDescription().With<Position>();
var query = Query.Create(world, in description);
```

不再使用：

- `MiniArch.Ecs`
- `MiniArch.Core.World`
- `MiniArch.Core.Entity`
- `MiniArch.Core.QueryDescription`
- `World.Query<T...>()`
- `World.Query().With<T>()...Build()`
