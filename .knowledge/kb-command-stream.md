---
title: Command Stream Runtime
module: MiniArch.Core CommandStream
description: CommandStream typed-store append-only recorder, compatible with FrameDelta. The per-entity deduplicating CommandBuffer was removed (YAGNI) — CommandStream is now the sole recorder.
updated: 2026-06-26 (移除 CommandBuffer, CommandStream 成为唯一录制器)
---
# Command Stream Runtime

## 这个模块是干什么的

- 提供 `CommandStream` 录制层，把结构变化和 hierarchy 变化先记成延迟命令
- append-only：`Add/Set/Remove` 按组件类型分片记录 typed value，不做录制期去重；同帧冲突命令的净效果由调用方负责
- `Submit()` 消费当前批次后自动清空，允许下一帧复用同一实例
- 约束并发只覆盖 recording，不把 `World` 变成并发写安全对象
- 历史：曾并存 `CommandBuffer`（per-entity 录制期去重的安全默认）。2026-06-26 按 YAGNI 移除——实测无真实消费者依赖去重语义，CommandStream 在所有工作负载上更快（Movement +37%、Attack +33%），双实现是冗余维护包袱。

## 架构

- 核心组成：
  - `src/MiniArch/Core/CommandStream.cs`：append-only recording API + `Submit()` + `Snapshot()` + `Clone()`
  - `src/MiniArch/Core/FrameDelta.cs`：帧快照 IR，可保留并重放到同步 world
  - `src/MiniArch/Core/World.cs`（+ partial 文件）：`ReserveDeferredEntity`、`ReleaseReservedEntity`、`Replay(FrameDelta)`
- 数据流 / 控制流：
  - 工作线程通过 `CommandStream` 只记录命令；`Create()` 立刻从 world 预留真实 `Entity`
  - recording 完成后可选：`Submit()`（直接执行到 world）→ `Snapshot()`（生成自包含 `FrameDelta`）→ `SubmitAndSnapshotAsync()`（并行执行 Submit + BuildDelta）
  - `Clone()`：深拷贝源实体及子树到 pending batch，用于 snapshot/replay 场景
  - `Add/Set` 进入按组件类型分组的 typed store；`Remove/Destroy` 进入 structural log；created entity 的组件录制期分流到 created side table（per-batch 单链表），提交时一次性 materialize；`Snapshot()` 从 typed stores + side table/log 生成 `FrameDelta`

## 关键约束

- `Create()` 仍使用 `World.ReserveDeferredEntity()`，所以生成的 `FrameDelta` 可以保留确定的 entity id/version。
- 组件数据按 typed value 记录，`Submit()` 直接写 typed value，`Snapshot()` 再转成 `FrameDelta` 所需 raw bytes。
- component `Add/Set` 按类型批处理，不承诺与 `Remove/Destroy` 的严格全局追加顺序；同帧冲突命令的净效果由调用方负责。
- created entity 组件在 record 时分流，避免提交时 O(created × commands) 扫描整条日志。
- created materialize 使用小型 archetype cache，避免每个 spawn 反复分配 signature/type array。
- `SubmitAndSnapshotAsync` 使用 FrozenState 双缓冲池（`_spareFrozen`/`_pendingFrozen`），稳态零分配。

## 数据模型

- existing entity 的 `Add/Set/Remove` 进入 per-component-type 的 typed store（`ComponentStore<T>`，flat `T[]` + `Entity[]` + `byte[] kinds`），append-only 不去重
- created entity 的组件写入共享 `byte[] _batchBuf`，通过 per-batch 单链表（`_batchHeads[]` + `BatchedComponent.Next`）归属；materialize 时稳定排序+去重达到 last-wins
- Arena slab allocator：组件值写入 `ArrayPool<byte>` 租来的 `_batchBuf`

## 决策

