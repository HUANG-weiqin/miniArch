# MiniArch API Unification Design

## 目标

- 消除 `MiniArch.Ecs` 与 `MiniArch.Core` 中重复同语义的公开概念。
- 只保留一份普通用户会直接接触的核心类型：
  - `MiniArch.World`
  - `MiniArch.Entity`
  - `MiniArch.QueryDescription`
- 移除 typed query façade：
  - `Query<T>`
  - `Query<T1, T2>`
  - `QueryItem<T>`
  - `QueryItem<T1, T2>`
  - `QueryEnumerator<T>`
  - `QueryEnumerator<T1, T2>`
- 默认与 advanced 两层统一通过 `QueryDescription` 驱动查询。

## 完成标准

- 包消费方默认只需要 `using MiniArch;` 就能使用普通 API。
- `MiniArch.Core` 不再公开第二份 `World`、`Entity`、`QueryDescription`。
- `World.Query(...)` 只保留 `Query(in QueryDescription)` 入口。
- 现有 README、知识库、测试全部对齐到新边界。
- `dotnet test` 与仓库验证脚本通过。

## 当前问题

- 当前普通层与 advanced 层重复定义了 `World`、`Entity`、`QueryDescription`。
- typed query 家族把“按 1~2 个组件读”的一种消费方式放大成一整组公开类型，造成 API 面积膨胀。
- 用户必须先判断：
  - 我该用哪层 `World`
  - 我该用哪层 `QueryDescription`
  - 我该用 typed query 还是 description query
- 这不是能力分层，而是同语义概念的重复暴露。

## 设计结论

### 1. 只保留一份核心概念

- `World` 只保留在 `MiniArch`。
- `Entity` 只保留在 `MiniArch`。
- `QueryDescription` 只保留在 `MiniArch`。

这些类型属于“用户表达 ECS 操作意图的基础语言”，不是 advanced 专属能力，不应重复定义。

### 2. `MiniArch.Core` 只保留 advanced 能力，不再重复核心概念

- `MiniArch.Core` 继续承载：
  - `CommandBuffer`
  - `FrameCommands`
  - `WorldSnapshot`
  - `Chunk`
  - `Archetype`
  - `Signature`
  - `EntityInfo`
  - 其他真正面向 runtime/storage/query internals 的类型
- `MiniArch.Core` 不再公开：
  - `World`
  - `Entity`
  - `QueryDescription`

### 3. 查询描述语言只有一种

- 整个仓库只保留 `MiniArch.QueryDescription`。
- 默认层和 advanced 层都消费这一份查询描述。
- “查什么”只有一种表达方式。
- “怎么消费结果”可以按默认层与 advanced 层区分，但不能再有两份描述类型。

### 4. 移除 typed query 家族

- 删除用户层 typed query：
  - `MiniArch.Ecs.Query<T>`
  - `MiniArch.Ecs.Query<T1, T2>`
  - 相关 `QueryItem` / `QueryEnumerator`
- 删除 core 层 generic/builder-style 查询快捷入口：
  - `World.Query()`
  - `World.Query<T1>()`
  - `World.Query<T1, T2>()`
  - `World.Query<T1, T2, T3>()`
  - `QueryBuilder`
  - `Query.With<T>()`
  - `Query.Without<T>()`
  - `Query.Any<T>()`
  - `Query.Or<T>()`

统一改为：

- `var description = new QueryDescription().With<A>().Without<B>().WithAny<C>();`
- `var query = world.Query(in description);`

### 5. 默认查询返回 entity-only 可枚举结果

- `MiniArch.World.Query(in QueryDescription)` 继续支持 `foreach`。
- 默认返回 entity-only 结果。
- 组件读取统一通过 `TryGet<T>(entity, out component)` 或其他后续单独设计的读取入口完成。

这里的取舍是明确的：

- 当前目标先收敛“重复语义 API”
- 不在本轮继续保留 1~2 组件专用 façade
- 如果后续需要更强的高性能组件读取形状，应单独设计，不与 typed query 家族绑定

