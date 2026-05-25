---
title: Command Buffer Runtime
module: MiniArch.Core CommandBuffer
description: Implemented command buffer model, fixed replay order, 0-GC byte-based recording/replay, cross-world FrameDelta replay, and validation notes
updated: 2026-05-25
---
# Command Buffer Runtime

## 这个模块是干什么的

- 这个模块负责：
  - 提供 `CommandBuffer` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
- `CommandBuffer` 本体可以长期持有；`Playback()` / `Play()` 在消费当前批次后会自动清空记录，允许下一帧复用同一个实例，避免每帧新建 buffer
  - 说明 `Playback()` 编译后的固定顺序：`create -> link/unlink -> add -> set -> remove -> destroy`
  - 提供 `Play()` 短路径，在不物化 `FrameDelta` 的情况下直接把同样的编译结果提交给 owning world
  - 说明 `World.Replay(FrameDelta)` 的 batch mutation 与 query 可见性边界
  - 约束并发只覆盖 recording，不把 `World` 变成并发写安全对象
- 这个模块不负责：
  - 直接把 `World` 改成并发写安全
  - 替代现有立即生效 `World.Create/Add/Set/Remove/Destroy` API
  - 在 replay 期间支持多线程 world mutation

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandBuffer.cs`：public recording API、固定桶编译、created-entity final-state 预计算
  - `src/MiniArch/Core/CommandBufferShard.cs`：线程本地 shard，避免 recording 热路径共享锁
  - `src/MiniArch/Core/CommandBufferEntityAllocator.cs`：线程安全地从 `World` 预留真实 entity handle
  - `src/MiniArch/Core/FrameDelta.cs`：编译后的 frame IR（原 `CompiledCommandBatch`），可保留并重放到同步 world
  - `src/MiniArch/Core/ComponentWriterCache.cs`：per-type typed column writer/reader delegate 缓存，公开可用
  - `src/MiniArch/Core/World.cs`：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`、structural mutation
- 数据流 / 控制流：
  - 工作线程通过 `CommandBuffer` 只记录命令；`Create()` 会立刻从 world 预留真实 `Entity`
- `Playback()` 和 `Play()` 共享同一套合并/归约逻辑，但 compile 现在改成"按 shard 直接归约到最终批次"，不再先把所有 shard 扁平化成中间大 `List`
- `CommandBuffer` 的 compile 现在复用 buffer-local scratch `Dictionary` / `HashSet` / `List`，把 command 去重、created-state materialize 和 component type cache 的临时分配压到单次 buffer 生命周期内部
  - `Playback()` 返回 `FrameDelta`，可以保留并跨 world replay
  - 对同帧 newly-created entity，`Playback()` 直接预计算 final signature 和 final component payload
  - 同帧 `create + destroy` 不会 materialize 到 world，而是放进 `ReleasedEntities`，在 replay 时回收到 free-list 并提升 version
  - `Play()` 走 owning-world 专用 compiled batch，不再先物化 `FrameDelta`；created entity 的 internal batch 会尽量复用 compile 阶段结果，减少 `ToArray` / `ToList` / LINQ 分配
  - `Replay(FrameDelta)` 先 materialize surviving creates，再执行 `link/unlink -> add -> set -> remove -> destroy`
  - `Replay()` 期间抑制逐条 query layout 发布，整批结束后只发布一次
  - 跨 world replay 时 `ResolveCompiledComponentType` 会验证 component type 对目标 world 的 registry 有效
- 和其他模块的交互方式：
  - 依赖 `World` 的 entity version / free-list / archetype mutation 基础设施
  - hierarchy 仍由 `HierarchyTable` side-table 持有，command buffer 只决定 replay 顺序
  - query 仍依赖 `World.QueryLayoutGeneration`，只是 replay 期间改为 batch publish
