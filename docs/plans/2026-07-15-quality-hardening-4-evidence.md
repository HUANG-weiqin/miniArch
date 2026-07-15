# MiniArch 4.0 Quality Hardening Evidence

## 结论

这是质量硬化分支的最终证据账本，不是性能 baseline。候选已通过 Release 全测试、单主机 32-seed soak、200,000 帧双跑确定性、multi-host lockstep 与 HeroComing 连续三次门禁，可以作为 4.0 发布候选；没有运行 `--update-baseline`。

结论有两个边界：lockstep verifier 提供高强度差分/变异校验，不等于形式化证明；CommandStream preflight 显著缩小部分提交面，但 Replay 仍不是普遍回滚事务。HeroComing 初始三次中有一次 Attack 低于门槛，连续计数清零后重新取得三次连续 PASS，失败样本保留在本文。

## 环境与代码点

- Date: 2026-07-15
- Runtime: .NET SDK 8.0.423
- OS: Microsoft Windows 11 Pro for Workstations 10.0.26100
- CPU: AMD Ryzen 7 5700X3D 8-Core Processor
- Branch: `codex/quality-hardening-4`
- Baseline commit: `5b9ca0cb48a250dc3be3e1b7a6039eab78e41357`
- Final runtime commit: `a4df7df`
- Final lockstep verifier commit: `c453049`
- Configuration: Release；CommandStream warmup 3s / measure 10s；每场景独立进程

## 起点构建与测试

```text
dotnet build -c Release --no-restore miniArch.sln
0 warnings, 0 errors

dotnet test -c Release --no-build miniArch.sln
MiniArch.Tests: 1013 passed
HeroPipeline.Tests: 5 passed
Exit code: 0
```

## CommandStream.Profile 起点

### 三次原始摘要

| Scenario | Run 1 ticks/s | Run 2 ticks/s | Run 3 ticks/s | Median ticks/s | Median ms/tick | Dominant phases |
|---|---:|---:|---:|---:|---:|---|
| existing-set | 10394.3 | 9790.2 | 10350.1 | 10350.1 | 0.0966 | record 65.6%, submit 34.3% |
| existing-add-remove | 2402.9 | 2398.5 | 2384.4 | 2398.5 | 0.4169 | record 52.6%, submit 47.4% |
| create-small4 | 3711.7 | 3556.8 | 3602.1 | 3602.1 | 0.2776 | record 52.5%, submit 47.5% |
| create-duplicates | 3562.8 | 3395.8 | 3443.1 | 3443.1 | 0.2904 | record 52.4%, submit 47.6% |
| create-destroy | 19358.3 | 20549.0 | 20711.1 | 20549.0 | 0.0487 | record 57.4%, submit 42.6% |
| snapshot-only | 69077.1 | 70253.8 | 73748.6 | 70253.8 | 0.0142 | record 58.5%, snapshot 41.5% |

### 内存解释边界

- `existing-set`、`existing-add-remove`、`create-destroy` 三次均为 Gen0=0；`create-destroy` live count 固定为 2000，是稳态提交路径证据。
- `create-small4` 与 `create-duplicates` 会持续增加 live entities；10 秒测量期达到约 3300 万实体和约 4.2 GB heap。该数值是 workload 的无界增长设计，不作为内存泄漏判据，只保留吞吐对比价值。
- `snapshot-only` 每 tick 创建 delta，三次 Gen0 为 450/458/480；它是 snapshot 吞吐基线，不是零分配证明。
- 最终“内存稳定”结论由 HeroComing、create-destroy 与长时间 soak 共同承担。

## HeroComing.Perf 起点

Command:

```powershell
dotnet run -c Release --no-build --project tools/perf/HeroComing.Perf -- --check-baseline
```

三份可核对摘要：

| Auditable run | Movement rounds/s | Movement gate | Attack rounds/s | Attack gate | Memory |
|---|---:|---|---:|---|---|
| A | 1639.6 | FAIL, threshold 1642 | 988.1 | FAIL, threshold 997 | stable |
| B | 1795.1 | PASS | 1041.8 | PASS | stable |
| C | 1863.2 | PASS | 1104.0 | PASS | stable |

