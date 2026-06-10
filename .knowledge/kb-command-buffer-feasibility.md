---
title: Command Buffer Runtime
module: MiniArch.Core CommandBuffer
description: CommandBuffer safety-first per-entity deduplicating recorder plus CommandStream typed-store expert mode, both compatible with FrameDelta
updated: 2026-06-10 (修复 CommandStream 三个 Buffer 一致性问题：pending Remove、空实体创建、同帧 Destroy 后操作安全性；补全 48 个测试)
---
# Command Buffer Runtime

## 这个模块是干什么的

- 提供 `CommandBuffer` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
- 录制时按实体+组件类型去重（而非追加日志），消除编译步骤
- `Submit()` 消费当前批次后自动清空，允许下一帧复用同一实例
- 约束并发只覆盖 recording，不把 `World` 变成并发写安全对象
- 提供 `CommandStream` 专家模式：typed component stores + lightweight structural log，降低 record 成本，同时能生成 `FrameDelta`

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandBuffer.cs`：public recording API + `Submit()` + `Snapshot()` + `Clone()`
  - `src/MiniArch/Core/CommandStream.cs`：append-only expert-mode API + `Submit()` + `Snapshot()`
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR，可保留并重放到同步 world
  - `src/MiniArch/Core/InlineMap.cs`：4 inline slot + overflow 的 per-entity map
  - `src/MiniArch/Core/OverflowPool.cs`：三个并行数组（keys, values, next）backed by `ArrayPool<T>` 的单链表节点池
  - `src/MiniArch/Core/World.cs`（+ partial 文件）：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`
- **已删除**：
  - `ICommandRecorder.cs`：接口已删除（YAGNI），CommandBuffer 直接使用
- 数据流 / 控制流：
  - 工作线程通过 `CommandBuffer` 只记录命令；`Create()` 立刻从 world 预留真实 `Entity`
  - recording 完成后可选：`Submit()`（直接执行到 world）→ `Snapshot()`（生成自包含 `FrameDelta`）→ `SubmitAndSnapshotAsync()`（并行执行 Submit + BuildDelta）
  - `Clone()`：深拷贝整个 CommandBuffer 状态（含所有 slab 数据），用于 snapshot/replay 场景
  - `CommandStream`：`Add/Set` 进入按组件类型分组的 typed store；`Remove/Destroy` 进入 structural log；created entity 的组件录制期分流到 created side table，提交时一次性 materialize；`Snapshot()` 从 typed stores + side table/log 生成 `FrameDelta`

## CommandStream 专家模式

- 适用场景：调用方能保证命令质量，优先需要低 record overhead 的高频游戏帧。
- API 首版：`Create()`、`Add<T>()`、`Set<T>()`、`Remove<T>()`、`Destroy()`、`Submit()`、`Snapshot()`。
- 关键约束：
  - 不替代默认 `CommandBuffer`；默认 API 继续保留录制期去重与安全归约语义。
  - `Create()` 仍使用 `World.ReserveDeferredEntity()`，所以生成的 `FrameDelta` 可以保留确定的 entity id/version。
  - 组件数据按 typed value 记录，`Submit()` 直接写 typed value，`Snapshot()` 再转成 `FrameDelta` 所需 raw bytes。
  - component `Add/Set` 按类型批处理，不承诺与 `Remove/Destroy` 的严格全局追加顺序；同帧冲突命令的净效果由调用方负责。
  - created entity 组件在 record 时分流，避免提交时 O(created × commands) 扫描整条日志。
  - created materialize 使用小型 archetype cache，避免每个 spawn 反复分配 signature/type array。

## 录制时去重模型

- 每个实体维护 O(1) 的 `ExistingEntityOps`（4 inline slot + overflow linked-list node）
- 同实体同组件类型的后续操作替换而非追加
- `CreatedState` 同样用 4 inline 槽位 + overflow pool
- Arena slab allocator：`CopyData<T>` 写入 `ArrayPool<byte>.Shared.Rent` 租来的 slab

## 决策

- `Snapshot()` 用于跨 world 同步或延迟回放；无此需求时优先用 `Submit()`
- `CommandStream.Snapshot()` 首版同步生成 `FrameDelta`；后续可在同一日志形状上增加 async/compile 变体。
- `SubmitAndSnapshotAsync()`：换出 buffer 状态后，主线程 Submit 与后台线程 BuildDelta 并行执行
- 记录期返回真实 `Entity`，但只是 reserved handle（`world.IsAlive(entity)` 仍为 false）
- query layout generation 在 replay 期间被抑制，整批结束后只递增一次
- `ICommandRecorder` 已删除：接口只有 CommandBuffer 一个实现者，YAGNI 原则下直接使用具体类型
- `Clone()` 新增：完整深拷贝，用于需要保留录制状态的场景

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