- 验证依赖 `tests/MiniArch.Tests/Core/CommandBufferTests.cs`，并辅以 lifecycle / structural-change / query 回归测试
- 与 `Arch` 的对比 benchmark 当前依赖 `CommandBufferSharedScenarios` 先验证共享结构命令场景的 parity，再跑 `record + play`

## 决策

- 当前已落地的方向是"多线程 `recording` + 单线程 `replay`"；`World` 本身仍不是并发写安全。
- 当不需要保留 frame 或跨 world replay 时，应优先用 `Play()`；它复用同样语义，但省掉 `FrameDelta` 物化分配。
- benchmark CLI 的 `command-buffer` 默认入口现在偏向快速 smoke 输出；完整 BenchmarkDotNet 套件需要显式 `--full`，避免本地验证卡在大矩阵上。
- `FrameDelta` 是可保留的 frame 数据；只要目标 world 和源 world 保持同步初始态与同步回放顺序，就可以顺序 replay 到另一个 world。
- 跨 world replay 时 `ResolveCompiledComponentType` 会验证 component type 对目标 world 的 registry 有效，保证跨实例安全。
- 当前与 `Arch.Buffer.CommandBuffer` 的公共可比子集是 `Create / Add / Set / Remove / Destroy`；`Link / Unlink` 不参与跨引擎达标判定
- `Play()` 当前的主要优化点是：
  - compile 阶段移除了 shard 扁平化中间桶
  - owning-world replay 复用 compile 批次里的 internal created/component 表示
  - replay 期间对 `Type -> ComponentType` 做批次级缓存，并去掉 created materialize 的重复 reservation 校验
- 记录期直接返回真实 `Entity`，但它只是 reserved handle：
  - fresh id 会扩展 `_versions/_locations`
  - recycled id 会先从 free-list 取出
  - 直到 replay materialize 前，`world.IsAlive(entity)` 仍为 `false`
- `Playback()` 使用固定桶顺序，而不是用户调用顺序；当前实现的同实体冲突归约规则是：
  - existing entity：`add` / `set` 同 bucket 采用最后一次写入值；`remove` 独立保留到 remove phase
  - created entity：直接以 `add -> set -> remove` 计算 final component map 和 final signature
- query layout generation 在 replay 期间被抑制，整批 replay 结束后只递增一次。
- 组件类型注册仍不是自由并发写安全；当前 `CommandBuffer` 通过内部锁串行调用 `ComponentRegistry.GetOrCreate<T>()`，把并发风险限制在 recording 层内部。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 对 world 的"延迟结构变更日志 + 可复制到同步 world 的批量提交器"，不是线程安全 world
- 这个模块里最重要的抽象是：
  - `reserved entity handle`
  - `operation bucket`
  - `FrameDelta`
- 常见误解：
  - 以为 `command buffer` 等于 world 从此支持多线程写
  - 以为 `Playback()` 已经把 world 改了；实际上真正 mutation 发生在 `Replay()`
  - 以为 `CommandBuffer` 只能消费一次；当前实现是"消费当前批次后清空，随后可继续复用同一个实例"
  - 以为 `Set<T>` 永远只是值更新；当前实现里组件不存在时它仍会退化成结构变更

## 入口

- 如果是第一次读这个模块，先看：
  - `src/MiniArch/Core/CommandBuffer.cs`：recording API、bucket compile、created-entity final-state 预计算
  - `src/MiniArch/Core/CommandBuffer.cs` 里的 `Play()`：短路径提交入口
  - `src/MiniArch/Core/FrameDelta.cs`：compiled frame IR、跨 world replay 的数据边界
  - `src/MiniArch/Core/World.cs`：`ReserveDeferredEntity` / `ReleaseReservedEntity` / `Replay(FrameDelta)` 的挂接点
