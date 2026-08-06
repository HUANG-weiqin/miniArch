---
title: Command Stream Runtime
module: MiniArch.Core CommandStream
description: CommandStream 与 ParallelCommandStream 的 typed-store 录制、consume-time 校验、Submit/Snapshot/Replay 确定性及 async ownership 契约
updated: 2026-08-06
---
# Command Stream Runtime

## 这个模块是干什么的

- `CommandStream`：单线程、非虚拟、可内联的默认延迟录制器。
- `ParallelCommandStream`：允许多个工作线程向同一个 stream 录制；consume 仍由单线程独占。
- 两者共享 `CommandStreamCore` 的 Submit、Snapshot、Replay、pending materialize、hierarchy、component store 与 async lifecycle。
- `FrameDelta` 是可序列化操作序列；端到端帧同步见 `kb-lockstep-playbook.md`。

## 架构

核心文件：

| 文件 | 职责 |
|---|---|
| `CommandStream.cs` | 单线程 public mutator；existing component command 的无锁 append |
| `ParallelCommandStream.cs` | 并行 public mutator；pending map 受锁保护、component store 使用 thread-local append |
| `CommandStreamCore.cs` | 共享字段、record helper、clone、clear、deferred/frozen state |
| `CommandStreamCore.Pending.cs` | pending batch、CreateMany materialize、placeholder resolve |
| `CommandStreamCore.ComponentStore.cs` | typed entries、parallel merge、stale prune、preflight、apply/emit |
| `CommandStreamCore.Hierarchy.cs` | hierarchy intent、overlay preflight、apply/emit |
| `CommandStreamCore.Submit.cs` | Submit/Snapshot/Replay、async handoff、consume preparation |

这些文件组成同一个 `public abstract partial class CommandStreamCore`。2026-07-15 拆分时逐项验证关键 canonical IL 与 JIT 内联边界不变；证据见 `docs/plans/2026-07-15-quality-hardening-4-evidence.md`。

### 数据流

```text
Create/Clone ──→ pending batch ──→ materialize or emit Create
Add/Set/Remove(existing) ──→ ComponentStore<T> ──→ prepare/prune → (flag 驱动 Entity 引用守卫) → apply/emit
AddChild/RemoveChild ──→ final hierarchy overlay ──→ endpoint wire 守卫 → (语义校验可选：Validate) → detach/apply
Destroy ──→ destroy list ──→ entity-shape wire 守卫 → final phase
```

Submit 与 BuildDelta 的阶段顺序统一为：Create → Hierarchy → Component Ops → Destroy。改变阶段或集合顺序前，必须证明 Submit 与 Snapshot→Replay 仍收敛且 free-list 演化一致。

## 决策

### public mutator 只在 sealed 具体类型上

`Create/Track/Add/Set/Remove/Destroy/AddChild/RemoveChild/Clone` 不在 base 上公开。两个 sealed 子类各自提供 public 非虚拟方法并调用共享 `*Core` helper，避免 generic virtual mutator 无法可靠 devirtualize/inline。调用方必须持有 `CommandStream` 或 `ParallelCommandStream`，不能用 base 引用录制。

### pending entity 折叠为最终创建状态

同批次 `Create/Clone` 返回的 pending entity，其 `Add/Set/Remove` 写入 batch side table，materialize 时只构造最终组件签名和值：

- 中间 Add→Remove、重复 Set 不产生独立 Watch transition/value event；placeholder resolve 与 emit/materialize 一样只读取 last-wins 后的有效值，superseded batch bytes 不参与语义。
- `Destroy(pending)` 取消创建，并按确定性顺序释放 reservation；其他有效组件值若仍引用该 placeholder，会在 allocator/target/worker 变化前 fail-fast。
- `CreateMany` 是一次性初始化 API；混合后续 Set/Add/Remove、重复组件类型等违反 fast-path 前置条件时 fail-fast，不静默降级。

### existing component command 在 consume 时判定存活

