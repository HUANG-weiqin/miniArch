---
title: MiniArch Core ECS
module: MiniArch.Core
description: Target ECS architecture for entities, archetypes, flat byte chunk storage, direct-index writes, signatures, and queries
updated: 2026-07-04 (AssertNotDisposed + AssertAlive rename; clarify DEBUG-only safety checks are intentional design)
---
# MiniArch Core ECS

## 这个模块是干什么的

- 创建、销毁和迁移实体
- 管理 entity metadata 容量和批量创建
- 给 command buffer 提供 deferred entity reservation、structural mutation 和 batch replay 挂接点
- 维护签名到 archetype 的映射，用 chunk 做 dense SoA 存储
- 让 `Set` 走 typed-column / direct-index 的原地写入路径
- 让 query 先按 archetype 过滤，再按 chunk 迭代

## 架构

- 核心组成（文件拆分为 partial 类）：
  - **World partial 文件族（7 个）**：详见 `kb-architecture-review.md` §10
  - **Archetype partial 文件族（3 个）**：
    - `Archetype.cs`：字段声明、构造函数、metadata 属性（EntityCount/Capacity/ComponentTypes）、edge cache（add/remove destination）、component index resolution
    - `Archetype.Storage.cs`：存储操作（EnsureCapacity、AddEntity、ReserveRows、RemoveAt、component read/write span、CopySharedComponentsFrom、CreateStorage、CopySmall）
    - `Archetype.TestHooks.cs`：`*ForTesting` 内部方法（ForceChunked、AddSegment），与生产职责分离
  - `ChunkView.cs`：**public** readonly struct 视图，直接包裹 Archetype（给用户 batch API 用）
  - `Signature.cs`：组件集合键（排序 `ComponentType[]` + `ComponentMask` 512-bit bitmask）
  - `ComponentMask.cs`：512-bit bitmask（8 × `ulong`），加速 signature 匹配
  - `ComponentColumnMap.cs`：`component id → column index` 映射的共享 helper
  - `QueryFilter.cs`：query filter 的内部执行形状
  - `QueryComponentSet.cs`：排序组件集合（仅 `CreateFrom` 批量入口）
  - `QueryDescription.cs`：可跨 world 复用的 query 描述，保存 world-agnostic 的 `Type` 集合
  - `Query.cs`：archetype 过滤和 chunk 遍历、单版本号全局快照失效；定义 `internal sealed class QueryCache`（用户面是 `MiniArch.Query` struct facade）
  - `ComponentRegistry.cs`：全局 `Type ↔ ComponentType` 双向映射（copy-on-write）；`GetFingerprint()` 算 SHA-256 指纹供跨进程握手
  - `ComponentSchema.cs`：**public** 静态门面，`Fingerprint()` 返回注册表 SHA-256 指纹——调试期版本兼容性校验工具
  - `ComponentType.cs`：`int` wrapper
  - `ComponentSizeCache.cs`：`Type → size` 缓存
  - `Entity.cs`：`(id, version)` 二元组
  - `EntityRecord.cs`：`(Archetype, RowIndex, Version)` 16 字节，合并版本与位置
  - `EntityAccessor.cs`：ref struct，一次 entity 定位后直读/直写多个组件（跳过重复的 `_records` 查找）
  - `WorldStats.cs`：`WorldStats`（全局诊断快照）+ `ArchetypeStats`（单 archetype 快照），纯按需推算、零新增状态
  - `EntityBatchRange.cs`：批量创建/克隆的连续范围记录
  - `ManagedReferenceCheck.cs`：托管引用检测
  - `SpanHelper.cs`：排序+去重、hash 合并等 span 工具
  - `HierarchyTable.cs`：`World` 持有的 runtime side-table parent-child 关系

- **已删除**：
  - `DebugMetrics.cs`：整个文件已删除（YAGNI 清理），`WorldDebugMetrics`/`CommandBufferDebugMetrics` 类型已移除

