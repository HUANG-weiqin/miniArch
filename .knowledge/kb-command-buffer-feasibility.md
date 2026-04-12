---
title: Command Buffer Runtime
module: MiniArch.Core CommandBuffer
description: Implemented command buffer model, fixed replay order, recording concurrency scope, and validation notes
updated: 2026-04-12
---
# Command Buffer Runtime

## 这个模块是干什么的

- 这个模块负责：
  - 提供 `CommandBuffer` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
  - 说明 `Playback()` 编译后的固定顺序：`create -> link/unlink -> add -> set -> remove -> destroy`
  - 提供 `Play()` 短路径，在不物化 `FrameCommands` 的情况下直接把同样的编译结果提交给 owning world
  - 说明 `World.Replay(in FrameCommands)` 的 batch mutation 与 query 可见性边界
  - 提供 rewind 配套入口：`World.ReplayWithReverse(...)` 在 replay 前捕获回退信息，`CommandBuffer.PlayWithReverse()` 把 `Playback()+ReplayWithReverse(...)` 串成一步，`World.Rewind(...)` 按 reverse frame 回退
  - 说明 `ReverseFrameCommands` 的定位：它记录的是把 world 从“当前 frame 已提交后的状态”退回到“提交前公开可观察状态”所需的最小反向命令集，并额外携带 reserved entity 轨迹信息，让 rewind 后同一帧可以再次成功 replay
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
  - `src/MiniArch/Core/FrameCommands.cs`：编译后的 frame IR，可保留并重放到同步 world
  - `src/MiniArch/Core/World.cs`：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(in FrameCommands)`、boxed structural mutation
- 数据流 / 控制流：
  - 工作线程通过 `CommandBuffer` 只记录命令；`Create()` 会立刻从 world 预留真实 `Entity`
  - `Playback()` 和 `Play()` 共享同一套合并/归约逻辑，但 compile 现在改成“按 shard 直接归约到最终批次”，不再先把所有 shard 扁平化成中间大 `List`
  - 对同帧 newly-created entity，`Playback()` 直接预计算 final signature 和 final component payload
  - 同帧 `create + destroy` 不会 materialize 到 world，而是放进 `ReleasedEntities`，在 replay 时回收到 free-list 并提升 version
  - `Play()` 走 owning-world 专用 compiled batch，不再先物化 `FrameCommands`；created entity 的 internal batch 会尽量复用 compile 阶段结果，减少 `ToArray` / `ToList` / LINQ 分配
  - `Replay()` 先 materialize surviving creates，再执行 `link/unlink -> add -> set -> remove -> destroy`
  - `ReplayWithReverse(...)` 先根据 replay 前的 live world 捕获 `ReverseFrameCommands`，再执行正常 `Replay(...)`
  - reverse capture 除了旧组件值、旧 parent-child 和 destroyed subtree，还会把本帧 `FrameCommands.ReservedEntities` 一并带进 reverse frame；`Rewind(...)` 末尾会恢复这些 reserved handles 的 reservation 轨迹，而不是只恢复公开组件状态
  - `Rewind(...)` 的安全顺序是：先恢复被 destroy 的旧实体、旧组件值和旧 hierarchy，再销毁本次 replay 期间新建且当前仍存活的实体，最后恢复 link/unlink、add/set/remove 带来的公开状态；这样可避免 replay 期间新建的父节点在销毁时级联误删刚恢复出来的旧实体
  - `Replay()` 期间抑制逐条 query layout 发布，整批结束后只发布一次
- 和其他模块的交互方式：
  - 依赖 `World` 的 entity version / free-list / archetype mutation 基础设施
  - hierarchy 仍由 `HierarchyTable` side-table 持有，command buffer 只决定 replay 顺序
  - query 仍依赖 `World.QueryLayoutGeneration`，只是 replay 期间改为 batch publish
- 验证依赖 `tests/MiniArch.Tests/Core/CommandBufferTests.cs`，并辅以 lifecycle / structural-change / query 回归测试
- 与 `Arch` 的对比 benchmark 当前依赖 `CommandBufferSharedScenarios` 先验证共享结构命令场景的 parity，再跑 `record + play`

## 决策

- 当前已落地的方向是“多线程 `recording` + 单线程 `replay`”；`World` 本身仍不是并发写安全。
- 当不需要保留 frame 或跨 world replay 时，应优先用 `Play()`；它复用同样语义，但省掉 `FrameCommands` 物化分配。
- `ReverseFrameCommands` 不是通用快照，也不承诺恢复内部缓存或历史中间态；它只保证 `IsAlive`、组件值、hierarchy parent-child、以及 query 枚举结果这类公开可观察 world 状态回到 replay 前，并只额外恢复 replay reservation 对齐所需的 reserved entity 轨迹，不扩展成“完全 internal state 镜像回滚”。
- rewind 的使用模型是相邻帧、栈式 `LIFO` 回退；测试覆盖的是连续 `ReplayWithReverse(...)` 后按压栈逆序 `Rewind(...)`，不把它当成任意乱序撤销系统。
- 端到端测试现在还覆盖“回到中间历史点后，按原后续 frame 序列重新 replay，最终公开可观察状态一致”；这锁定了 rewind 不只要能回去，还要能沿原历史重新走回同一个结果。
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
- `FrameCommands` 是可保留的 frame 数据；只要目标 world 和源 world 保持同步初始态与同步回放顺序，就可以顺序 replay 到另一个 world。
- `ReverseFrameCommands` 只对捕获它的同一个 world 实例有效；它依赖 capture 时读取到的旧组件值、旧 hierarchy、被销毁子树内容，以及同一帧 reserved entity 的 reservation 对齐信息。
- reservation rollback 的当前目标不是恢复所有内部数据结构细节，而是保证 `ReplayWithReverse -> Rewind -> ReplayWithReverse` 在 created / transient frame 上仍能拿回同一批 deferred handles 并再次成功提交。
- hierarchy destroy 的回退语义已经落地为：销毁旧子树时会捕获整个 existing subtree 的组件和 parent；如果同一帧里挂到该子树上的新建节点随后随级联销毁一起消失，`Rewind(...)` 不会把这些新建节点恢复出来。
- query layout generation 在 replay 期间被抑制，整批 replay 结束后只递增一次。
- 组件类型注册仍不是自由并发写安全；当前 `CommandBuffer` 通过内部锁串行调用 `ComponentRegistry.GetOrCreate<T>()`，把并发风险限制在 recording 层内部。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 对 world 的“延迟结构变更日志 + 可复制到同步 world 的批量提交器”，不是线程安全 world
- 这个模块里最重要的抽象是：
  - `reserved entity handle`
  - `operation bucket`
  - `FrameCommands`
- 常见误解：
  - 以为 `command buffer` 等于 world 从此支持多线程写
  - 以为 `Playback()` 已经把 world 改了；实际上真正 mutation 发生在 `Replay()`
  - 以为 `Set<T>` 永远只是值更新；当前实现里组件不存在时它仍会退化成结构变更
  - 以为 rewind 会恢复所有内部实现细节；当前契约只覆盖公开可观察状态，不覆盖 query cache 这类内部中间态

## 入口

- 如果是第一次读这个模块，先看：
  - `src/MiniArch/Core/CommandBuffer.cs`：recording API、bucket compile、created-entity final-state 预计算
  - `src/MiniArch/Core/CommandBuffer.cs` 里的 `Play()`：短路径提交入口
  - `src/MiniArch/Core/FrameCommands.cs`：compiled frame IR、跨 world replay 的数据边界
  - `src/MiniArch/Core/World.cs`：`ReserveDeferredEntity` / `ReleaseReservedEntity` / `Replay()` 的挂接点
- 如果是修 bug，先看：
- `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：playback/replay 契约、created final state、free-list reuse、并发 recording
- `tests/MiniArch.Tests/Core/CommandBufferParityTests.cs`：共享 `MiniArch vs Arch` benchmark 场景的最终结构摘要是否一致
  - `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：`ReplayWithReverse(...)` / `PlayWithReverse()` / `Rewind(...)`、LIFO 多帧回退、destroy subtree 回退，以及 `same-frame create+destroy replay-after-rewind` / `create-survivor replay-after-rewind`
  - `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`：existing entity 的 structural semantics 是否仍与立即生效 API 对齐
  - `tests/MiniArch.Tests/Core/QueryTests.cs`：batch replay 后 query 可见性和快照失效
- `benchmarks/MiniArch.Benchmarks/CommandBufferSharedScenarios.cs`：共享结构命令脚本和跨引擎 parity helper
- 如果是加功能，先看：
  - `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`：entity id/version/free-list/hierarchy 的底层契约
  - `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`：record/playback/replay 的性能入口

## 坑点

- 历史上容易出问题的地方：
  - 把 `add/set/remove` 当成完全可交换操作；existing entity 和 created entity 的归约规则并不相同
  - 忘记释放同帧 `create + destroy` 的 reserved entity，导致 id 被永远占用
  - replay 期间逐条触发 query layout 失效，导致额外开销和观察窗口不稳定
  - 为了优化 `Play()` 而在 `Playback()` compile 阶段提前污染 source world 的组件注册；这会破坏“`Playback()` 不改 world”的边界
  - 把 rewind 当成通用 undo；当前 reverse frame 是按一次 replay 捕获并按 LIFO 使用的，乱序回退不在契约内
  - 以为只恢复公开组件状态就足够；created/transient frame 还需要恢复 reserved entity 轨迹，否则第二次 `ReplayWithReverse(...)` 会在 reservation 对齐时失败
  - 在 `Rewind(...)` 里先销毁 replay 期间新建的实体，再恢复旧 hierarchy；如果这些新实体里有临时父节点，级联销毁可能误删刚恢复出来的旧实体
  - 误把“恢复 destroyed subtree”理解成“恢复这一帧里临时创建后又被级联销毁的新节点”；当前实现只恢复 replay 前已存在的那部分子树
- 容易误判的地方：
  - 以为 `destroy` 放最后就足够，实际上 created entity 的 final-state 预计算同样关键
  - 以为 recording 并发问题只在 shard merge；实际上 entity reservation 和 component registration 也需要保护
- 改这里时要特别小心：
  - 当前 `World.Set<T>` 在组件不存在时会走“补组件 + 迁移”路径，command buffer 的 `Set` 语义必须与它兼容
  - `ReleaseReservedEntity` 会提升 version 并归还 free-list；如果漏掉 version 递增，stale handle 会重新变活
  - 跨 world replay 现在依赖“目标 world 与源 world 从同一初始态开始，并按相同 frame 顺序推进”；如果这个前提破坏，replay 会在 reservation 对齐时失败

## 关联模块

- `kb-core-ecs.md`：当前 world / archetype / chunk / query 的运行时边界
- `kb-test-workflow.md`：后续该怎么补行为测试和 benchmark
- `tests/MiniArch.Tests/Core/CommandBufferTests.cs`：command buffer 专属行为网
- `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`：实体生命周期、id/version、chunk 顺序
- `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`：`Add/Set/Remove` 当前契约
- `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`：record/playback/replay benchmark 入口
- `benchmarks/MiniArch.Benchmarks/CommandBufferSharedScenarios.cs`：共享结构命令 benchmark 场景与 parity helper
