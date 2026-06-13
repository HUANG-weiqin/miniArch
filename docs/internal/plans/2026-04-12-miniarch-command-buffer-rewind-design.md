# MiniArch Command Buffer Frame Rewind Design

## 结论

- 推荐采用“正向 `FrameCommands` + 独立 `ReverseFrameCommands`”方案，而不是把回退信息直接塞进现有 `FrameCommands`，也不采用整 world snapshot 回滚。
- `CommandBuffer.cs` 继续负责 recording 和 compile 正向命令，不直接承担回退状态存储；回退日志由 `World.cs` 在真正执行 `Replay()` 或 `Play()` 时生成，因为只有运行时 world 才知道 existing entity 的旧组件值、旧 hierarchy 关系和 destroy 前子树状态。
- `FrameCommands.cs` 新增独立公开状态 `ReverseFrameCommands`，它表示“刚刚成功应用的一帧如何逆向撤销”，与现有 `FrameCommands` 对称，但语义是 undo log，不要求可跨任意不同步 world 自由重放。
- `World.cs` 增加成对 API：正向应用返回 reverse，reverse 再交给 world 执行 rewind。推荐主入口形态为：
  - `ReverseFrameCommands ReplayWithReverse(in FrameCommands frameCommands)`
  - `ReverseFrameCommands PlayWithReverse(CommandBuffer buffer)` 或 `ReverseFrameCommands Play(in CommandBuffer.CompiledCommandBatch compiled)` 的内部变体
  - `void Rewind(in ReverseFrameCommands reverseFrame)`
- `HierarchyTable.cs` 不保存完整历史栈；它只在 `World` 采集 undo log 时提供当前 parent、children 和 destroy 子树信息。这样能保持 hierarchy 仍然是 runtime side table，不演变成事件溯源系统。
- rewind 的公开状态应当被定义为“只对生成它的目标 world 的当前版本链有效”，不是通用 snapshot，也不是跨 world 同步协议。

## 目标

- 为 command buffer 增加 frame 级撤销能力：一帧成功 `Replay()` 或 `Play()` 后，可以把这一帧对 world 造成的结构变化和 hierarchy 变化完整回退。
- 保持现有固定顺序语义：正向仍是 `create -> link/unlink -> add -> set -> remove -> destroy`，回退则执行其严格逆序语义。
- 保持 `CommandBuffer` 的 recording 并发边界不变；rewind 仍然只在单线程 world mutation 阶段发生。
- 为后续“时间回退 / 逐帧撤销 / 调试回放”提供可保留的 frame 级 reverse 状态。

## 非目标

- 不把 `World` 扩展成并发写安全。
- 不把 rewind 做成任意历史版本查询系统。
- 不把 reverse 状态设计成跨 world 通用同步格式。
- 不要求支持“外部任意改动后仍可安全 rewind 旧 reverse frame”；reverse 只保证在栈式、相邻帧场景中可靠。
- 不用整 world snapshot 替代 command buffer frame rewind。

## 现状与缺口

### 已有基础

- `CommandBuffer.cs` 已能把 recording 编译为固定桶顺序，并对 same-frame created entity 预计算 final state。
- `FrameCommands.cs` 已是稳定的正向 frame IR，公开了 `CreatedEntities / LinkCommands / AddCommands / SetCommands / RemoveCommands / DestroyedEntities / ReleasedEntities`。
- `World.cs` 已有 `Replay(in FrameCommands)` 和 `Replay(CompiledCommandBatch)`，并且 batch 期间会抑制 query layout 的逐条发布。
- `HierarchyTable.cs` 已有 `Link / Unlink / TryGetParent / GetChildren / CollectDestroySubtree / RemoveDestroyed`，足以支持 hierarchy undo 所需的当前态查询。
- `tests/MiniArch.Tests/Core/CommandBufferTests.cs` 已覆盖 playback/replay、same-frame create+destroy 消除、跨 world replay、并发 recording、`Play()` 等核心正向语义。

### 当前缺口

- `FrameCommands` 只表达“要做什么”，不表达“做之前 world 是什么样”。
- `World.Replay()` 在执行 `Add/Set/Remove/Destroy/Link/Unlink` 前没有采集旧值，因此无法生成精确回退信息。
- existing entity 的组件回退需要旧 payload；destroy 回退需要实体销毁前的完整组件集与 hierarchy 关系；这些信息 compile 阶段无法仅靠 `CommandBuffer` 得到。
- `HierarchyTable` 当前只有当前关系表，没有 frame 级 inverse log。
- 测试层目前没有“apply -> rewind -> state restored”这一类断言网。

## 方案对比

### 方案 A：把 reverse 信息直接并入 `FrameCommands`