另一次完整运行的高频明细被终端截断，未保留最终摘要，因此不进入证据表。结果分布跨越门槛，最终规则为：同一最终 commit 连续三次执行，三次均通过 Movement/Attack/Memory 才闭环。

## Pre-split CommandStream 锚点

锚点 commit：`ac15497053d13071f08c5ab50a4ced2d0c5a45ce`。该 commit 位于全部已计划行为修复之后、任何 `CommandStreamCore` partial 迁移之前。

### 拆分前构建与正确性

```text
Debug WorldStructuralChangeTests: 8/8 passed
Release MiniArch.Tests: 1039/1039 passed
Release HeroPipeline.Tests: 5/5 passed
HeroComing.Perf: Movement 1780.1, Attack 1066.8, Memory OK
```

### 六场景三次采样

参数仍为每场景独立进程、Release、warmup 3s、measure 10s。保留全部原始值；不删除系统噪声造成的低样本。

| Scenario | Run 1 ticks/s | Run 2 ticks/s | Run 3 ticks/s | Median ticks/s | Median ms/tick | Median dominant phases |
|---|---:|---:|---:|---:|---:|---|
| existing-set | 10867.6 | 10883.5 | 10107.3 | 10867.6 | 0.0920 | record 65.3%, submit 34.7% |
| existing-add-remove | 1868.6 | 2341.8 | 2265.9 | 2265.9 | 0.4413 | record 52.3%, submit 47.7% |
| create-small4 | 3588.0 | 3543.0 | 3604.2 | 3588.0 | 0.2787 | record 53.4%, submit 46.6% |
| create-duplicates | 3592.1 | 3559.2 | 3610.8 | 3592.1 | 0.2784 | record 53.1%, submit 46.9% |
| create-destroy | 20121.6 | 20715.6 | 20678.5 | 20678.5 | 0.0484 | record 57.1%, submit 42.9% |
| snapshot-only | 72021.6 | 70795.3 | 72904.8 | 72021.6 | 0.0139 | record 58.2%, snapshot 41.8% |

`existing-add-remove` Run 1 明显偏低，但同一轮阶段占比没有转移；拆后比较继续使用三次中位数，并保留原始样本。create 两个 workload 的 live/heap 继续沿用起点解释边界，不作为泄漏证据。

### Release IL hash

本机无 `ildasm` / `ilspycmd`，因此用 workspace 外的只读 Reflection inspector 读取 `MethodBody.GetILAsByteArray()`。同时记录 raw hash 与把 metadata token 解析为稳定 member identity 后的 canonical hash；迁移 partial 后优先要求 canonical hash 完全相同，raw hash 差异只允许来自 metadata token 重编号。

| Method | IL bytes | Raw SHA256 | Canonical SHA256 |
|---|---:|---|---|
| `CommandStreamCore.Submit` | 129 | `574639A16C0095EEC7A443419DE277B717C5CB976F3892C39D605A930ADFA25A` | `A4CD0025FD16F70A3A36231E7036C0BA5CDF734E24CFBDD01B1C82708DA6536C` |
| `CommandStreamCore.PrepareStores` | 13 | `9A97066EE59FBC3C8A8538F9FCA9D9A3C782AB053DDFFE5C5763AB90C1CE3073` | `8E674FD5A583EA118D6714B1C57D7B079B4BC76E13F9A1649EB6A8B07D33688F` |
| `CommandStreamCore.ApplyComponentStores` | 54 | `B873356CA7190A6D524E464A5161366D900E9F11D5CA8334A2C09B05CE06669A` | `A9232816DBEFC5B1ED0E6F5337751033BD2611BFB6DC6C16F0E312993CBF47AC` |
| `CommandStreamCore.GetOrCreateStore<T>` | 278 | `C2A451F559831283EBE449A3E86EA53F3EC09F28DE0D562D409E2705334A0842` | `496A16BC760A6F6215595CF2358750E1FA738743EBC46117597CA5232C565ADC` |
| `ComponentStore<T>.Append` | 76 | `379214DAEBAD29E382431B2DCFB250B09BF6E59224DAC52BEC26B7DAA4B14F79` | `30125B51C48A965C9D7B424B63F44F882928E6FDA58F9DB10E29DEF2BD6B843F` |
| `ComponentStore<T>.ApplyToWorld` | 826 | `4D2905D1884365303A41BD0CA1F5486572CE4BB2EAE634D5CDA2B44C70470B3B` | `4296765483D8B63277DFFE8399AAC307F625FD4128578E45C49DE2B7E606EDD4` |
| `CommandStream.Set<T>` | 73 | `581AEEA14D50CEC37A5F098F7C39E2509BA5C047A79976AE77DD54C9731CFA48` | `102D1AE04385C1A40F13E8A08E09C989F2354A11F14B7F040FFA4E9D68259172` |
| `ParallelCommandStream.Set<T>` | 105 | `8E7BADD7DFF97F7386C6F7F504337F26D020C2B2ACABBF1F2E9D66D5049D0ADB` | `0AF7F46681A1BFC999D5C44C33CF4B2CD688FCD1473DACDF1D472420E166E2FD` |