`Add/Set/Remove` 录制 existing handle 时不读取 World。所有消费入口先执行：

```text
PrepareStores()
  → SealParallelStores()
  → ComponentStore<T>.PrepareForConsume(world, buildSetLocationCache)
```

prune 按完整 `Entity(Id, Version)` 丢弃“record 时已 stale”和“record 后才 stale”两类命令，防止复用 Id 被误写。Snapshot、SnapshotInto 和两个 async 入口也走同一流程，所以安全裁决不只存在于 Submit。

pending/foreign placeholder 的 `IsPlaceholder` 仍在 record 阶段用于本地分流；它不是 World liveness 检查。

回归入口：

- `Existing_entity_component_liveness_is_decided_when_the_stream_is_consumed`
- `BUG_stale_existing_entity_set_is_skipped_so_submit_matches_replay`
- `BUG_existing_entity_that_becomes_stale_before_consume_is_skipped_so_submit_matches_replay`
- `Parallel_recording_skips_stale_existing_entity_component_commands`
- `SubmitAndSnapshotAsync_skips_existing_entity_commands_that_become_stale_before_consume`

### 零校验契约与 `Validate()`（2026-08-06）

**默认语义：CommandStream / ParallelCommandStream 的 Submit/Snapshot/async 路径不做语义校验**（组件存在性、hierarchy 环等）。语义校验抽成公共 API `public void Validate()`（基类 `CommandStreamCore`，两子类继承；幂等、无副作用声明、首个违规处抛 `InvalidOperationException`，错误消息含实体 id + 组件类型）。

- **`Validate()` 保证**：占位符生命周期（batch last-wins dedup + store lazy-offsets、deferred-aware）、组件 store strict presence（Add 必须缺失 / Set 必须存在 / Remove 缺失 no-op）、hierarchy overlay endpoint/自环/parent-chain 环；只准备 stream 内部 scratch，不消费命令、不改 World/free-list。cancelled-batch free-list alignment 只在真正 consume 时执行。
- **`Validate()` 不覆盖**：CreateMany 组一致性（Materialize 期 `ThrowCreateManyMismatch/MaskFailure` 抛）、slot reservation（Submit 内 `PreValidatePendingSlots` 防御性检查，A 类保留）、FrameDelta 预算（Submit/Snapshot 期 `PreflightFrameDeltaBudget`）。
- **不调 Validate 的后果**：存在性/hierarchy 违规在 apply 期抛（消息统一 `"Entity {X} does not have component {T}."`），**部分应用**（前序 batch/store 已落地，reserved ids 由内部清理释放）；`SubmitAndSnapshotIntoAsync` 失败时 **target 内容未定义**（旧 "target remains unchanged" 承诺撤销，见 async 节）。

#### 库能力守卫（P0 裁决，不可移入 Validate）

未知/已取消占位符引用（`Entity(-1, seq)`）、非法显式 entity shape 与不可解析布局（nested Entity / LayoutKind.Auto + Entity 字段）**绝不落库/落 wire**——这是 lockstep 分叉防线（源端静默应用/发出 → 副本 Replay/`FrameDelta.Validate()` 拒绝），不是组件存在性或 hierarchy 环等语义校验：