- 做法：在 `Playback()` 时直接生成正反两份命令，或把 inverse payload 混在 `FrameCommands` 内部。
- 优点：
- 一个对象可同时承载 forward/reverse。
- `Play()` 和 `Playback()+Replay()` 的表面 API 看起来更统一。
- 缺点：
- compile 阶段拿不到 existing entity 的旧组件值、旧 parent、destroy 前子树完整状态，最终还是要在 `World` 补采集。
- 破坏 `FrameCommands` 当前“world-agnostic forward IR”定位，正向和逆向生命周期耦合过重。
- 会让只需要跨 world 正向 replay 的场景白白背负 reverse 内存成本。

### 方案 B：独立 `ReverseFrameCommands` / undo log

- 做法：`Playback()` 仍只产出正向 `FrameCommands`；`World` 在成功应用正向帧时同步采集 inverse 信息，返回独立 `ReverseFrameCommands`。
- 优点：
- 与现有架构最对齐：compile 负责“计划”，world 负责“执行时观测”。
- 不污染现有跨 world replay 语义；需要 rewind 的调用方才持有 reverse 状态。
- hierarchy、destroy、existing component old value 都能在单一执行点统一采集。
- 缺点：
- 需要新增一套 reverse IR 和 world 执行路径。
- `Play()` 与 `Replay()` 都要接入同一套 undo 采集逻辑。

### 方案 C：每帧保存 snapshot/diff，再整帧回滚

- 做法：每次应用 command buffer 前保存 world snapshot，rewind 时整体恢复。
- 优点：
- 概念简单，正确性直观。
- 不必为每类命令单独设计 inverse op。
- 缺点：
- 成本过高，和 command buffer 的轻量增量提交模型冲突。
- 会把 hierarchy、entity metadata、chunk 数据全部复制，明显偏离本项目“简单高效”的目标。
- 很难复用现有 `CommandBuffer.cs / FrameCommands.cs` 的结构优势。

## 推荐方案

- 采用方案 B。
- 原因：回退所需的关键信息天然属于 `World` 执行期，而不属于 `CommandBuffer` compile 期；把 reverse 状态独立出来，能最小化对现有 forward IR 的扰动。
- 设计原则：
- forward 与 reverse 分离：`FrameCommands` 仍代表“可保留的正向帧”，`ReverseFrameCommands` 代表“本 world 刚执行完这一帧后可立即撤销的逆向帧”。
- reverse 只记录最小必要旧状态，不做通用 snapshot。
- reverse 生成必须和正向 replay 在同一事务边界内：正向成功，才返回完整 reverse；正向抛异常，不应返回半成品 reverse。

## 数据结构建议

### 1. 独立公开状态：`ReverseFrameCommands`

- 位置：`src/MiniArch/Core/FrameCommands.cs`
- 形态：`public readonly struct ReverseFrameCommands`
- 目标：让调用方可以把“上一帧如何撤销”作为显式值保存，而不是把 undo 栈藏进 `World` 私有状态。

建议公开字段：

- `IReadOnlyList<Entity> CreatedEntities`
  - 正向帧里新创建并最终存活的实体；rewind 时应 destroy 它们。
- `IReadOnlyList<ReverseDestroyedEntity> RestoredEntities`
  - 正向帧里被 destroy 的 existing entity；rewind 时按其销毁前完整状态重建。
- `IReadOnlyList<ReverseHierarchyCommand> HierarchyCommands`
  - 回退 link/unlink 所需的旧关系操作。
- `IReadOnlyList<ReverseComponentCommand> ComponentCommands`
  - 对 existing entity 的 add/set/remove 回退。
- `IReadOnlyList<Entity> ReacquiredReservedEntities`
  - 对应正向帧中的 `ReleasedEntities`；rewind 时需要重新预留这些 same-frame create+destroy 掉的 slot，使 frame 前后的 free-list/version 状态一致。

### 2. 建议的 reverse 记录类型

建议新增：

- `public readonly record struct ReverseDestroyedEntity(Entity Entity, IReadOnlyList<FrameComponentValue> Components, Entity Parent)`
  - 表示一个被正向 destroy 掉、rewind 时要恢复的实体。
  - `Components` 为销毁前完整组件快照。
  - `Parent` 为销毁前直接父节点；子关系不单独存这里，子树恢复顺序由 `RestoredEntities` 列表顺序保证。
- `public readonly record struct ReverseHierarchyCommand(Entity Child, bool HadParent, Entity Parent)`
  - `HadParent=false` 表示 rewind 后 child 应处于 unlinked。
  - `HadParent=true` 表示 rewind 后 child 应链接回指定 parent。
- `public readonly record struct ReverseComponentCommand(Entity Entity, Type ComponentType, ReverseComponentOp Op, object? Value)`
  - `RestoreRemoved`: 正向是 `Remove`，回退时重新补回旧值。
  - `RestorePreviousValue`: 正向是 `Set` 或命中了已有组件的 `Add`，回退时写回旧值。
  - `RemoveAdded`: 正向是给 existing entity 新增组件，回退时把它删掉。