- 如果是修 bug，先看：
- `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：playback/replay 契约、created final state、free-list reuse、并发 recording
- `tests/MiniArch.Tests/Core/CommandBufferParityTests.cs`：共享 `MiniArch vs Arch` benchmark 场景的最终结构摘要是否一致
  - `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`：existing entity 的 structural semantics 是否仍与立即生效 API 对齐
  - `tests/MiniArch.Tests/Core/QueryTests.cs`：batch replay 后 query 可见性和快照失效
- `benchmarks/MiniArch.Benchmarks/CommandBufferSharedScenarios.cs`：共享结构命令脚本和跨引擎 parity helper
- 如果是加功能，先看：
  - `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`：entity id/version/free-list/hierarchy 的底层契约
  - `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`：`record + play` 性能入口

## 坑点

- 历史上容易出问题的地方：
  - 把 `add/set/remove` 当成完全可交换操作；existing entity 和 created entity 的归约规则并不相同
  - 忘记释放同帧 `create + destroy` 的 reserved entity，导致 id 被永远占用
  - replay 期间逐条触发 query layout 失效，导致额外开销和观察窗口不稳定
  - 为了优化 `Play()` 而在 `Playback()` compile 阶段提前污染 source world 的组件注册；这会破坏"编译阶段不改 world"的边界
- 容易误判的地方：
  - 以为 `destroy` 放最后就足够，实际上 created entity 的 final-state 预计算同样关键
  - 以为 recording 并发问题只在 shard merge；实际上 entity reservation 和 component registration 也需要保护
- 改这里时要特别小心：
  - 当前 `World.Set<T>` 在组件不存在时会走"补组件 + 迁移"路径，command buffer 的 `Set` 语义必须与它兼容
  - `ReleaseReservedEntity` 会提升 version 并归还 free-list；如果漏掉 version 递增，stale handle 会重新变活
  - 跨 world replay 现在依赖"目标 world 与源 world 从同一初始态开始，并按相同 frame 顺序推进"；如果这个前提破坏，replay 会在 reservation 对齐时失败

## Phase 1 Implementation Results: 0-GC Optimization

### GC Measurement Results (Release mode)

| Scenario | Gen0 | Gen1 | Gen2 |
|----------|------|------|------|
| Set 100x1000 (reuse buffer) | 0 | 0 | 0 |
| Set 100x1000 (new buffer each) | 0 | 0 | 0 |
| Create 100 with 2 comps | 0 | 0 | 0 |
| Destroy 50 | 0 | 0 | 0 |
| Record 100x1000 (no play) | 0 | 0 | 0 |
| First-time Set 100 (cold cache) | 0 | 0 | 0 |
| Create 1000 with 2 comps | 0 | 0 | 0 |
| Set 10x10000 (reuse buffer) | 0 | 0 | 0 |

- Set 10x10000 allocated 10.3MB (boxing of value types), but zero GC collections

### Optimizations Implemented

1. **ComponentTypeCache\<T\> added to CommandBuffer**: `GetComponentTypeId<T>()` method with `AggressiveInlining`. After first call per T per World, reads one published immutable cache entry and compares its registry. Eliminates `GetOrCreate<T>()` concurrent dict lookup on every recording call while avoiding registry/id split-field cache races.

2. **Dead code removed**: `_componentTypeCacheScratch` field, `ResolveComponentTypeById` method, and its `Compile()` usage.

3. **ResolveComponentTypeInfo simplified**: `componentType = default` on failure instead of `(ComponentType)(-1)`.

4. **ArrayPool for ToCompiledEntity()**: Rents `CompiledComponentValue[]` and `ComponentType[]` from ArrayPool for sorting, returns in finally block. Still allocates exact-sized output arrays for `Signature.CreateNormalized` (takes ownership) and `CompiledCreatedEntity` (stores array).

5. **World.Replay(FrameDelta) optimized**: Removed `_compiledReplayComponentTypeScratch` dictionary entirely from this path. Now calls typed column writers directly via `ComponentWriterCache` since ComponentType is always valid from recording.

### Key Design Decisions

- **Recording-time ComponentTypeCache\<T\> chosen over §5.3 "compile-time only lookup"** because:
  - After first call it's essentially free (2 static field reads)
  - The componentTypeId is needed at recording time for `EntityComponentKey(int)`
  - Avoids redundant compile-time dictionary lookups

- **ArrayPool used instead of pre-allocated scratch arrays** because created entity component counts are variable and unpredictable

- **Play() path achieves true 0-GC** because ComponentType is always valid from recording

### Build/Test Status

- 0 warnings, 0 errors
- 160/160 tests pass

## Phase 2: Byte-based Boxing Elimination

### 已实施：byte-based untyped storage

用 `byte[]` + `Unsafe.WriteUnaligned` 替代 `object?` 装箱，在录制和 replay 热路径上消除所有值类型装箱。

### 架构变化

- **`ComponentWriterCache`**（`src/MiniArch/Core/ComponentWriterCache.cs`），现已公开：
  - `ColumnWriterDelegate(Array column, int row, byte* source)` — per-type 缓存 delegate，执行 `((T[])column)[row] = Unsafe.Read<T>(source)`
  - `ComponentReaderDelegate(void* destination, byte* source)` — per-type 缓存 delegate
  - `ReadBoxed(Type, byte[], int)` — 冷路径：用 `RuntimeHelpers.GetUninitializedObject` + `GCHandle.Alloc(pinned)` + reader 从 bytes 反序列化 boxed object
  - `GetSize(Type)` / `GetColumnWriter(Type)` / `GetReader(Type)` — 均通过 `ConcurrentDictionary` + `MakeGenericMethod` 缓存

- **`CommandBufferShard`**（`CommandBufferShard.cs`）：
  - 新增 `byte[] _data` + `_dataLength` + `AllocateData(int size)` 方法
  - `Adds/Sets` 类型从 `List<RecordedComponentCommand>` 改为 `List<RecordedRawCommand>`

- **`CommandBuffer` 录制**（`CommandBuffer.cs`）：
  - `Add<T>/Set<T>` 通过 `Unsafe.WriteUnaligned(ref shard.Data[offset], component)` 直接写入 shard byte buffer — 零装箱
  - `Compile()` 中 shard data 合并到 `FrameDelta.Data`，offset 重映射
  - `CompiledRawComponentValue(Data, DataOffset, DataSize)` — 携带 byte[] 引用

- **`World.Replay(FrameDelta)`**（`World.cs`）：
  - 新增 `ApplyRawAddOrSet` / `WriteComponentFromBytes` / `MoveEntityFromBytes` — 从 bytes 通过 `ComponentWriterCache.ColumnWriterDelegate` 直接写入 chunk typed column
  - `MaterializeReservedEntity` 接受 `CompiledRawComponentValue[]` + `byte[] batchData`

- **项目配置**：`MiniArch.csproj` 添加 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`

### 数据流

- `CommandBuffer records → FrameDelta (raw bytes) → World.Replay(FrameDelta)`
- `Add<T>` → `Unsafe.WriteUnaligned` 写入 shard byte[] → `RecordedRawCommand(entity, id, offset, size)` → compile 合并 bytes → replay 通过 typed delegate 写入 chunk column
- `Play()` 和 `Playback()+Replay()` 现在分配相似：两者都走 raw bytes 路径，不再有装箱差异

### 验证状态

- 0 warnings, 0 errors
- 160/160 tests pass
- 所有热路径不再装箱

## 关联模块

- `kb-core-ecs.md`：当前 world / archetype / chunk / query 的运行时边界
- `kb-test-workflow.md`：后续该怎么补行为测试和 benchmark
- `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：command buffer 专属行为网
- `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`：实体生命周期、id/version、chunk 顺序
- `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`：`Add/Set/Remove` 当前契约
- `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`：record/playback/replay benchmark 入口
- `benchmarks/MiniArch.Benchmarks/CommandBufferSharedScenarios.cs`：共享结构命令 benchmark 场景与 parity helper
