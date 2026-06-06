---
title: MiniArch Core ECS
module: MiniArch.Core
description: Target ECS architecture for entities, archetypes, flat byte chunk storage, direct-index writes, signatures, and queries
updated: 2026-06-07
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

- 核心组成：
  - `World.cs`：实体生命周期、archetype 生成、query 缓存
  - `Archetype.cs`：chunk 列表、实体计数、结构变化入口
  - `Chunk.cs`：实体列和 flat byte-backed component columns 的密集存储
  - `Signature.cs`：组件集合键（排序 `ComponentType[]` + `long` bitmask）
  - `QueryFilter.cs`：query filter 的内部执行形状
  - `QueryComponentSet.cs`：排序组件集合（仅 `CreateFrom` 批量入口）
  - `QueryDescription.cs`：可跨 world 复用的 query 描述，保存 world-agnostic 的 `Type` 集合
  - `Query.cs` / `QueryIterators.cs`：archetype 过滤和 chunk 遍历
  - `ArchetypeEdges.cs`：增删组件迁移计划缓存（componentId 直索引稀疏数组）
  - `MigrationPlan.cs`：缓存 archetype 迁移的目标 archetype、共享组件列映射和 Add 新组件列索引
  - `ComponentColumnMap.cs`：`component id -> column index` 映射的共享 helper
  - `ComponentRegistry.cs`：全局 `Type ↔ ComponentType` 双向映射（copy-on-write）
  - `ComponentType.cs`：`int` wrapper
  - `Entity.cs`：`(id, version)` 二元组
  - `EntityLocation.cs`：`(archetype, chunkIdx, row)`
  - `EntityAccessor.cs`：ref struct，一次 entity 定位后直读/直写多个组件（跳过重复的 `_records` 查找）
  - `HierarchyTable.cs`：`World` 持有的 runtime side-table parent-child 关系

- 数据流 / 控制流：
  - `World` 创建实体后放入空签名 archetype
  - `World.Create<T...>` 当前为 `1..16` 个组件提供固定重载；warmed 路径缓存在泛型 static cache（`CreateArchetypeCache<T...>`），O(1) 无分配
  - `World.EnsureCapacity` 负责提前扩好 entity metadata 存储
  - `World.CreateMany` 先批量准备 entity id，再用 chunk-batched reservation 一次性落入空签名 archetype
  - `Add/Remove` 先算目标签名，再复用 edge-cached `MigrationPlan` 搬迁共享组件
  - `Set` 在组件已存在时直接定位到 typed column 的 row，原地写回，不触发迁移
  - `EntityAccessor` 缓存 `(Archetype, Chunk, RowIndex)`，后续 `Get<T>` / `Set<T>` / `Has<T>` 跳过 `_records` 查找和 version check，直接通过 `GetComponentIndexFast` + `GetComponentRefAt` 定位数据
  - `Destroy` 走 leaf-entity 快速路径：无 children 时跳过 `CollectDestroySubtree`
  - `Clone` 执行 deep clone：先 `CloneSingle` 复制 root 实体（同 archetype memcpy），有 children 时 DFS 遍历 subtree
  - Query 读路径使用 world 发布的 archetype 数组快照和 query 自身发布的 matched-archetype 数组快照
  - `Archetype` 维护 non-full chunk 栈（LIFO），`GetWritableChunk()` 从栈顶取可写 chunk
  - `Chunk` 的组件列存储为一块 `_data: byte[]`，通过 `_columnByteOffsets[column] + row * _elementSizes[column]` 定位元素
  - `World.Create/Destroy` 热路径无锁（单线程 world mutation 前提）；`CommandBuffer` 的 `ReserveDeferredEntity` 保留锁

## 决策

- 用 `ComponentType` 而不是直接用 `Type` 作为运行时 key
- 用 `Signature` 作为 archetype key，保证等价组件集合落在同一个 storage family
- Set/Add 的原地写入路径优先走 flat byte storage + component id → 列索引 direct map
- `World` 的 entity metadata 需要显式容量管理，不依赖 `List<T>` 自然扩容
- `default(Entity)` 不合法；真实实体从 `Version = 1` 起步
- 单实体带组件创建直接落到目标签名 archetype，不经过 `Create → Add` 迁移链
- 空签名 archetype 默认 chunk size 比普通 archetype 更大
- 默认 chunk 容量按每实体近似字节数自适应接近固定目标字节数
- 显式 `chunkCapacity` 仍视为调用方确定性约束，不静默改写
- Query 并发读优先用 copy-on-write 快照发布，而不是加锁
- Query snapshot 已内联在 `Query` 上（`_snapshotArchetypes`、`_snapshotChunks`、`_snapshotGeneration`），`MatchingSnapshot` class 已消除
- 热路径安全检查（bounds check、capacity check 等）包裹 `#if DEBUG`，Release 下零开销
- `[SkipLocalsInit]` + `AggressiveInlining` + `Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), offset)` 消除 JIT 边界检查
- 高频结构迁移缓存 `MigrationPlan`，避免每次 Add/Remove 都重新匹配共享组件列；小组件搬迁用 1/4/8/12/16-byte 专门 copy 分支
- Entity version 和 location 合并存储在 `EntityRecord[] _records`：一次随机访问拿到版本与位置，减少实体随机访问 cache miss
- flat byte chunk 只面向 unmanaged 组件；含托管引用组件在 chunk 构造时 fail fast

## 认知模型

- 一条从 entity id 到 dense typed storage 的映射链
- 最重要的抽象：`World` → `Signature` → `Archetype` → `Chunk`

## 入口

- 第一次读：`World.cs`（完整控制流入口）→ `Signature.cs`（archetype key 规则）→ `Chunk.cs`（底层存储布局）→ `Archetype.cs`（chunk 扩展点）
- EntityAccessor：`EntityAccessor.cs`（ref struct，单实体多组件直读）
- 修 bug：`World.cs`（实体迁移和版本校验）→ `QueryIterators.cs`（chunk 枚举）

## 坑点

- 迁移后必须更新 moved entity 的 location
- swap-remove 必须同时移动 entity 和每个组件列的对应 byte block
- Archetype 不能只复用最后一个 chunk——non-full chunk 栈防止 `Remove` 分配被放大
- `Create<T...>` 如果复用 `Add` 迁移路径，会留下中间态 archetype
- `CreateMany` 不能退化成外部循环调 `Create`
- Edge cache 用 `MigrationPlan?[]` 按 componentId 直索引，当组件 ID 稀疏时数组可能膨胀
- `Set` 和 `Add` 在内部调用同一个 `ApplyTypedAddOrSet`——组件不存在时 `Set` 会静默添加
- **`MigrationPlan` 缓存 byte offset 而非 column index**：`Archetype.EnsureCapacity()` 扩容后 `_columnByteOffsets` 会重新计算；预缓存的 byte offset 指向旧内存位置，导致迁移时组件数据写到错误位置。修复：`CopyEntry` 存 column index，拷贝时动态解析到当前 offset。
- Query 快照是非原子的（archetype 和 chunk 数组分开写入），安全性依赖"world 无并发写"前提
- `IsAlive` 必须和 `TryGetLocation` 共用同一条 version/location 校验链，不能有独立状态
- 性能验证必须看 Arch 对照数据，不能只看自己变快
- `EntityAccessor` 是 ref struct，不可装箱、不可存字段、不可捕获在 lambda 中
- 结构变更（Add/Remove）后 entity 可能换 archetype，此时已获取的 accessor 指向旧位置，必须丢弃
- 本页描述的是当前实现，不是旧版 `Dictionary<ComponentType, object?>` 实现