- `public enum ReverseComponentOp`
  - `RemoveAdded`
  - `RestorePreviousValue`
  - `RestoreRemoved`

### 3. 内部构建状态

- `World.cs` 在 replay 过程中维护内部 `ReverseFrameBuilder`，逐类采集：
- existing entity 旧组件值
- hierarchy 旧 parent
- destroy 前子树的完整实体快照
- same-frame released entity 的 reservation 恢复信息
- 最终再一次性物化为 `ReverseFrameCommands`。

### 4. 为什么不复用 `FrameCommands`

- reverse 命令的执行前提和正向不同：
- 正向 `CreatedEntities` 是“materialize reserved entity”。
- 逆向恢复 destroy 则是“用旧 id/version 和旧组件集重建被删实体”。
- hierarchy undo 需要表达“child 之前是否有 parent”，而不是仅仅表达 link 或 unlink。
- same-frame create+destroy 的回退关注的是 free-list/version 对齐，不是普通 create/destroy 对称。

## 公开状态定义

### 对外契约

- `FrameCommands`
  - 继续保持现义：world-agnostic 的正向帧，可在同步前提下 replay 到另一个 world。
- `ReverseFrameCommands`
  - 新语义：target-world-bound 的逆向帧，只保证能撤销它所对应的那次成功应用。
- `ReverseFrameCommands` 必须满足：
  - 不可变。
  - 不暴露可变内部数组。
  - 允许被外部保存为栈，用于多帧连续 rewind。
  - 明确文档声明：如果 world 在生成 reverse 后又发生了非配对的外部 mutation，再执行该 reverse，行为应视为未定义或抛异常。

### 建议 API

- `World.ReplayWithReverse(in FrameCommands frameCommands)`
  - 应用正向帧并返回对应 reverse。
- `World.Rewind(in ReverseFrameCommands reverseFrame)`
  - 执行回退。
- `CommandBuffer.Playback()`
  - 保持不变，只产出 forward。
- `CommandBuffer.Play()`
  - 可保持原有无返回版本。
- 额外新增：`CommandBuffer.PlayWithReverse()`
  - 直接在 owning world 上执行并返回 reverse。

这样可以保持：

- 原有调用方零破坏继续使用 `Playback()` / `Play()`。
- 需要 rewind 的调用方显式选择 `ReplayWithReverse()` / `PlayWithReverse()`。

## 执行流程建议

### 正向 `ReplayWithReverse()`

1. 校验 reservation，与现有 `Replay()` 一致。
2. 初始化 `ReverseFrameBuilder`。
3. 处理 `ReleasedEntities` 前，先把这些 reserved entity 记录到 reverse 的 `ReacquiredReservedEntities`。
4. materialize `CreatedEntities` 时，把真正存活的新实体加入 reverse 的 `CreatedEntities`。
5. 执行 `Link/Unlink` 前，先查询 child 当前 parent，生成对应 `ReverseHierarchyCommand`。
6. 执行 `Add/Set/Remove` 前，对 existing entity 读取旧组件存在性与旧值，生成对应 `ReverseComponentCommand`。
7. 执行 `Destroy` 前，调用世界侧辅助函数收集待销毁子树中每个实体的完整组件集与旧 parent，生成 `RestoredEntities`。
8. 正向全部成功后结束 deferred layout update，并返回不可变 `ReverseFrameCommands`。

### 逆向 `Rewind()`

建议逆向顺序：

1. 重新预留 `ReacquiredReservedEntities`，把 same-frame create+destroy 的 free-list/version 状态恢复到正向执行前。
2. 恢复 `RestoredEntities`，顺序应为 parent-first materialize，再按记录恢复 parent 关系。
3. 执行 `ComponentCommands`，把 existing entity 组件恢复到旧状态。
4. 执行 `HierarchyCommands`，恢复未被 destroy 覆盖的旧 parent 关系。
5. destroy `CreatedEntities`，撤销正向帧中新创建并存活下来的实体。

关键点：

- reverse 不是简单把正向顺序倒过来逐条执行，而是按“恢复旧世界状态”分组执行。
- destroy 的逆向恢复必须先于对 surviving existing entity 的 hierarchy/component 修补完成，否则 child/parent 句柄可能尚未 materialize。

## 各文件职责对齐

### `src/MiniArch/Core/CommandBuffer.cs`

- 保持 recording API 不变。
- compile 仍只生成 forward batch。
- 新增 `PlayWithReverse()` 时，直接复用 compile 结果并调用 `World` 的带 reverse 执行入口。

### `src/MiniArch/Core/FrameCommands.cs`