- `Snapshot()` 用于跨 world 同步或延迟回放；无此需求时优先用 `Submit()`
- `CommandStream.Snapshot()` 首版同步生成 `FrameDelta`；后续可在同一日志形状上增加 async/compile 变体。
- `SubmitAndSnapshotAsync()`：换出 buffer 状态后，主线程 Submit 与后台线程 BuildDelta 并行执行
- 记录期返回真实 `Entity`，但只是 reserved handle（`world.IsAlive(entity)` 仍为 false）
- query layout generation 在 replay 期间被抑制，整批结束后只递增一次
- `ICommandRecorder` 接口存在但仅用于测试抽象层，CommandStream 实现它
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
2. **Cache line 利用率提升**：struct 缩小 30-50%，同样 cache line（64B）装更多条目，遍历时的 cache miss 大幅减少。
3. **内存带宽减少**：高频复制（List.Add、DeepCopyOwnedData）的 per-copy 数据量减少。

**教训：在热路径 struct 中避免持有引用类型字段（即使只是缓存），代价远比看起来大。**

## 认知模型

- `CommandStream`：对 world 的"低成本 typed component stores + created side table + 可编译 FrameDelta"的延迟命令录制器。

## 入口

- 第一次读：`src/MiniArch/Core/CommandStream.cs` → `src/MiniArch/Core/FrameDelta.cs`
- 修 bug：`tests/MiniArch.Tests/Core/CommandStreamTests.cs`

## 坑点

- existing entity 和 created entity 的归约规则不同
- 同帧 `create + destroy` 必须释放 reserved entity，否则 id 被永久占用
- replay 期间逐条触发 query layout 失效会增加额外开销
- existing destroy 必须存完整 `Entity`（含 version），不能只存 id
- **`ResolveTypeInfo` 缓存哨兵值不能用 `IsValid`**（已修复 2026-06-08）：删除 `RuntimeType` 后缓存元组变为 `(ComponentType, int)`，用 `cached.Size > 0` 判断命中。**教训：缓存哨兵不能依赖值域内的合法值。**
- `Clone()` 是完整深拷贝，包含所有 slab 数据，大 buffer 克隆成本高
- `CommandStream` 不做 per-entity/per-component 去重；重复命令的净效果由调用方负责。
- **混合 World.Create（排序）与 CommandStream（未排序）可能产生重复 archetype**：两者创建组件的顺序不同（`World.Create<T>` 用 `public Signature(params ComponentType[])` 构造函数，自动排序去重；CommandStream 用 `Signature.CreateNormalized` 按 ADD 顺序存储，不做排序）。如果同一组件集用两条路径分别创建，`_archetypes` 字典中会出现两个不同键的 Signature，产生两个功能等价但列顺序不同的 archetype。`World.TryGetArchetype` 的集合比较能正确匹配两者。**解决办法：整个项目统一用一种创建路径**。
- **CommandStream 支持对 pending entity 调用 Remove**（2026-06-10 修复）：同批次内通过 `Create()` 或 `Clone()` 创建的实体处于 pending 状态。对其调用 `Remove<T>()` 现在通过 `BatchedComponent.Removed` 标记正确移除组件。`CommandStreamTests.Remove_on_pending_clone_removes_component` 验证。
- **CommandStream 支持创建零组件的空实体**（2026-06-10 修复）：`Create()` 后未调用任何 `Add<T>()` 的实体现在通过 `MaterializeReservedEntity(entity, Array.Empty<>())` 正确 materialize 为无组件实体。`CommandStreamTests.CrossWorld_replay_create_empty_entity` 验证。
- **CommandStream 同批次中 Destroy 后不再影响其他操作**（2026-06-10 修复）：`ApplyAllEntries` 改为两遍处理（pass 1: Create/Add/Set/Remove，pass 2: Destroy），"先 ApplyOps 再 Destroy"顺序。同批次内先 Destroy 后 Add/Set 同一实体现在安全（Add/Set 先执行，然后 Destroy 销毁实体）。对 pending entity，`CancelPendingEntity` 后的条目在 materialize 时通过 `_pendingBatch[id] < 0` 检测跳过。`CommandStreamTests.Fuzz_200_frames_submit_and_verify_entity_count_stability` 不再需要 `destroyedThisFrame` 过滤。
- **CommandStream pending batch 组件归属使用 per-batch 链表**（2026-06-11 修复）：旧实现用 `_batchCompCounts` 前缀和 + 扁平 `_batchComps` 数组，隐式假设每个 batch 的组件在数组中连续。但录制 API 不保证此顺序（例如 `Create(A), Create(B), Add(B,V), Add(A,P)` 时组件按 B→A 顺序写入数组，前缀扫描会将 V 误归给 A）。修复为 per-batch 单链表（`_batchHeads[]` 指向每个 batch 的链表头，`BatchedComponent.Next` 链接节点），彻底消除组件归属错误。`CommitBatchComponent` 不做 last-wins 遍历（O(1) prepend），materialize 时稳定排序+去重达到 last-wins 语义。详见测试 `Interleaved_pending_creates_get_correct_components`、`Remove_pending_component_then_create_another_entity` 等。

