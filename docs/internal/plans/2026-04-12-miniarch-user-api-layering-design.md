# MiniArch User API Layering Design

## 目标

- 为普通游戏逻辑提供一套更低心智负担的 API。
- 保留 `MiniArch.Core` 现有高性能、结构感更强的 advanced 入口。
- 让默认查询直接支持 `foreach`，并避免额外 GC。

## 当前问题

- 当前所有类型都在 `MiniArch.Core`，普通用户会直接看到 `Archetype`、`Chunk`、`Signature`、`QueryBuilder` 这些底层概念。
- `world.Query<T>()` 返回的是底层 `MiniArch.Core.Query`，默认消费方式仍然是 `foreach chunk -> for row`。
- 组件按实体读取缺少直接的 `TryGet<T>`，普通逻辑代码容易退化成“先 query entity，再自己找组件”。
- `QueryBuilder` / `MatchedChunks` / `MatchedArchetypes` 这类 API 适合做调优和结构调试，但不适合作为默认入口。

## 方案选项

### 方案 A：直接重命名/迁移现有 public 类型

- 做法：
  - 把当前 `MiniArch.Core.World` / `Query` / `Entity` 等直接迁到 `MiniArch`。
  - 再把底层类型搬去 `MiniArch.Advanced` 或重新命名。
- 优点：
  - 最终 API 最干净。
- 缺点：
  - 破坏面最大。
  - 现有测试、benchmark、未来外部依赖都会一起改。
  - 很容易把“普通 API 改造”和“底层 runtime 重组”耦合到一起。

### 方案 B：在 `MiniArch.Ecs` 新增 facade，`MiniArch.Core` 保持 advanced

- 做法：
  - 新增 `MiniArch.Ecs.World`、`MiniArch.Ecs.Query<T>`、`MiniArch.Ecs.Query<T1,T2>`、`MiniArch.Ecs.Entity`。
  - facade 内部委托给 `MiniArch.Core.World` / `MiniArch.Core.Query`。
  - `MiniArch.Core` 继续保留 `Chunk`、`Archetype`、`QueryBuilder`、`Signature` 等底层 API。
- 优点：
  - 对现有 runtime 改动最小。
  - 普通/advanced 命名空间边界立即清晰。
  - benchmark 与原有 core tests 基本可以继续复用。
- 缺点：
  - 会出现“两套入口并存”。
  - 需要补一层实体和查询结果适配。

### 方案 C：只给 `MiniArch.Core.World` 增加 extension method

- 做法：
  - 保持所有类型不动，只通过扩展方法增加 `Query<T>()` 的易用 foreach 包装。
- 优点：
  - 改动最小。
- 缺点：
  - 命名空间边界不清晰。
  - 普通用户仍然会直接暴露在 `MiniArch.Core` 全部概念前。
  - 无法真正解决“普通 API / advanced API 一眼可见”的目标。

## 推荐方案

- 采用方案 B。

理由：

- 它满足了“普通用户只接触少量核心概念”和“advanced API 显式保留”的双目标。
- 它不需要把底层 runtime 大规模搬家，风险明显低于直接重命名。
- 它允许普通查询完全按零分配枚举器单独设计，而不强迫底层 `MiniArch.Core.Query` 改成面向普通用户的形状。

## API 分层

### 普通 API

- 命名空间：`MiniArch.Ecs`
- 暴露类型：
  - `World`
  - `Entity`
  - `Query<T>`
  - `Query<T1, T2>`
  - `QueryItem<T>`
  - `QueryItem<T1, T2>`
- 面向用户的核心操作：
  - `Create`
  - `Add`
  - `Set`
  - `Remove`
  - `Destroy`
  - `TryGet`
  - `Query<T>()`
  - `Query<T1, T2>()`

### Advanced API

- 命名空间：`MiniArch.Core`
- 保留类型：
  - `Archetype`
  - `Chunk`
  - `Signature`
  - `ComponentRegistry`
  - `QueryBuilder`
  - `Query`
  - `EntityInfo`
  - `ArchetypeEdges`
  - `World`
- 定位：
  - 面向结构调试、热路径优化、benchmark 和未来更底层系统开发。

## 查询设计

### `MiniArch.Query<T>`

- `world.Query<Position>()` 返回普通 facade 查询对象。
- 支持：
  - `foreach (var item in world.Query<Position>())`
- 每次迭代返回 `QueryItem<T>`：
  - `item.Entity`
  - `item.Component`
- 不复制整列，不分配中间集合。

### `MiniArch.Query<T1, T2>`

- `world.Query<Position, Velocity>()` 返回双组件 facade 查询对象。
- 支持：
  - `foreach (var item in world.Query<Position, Velocity>())`
- 每次迭代返回 `QueryItem<T1, T2>`：
  - `item.Entity`
  - `item.First`
  - `item.Second`

## 零 GC 设计

- facade 查询对象本身是 `readonly struct`，内部只保存底层 `MiniArch.Core.Query` 引用和已解析的 `ComponentType`。
- `GetEnumerator()` 返回 `struct enumerator`。
- 枚举器直接复用底层 query 的 matched chunk snapshot。
- 每次切换 chunk 时只缓存：
  - `Chunk`
  - 当前 chunk row 数
  - 当前 chunk 的实体数组
  - 当前 chunk 的 typed component 数组
- `Current` 返回轻量 value-type item，内部保存数组引用和 row index，通过属性按需读取，不生成堆对象。

## 需要补的底层适配

- 为普通 facade 增加少量 internal 辅助，不改动底层 public 语义：
  - `Chunk` 暴露 internal typed array 访问。
  - `World` 增加 internal/public `TryGet<T>` 快路径。
- 底层 `MiniArch.Core.Query` 仍保留 `MatchedChunks`、`MatchedArchetypes`、`GetChunkSpan()` 给 advanced 用户。

## 测试策略

- 新增普通 API 测试：
  - `foreach` 单组件查询可直接遍历。
  - `foreach` 双组件查询可直接遍历。
  - facade `TryGet<T>` 不需要先 `Has`。
  - facade 查询热路径在预热后不产生额外 GC。
- 保留并复跑现有 core tests，确保 runtime 行为不回退。

## 风险点

- `MiniArch.Entity` 和 `MiniArch.Core.Entity` 的桥接必须保持轻量，不要引入装箱或隐藏分配。
- `QueryItem<T1, T2>` 如果错误设计成直接持有组件值，会把大 struct 拷贝放大到每次迭代。
- 不能为了普通 API 把 `MiniArch.Core.Query` 改成面向普通用户的臃肿类型，否则会污染 benchmark 和 advanced 路径。