### JIT 内联锚点

诊断参数：`COMPlus_TieredCompilation=0`、`COMPlus_JitPrintInlinedMethods=1`，existing-set 单独进程。

- `ExistingSetScenario.RunTick` 为 `FullOpts`，报告 `3 inlinees with PGO data; 41 single block inlinees; 15 inlinees without PGO data`，总代码 1808 bytes。
- 生成代码在 record loop 内没有 `CommandStream.Set<T>`、`GetOrCreateStore<T>` 或 `ComponentStore<T>.Append` 调用，三层均已内联；loop 结束仅保留对 `CommandStreamCore.Submit()` 的调用。
- `ComponentStore<Position>.ApplyToWorld` 为 `FullOpts`，报告 `62 inlinees with PGO data; 59 single block inlinees; 8 inlinees without PGO data`，总代码 3472 bytes。

拆分后必须重新检查上述调用边界；不能只凭“同一 partial class 理论上 IL 不变”跳过实测。

## CommandStreamCore 拆分后验证

拆分仅把同一 `partial class` 的成员迁移到 `Hierarchy`、`Pending`、`ComponentStore`、`Submit` 四个文件，未修改方法体。Release 独占进程、`warmup=3s`、`measure=10s`，每个场景 3 次；曾有一次与测试并跑的 `existing-set` 样本受到 CPU 争用，已丢弃且不计入下表。

| Scenario | 拆分前中位数 | 拆分后三次 | 拆分后中位数 | 变化 |
|---|---:|---|---:|---:|
| `existing-set` | 10867.6 | 11036.2 / 11052.0 / 10890.1 | 11036.2 | +1.55% |
| `existing-add-remove` | 2265.9 | 2344.1 / 2467.1 / 2410.8 | 2410.8 | +6.39% |
| `create-small4` | 3588.0 | 3605.4 / 3616.5 / 3544.0 | 3605.4 | +0.49% |
| `create-duplicates` | 3592.1 | 3813.4 / 3564.3 / 3574.6 | 3574.6 | -0.49% |
| `create-destroy` | 20678.5 | 20691.3 / 20683.8 / 20337.8 | 20683.8 | +0.03% |
| `snapshot-only` | 72021.6 | 72536.0 / 71068.0 / 72284.8 | 72284.8 | +0.37% |

结论：六个工作负载均无稳定退化；正向波动只视为“未退化”证据，不宣称机械拆分带来性能收益。

- `CommandStreamTests` + `FrameDeltaDeterminismTests`：158 / 158。
- `MiniArch.Tests`：1039 / 1039；`HeroPipeline.Tests`：5 / 5。
- 上表 8 个关键方法的 canonical IL SHA256 与拆分前逐项完全相同。
- `ExistingSetScenario.RunTick` 拆分后仍为 `3 / 41 / 15` 个 inlinees，1808 bytes；record loop 内仍无 `Set<T>`、`GetOrCreateStore<T>`、`Append` 调用。
- `ComponentStore<Position>.ApplyToWorld` 拆分后仍为 `62 / 59 / 8` 个 inlinees，3472 bytes。

## Existing component command 的 consume-time liveness

候选只删除 `CommandStream` / `ParallelCommandStream` 的 `Add/Set/Remove` record-time `World.IsAlive`；pending/foreign placeholder 的本地分流保留。所有消费入口继续通过 `PrepareStores()` → `PruneStaleCommands(world)` 按完整 `(Id, Version)` 过滤 stale command。