- **record 期泛型静态探测**：`FieldKinds<T>`（`static readonly bool HasEntityFields` + `static readonly int[] Offsets` + 静态 ctor，ctor 永不抛——布局失败保守置 `HasEntityFields=true`，由扫描器抛）；无 Entity 字段类型每值 1 次 static 读 + 1 分支。探测点 = `WritePendingComponent<T>`（batch 写前）+ `CommandStream/ParallelCommandStream.Add/Set<T>` 的 store 分支（5 处）。
- **Clone raw 导入探测（非泛型）**：Clone 从 World/raw archetype 复制字节时无泛型类型可用，走 `FlagFrameIfMayContainPlaceholder(ComponentType, ReadOnlySpan<byte>)`：`GetOffsets` 成功仅在实际 top-level Entity 字段为 placeholder 时置 flag（值探测）；`InvalidOperationException`（nested Entity / LayoutKind.Auto + Entity 字段）保守置 flag 但 Clone 期不抛（布局错误由 consume 扫描器在原有时机抛）；flag 已为 1 直接返回。探测点 = 单线程 `CloneMaterializedComponents` 的 merger **最终有效 values**（不是 raw archetype 副本——避免被 store overlay 覆盖/移除的旧 world 值误报）+ 并行 `Clone` root 与 `CloneChildrenFromWorld` child 两个 raw copy 点，均在 raw bytes 复制完成后、`CommitBatchComponent` 前。`CopyComponentsFromBatch`（pending source clone）的字节源均由已探测的提交路径产生，无需重复探测；`WritePendingComponent`/store 泛型热路径不改。
- **显式 endpoint 探测**：AddChild/RemoveChild 的 placeholder/非法 shape 与 Destroy 的非法 shape 也置同一 flag；扫描 final hierarchy overlay（避免被后写覆盖的旧 intent 假拒绝）和 destroy list。合法 real 只做可内联 `Entity.IsValid` 分支，flag 已置位后不重复 locked exchange。
- **帧级 flag**：`FrozenState.MayNeedEntityReferencePreflight`（`Interlocked` 置位，`Clear`/`SwapOutState` 复位）。
- **consume 扫描器**：Submit/Snapshot/SnapshotInto/PrepareAsyncHandoff 在 reserve/free-list/materialize/emit **前**调 `PreflightEntityReferences()`——首行 volatile 读 flag，0 直接 return；1 时扫描 final explicit endpoints + component refs/layout（batch last-wins：id<512 走固定 bitset、id≥512 与 materialize/emit 共用线性 fallback；store lazy-offsets；deferred-aware：同帧合法 placeholder 放行）。**原子拒绝**：失败不消耗 id/version（v1 保留）、不先 detach hierarchy、不会发出本地 `FrameDelta.Validate()` 拒绝的 wire。
- `Validate()` 绕过 flag 全量扫（`useFlagFastPath: false`，用户显式调用）。

**历史**：preflight 序列（0fca3eb，2026-07-25 引入 Submit 隐式 4 项 preflight）→ 08-05 flag 优化（`MayContainPlaceholder` record 探测 + 帧级 flag）→ **08-06 语义校验抽为 `Validate()` + 守卫保留**（探测改 `FieldKinds<T>` 泛型静态，修复 P0#1/P0#2）。期间经历 T2.5（统一扫描器，-7.6%）、T2.6（融合进既有遍历，-5.4%）两版性能实验后定稿为 flag 驱动方案（-3~-6% 噪声带，门控 A/B 见 `kb-hero-pipeline-regression.md`）。

hierarchy 消费按最终 overlay 落地：先按 child id 解除所有有效 intent 涉及的旧链接，再安装最终 Add；Snapshot wire 同样先 RemoveChild 后 AddChild（否则合法的父子方向反转会因 World 中尚未解除的旧边产生瞬时 cycle，导致 Submit/Replay 错误拒绝）。

Set-only 且全 store 无结构命令时，`PrepareForConsume` 在 prune stale entity 的同时构建 Set location cache，Apply 复用 row/archetype，避免第二次读取 `EntityRecord`。cache 是单一状态机：`None`、`UniformArchetype`、`PerEntryArchetype`。任一 component store 含 Add/Remove 时全局禁用 row cache（`BUG_set_preflight_row_cache_is_disabled_when_any_store_is_structural`）。

这些守卫防止已知用户契约错误导致 lockstep 分叉或部分提交，但不是通用事务系统。灾难性异常或未建模的内部失败仍不承诺 rollback；Replay 也没有通用事务语义。

### async frozen-state ownership