- 数据流 / 控制流：
  - `World` 创建实体后放入空签名 archetype（`World.EntityLifecycle.cs`）
  - `World.Create<T...>` 为 `1..16` 个组件提供固定重载（`World.Create.Generated.cs`）；warmed 路径缓存在泛型 static cache（`CachedCreateArchetype`），O(1) 无分配
  - `World.GetSingleton<T>()` 扫描所有 archetype 返回唯一含 `T` 的实体（singleton 语义：0 或 >1 抛异常），O(archetypes) 冷路径
  - `World.EnsureCapacity` 负责提前扩好 entity metadata 存储
  - `World.CreateMany` 先批量准备 entity id，再一次性落入空签名 archetype
  - `Add/Remove`（`World.StructuralChange.cs`）先算目标签名，再通过 edge cache（`_addDestinationCache`/`_removeDestinationCache` `Archetype?[]` 按 componentId 直索引）找到目标 Archetype，用 `Archetype.CopySharedComponentsFrom` 搬迁共享组件
  - `Set` 在组件已存在时直接定位到 typed column 的 row，原地写回，不触发迁移
- `EntityAccessor` 缓存 `(Archetype, RowIndex)`，后续 `Get<T>` / `Set<T>` / `Has<T>` 跳过 `_records` 查找和 version check
- `Destroy` 走 leaf-entity 快速路径：无 children 时跳过 `CollectDestroySubtree`
- `Clone` 执行 deep clone：先 `CloneSingle` 复制 root 实体，有 children 时 DFS 遍历 subtree
- Query 读路径使用 world 发布（volatile write）的 archetype 数组快照和 query 自身发布的 matched-archetype/chunk 数组快照（`World.QueryCache.cs`）
- **Archetype 支持单块和分段两种存储模式**（详见 `kb-chunk-storage.md`）：存储为 Archetype 直持的 `_data: byte[]`（单块）或 `_segments: Segment[]`（分段），通过 `_columnByteOffsets[column] + row * _elementSizes[column]` 定位元素
- Archetype 按需线性增长：`EnsureCapacity`（`Archetype.Storage.cs`）按 double 策略扩容，每列 `CopyBlockUnaligned` 整列搬移
- `World.Create/Destroy` 热路径无锁（单线程 world mutation 前提）；`World.ReserveDeferredEntity` 保留锁（供 defering entity 使用）；`World.ReserveDeferredEntityUnsafe` 无锁变体要求调用方保证无并发 reserve/write（CommandStream 单线程路径用此跳过 lock）

### 用户 API 分层（`MiniArch` namespace）

用户面 API 收敛在 `MiniArch` 根命名空间，`MiniArch.Core` 对用户不可见：

| 概念 | 公开定义 | 用户入口 |
|------|---------|---------|
| World | `MiniArch.World` | `new World()` |
| Entity | `MiniArch.Entity` | `world.Create<T>()` |
| Query 描述 | `MiniArch.QueryDescription` | `new QueryDescription().With<T>()` |
| Query facade | `MiniArch.Query` (public struct) | `world.Query(desc)` → `GetChunks()` / `ForEachChunk` |
| 零分配 chunk job | `MiniArch.IChunkForEach` (public interface) | `query.ForEachChunk<TForEach>(ref job)` |
| Chunk 视图 | `ChunkView` (public readonly struct) | `chunk.GetSpan<T>()` |
| Singleton 实体查找 | `World.GetSingleton<T>()` | `world.GetSingleton<T>()`（找唯一含 T 的实体，O(archetypes)） |
| 注册表指纹 | `MiniArch.ComponentSchema` (public static) | `ComponentSchema.Fingerprint()` |

**关键分层边界：**
- `MiniArch.Core.QueryCache`（原 `Core.Query`，2026-06-30 重命名）是 `internal sealed class`（`Core/Query.cs:11`），用户不能接触。重命名消除了 public struct `MiniArch.Query` 与 internal class `Core.Query` 的命名空间碰撞
- `MiniArch.Query.Advanced` 是 `internal`，**用户层无 advanced 入口**——batch/parallel 已在 `MiniArch.Query` 上直接提供
- EachSpan API 已删除，统一走 `ChunkView.GetSpan<T>()`
- typed query 家族（`Query<T>`、`Query<T1,T2>` 等）已移除
- builder 风格 `World.Query()...Build()` 已移除
- `OrderByEntityId()` / `OrderByComponent<T>(Comparison<T>)` 在 `MiniArch.Query` facade 上提供，不缓存排序结果（每次枚举租 `ArrayPool` 排序）。`OrderByComponent<T>` 批量线性扫描组件值后排序，避免 per-comparison `world.Get` 开销