## 方案比较

### 方案 A：保留 `MiniArch.Ecs`，只删 typed query

- 优点：
  - 改动较小
- 缺点：
  - `World` / `Entity` / `QueryDescription` 的重复仍然存在
  - 不能真正解决“同语义 API 两层都有”的问题

### 方案 B：`MiniArch` 只保留普通层，`MiniArch.Core` 继续保留另一份核心概念

- 优点：
  - 迁移路径看似平滑
- 缺点：
  - 重复语义没有消失，只是换了命名空间

### 方案 C：统一核心概念到 `MiniArch`，`MiniArch.Core` 只保留 advanced 能力

- 优点：
  - 最符合“去重语义”的目标
  - 默认用户只接触一套核心语言
  - advanced 边界清晰
- 缺点：
  - breaking changes 较大
  - 需要同步调整 README、测试命名空间和知识库

## 推荐方案

- 采用方案 C。

## 命名空间边界

### `MiniArch`

- `World`
- `Entity`
- `QueryDescription`

### `MiniArch.Core`

- `Query`
- `ChunkEnumerable`
- `ChunkEnumerator`
- `Chunk`
- `Archetype`
- `Signature`
- `EntityInfo`
- `ComponentRegistry`
- `ComponentType`
- `CommandBuffer`
- `FrameCommands`
- `ReverseFrameCommands`
- `WorldSnapshot`

说明：

- `MiniArch.Core.Query` 可以保留为 advanced 查询结果对象。
- 但它必须消费 `MiniArch.QueryDescription`，而不是第二份 `MiniArch.Core.QueryDescription`。

## 兼容性与 breakage

### 明确 breaking changes

- 删除 `MiniArch.Ecs` 命名空间下的公开用户 API。
- 删除所有 typed query 公开类型。
- 删除 `MiniArch.Core.World`、`MiniArch.Core.Entity`、`MiniArch.Core.QueryDescription`。
- 删除 `QueryBuilder` 和 `World.Query<T...>()` 这类 generic 查询快捷入口。

### 迁移后调用形状

- 旧：
  - `using MiniArch.Ecs;`
  - `var world = new World();`
  - `foreach (var item in world.Query<Position, Velocity>())`
- 新：
  - `using MiniArch;`
  - `var world = new World();`
  - `var description = new QueryDescription().With<Position>().With<Velocity>();`
  - `foreach (var entity in world.Query(in description))`

## 实现策略

### 第一步：先锁定新契约测试

- `MiniArch` 根命名空间暴露 `World` / `Entity` / `QueryDescription`
- `World.Query<T...>()` 不再可用
- `World.Query(in QueryDescription)` 继续可 `foreach`
- `MiniArch.Core.QueryDescription` / `MiniArch.Core.World` 不再可用

### 第二步：收敛公开类型

- 迁移用户层 `World` / `Entity` / `QueryDescription` 到 `MiniArch`
- 删除 `src/MiniArch/Ecs/Query.cs`
- 删除 typed query 入口和类型
- 修改 core 查询实现消费新的 `MiniArch.QueryDescription`

### 第三步：修复所有调用点

- user tests
- core tests
- benchmarks
- README
- `.knowledge`

## 风险点

- 测试工程当前使用 `MiniArch.Tests.*` 命名空间；把类型提升到根命名空间后，需要确认没有新的名称解析冲突。
- `MiniArch.Core.Query` 仍然存在时，需要避免它反向依赖另一套核心类型。
- 删除 typed query 后，现有 allocation smoke test 需要改写成 description-based 枚举路径。
- benchmark 里大量使用 `World.Query().With<T>().Build()`，需要一次性收敛到 description-based 入口，否则会出现大量编译失败。

## 验证策略

- 用户 API 测试先验证新命名空间和查询入口。
- core query tests 再验证 description 查询行为不回退。
- benchmark 项目至少编译通过。
- 完整执行 `dotnet test` 与 `scripts/verify.ps1`。