`SubmitAndSnapshotAsync` / `SubmitAndSnapshotIntoAsync` 在 active state 仍由调用线程独占时完成：flag 驱动 Entity 引用/布局守卫（`PreflightEntityReferences()`，仅 flagged 帧全量扫）与 FrameDelta 整帧预算 preflight；real-id 输出中尚未 resolve 的 placeholder endpoint 按合法 Entity 最大 wire width（5B id + 5B version）计费。守卫失败时不消耗 id、不 handoff、不启动 worker。之后才允许 free-list 对齐、placeholder real-id resolve、state swap、worker 启动和本地 materialize。worker 创建后立即登记 `_pendingFrozen/_pendingTask` ownership。若内部 Submit 随后失败，先观察 worker 完成再回收 frozen state，并保留原同步异常。

async real-id 输出会省略仍是 placeholder 的 cancelled deferred batch：这类 Create 从未触碰 source allocator，因此不能为它发 Reserve+Release；immediate real-id cancellation 仍必须发 Reserve+Release，镜像 record 期已经发生的 reserve/release。

因此“不被本地 Submit、Entity 引用/布局守卫或 FrameDelta 预算接受的 frame”不会先交给后台 worker。**target 语义（`SubmitAndSnapshotIntoAsync`）**：mandatory 守卫失败发生在 `target.Clear()` 前 → target 未动；其他 consume 期错误（如存在性违规）可能已 Clear 或部分写入 → **失败时 target 内容未定义**（旧 “target remains unchanged” 承诺已撤销，由 `Contract_*` 测试锁定）。

### deferred entity 两种模式

| 模式 | `Create()` 返回 | Snapshot 产物 | 用途 |
|---|---|---|---|
| `DeferredEntities=false` | World 预留的 real Entity | real-id delta | 单 host / authoritative server |
| `DeferredEntities=true` | `Entity(-1, seq)` placeholder | placeholder delta | 独立 World 的多 host lockstep |

placeholder 只在当前 stream/batch 内有效。跨帧持有解析结果使用 `EntitySlot` + `Track()`；不要手写 placeholder→real map。

### FrameDelta 结构与预算边界

生产端与消费端共享硬上限：`MaxOpsPerFrame = 1_000_000`、`MaxFrameBytes = 16 MiB`。每个 `FrameDelta.Add*` 在写任何字节前精确检查完整 operation，避免超限时留下半条 op；`Snapshot` / `SnapshotInto` / 两条 async submit+snapshot 路径在 deferred id resolution、改写 target、swap frozen state 或提交 World 前先跑 flag 驱动 Entity 引用/布局守卫与整帧预算 preflight。real-id 模式无法在不修改 allocator 的前提下知道 placeholder 将得到多宽的 id/version，因此 pending Reserve/Create 与 hierarchy endpoint 预先按最大 10B Entity wire 计费。因此 producer 不会返回随后被副本 Replay 拒绝的 dangling-placeholder delta，预算失败也不会消费 allocator id/version。

常规帧先用 O(store 数) 的保守 upper bound 证明安全，只有接近上限时才扫描，因此 `snapshot-only` A/B（10k Set，Release，1s warmup + 3s measure，各 3 次中位数）保持 42909.3 → 43014.0 ticks/s（+0.2%，噪声内）。已解析 endpoint 的扫描与实际 writer 共用 sizing 规则，由 `Budget_matches_writer_for_every_operation_shape` 守卫；未解析 real-id placeholder 则由 `Real_id_budget_uses_max_wire_width_for_unresolved_placeholder_endpoints` 锁定保守上界。

不可信 wire 在 Replay 前必须调用 `FrameDelta.Validate()`。验证器保证 delta 自身满足：

- 每个 `Reserve` 都在同一 delta 内以 `Create` 或 `Release` 结束；
- placeholder 与同 delta 中被 reserve 的 real entity 不能在 `Create` 前成为操作目标，release 后也不能再被操作；
- public operation entity 的 real version 必须大于 0；
- Create/Add/Set/Remove 引用的 component type 必须已注册，payload 大小必须匹配；
- component payload 内的 placeholder `Entity` 字段必须已有前置 Reserve 映射；
- Create payload 内的 component type 不重复。