**坑点：**
- `MiniArch.Query` 是 struct wrapper，不能用于 identity 断言
- `EachSpan` 引用会编译失败——改用 `chunk.GetSpan<T>()`
- `GetChunks()` 返回的 `ChunkView` 是 readonly struct，不能长期持有
- `GetSingleton<T>()` 是 O(archetypes) 全量扫描，仅用于"全局唯一组件"（设置/状态），不适合热路径；多实体访问走 Query

## 决策

- 用 `ComponentType` 而不是直接用 `Type` 作为运行时 key
- 用 `Signature` 作为 archetype key，保证等价组件集合落在同一个 storage family
- Set/Add 的原地写入路径优先走 flat byte storage + component id → 列索引 direct map
- `World.Add<T>` 是严格 Add（组件已存在时抛异常）；`World.Set<T>` 是严格 Set（组件不存在时抛异常）。CommandStream 和 Replay 路径同样走 strict 分发（`DeltaOpKind.Add` → `ApplyRawAdd`，`DeltaOpKind.Set` → `ApplyRawSet`），零额外开销
- `World` 的 entity metadata 需要显式容量管理，不依赖 `List<T>` 自然扩容
- `default(Entity)` 不合法；真实实体从 `Version = 1` 起步
- 单实体带组件创建直接落到目标签名 archetype，不经过 `Create → Add` 迁移链
- Query 并发读优先用 copy-on-write 快照发布，而不是加锁
- Query snapshot 直接内联在 `Query` 上（`_snapshotArchetypes`、`_snapshotChunkViews`、`_lastArchetypeCount`）
- **Archetype 支持单块/分段双模式**：单块模式下 Archetype = 1 个连续存储块；分段模式下 Archetype = N 个固定大小 Segment，详见 `kb-chunk-storage.md`
- **World 拆分为 partial 文件**（按职责分组）：EntityLifecycle、Create.Generated、QueryCache、StructuralChange。主文件保留字段声明、component 读写、Clone、Replay、hierarchy
- **Archetype 拆分为 partial 文件**：主文件保留字段/构造/metadata/edge cache/component index；Storage 文件负责所有存储操作
- **DebugMetrics 已删除**：YAGNI 清理，不再维护 debug 计数器和报告 API。替代方案：`WorldStats`/`ArchetypeStats` 纯快照式诊断（`World.GetStats()`/`World.GetArchetypeStats()`），零新增状态、零热路径开销
- ~~**ICommandRecorder 保留**~~ — 已删除（YAGNI）。测试层直接使用 `CommandStream`
- **Edge cache 内联**：增删目标缓存直接挂在 Archetype 上（`Archetype?[]` 按 componentId 直索引），无需独立 ArchetypeEdges 对象
- **迁移拷贝内联**：`CopySharedComponentsFrom` 直接在 Archetype 上实现，无需 MigrationPlan class
- 热路径安全检查（bounds check、capacity check、`AssertNotDisposed`、`AssertAlive` 等）包裹 `[Conditional("DEBUG")]`，Release 下零开销。这是有意设计：将这些检查改为常开会为每个 public API 增加分支，在没有明确契约变更和 perf 证明前保持 DEBUG-only
- `[SkipLocalsInit]` + `AggressiveInlining` + `Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), offset)` 消除 JIT 边界检查
- Entity version 和 location 合并存储在 `EntityRecord[] _records`：`(Archetype, RowIndex, Version)` 16 字节紧凑布局
- flat byte storage 只面向 unmanaged 组件；含托管引用组件在 storage 构造时 fail fast
- `GetSingleton<T>()` 扫描 archetype 返回唯一含 T 的实体（singleton：恰好一个），取代旧的 `GetFirst<T>`（旧 API 只匹配单组件 `{T}` 原型，多组件原型实体被漏掉，语义误导）

## 认知模型

- 一条从 entity id 到 dense typed storage 的映射链
- 最重要的抽象：`World`（拆分为 partial）→ `Signature` → `Archetype`（拆分为 partial，Chunk 是其 readonly 视图）

### 最小示例（C#）

