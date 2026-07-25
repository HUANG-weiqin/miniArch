---
title: Command Stream Runtime
module: MiniArch.Core CommandStream
description: CommandStream 与 ParallelCommandStream 的 typed-store 录制、consume-time 校验、Submit/Snapshot/Replay 确定性及 async ownership 契约
updated: 2026-07-25
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
Add/Set/Remove(existing) ──→ ComponentStore<T> ──→ prepare/prune → preflight → apply/emit
AddChild/RemoveChild ──→ final hierarchy overlay ──→ preflight → detach affected children → apply/emit final adds
Destroy ──→ destroy list ──→ final phase
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

### Submit preflight 与失败边界

`Submit()` 在 allocator/free-list/materialize/World mutation 前依次检查：

1. pending 与 existing component store 的有效 Entity 字段不得引用 cancelled/unknown deferred placeholder；
2. pending slot 仍为 reserved；
3. component store 的 strict presence：Add 必须缺失、Set 必须存在、Remove 缺失为 no-op；
4. final hierarchy overlay 的 endpoint、自环与 parent-chain cycle。

通过全部 contract preflight 后才对齐 cancelled reservation 的 free-list 顺序并解析 deferred id。placeholder 检查使用与 materialize/emit 相同的 pending last-wins dedup，不能让已被后续 Set 覆盖的死值导致误拒绝。

Hierarchy 的 preflight 检查最终 overlay，因此消费也必须按最终状态落地：先按 child id 解除所有有效 intent 涉及的旧链接，再按 child id 安装最终 Add。Snapshot wire 同样先发 RemoveChild、后发 AddChild；否则合法的父子方向反转会因 World 中尚未解除的旧边产生瞬时 cycle，导致 Submit/Replay 错误拒绝。

Set-only 且全 store 无结构命令时，`PrepareForConsume` 在 prune stale entity 的同时构建 Set location cache，Apply 复用 row/archetype，避免第二次读取 `EntityRecord`。cache 是单一状态机：`None`、`UniformArchetype`、`PerEntryArchetype`。任一 component store 含 Add/Remove 时全局禁用 row cache，因为前一个 store 可能迁移实体。

这些检查防止已知用户契约错误导致部分提交，但不是通用事务系统。灾难性异常或未建模的内部失败仍不承诺 rollback；Replay 也没有通用事务语义。

### async frozen-state ownership

`SubmitAndSnapshotAsync` / `SubmitAndSnapshotIntoAsync` 在 active state 仍由调用线程独占时完成 contract preflight（包括有效组件值中的 placeholder lifecycle）与 FrameDelta 整帧预算 preflight；只有两者都通过，才允许 free-list 对齐、placeholder real-id resolve、state swap、worker 启动和本地 materialize。worker 创建后立即登记 `_pendingFrozen/_pendingTask` ownership。若内部 Submit 随后失败，先观察 worker 完成再回收 frozen state，并保留原同步异常。

因此“不被本地 Submit、placeholder lifecycle 或 FrameDelta 预算接受的 frame”不会先交给后台 worker，复用的 target 也不会在 preflight 失败时被改写。

### deferred entity 两种模式

| 模式 | `Create()` 返回 | Snapshot 产物 | 用途 |
|---|---|---|---|
| `DeferredEntities=false` | World 预留的 real Entity | real-id delta | 单 host / authoritative server |
| `DeferredEntities=true` | `Entity(-1, seq)` placeholder | placeholder delta | 独立 World 的多 host lockstep |

placeholder 只在当前 stream/batch 内有效。跨帧持有解析结果使用 `EntitySlot` + `Track()`；不要手写 placeholder→real map。

### FrameDelta 结构与预算边界

生产端与消费端共享硬上限：`MaxOpsPerFrame = 1_000_000`、`MaxFrameBytes = 16 MiB`。每个 `FrameDelta.Add*` 在写任何字节前精确检查完整 operation，避免超限时留下半条 op；`Snapshot` / `SnapshotInto` / 两条 async submit+snapshot 路径在 deferred id resolution、改写 target、swap frozen state 或提交 World 前先判断 embedded placeholder lifecycle 与整帧预算。因此 producer 不会返回随后被自身 `Validate()` 拒绝的 dangling-placeholder delta，预算失败也不会消费 allocator id/version。

常规帧先用 O(store 数) 的保守 upper bound 证明安全，只有接近上限时才精确扫描，因此 `snapshot-only` A/B（10k Set，Release，1s warmup + 3s measure，各 3 次中位数）保持 42909.3 → 43014.0 ticks/s（+0.2%，噪声内）。精确扫描与实际 writer 共用同一 sizing 规则，由 `Budget_matches_writer_for_every_operation_shape` 守卫。

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