- 保留现有 `FrameCommands` 及其 forward record structs。
- 新增 `ReverseFrameCommands` 和 reverse record structs。
- 继续采用 immutable state object 封装数组，避免向外暴露可变集合。

### `src/MiniArch/Core/World.cs`

- 新增带 reverse 的 apply/rewind 入口。
- 把 undo 采集逻辑放在这里，而不是挪进 `CommandBuffer`。
- 新增少量内部 helper：
  - 采集 entity 完整组件快照
  - 采集 hierarchy 旧关系
  - 以既定 `Entity` 重建被 destroy 的实体
  - 重新预留指定 reserved entity

### `src/MiniArch/Core/HierarchyTable.cs`

- 保持 side-table 定位。
- 只提供当前关系读取与子树收集，不承担历史日志职责。
- 若需要，可补一个轻量只读入口，如“读取 child 当前 parent（允许无 parent）”，供 `World` 生成 reverse hierarchy 命令。

### 相关测试文件

- `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
  - 新增 apply->rewind 基础路径、复杂随机脚本、`PlayWithReverse()` 与 `Playback()+ReplayWithReverse()` 等价。
- `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`
  - 新增 existing entity 的 add/set/remove 回退后位置与组件值恢复断言。
- `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`
  - 新增 create/destroy/free-list/version/hierarchy 回退语义。
- `tests/MiniArch.Tests/Core/QueryTests.cs`
  - 新增 rewind 后 query 可见性和 snapshot 失效/刷新验证。

## 测试策略

### 1. 命令级确定性测试

- existing entity `Add` 后 rewind，组件消失且 archetype 回到原位。
- existing entity `Set` 后 rewind，值恢复且不发生额外迁移。
- existing entity `Remove` 后 rewind，组件和值恢复。
- `Link/Unlink` 后 rewind，parent/children 恢复。
- `Create` 后 rewind，实体不再 alive。
- `Destroy` 后 rewind，实体及其 hierarchy 关系恢复。

### 2. 复杂帧测试

- 同帧混合 `Create/Add/Set/Remove/Link/Unlink/Destroy` 后 rewind，source world 回到 apply 前状态。
- same-frame `create + destroy` 的 frame 在 rewind 后，应恢复 free-list/version，而不是留下多余 live entity。
- child-first cascade destroy 的正向帧在 rewind 后，应恢复整棵子树和原 parent-child 关系。

### 3. 等价性测试

- `PlayWithReverse()` 的正向结果与 `Playback()+ReplayWithReverse()` 一致。
- 对同一随机脚本，`apply -> rewind` 后的 world 摘要应与 apply 前完全一致：
  - live entity 集合
  - entity version
  - archetype signature
  - 选定组件值
  - hierarchy links

### 4. query 可见性测试

- `ReplayWithReverse()` 与 `Rewind()` 都应保持 batch publish：每次结束后 query 看到的是稳定终态，不暴露中间态。
- rewind 后旧 query cache 不应读到过期 archetype/chunk 快照。

### 5. 失败路径测试

- reverse 只允许消费一次，避免重复 rewind 同一帧。
- 对不匹配 world 或已被外部 mutation 破坏的 reverse，优先抛出清晰异常，而不是静默写坏状态。

## 风险

- destroy rewind 是最高风险点：它同时涉及 entity version、free-list、组件重建和 hierarchy 子树恢复。
- same-frame `create + destroy` 的回退很容易只恢复 live state，却漏掉 reservation/free-list/version 对齐。
- hierarchy 回退如果只恢复 parent，不恢复 destroy 前子树 materialization 顺序，容易出现 link 到 stale entity 或父先缺失的问题。
- 若 reverse 采集散落在多个分支里，`Replay()` 和 `Play()` 可能发生语义漂移；需要共用一套 apply-with-reverse 内核。
- 公开 reverse 状态若被误解为跨 world 通用格式，会产生错误使用；文档和 API 命名必须明确其 world-bound 属性。
- reverse 日志会增加每帧内存；若后续需要优化，应优先复用 compile/replay 现有缓存和 typed component 读取路径，而不是回退到整 snapshot。

## 落地顺序建议

1. 先在 `tests/MiniArch.Tests/Core/CommandBufferTests.cs` 与 `WorldLifecycleTests.cs` 锁定最小 rewind 契约。
2. 在 `FrameCommands.cs` 定义 `ReverseFrameCommands` 公开状态。
3. 在 `World.cs` 实现 `ReplayWithReverse()` 的核心 undo 采集与 `Rewind()`。
4. 让 `CommandBuffer.cs` 补 `PlayWithReverse()`，确保短路径与长路径共享同一内核。
5. 最后补 query 与复杂脚本回归测试，确认 fixed bucket order 和 hierarchy/destroy 组合帧不回退。