```csharp
// 1. 创建 world
var world = new World();

// 2. 带组件创建实体
var e = world.Create<Position, Velocity>();

// 3. 组件读写
ref var pos = ref world.Get<Position>(e);
pos.X += 1;

// 4. 结构变更
world.Add<Health>(e, new Health { Value = 100 });

// 5. 查询
var query = world.Query(new QueryDescription().With<Position>());
foreach (var chunk in query.GetChunks())
{
    var positions = chunk.GetSpan<Position>();
    for (var i = 0; i < chunk.Length; i++)
        positions[i].X += 1;
}

// 6. 销毁
world.Destroy(e);
```

> 完整 API 参考见源码 `MiniArch.Query` struct 和 `World` 方法。

## 入口

- 第一次读：`World.cs`（字段声明+component 读写+hierarchy）→ `World.EntityLifecycle.cs`（Create/Destroy）→ `World.StructuralChange.cs`（Add/Set/Remove）→ `Archetype.cs`（metadata+edge cache）→ `Archetype.Storage.cs`（存储操作）→ `ChunkView.cs`（公共视图）
- 用户 API 层：`ChunkView.cs`（公共 batch 视图）、`Query.cs`（用户 Query struct）
- EntityAccessor：`EntityAccessor.cs`（ref struct，单实体多组件直读）
- 修 bug：`World.EntityLifecycle.cs`（实体版本校验）→ `World.StructuralChange.cs`（迁移逻辑）→ `Query.cs`（快照刷新逻辑）

## 坑点

- 迁移后必须更新 moved entity 的 location（`FinishMoveEntity` 在 `World.StructuralChange.cs`）
- swap-remove 必须同时移动 entity 和每个组件列的对应 byte block（`Archetype.Storage.cs: RemoveAt`）
- Archetype 扩容后 `_columnByteOffsets` 重新计算，`CopySharedComponentsFrom` 中每次拷贝都动态解析 offset，因此安全
- `Create<T...>` 如果复用 `Add` 迁移路径，会留下中间态 archetype
- `CreateMany` 不能退化成外部循环调 `Create`
- Edge cache 用 `Archetype?[]` 按 componentId 直索引，当组件 ID 稀疏时数组可能膨胀
- Add/Set 是 strict 语义：`Add<T>` 组件已存在时抛异常，`Set<T>` 组件不存在时抛异常（详见 `kb-design-rationale.md` §2.9）
- Query 快照是非原子的，安全性依赖 volatile publish + "world 无并发写"前提
- `IsAlive` 必须和 `TryGetLocation` 共用同一条 version/location 校验链
- 性能验证必须看 Arch 对照数据，不能只看自己变快
- `EntityAccessor` 是 ref struct，不可装箱、不可存字段、不可捕获在 lambda 中
- 结构变更（Add/Remove）后 entity 可能换 archetype，此时已获取的 accessor 指向旧位置，必须丢弃
- `GetSingleton<T>()` 取代了旧的 `GetFirst<T>()`：旧 API 用 `CreateArchetypeCache<T>` 缓存只命中单组件 `{T}` 原型；新 API 全量扫描，不再依赖该缓存
- DebugMetrics 相关的 `#if DEBUG` 计数累加语句已全部删除，不应再引用
- **同帧 `World.Destroy(e)` + `World.Create()` 的 id 回收与 version 一致性**（**理论风险，当前串行单线程路径安全，但修改时要小心**）：Destroy 把 `(id, version)` 推回 free-list 并对 `EntityRecord[id]` 做 swap-remove + version bump；Create 从 free-list 弹 id 并写新 `EntityRecord[id]`。两者必须严格串行且 Destroy 的 swap-remove 必须先于 Create 的 slot 写入，否则新实体可能读到被销毁实体的旧 `(Archetype, RowIndex)` 或 stale version。验证链：`World.EntityLifecycle.cs:Destroy` → `Archetype.Storage.cs:RemoveAt`（swap-remove）→ free-list push → `World.EntityLifecycle.cs:Create` → free-list pop。**当前在单线程串行调用下安全**（Destroy 的 version bump + swap-remove 在 Create 读 free-list 前完成），但引入 CommandStream 批量 materialize、多线程或 `Destroy` callback 嵌套 `Create` 时可能被打破。**回归测试入口**：`tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`（`Destroy_recycles_ids_safely` 覆盖同帧 id+version 正确性；`Destroy_and_recreate_cycle_preserves_correct_versions_across_many_iterations` 在 `TrickyEdgeCaseTests.cs` 覆盖 100 次循环的版本单调性）