- **Submit vs Replay 命令顺序对齐**：`World.ReplayCore`（`World.cs:450`）是 `while (decoder.MoveNext()) switch`，**按字节流时序处理**——所谓"canonical 顺序（Reserve→Release→Create→Link→Unlink→Add→Set→Remove→Destroy）"实际是 `BuildDelta` 分段写入 buffer 的产物，不是 ReplayCore 硬编码。Submit 与 BuildDelta 顺序**完全对齐**（Create→Hierarchy→Ops→Destroy），所有命令组合（含 Link+Set、Link+Destroy、Unlink+Set 等同帧混合）下 Submit 与 Replay 都收敛：
  - CommandStream 的 `ComponentStore<T>.ApplyToWorld` 与 `.EmitToDelta` 都按相同 `_kinds` 数组顺序遍历，Submit 与 Replay 行为一致。
  - 验证：`Submit_on_source_equals_Replay_on_replica_for_safe_patterns` (`FrameDeltaDeterminismTests.cs:55`) 用 `BuildComplexScenario` 覆盖多样命令组合；`Submit_link_and_set_on_same_child_same_frame_converges_with_replay` / `Submit_link_parent_then_destroy_parent_same_frame_converges_with_replay` / `Submit_unlink_then_set_same_frame_converges_with_replay` 等针对性测试（`FrameDeltaDeterminismTests.cs:592` 起）覆盖所有同帧组合。

## CommandStream vs Friflo: Record 阶段瓶颈分析（2026-06-13，历史——移除 CommandBuffer 的依据）

### 分阶段计时数据

**基准测试 A：DenseExisting（纯 mutation，10k 实体，40k ops/iter）**

| Engine | record(us) | submit(us) | total(us) |
|---|---|---|---|
| MiniArch CS | 263 | 703 | 965 |
| Friflo | 169 | 414 | 583 |

- **CS record 是 Friflo 的 1.6x**：flat append，剩 typed store 管理 + entry 流开销
- **CS submit 比 Friflo 慢 1.7x**：遍历 entry stream → switch → 虚方法 `WriteToWorld`

> 历史 CB 数据（已删除的实现）：record 540us（Friflo 3.2x）、submit 293us（Friflo 0.7x）。CB 因 InlineMap 4-slot linear scan + 每条 op 重复 version check 而 record 固定慢 2.5-3.2x。这正是 CommandStream 取代 CommandBuffer 的核心依据。

**基准测试 B：CommandStreamGame.Perf（真实游戏循环，含 query）**

| 场景 | Engine | Ticks/s | record% | apply% |
|---|---|---|---|---|
| Combat | CS | 3,274 | 25.4% | 59.5% |
| Combat | Friflo | 3,384 | 16.6% | 67.5% |
| ParticleStorm | CS | 1,691 | 46.3% | 53.7% |
| ParticleStorm | Friflo | 1,655 | 24.8% | 75.2% |
| HeroLight | CS | 341k | 51.0% | 48.5% |
| HeroLight | Friflo | 354k | 34.1% | 65.4% |

### 关键发现

1. **CS record 固定慢 1.5-1.8x**：差距来自 typed store 管理（`GetOrCreateStore<T>` + resize check）和 entry 流 append。这个差距在 Create-heavy 场景能被 submit 的 batch materialization 收益弥补甚至反超。
2. **CS 在 Create-heavy 场景反超 Friflo**：ParticleStorm（4000 Create/tick）中 CS 的 apply 阶段通过 batch materialization 省了 137us/tick，超过 record 阶段亏的 124us，总时间 591us < Friflo 604us，达到 +2%。