新增 `Existing_entity_component_liveness_is_decided_when_the_stream_is_consumed`：handle 在 record 时 dead、rollback restore 后在 consume 时重新 alive，Submit 与 Snapshot→Replay 都必须执行命令并收敛。修改前该测试 RED（`Submit()` 返回 false），修改后转绿。既有 record-time stale、delayed stale、parallel stale、async stale/ID reuse 回归同时通过。

测量纪律：修改后必须先显式 `dotnet build -c Release tools/perf/CommandStream.Profile/CommandStream.Profile.csproj`。第一轮误用了未刷新的 profiler output（`--no-build`），样本已全部丢弃；下表仅包含重建后的独占进程结果。

| Scenario | 候选前中位数 | 候选后三次 | 候选后中位数 | 变化 |
|---|---:|---|---:|---:|
| `existing-set` | 11036.2 | 12029.6 / 11759.0 / 11623.1 | 11759.0 | +6.55% |
| `existing-add-remove` | 2410.8 | 2466.7 / 2427.5 / 2147.8 | 2427.5 | +0.69% |
| `snapshot-only` | 72284.8 | 73693.2 / 74160.9 / 74283.8 | 74160.9 | +2.60% |

- `existing-set` record 占比从约 65.3% 降至约 63%，端到端收益稳定；未把成本转移为 submit/snapshot 回归。
- JIT：`ExistingSetScenario.RunTick` 仍完全内联 record 三层，inlinees 从 `3 / 41 / 15` 降为 `3 / 37 / 14`，代码从 1808 降至 1738 bytes；record loop 仅在末尾调用 `Submit()`。
- Release：`MiniArch.Tests` 1040 / 1040，`HeroPipeline.Tests` 5 / 5。
- Hero gate：Movement 1741.9 rounds/s，Attack 1041.3 rounds/s，Memory OK，baseline 未更新。

## 最终验证

### Release 构建与测试

```text
dotnet build -c Release --no-restore miniArch.sln
0 warnings, 0 errors

dotnet test -c Release --no-build miniArch.sln
MiniArch.Tests: 1040 passed
HeroPipeline.Tests: 5 passed
Exit code: 0
```

针对 stale-only dirty 汇总的 3 个回归先 RED（`Submit()` 实际返回 `true`），修复后 3 / 3 PASS；全套结果如上。

### Single-host soak 与确定性

最终 runtime 上执行：

```powershell
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 100000
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --determinism --frames 200000
```

- sweep：32 / 32 PASS，每个 seed 100,000 帧，约 334k–336k operations。
- determinism run 1：`12DBCA33984E019C200B6D4C766AABA2048F33055D9017688D4C4A6BC20B4A75`。
- determinism run 2：`12DBCA33984E019C200B6D4C766AABA2048F33055D9017688D4C4A6BC20B4A75`。
- 结论：200,000 帧两次 checksum 完全相同，DETERMINISM PASS。

### Multi-host lockstep verifier

`--checksum-interval` 与 `--validate-interval` 独立：前者控制全状态 checksum，后者控制轻量 CompA/CompB mutation oracle。默认 `--checksum-interval 1`，仍是逐帧最强门禁；长跑可显式降频，最终帧始终校验。降频可能漏掉随后重新收敛的瞬态差异，因此不能替代逐帧 witness。

| Workload | Checksum | Result | Evidence |
|---|---:|---|---|
| 原始 witness：seed 1234567，4 hosts，10,000 frames，8 ops/frame | 每帧 | PASS | 130,865 aggregate ops；121,648 oracle checks；Gen2=0 |
| diversity：8 seeds × 10,000 frames，4 hosts | 每 100 帧 | 8 / 8 PASS | 每 seed 约 113k–134k oracle checks |
| 高密度：8 seeds × 50,000 frames，4 hosts，50 ops/frame，cap=100 | 每 100 帧 | 8 / 8 PASS | 总计约 24.78M ops；每 seed 9,150–12,700 oracle checks |
| determinism：2 × 2,000 frames，3 hosts | 每 100 帧 | PASS | checksum 均为 `E9F55D78887795DC5BAEF9FB111B6804E47CF497525465C62D23D57C582CB5C3`；每次 23,480 oracle checks |

