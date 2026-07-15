---
title: Command Stream Runtime
module: MiniArch.Core CommandStream
description: CommandStream 与 ParallelCommandStream 的 typed-store 录制、consume-time 校验、Submit/Snapshot/Replay 确定性及 async ownership 契约
updated: 2026-07-15
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
Add/Set/Remove(existing) ──→ ComponentStore<T> ──→ prune → preflight → apply/emit
AddChild/RemoveChild ──→ final hierarchy overlay ──→ preflight → apply/emit
Destroy ──→ destroy list ──→ final phase
```

Submit 与 BuildDelta 的阶段顺序统一为：Create → Hierarchy → Component Ops → Destroy。改变阶段或集合顺序前，必须证明 Submit 与 Snapshot→Replay 仍收敛且 free-list 演化一致。

## 决策

### public mutator 只在 sealed 具体类型上

`Create/Track/Add/Set/Remove/Destroy/AddChild/RemoveChild/Clone` 不在 base 上公开。两个 sealed 子类各自提供 public 非虚拟方法并调用共享 `*Core` helper，避免 generic virtual mutator 无法可靠 devirtualize/inline。调用方必须持有 `CommandStream` 或 `ParallelCommandStream`，不能用 base 引用录制。

### pending entity 折叠为最终创建状态

同批次 `Create/Clone` 返回的 pending entity，其 `Add/Set/Remove` 写入 batch side table，materialize 时只构造最终组件签名和值：

- 中间 Add→Remove、重复 Set 不产生独立 Watch transition/value event。
- `Destroy(pending)` 取消创建，并按确定性顺序释放 reservation。
- `CreateMany` 是一次性初始化 API；混合后续 Set/Add/Remove、重复组件类型等违反 fast-path 前置条件时 fail-fast，不静默降级。

### existing component command 在 consume 时判定存活

`Add/Set/Remove` 录制 existing handle 时不读取 World。所有消费入口先执行：

```text
PrepareStores()
  → SealParallelStores()
  → ComponentStore<T>.PruneStaleCommands(world)
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

1. pending slot 仍为 reserved；
2. component store 的 strict presence：Add 必须缺失、Set 必须存在、Remove 缺失为 no-op；
3. final hierarchy overlay 的 endpoint、自环与 parent-chain cycle；
4. cancelled reservation 的 free-list 顺序对齐。

Set-only 且全 store 无结构命令时，preflight 缓存 row 供 Apply 复用；任一 store 含 Add/Remove 时禁用跨 store row cache，因为前一个 store 可能迁移实体。

这些检查防止已知用户契约错误导致部分提交，但不是通用事务系统。灾难性异常或未建模的内部失败仍不承诺 rollback；Replay 也没有通用事务语义。

### async frozen-state ownership

`SubmitAndSnapshotAsync` / `SubmitAndSnapshotIntoAsync` 在 active state 仍由调用线程独占时完成全部 preflight；通过后才 resolve、swap、启动 delta worker。worker 创建后立即登记 `_pendingFrozen/_pendingTask` ownership。若内部 Submit 随后失败，先观察 worker 完成再回收 frozen state，并保留原同步异常。

因此“不被本地 Submit 接受的 frame”不会先交给后台 worker，复用的 target 也不会在 preflight 失败时被改写。

### deferred entity 两种模式

| 模式 | `Create()` 返回 | Snapshot 产物 | 用途 |
|---|---|---|---|
| `DeferredEntities=false` | World 预留的 real Entity | real-id delta | 单 host / authoritative server |
| `DeferredEntities=true` | `Entity(-1, seq)` placeholder | placeholder delta | 独立 World 的多 host lockstep |

placeholder 只在当前 stream/batch 内有效。跨帧持有解析结果使用 `EntitySlot` + `Track()`；不要手写 placeholder→real map。

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