### 优化方向

- **CS record**：合并 `GetOrCreateStore<T>` 和 `AppendEntry` 为一步，预分配 store 数组避免 resize。差距已经较小（1.5x），ROI 有限。

## FrameDelta 决定性与帧同步（2026-06-14）

### 已验证的决定性属性

`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs` 通过 `WorldFingerprint`（投影 stats / archetype 列布局 / per-slot version / 组件值 / hierarchy 到字符串）验证以下性质：

1. **同一 delta 序列重放到 N 个全新 world → 状态完全一致**（含 entity id、version、archetype signature、组件字节、父子关系）。
2. **Submit(源) == Replay(副本)** 在避开 Remove+Add 同组件同帧模式时成立（见前文坑点）。这保证 host 用 Submit、client 用 Replay 的混合模式可收敛。
3. **ID 回收、hierarchy 演化、深克隆、批量 spawn + 剪枝**等场景下决定性稳定。
4. **Safety net**：target world 的 entity id allocator 状态与 source 分叉时，`EnsureReplayReservation` 抛 `InvalidOperationException("out of sync")`，不会静默别名。

### 关键约束：必须从 frame 0 完整重放

`EnsureReplayReservation`（World.EntityLifecycle.cs:418）假设 target 的 free-id 栈和 source 同步。**不能从 delta 序列中间开始 replay**——target 的 id allocator 必须被同样的历史驱动到同样位置。否则要么抛 out-of-sync，要么静默分配到错误的 id。

实践含义：
- **存档/回放**：从空 world 开始，按帧顺序 replay 全部 delta。可工作。
- **Lockstep/rollback**：所有 peer 必须从相同的初始快照开始，按相同顺序应用相同 delta。可工作。
- **断点续传 / 增量同步**：当前不支持。需要 id remap 或显式 checkpoint 机制。

### 帧同步尚缺的二块

1. ~~**序列化**~~ → 已完成。`delta.AsSpan()` + `FrameDelta.Deserialize(ReadOnlySpan<byte>)` 开箱即用。
2. **State checksum**：无需新 API。`WorldSnapshot.Save` 输出的字节流可以直接喂给 SHA256/XXHash64 做决定性 hash——前提是两个 world 走过相同的 delta 历史（lockstep 标准用法）。测试中 `HashWorld(World)` 已实现此模式（`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs`）。**注意**：Save 的字节依赖 archetype 创建顺序和 swap-remove 历史，因此只在"同 delta 序列"场景下稳定；不做"逻辑等价但路径不同"的规范化（YAGNI）。
3. **Divergent peer resync**：`EnsureReplayReservation` 抛异常而非尝试对齐。对抗性 netcode 需要 snapshot diff + 重放补救。

### 次要问题

- **Submit 与 Replay 命令顺序不一致**（已记录在坑点）：纯 lockstep 模式（只用 Replay）不受影响，但禁止同帧内某些 peer 用 Submit、另一些用 Replay。
- Replay 期间 query layout generation 抑制是一次性的，超大 delta 或分片重放可能触发额外失效。

### Merge + id 回收 → 已通过重构根除（2026-06-14，commit `b0acf38`）

**结论：** bug 已不存在。`FrameDelta` 重构为 packed `byte[]` + 时序 op 流后，`Merge` 退化为 15 行 `Array.Copy` 拼接，不再做任何 entity 状态折叠，因此不会丢 op、不会改变时序，跨帧 id 回收自然正确。

**历史背景（保留可追溯）：**

旧 `FrameDelta` 用 9 个 `List<T>` 按 op 类型分段存储，`Merge` 是 200 行的"折叠状态机"（`ProcessCommandsInto` / `RebuildFromState` / `FoldComponent` / `SquashEntityState`）。折叠逻辑遇到**跨帧 entity id 回收**会让 target replay 时 id allocator 与 source 分叉：