- `CommandBuffer`：对 world 的"延迟结构变更 + 录制时去重 + 直接批量提交器"。
- `CommandStream`：对 world 的"低成本 typed component stores + created side table + 可编译 FrameDelta"。

## 入口

- 第一次读：`src/MiniArch/Core/CommandBuffer.cs` / `src/MiniArch/Core/CommandStream.cs` → `src/MiniArch/Core/FrameDelta.cs`
- 修 bug：`tests/MiniArch.Tests/Core/CommandBufferTests.cs`、`tests/MiniArch.Tests/Core/CommandStreamTests.cs`

## 坑点

- existing entity 和 created entity 的归约规则不同
- 同帧 `create + destroy` 必须释放 reserved entity，否则 id 被永久占用
- replay 期间逐条触发 query layout 失效会增加额外开销
- existing destroy 必须存完整 `Entity`（含 version），不能只存 id
- `InlineMap` 的 `OverflowHead` 默认值 0 在 `Clear()` 后可能导致遍历空 pool；新分配 map 必须显式初始化 `OverflowHead = -1`
- **`ResolveTypeInfo` 缓存哨兵值不能用 `IsValid`**（已修复 2026-06-08）：删除 `RuntimeType` 后缓存元组变为 `(ComponentType, int)`，用 `cached.Size > 0` 判断命中。**教训：缓存哨兵不能依赖值域内的合法值。**
- `Clone()` 是完整深拷贝，包含所有 slab 数据，大 buffer 克隆成本高
- `CommandStream` 是专家模式，不做 `CommandBuffer` 那样的 per-entity/per-component 去重；重复命令的净效果由调用方负责。
- **混合 World.Create（排序）与 CommandBuffer/CommandStream（未排序）可能产生重复 archetype**：两者创建组件的顺序不同（`World.Create<T>` 用 `public Signature(params ComponentType[])` 构造函数，自动排序去重；CommandBuffer/CommandStream 用 `Signature.CreateNormalized` 按 ADD 顺序存储，不做排序）。如果同一组件集用两条路径分别创建，`_archetypes` 字典中会出现两个不同键的 Signature，产生两个功能等价但列顺序不同的 archetype。`World.TryGetArchetype` 的集合比较能正确匹配两者，但 `ArchetypeMatchesComponentSpan`（CommandStream 分组用顺序比较）可能因顺序不同而失败。**解决办法：整个项目统一用一种创建路径**。
- **CommandStream 支持对 pending entity 调用 Remove**（2026-06-10 修复）：同批次内通过 `Create()` 或 `Clone()` 创建的实体处于 pending 状态。对其调用 `Remove<T>()` 现在通过 `BatchedComponent.Removed` 标记正确移除组件。与 CommandBuffer 行为一致。`CommandStreamTests.Remove_on_pending_clone_removes_component` 验证。
- **CommandStream 支持创建零组件的空实体**（2026-06-10 修复）：`Create()` 后未调用任何 `Add<T>()` 的实体现在通过 `MaterializeReservedEntity(entity, Array.Empty<>())` 正确 materialize 为无组件实体。与 CommandBuffer 行为一致。`CommandStreamTests.CrossWorld_replay_create_empty_entity` 验证。
- **CommandStream 同批次中 Destroy 后不再影响其他操作**（2026-06-10 修复）：`ApplyAllEntries` 改为两遍处理（pass 1: Create/Add/Set/Remove，pass 2: Destroy），与 CommandBuffer 的"先 ApplyOps 再 Destroy"顺序一致。同批次内先 Destroy 后 Add/Set 同一实体现在安全（Add/Set 先执行，然后 Destroy 销毁实体）。对 pending entity，`CancelPendingEntity` 后的条目在 materialize 时通过 `_pendingBatch[id] < 0` 检测跳过。`CommandStreamTests.Fuzz_200_frames_submit_and_verify_entity_count_stability` 不再需要 `destroyedThisFrame` 过滤。
- **FrameDelta.Replay 命令处理顺序与原始记录顺序不同**（架构差异，非 bug）：`World.ReplayCore` 按固定顺序处理：Reserved → Released → Created → Link → Unlink → AddCommands → SetCommands → RemoveCommands → Destroy。当同帧对同一组件类型执行 `Remove<Pos>` 后 `Add<Pos>` 时，`Submit` 与 `Snapshot+Replay` 可能产生不同结果。**CommandBuffer 不受此影响：录制时 per-component 去重消除了重复操作。CommandStream 调用方应避免同帧对同组件 Remove+Add 模式，改用 `Set`。**