原版 verifier 在 seed 1234567 / frame 413 曾因同帧 presence 跟踪不完整生成重复 strict Add；同一失败可在 3.6 baseline `7cde430` 复现，所以这是测试生成器 bug，不是 MiniArch runtime bug。修复包括：按实体/组件跟踪同帧 presence、解析 placeholder 模型键、补齐 clone presence、禁止吞掉 clone 异常，并以非零 mutation oracle checks 作为硬门禁。

### CommandStream 最终性能与 stale-only 修正

最终六场景相对拆分锚点均在 ±3% 内；existing 热路径仍保留 consume-time liveness 的主要收益。

| Scenario | 拆分锚点中位数 | 最终中位数 | 变化 |
|---|---:|---:|---:|
| `existing-set` | 11036.2 | 11654.7 | +5.60% |
| `existing-add-remove` | 2410.8 | 2393.9 | -0.70% |
| `create-small4` | 3605.4 | 3515.6 | -2.49% |
| `create-duplicates` | 3574.6 | 3512.5 | -1.74% |
| `create-destroy` | 20683.8 | 20672.8 | -0.05% |
| `snapshot-only` | 72284.8 | 71665.8 | -0.86% |

review 发现 consume prune 删除所有 stale entries 后仍遗留 `_hasStoreCommands=true`，使空提交错误返回 `true` 并可能启动无效 async 工作。修复后 `PruneStaleCommands` 同时刷新 dirty 汇总。同期 A/B 的 `snapshot-only` 中位数从旧汇总行为 65068.6 升至修复后 66444.1（+2.1%）；因此没有用绝对跨时段波动归因。

### HeroComing 连续门禁

最终 runtime 的首个三次序列：

| Run | Movement | Attack | Memory | Result |
|---|---:|---:|---|---|
| 1 | 1821.0 | 1073.3 | OK | PASS |
| 2 | 1827.1 | 1047.5 | OK | PASS |
| 3 | 1666.3 | 976.2 | OK | FAIL（Attack < 997） |

失败后清零连续计数；只读进程检查同时看到 Godot、ChatGPT/Citrix 活跃，说明机器噪声是合理解释，但失败样本不据此删除。重新开始的连续序列：

| Consecutive run | Movement | Attack | Memory | Result |
|---|---:|---:|---|---|
| 1 | 1783.1 | 1003.0 | OK | PASS |
| 2 | 1746.7 | 1096.7 | OK | PASS |
| 3 | 1760.0 | 1075.7 | OK | PASS |

Movement 门槛 1642，Attack 门槛 997；最终连续 3 / 3 PASS，内存均稳定。没有运行 `--update-baseline`。

### Public API diff

相对 3.6 baseline 的有意 breaking diff 仅为：

- `ChunkView.GetComponentSpanAt<T>(int)` → `UnsafeGetComponentSpanAt<T>(int)`；Release 继续保留用户显式选择的 unchecked 高速路径，XML 明确 cached column index 的来源与失效条件，Debug 校验 type/index pair。
- 删除冗余 `Entity.IsUnmappedSentinel`；deferred entity 统一使用 `IsPlaceholder`。
- assembly version：3.6 → 4.0。

### 收口分类

- runtime bug：segment capacity 溢出/提交顺序、结构 scope 异常恢复、stale-only dirty 汇总均已修复并有回归。
- 预防性硬化：component/hierarchy/pending/async preflight 在副作用前拒绝已知非法输入。
- 性能改动：existing Add/Set/Remove 的 record-time `World.IsAlive` 被移除，完整 `(Id, Version)` liveness 只在 consume prune 裁决；placeholder 路由与 Clone 的 record-time 捕获不属于重复检查，保留。
- verifier bug：原始重复 strict Add 来自 lockstep generator，已修；不冒充 runtime bug。
- 文档/API：危险高速 API 未删除，只做诚实局部改名和 XML 契约；过时 proof 用语已降级为 verification/evidence。
- 非目标：Replay 不是通用事务回滚；并行冲突顺序由调用方保证；World 仍要求独占结构写入。