- 复现：`Frame1 Create A(0,v1)` → `Frame2 Destroy A → free=[(0,v2)]` → `Frame3 Create B(0,v2) 回收 id=0`。
- 旧 Merge 把 A 折叠成 `Reserve+Release`、把 B 当 fresh create，`ReplayCore` 批处理顺序（全部 Reserve → 全部 Release → 全部 Create）丢失了 `reserve A → release A → reserve B 回收 A 的 id` 的时序依赖，导致抛 `out-of-sync` 异常或静默错误。
- 更阴的变体：纯 `Reserve X` + `Release X` 被 `RebuildFromState` 的 `continue` 整个丢弃，target free list 与 source 不一致，后续帧必然分叉。

**修复方案：** FrameDelta 内部改为单 `byte[] _buffer` + 时序排列的 op（`[1B tag][varint entityId][varint version][payload]`）。`ReplayCore` 改为单趟 `while (MoveNext()) switch`。`Merge` 不再分析语义，只 `Array.Copy` 两个 buffer——时序信息完整保留，跨帧 id 回收按原始顺序被 replay。

**回归测试：** `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs::CB_merge_destroy_recycle_round_trip_is_correct`（`tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs:439`）正中原始复现场景：Create A → Destroy A → Create B（id 回收），三帧 delta `Aggregate(Merge)` 后序列化/反序列化/replay，`AssertIdentical(source, target)` 通过。另覆盖 `CB_merged_delta_round_trip_produces_identical_world`、`CS_merged_delta_round_trip_produces_identical_world`、`Cross_CB_and_CS_merged_delta_round_trip_is_correct`。

### `WorldFingerprint` 模式（已删除）

早期测试用字符串 fingerprint 做状态对比，已替换为 `WorldSnapshot.Save` → SHA256。原因：
- 字符串太长（KB 级），失败时不可读
- 与生产路径脱节——无法复用
- Hash 模式直接复用 Save 序列化路径，零新 API，未来还可以喂给网络同步用

hash 失败时只在 message 里打印 stats（entityCount / archetypeCount / hash 前 16 位）做定位起点，详细差异用调试器查。

## FrameDelta 序列化压缩方案（2026-06-14）

### 目标

把 FrameDelta 序列化字节做到**贴近物理下界**。lockstep 场景对带宽敏感（每帧每个 peer 都要广播），YAGNI 原则下只取收益/复杂度比高的技巧。

### 物理下界估算（基线场景）

典型动作游戏一帧：`spawn 3 个怪(Pos+Vel+Health 共 20B payload)、set 50 个 Pos(8B)、destroy 5 个`。

| 命令 | 不可压缩信息 | 物理下界 |
|---|---|---|
| 3 creates | 3×(20B payload) + 3×~5bit 实体引用 + 9×~5bit 类型引用 | ~90 B |
| 50 sets | 50×(8B payload) + 50×~16bit 实体引用 + 50×~5bit 类型引用 | ~600 B |
| 5 destroys | 5×~16bit 实体引用 | ~12 B |
| **合计** | | **~702 B** |

payload 本身就是大头（>85%），实体/类型引用是零头。

### 当前裸序列化（baseline）

直接 record struct 一个个写：

- 3 creates: 3 × (8 entity + 3×(4 type + payload)) = 120 B
- 50 sets: 50 × (8 + 4 + 8) = 1000 B
- 5 destroys: 5 × 8 = 40 B
- **~1160 B ≈ 1.65× 物理下界**

冗余主要在：8B Entity(4 id + 4 version) 远超必要；4B ComponentType 远超必要；每命令重复存 type 标签。

### Tier 1 技巧（成本极低，直接打平物理极限）

1. **Varint entity id**（LEB128 或 zigzag varint）：entity id 几乎都 < 65536，2-3 字节代替 8 字节。version 在 lockstep 中是冗余的（可从 delta 序列推算），fresh entity 直接省掉。
2. **Per-section packed array**：header 列出每段 count，整段同质命令连续打包。**省掉每命令 1 字节 tag**。
3. **ComponentType → 1 byte varint**：已注册类型通常 < 256，1 字节够。
4. **Delta-local 实体引用**（可选）：本帧 reserved/created 的实体，后续命令引用时用"本帧第 N 个"，1 字节代替完整 entity。

