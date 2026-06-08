---
title: Command Buffer Runtime
module: MiniArch.Core CommandBuffer
description: Single-threaded per-entity-deduplicating command buffer with arena slab allocator, inline CreatedState/ExistingEntityOps, direct Submit() path, Snapshot() for cross-world replay, FrameDelta merge, SubmitAndSnapshotAsync()
updated: 2026-06-08 (YAGNI 清理 FrameDelta 冗余字段，struct 缩小带来 Movement +50% / Attack +29% 涨幅)
---
# Command Buffer Runtime

## 这个模块是干什么的

- 提供 `CommandBuffer` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
- 录制时按实体+组件类型去重（而非追加日志），消除编译步骤
- `Submit()` 消费当前批次后自动清空，允许下一帧复用同一实例
- 约束并发只覆盖 recording，不把 `World` 变成并发写安全对象

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandBuffer.cs`：public recording API + `Submit()` + `Snapshot()`
  - `src/MiniArch/Core/ICommandRecorder.cs`：只录接口，供系统代码多态使用
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR，可保留并重放到同步 world
  - `src/MiniArch/Core/InlineMap.cs`：4 inline slot + overflow 的 per-entity map
  - `src/MiniArch/Core/OverflowPool.cs`：三个并行数组（keys, values, next）backed by `ArrayPool<T>` 的单链表节点池
  - `src/MiniArch/Core/World.cs`：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`
- 数据流 / 控制流：
  - 工作线程通过 `CommandBuffer` 只记录命令；`Create()` 立刻从 world 预留真实 `Entity`
  - recording 完成后可选：`Submit()`（直接执行到 world）→ `Snapshot()`（生成自包含 `FrameDelta`）→ `SubmitAndSnapshotAsync()`（并行执行 Submit + BuildDelta）

## 录制时去重模型

- 每个实体维护 O(1) 的 `ExistingEntityOps`（4 inline slot + overflow linked-list node）
- 同实体同组件类型的后续操作替换而非追加
- `CreatedState` 同样用 4 inline 槽位 + overflow pool
- Arena slab allocator：`CopyData<T>` 写入 `ArrayPool<byte>.Shared.Rent` 租来的 slab

## 决策

- `Snapshot()` 用于跨 world 同步或延迟回放；无此需求时优先用 `Submit()`
- `SubmitAndSnapshotAsync()`：换出 buffer 状态后，主线程 Submit 与后台线程 BuildDelta 并行执行
- 记录期返回真实 `Entity`，但只是 reserved handle（`world.IsAlive(entity)` 仍为 false）
- query layout generation 在 replay 期间被抑制，整批结束后只递增一次

## 性能数据（Release, 全帧 record+submit, 3s×1）

| Case | Mini Submit | Mini Async+Snp | Arch | Async vs Submit |
|---|---|---|---|---:|
| 1000/CreateHeavy | 3217 | 2847 | 1343 | -11.5% |
| 10000/CreateHeavy | 331 | 277 | 154 | -16.3% |
| 10000/DenseExisting | 285 | 284 | 189 | -0.4% |
| 10000/MixedScript | 357 | 282 | 182 | -21.0% |

### Struct 缩小带来显著性能提升（2026-06-08）

从 FrameDelta 热路径 struct 中删除 `Type RuntimeType`（8B 引用指针）、冗余 `int ComponentTypeId`、`Signature` 字段后：

| 结构体 | 改前 | 改后 | 缩小 |
|---|---|---|---|
| `AddSetEntry` | 24B | 16B | -33% |
| `CreatedComponent` | 24B | 16B | -33% |
| `TypeInfoCache` tuple | 16B | 8B | -50% |
| `RawComponentValue` | 32B | 20B | -38% |
| `RawComponentCommand` | 40B | 28B | -30% |
| `RawRemoveCommand` | 24B | 12B | -50% |

HeroPipeline 回归测试涨幅：

| 链路 | 改前 | 改后 | 涨幅 |
|---|---|---|---|
| Movement | 858.7 rounds/s | 1284.3 rounds/s | **+49.6%** |
| Attack | 626.7 rounds/s | 809.2 rounds/s | **+29.1%** |

**根因分析（三个因素叠加）：**

1. **GC write barrier 消除**（最大因素）：`Type` 是引用类型，每次构造包含它的 struct 时 JIT 必须插入 write barrier 更新 GC card table。删除后热路径零 write barrier。
2. **Cache line 利用率提升**：struct 缩小 30-50%，同样 cache line（64B）装更多条目，InlineMap 遍历时的 cache miss 大幅减少。
3. **内存带宽减少**：高频复制（List.Add、InlineMap.Set、DeepCopyOwnedData）的 per-copy 数据量减少。

**教训：在热路径 struct 中避免持有引用类型字段（即使只是缓存），代价远比看起来大。**

## 认知模型

- 对 world 的"延迟结构变更 + 录制时去重 + 直接批量提交器"

## 入口

- 第一次读：`src/MiniArch/Core/CommandBuffer.cs` → `src/MiniArch/Core/FrameDelta.cs`
- 修 bug：`tests/MiniArch.Tests/Core/CommandBufferTests.cs`

## 坑点

- existing entity 和 created entity 的归约规则不同
- 同帧 `create + destroy` 必须释放 reserved entity，否则 id 被永久占用
- replay 期间逐条触发 query layout 失效会增加额外开销
- existing destroy 必须存完整 `Entity`（含 version），不能只存 id
- `InlineMap` 的 `OverflowHead` 默认值 0 在 `Clear()` 后可能导致遍历空 pool；新分配 map 必须显式初始化 `OverflowHead = -1`
- ~~**FrameDelta.Merge 对 CreatedEntity 不产出 ReservedEntities**~~（已修复 2026-06-08）：`RebuildFromState` 的 `IsCreated` 分支现在也会加 `ReservedEntities`，与 `BuildDelta` 正常路径保持一致。修复前跨 world replay 会因 Version 未设导致 `IsAlive` 返回 false。伴随此修复，`MergeOfMerge_created_then_destroy_then_recreate` 测试的 `Assert.Empty(ReservedEntities)` 也被修正为 `Assert.Single(ReservedEntities)`——因为 cancel-out 的 reserve+release 对被消除后，新 CreatedEntity 的 reserve 仍然存在。
- **`ResolveTypeInfo` 缓存哨兵值不能用 `IsValid`**（已修复 2026-06-08）：删除 `RuntimeType` 后缓存元组从 `(Type, ComponentType, int)` 变为 `(ComponentType, int)`，用 `ComponentType.IsValid`（`Value >= 0`）判断命中。但 `Position` 的 ComponentTypeId 恰好是 0，未初始化的 slot 也 `IsValid == true`，导致返回 `Size=0`，数据未被拷贝。改为 `cached.Size > 0` 判断。**教训：缓存哨兵不能依赖值域内的合法值，必须有不属于值域的非法状态（或显式 bool flag）。**