这些是 wire 自身契约，不证明 target World allocator/free-list 与该历史兼容，也不提供 Replay rollback。

real-id Replay 的 reservation 只接受三种状态：matching free slot、同一 handle 已被 source producer 预留、或紧邻的 version-1 fresh slot。只有实际从 free list 取出或创建 fresh slot 才增加 `_reservedCount`；已预留 slot 不重复计数。其他 allocator 状态在任何 ID/reservation mutation 前 fail-fast。该局部原子性不等于后续 Replay 操作具有通用 rollback。

## 性能门禁

先用专用 runner 判断 record/submit/snapshot/clear 哪段主导：

```powershell
dotnet build -c Release tools/perf/CommandStream.Profile/CommandStream.Profile.csproj
dotnet run -c Release --no-build --project tools/perf/CommandStream.Profile -- --list
dotnet run -c Release --no-build --project tools/perf/CommandStream.Profile -- --scenario existing-set --warmup 3 --measure 10
```

规则：

- runtime 改动后先重建 profiler 输出，再使用 `--no-build`；否则可能测到旧 `MiniArch.dll`。
- 基准独占进程运行，不与 build/test 并发。
- 每个候选至少三次，看端到端中位数和阶段占比；不以单次幸运值闭环。
- 改 `src/MiniArch/` 后仍要跑 `HeroComing.Perf --check-baseline`。

2026-07-15 consume-time liveness 候选的有效 A/B：`existing-set` 中位数 11036.2 → 11759.0 ticks/s（+6.5%），`snapshot-only` 72284.8 → 74160.9（+2.6%）；JIT record loop 保持完整内联。完整数据见本轮 evidence 文档。

consume prune 必须同时刷新 stream 的 store-dirty 汇总。若 stale-only entries 全被删除，`Submit()` 返回 `false`、Snapshot 为空，async 路径不应因旧 `_hasStoreCommands` 启动无效工作；single/parallel 与 consume 前 ID reuse 回归共同守卫该契约。

## 认知模型

把 CommandStream 看成“append-only intent + 单次消费裁决”，不是 World 的事务镜像：

- record 尽量只分类和追加；
- consume 统一处理依赖 World 当前状态的校验；
- Submit 和 emit 必须共享同一过滤、排序与 placeholder 语义；
- `Clear()` 丢弃 intent，`Submit()` 消费 intent，`Snapshot()` 只编译 intent。

## 入口

- record：`CommandStream.cs`、`ParallelCommandStream.cs`
- consume/preflight：`CommandStreamCore.Submit.cs`
- typed store：`CommandStreamCore.ComponentStore.cs`
- pending/CreateMany：`CommandStreamCore.Pending.cs`
- hierarchy：`CommandStreamCore.Hierarchy.cs`
- wire：`FrameDelta.cs`、`World.EntityLifecycle.cs` 的 Replay 路径
- 测试：`tests/MiniArch.Tests/Core/CommandStreamTests.cs`、`FrameDeltaDeterminismTests.cs`

## 坑点

- 不能把 `Snapshot()` 当作无 World 的纯 emit：它会使用 source World prune stale existing command。
- 不能跨结构变更复用 preflight row、column index、ChunkView 或裸 span。
- strict Add/Set/Remove 契约不能为吞吐放松；Remove 缺失保持幂等 no-op。
- parallel 只表示录制可并发，不表示同一 World 可被多个 stream 并发 reserve/submit。
- 自己生成的 local delta 只有在显式 `Replay(delta, resolveSlots: true)` 时解析本 stream 跟踪的 `EntitySlot`；网络反序列化副本没有本地 slot ownership。
- `FrameDelta.Validate()` 是不可信 wire 的结构预检，不提供 target World rollback。
- source 对自己的 real-id Snapshot 直接 Replay 时会复用 producer 已有 reservation；不要把“不在 free list”误判成需要再次增加 reservation。