应用 1+2+3（都很容易）：

- 3 creates: 3 × (3 entity + 3×1 type + 20 payload) = 78 B
- 50 sets: 50 × (3 entity + 1 type + 8 payload) = 600 B
- 5 destroys: 5 × 3 = 15 B
- section header ~10 B
- **~703 B ≈ 1.00× 物理下界**

### Tier 2 技巧（再省几 B，需要 schema 同步）

- **Archetype-batched create**："spawn N 个 archetype X" + N×payload。省掉每个 create 的类型引用（本例 ~15 B）。需要双方同步 archetype 表。
- **Component payload delta 编码**：只传位置差。适合高频 set，但需要前帧状态，属于 state-sync 层而非命令日志层。

### 不建议

- **Bit packing**（type ref 5 bit、entity ref 不对齐字节）：CPU 处理开销超过节省的字节。
- **通用压缩（zstd/LZ4）**：payload 已经是密度高的二进制，对小 delta 收益小，对 batched 多帧才划算（应在 transport 层做，不在 FrameDelta 层）。

### 推荐落地路径

| 阶段 | 内容 | LOC | 收益 |
|---|---|---|---|
| Tier 1 | varint + per-section packed + 1B type | ~200 | 1.65× → 1.00× 物理下界 |
| Tier 2 | + archetype-batched create | +100 | 把 create 路径也贴死物理下界 |

建议直接做 Tier 1：单次工作量大头，把 65% slack 一次吃光。Tier 2 等 profiling 显示 create 占比高再做。

### 实现注意事项（待定）

- **端序**：小端（与 `WorldSnapshot.Save` 现有约定一致）。
- **Varint 方案**：LEB128（无符号）/ zigzag varint（有符号）。entity id 用 LEB128 即可。
- **Section header 编码**：每段开头 1 byte tag + varint count。tag 值固定（Reserved=0x01, Released=0x02, Created=0x03, Link=0x04, Unlink=0x05, Add=0x06, Set=0x07, Remove=0x08, Destroy=0x09），与 `FrameDelta.ReplayCore` 处理顺序对齐。
- **接口形态**：`IFrameDeltaWriter` / `IFrameDeltaReader` 双向，或静态 `FrameDeltaCodec.Serialize(delta, IBufferWriter<byte>)` / `Deserialize(ReadOnlySequence<byte>)`。倾向后者（无状态、零分配）。
- **Created entity 的 Components 数组**：count + 紧凑排列 (typeId varint + size varint + payload bytes)，无对齐 padding。
- **版本协商**：第一字节留 4 bit version（wire format 版本）+ 4 bit flags（reserved for compression/extension）。

### 实际落地（2026-06-14）

方案已完全实现，且比原计划更简单：

**核心改动：**
- `FrameDelta` 内部改为 `byte[] _buffer` + `int _length` + `int _opCount`
- 每个 op 编码为 `[1 byte tag][varint entityId][varint entityVersion][payload...]`
- buffer 就是 wire format — 无需单独序列化步骤
- `Merge` 从 200 行折叠状态机变为 15 行 `Array.Copy` 拼接

**开箱即用的网络 API：**
```csharp
// 发
var wire = delta.AsSpan();           // ReadOnlySpan<byte>
socket.Send(wire);

// 收
var received = socket.Receive();
var delta = FrameDelta.Deserialize(received);
world.Replay(delta);
```

**约束：**
- lockstep 先决条件（两边 state 一致）满足时可直接收发
- `ComponentType.Value` 是进程内 int，跨进程使用需要两边 `ComponentRegistry` 注册顺序一致
- 跨进程场景可加 type mapping header（约 2-3 字节）

**已删除的死代码：**
- `Clear()`、`DeepCopyOwnedData()` — 零调用
- `BuildFromFrozen` 返回的 `CopiedBytes`
- 所有旧的 `ProcessCommandsInto` / `RebuildFromState` / `FoldComponent` / `SquashEntityState`

