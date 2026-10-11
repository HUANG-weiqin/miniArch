---
title: MiniArch 正确性验证报告
module: Proof
description: 版本化记录 MiniArch 的测试范围、执行命令、当前证据与明确未覆盖项；不把有限测试表述为绝对安全证明
updated: 2026-10-11
---
# MiniArch 正确性验证报告

## 这个模块是干什么的

- 记录某个明确 commit 上实际运行过的正确性、确定性、性能与内存门禁。
- 区分“当前复验结果”和“历史压力证据”，避免旧数字伪装成当前保证。
- 明确测试没有证明什么；本页不是形式化证明，也不承诺任意输入、并发或资源耗尽下绝对安全。

## 架构

验证分为四层，任一层都不能代替其他层：

| 层 | 入口 | 主要回答 |
|---|---|---|
| 单元/回归 | `MiniArch.Tests`、`HeroPipeline.Tests` | 已知契约与历史 bug 是否回归 |
| 双路径确定性 | Submit、Snapshot→Replay、CanonicalChecksum | 相同输入序列是否收敛 |
| 长程压力 | `MiniArch.Soak`、`MiniArch.LockstepSoak` | ID 复用、free-list、跨 host 是否长期漂移（层级相关历史覆盖已退役） |
| 性能/内存 | `HeroComing.Perf --check-baseline` | runtime 改动是否跌破发布阈值或持续增内存 |

### 当前复验快照（2026-10-11，DestroyMany 冷 span 门槛重定标）

**用户明确授权调整辅助 perf 门槛；本轮仅改变验收政策，不修改 ECS runtime，也不宣称新的提速。** `DestroyMany.Perf` 的 full 50K（旧 ≥1.20×）及 R/M ≥50% sweep（旧 ≥1.00×）改用共享 **≥0.65×** 冷 span 底线。Query 两场景仍 ≥1.20×；Hero 的 1642 / 997 和 baseline 不变。负载、计时窗口、首次 scratch 成本、correctness oracle、分配测量均未改。

先冻结 `951c362` 原程序，事先确定“完整运行 3 轮，取三个受检场景中最低的速度比中位数 ×80%，再向下取 0.05 档”，保留全部样本，不选最快一轮：

| 原程序场景 | 第 1 轮 | 第 2 轮 | 第 3 轮 | 中位数 |
|---|---:|---:|---:|---:|
| full 50K | 1.10× | 1.11× | 1.35× | 1.106319× |
| R25K / 50K | 0.75× | 0.87× | 1.35× | **0.870359×** |
| R40K / 50K | 1.43× | 1.27× | 1.45× | 1.433073× |
| 原程序 exit code | **1** | **1** | **0** | 不改写历史判定 |

最低中位数 `0.870359 × 0.80 = 0.696287`，向下取档得 **0.65**。full 比值用日志 ops/s 推导，sweep 用 for/batch μs 推导，避免只取两位小数的展示值。此底线是开发机上的**粗粒度报警线**，允许 batch 慢于 for-loop，不是“dense 必胜”或严格统计性能基线；校准前后 CPU 采样仍见 dotnet/Godot/Chrome 活动（例如第 2 轮后两个 Godot 合计约 16.6% 整机 CPU），不能据此声称获得全机独占或稳定性能置信区间。

| 最终新政策验证 | 结果 |
|---|---|
| Solution Release build / tests | 0 warnings / 0 errors；MiniArch **1118 / 1118**、HeroPipeline **7 / 7**，0 skipped，均 exit 0 |
| 完整 DestroyMany | **exit 0**；full / R25K / R40K 为 **1.12× / 1.10× / 1.29×**，均高于新底线 |
| 未调整的 Query | 1/16 原型为 **19.28× / 13.77×**，均达到原 1.20×；暖态分配观测仍 0 B/op |
| runtime / Hero 变更检查 | 源码无 diff；校准与最终运行的 MiniArch DLL SHA256 均为 `51A7DE6614CF7F4663178801788B4D632042CE349908163D2C2CE8C9C076894F`。本轮未重跑 Hero；最近必要门禁结果见下一节 |

最终 full 的 1.12× 按旧 1.20× 仍会失败，**新 PASS 不能解释为新优化或旧标准已通过**。harness 的暖分配段是测量输出，不是新的零分配断言；严格分配承诺继续由完整测试中的相应回归保障。版本、包和 `v7.0.0` 未变。

证据目录：`artifacts/destroy-thresholds-20261011/`；预定规则与三轮原始退出码在 `calibrate.ps1` / `calibration-exits.log`，结果在 `calibration-{1,2,3}.log` / `calibration-summary.json`，CPU 采样在 `cpu-*.log`。原 harness DLL SHA256 为 `C3DA34FA34380EE7128E5F16412B7E1D20B3D2D513BC557E31BBA992C35F3546`；最终执行命令与日志见 `verify.ps1`、`verification-exits.log`、`final-*.log`。

### 历史复验快照（2026-10-11，冷 span scratch 与重复工作收敛）

**过量 group scratch 已修复，正确性与原 Hero 门禁通过；冷 span 的全部速度断言尚未通过。** 本轮没有改算法阈值、增加公共 API 或更改 CommandStream 执行/验证逻辑；重复取消的最新合同裁决见 `kb-code-review-findings.md`。版本、`v7.0.0`、本地包与 baseline 均未改变。

| 验证 | 最终结果 |
|---|---|
| Solution Release build / tests | build 0 warnings / 0 errors；MiniArch **1118 / 1118**、HeroPipeline **7 / 7**，0 skipped，含严格全线程分配回归，exit 0 |
| Debug targeted tests | lifecycle、scratch、Clone、invalid shape 共 **86 / 86**，exit 0 |
| 同步 / async / 4-host soak | seed 111，分别 20K / 5K / 10K 帧；350,079 / 87,447 / 587,460 ops；async 含 7,832 Clone，4-host oracle 1,186 checks，均 exit 0 |
| BulletLockstep CLI | Slice 2–9 均 exit 0；chunked storage、持久化、async authority/mirror、Clone/rollback 均通过 |
| HeroComing 原门禁 | **Movement 2008.1 / Attack 1167.0 rounds/s** ≥ 1642 / 997；两场景 memory OK，exit 0，未更新 baseline |
| 容量回归与独立审阅 | 新 `BUG_Destroy_batch_group_scratch_tracks_archetypes_and_grows_when_needed` 在旧 DLL 真正 red（128 > 16），新版通过；非作者审阅未发现容量、alias、版本或 free-list 顺序回归 |

`DestroyMany.Perf` 仍用原场景、窗口和断言，只纠正了“每次新建 World”不应标作 steady-state 的描述。下表是本轮先后运行的原始观察值，不是多轮 A/B 的稳定提升承诺：

| Cold span 场景 | `5d6fe0c` 基准 | 最终源码 |
|---|---:|---:|
| full 50K：分配 B/op | 2,050,877 | **1,201,212**（约减少 41%） |
| full 50K：batch μs / 相对 for 倍数 | 832.5 / 0.86× | **684.1 / 1.01×**（仍低于 1.20×） |
| 删 25K / 50K：batch μs / 倍数 | 770.78 / 0.80× | **653.64 / 0.94×**（仍低于 1.00×） |
| 删 40K / 50K：batch μs / 倍数 | 889.15 / 1.20× | **841.88 / 1.07×**（通过 1.00×） |

因此最终 `destroy-many.log` 和整串 `final/verify.ps1` 都保留 **exit 1**。不降低阈值、不把冷成本移出计时，也不为追逐旧倍数再添加特殊快路径。剩余限制是冷 span 不能保证在所有密度领先单体循环，不影响本轮已验证的容量修正与生命周期合同。

- 首版只分离 group/candidate 容量，实测已省约 0.85 MB，但 full batch 仍 828.5 μs；隔离副本的阶段探针确认 group 与 kill 仍占显著工作后，才复用已有 bulk kill 循环、消除 collection 后的重复 record 校验。探针仅用于诊断，未进入生产代码或替代原基准。
- 同 World 复用的辅助 span 探针（40 warmups + 100 measured，同步线程口径）在 25K / 40K / 50K 三组均 **0 B/op**，最终 batch 为 764.42 / 779.43 / 393.74 μs；不把这些暖数据代替上述冷断言。共享 bulk helper 后 Query 的本次冷 1/16 原型观测为 63.8 / 64.3 μs，暖态 57.6 / 58.5 μs、0 B/op；没有改变 Query 的删除算法。
- CPU 重任务串行，仍采样到外部 Chrome/Godot/claude 活动；未终止外部进程或挑更快样本。async soak 跨 await 的 thread-local 分配数仍不作零分配证据。
- 证据根目录 `artifacts/destroy-scratch-20261011/`：`baseline.log`、`candidate-*.log`、`old-runtime-red.log`、`phases*.log`；最终完整命令与结果在 `final/verify.ps1`、`final/exits.log` 和同目录各步骤日志。
- 最终 runtime 补丁指纹（`git hash-object`）为 `79e40e5a46e3a8d3ca80e6ddf8c98c7b9ff5fe54`；DLL SHA256 为 `6D5CD2BE5B5ECBCAA8520209F566D127D16DA82774019081EA53F8A0F3BFDC66`，已核对 src、测试、Hero 和 DestroyMany 输出副本一致。对照 DLL SHA256 为 `85DCD49432DCD64EDA756F1F3A437A53845FE36FB084355701AD7DF1E0F59044`。两者版本字符串都带父提交 `5d6fe0c`，应以哈希而非版本字符串区分。

### 历史复验快照（2026-10-11，层级遗留收敛）

**必要的正确性与 Hero 门禁通过；补充 DestroyMany 基准不是全绿。** 验证源码原样提交为 `3cf43e7`：Query Destroy/Clear 共路、单体 Clone 局部收敛、移除无生产者的 Destroy-placeholder 分支，详见 `kb-hierarchy-runtime.md`。未升级版本、移动 `v7.0.0` 或重发包。

| 验证 | 结果 |
|---|---|
| Solution Release build | 0 warnings / 0 errors，exit 0 |
| Solution Release tests | MiniArch 1117 / 1117、HeroPipeline 7 / 7，0 skipped；包含独立子进程的全线程严格 0 B 并行 Query 回归，exit 0 |
| Debug targeted tests | lifecycle、destroy scratch、entity/World Clone、invalid explicit shape 共 85 / 85，exit 0 |
| 单 host 同步 soak | seed 111，20,000 帧，350,079 ops，exit 0 |
| 单 host async soak | seed 111，5,000 帧，87,447 ops（7,832 Clone），exit 0 |
| 4-host placeholder soak | seed 111，10,000 帧，587,460 ops，host0 oracle 1,186 checks，exit 0 |
| BulletLockstep 实际 CLI 入口 | Slice 2–9 全部 exit 0；boss=False、weakpoints=0；chunked=True；持久化、async authority/mirror、Clone/rollback 阶段均通过 |
| HeroComing Movement / Attack | **2079.5 / 1121.5 rounds/s**（0.481 / 0.892 ms/round），达到原阈值 1642 / 997，exit 0 |
| HeroComing memory | 两场景均 OK；baseline 未更新 |
| 非作者独立源码审阅 | 未发现本次变更引入的可达语义违例；不代替上述执行验证 |

三个 soak 均为 cap=100、floor=20、ops-per-frame=50；完整命令与原始结果位于 `artifacts/flat-cleanup-20261011/verify.ps1`、`exits.log` 及同目录各步骤日志。验收在提交前的最终源码进行，runtime diff 的 git blob hash 为 `4e49271361a8790ad72a38cc1ff1031b922e8183`，随后没有 runtime 修改。当前 DLL SHA256 为 `AB9B629D078D8ECF4E2ACB1F487288F3276A5B91AD12E3F497261C95F3892271`；其构建版本仍带父提交 `bbc709b`，不能只凭版本字符串把它误认成已发布 DLL。

#### 补充 DestroyMany 对照与未修边界

`DestroyMany.Perf` 使用原场景、计时窗口与阈值。最终版和同一 harness 加载已发布 7.0.0 DLL 的对照**均 exit 1**；因此整串 `verify.ps1` 也保留 exit 1，不报告“所有基准通过”。失败项是本轮未改算法的 cold span 路径：

| span 场景 / 最低 speedup | 已发布 7.0.0 | 本次清理 |
|---|---:|---:|
| 50K 全量 / ≥1.20× | 0.79× | 0.87× |
| 50K 中删 25K / ≥1.00× | 0.88× | 0.73× |
| 50K 中删 40K / ≥1.00× | 0.97× | 0.83× |

吞吐循环每次新建 World，计时内的首次 span Destroy 包含约 **2.05 MB** scratch 分配（50K 全量场景）；它不等于复用 World 的预热后成本，不能据旧经验宣称 dense batch 始终领先。本次不重写 span 策略，也不改阈值/预热方式来掩盖失败。

实际改动的 Query 路径通过原吞吐断言和 correctness oracle：40K 实体、1/16 原型的冷调用分别 **317.7 / 320.0 μs**，旧 DLL 为 799.1 / 742.1 μs；同 World 预热后本次为 **308.3 / 304.5 μs、0 B/op**。`Destroy(query)` 与 `Clear(query)` 同轮对照接近相等；新 scratch 回归直接证明首次 Query 删除不初始化 span scratch。这里只报告观察值，不从一组先后运行的数据承诺稳定提速或认定其他密度没有性能回退。

- 对照脚本：`compare-released.ps1`；日志：`destroy-many.log`、`destroy-many-released7.log`。旧 DLL SHA256 `10805056ABFECAD68FEC609EFC150F37AFD1E2F871854691EA5B053FA19E3B13` 已校验；同一新版 harness 的“shared path”标题不描述旧 DLL 内部实现。
- CPU 重任务串行，但前后采样仍见 Chrome/Godot/claude 活动，未取得全机独占窗口；未重新选择更快样本或刷新 baseline。
- immediate `Create → Destroy → Destroy → Snapshot().Validate()` 在发布版和本次 DLL 均复现 released-entity 拒绝。当时登记为未修风险；2026-10-11 用户明确取消后句柄不得继续作为命令目标，现排除为非法输入，不改运行时校验。最新裁决只维护在 `kb-code-review-findings.md`。
- async soak 的 thread-local 分配数字跨 await 不可用（本次也出现负数）；不采信它来证明零分配。零分配仍以对应严格回归的测量边界为准。其余覆盖限制仍适用下方“明确未覆盖/不承诺”。

### 历史复验快照：扁平迁移（2026-10-10）

验证 runtime：`393ca72`（扁平实体、命令时间线、free-list 版本单源与按需销毁 scratch）。完整命令、日志位置、兼容性见证和验收边界见 [执行与验收记录](../docs/plans/2026-10-10-flat-entities-command-pipeline.md)。

| 门禁 | 本轮结果 |
|---|---|
| Solution build / tests，Release | build 0 warnings / 0 errors；MiniArch 1095 / 1095、HeroPipeline 7 / 7，0 skipped，exit 0 |
| 单 host 同步 soak | seed 111，20,000 帧，350,079 ops，exit 0 |
| 单 host async soak | 原失败 seed 111，5,000 帧，87,447 ops（含 7,832 Clone），exit 0 |
| 4-host placeholder soak | seed 222，10,000 帧，587,181 ops，host0 oracle 3,248 checks，exit 0 |
| BulletLockstep 真实 CLI 入口 | Slice 2–9 全部 exit 0；Slice 5 最终 boss 已销毁、weakpoints 为 0；包含持久化、异步 mirror、回滚重放 |
| HeroComing Movement / Attack | 1727.2 / 1031.2 rounds/s，达到原阈值 1642 / 997，exit 0 |
| HeroComing memory | 两场景均 OK；未运行 `--update-baseline` |
| 独立源码审阅 | 未发现新的已证实可达违约；不是对所有未来输入的证明 |

**性能证据限制**：团队 build/test/perf 已串行，外部 7 个 Python 高负载进程及两个 Godot 进程仍在运行，未取得全机干净窗口。同环境原始 `a98f4d3` 对照为 Movement 1771.9 / Attack 945.9，exit 1（Attack 未达阈值）。本轮只报告最终版的门槛检查通过，不据单轮有干扰数据声称性能提升、精确回退幅度或发布新 baseline。

**同 runtime 追加复测（2026-10-10）**：再次运行 Release `HeroComing.Perf --check-baseline`，Movement **1974.4 rounds/s（0.506 ms/round）**、Attack **1236.9 rounds/s（0.808 ms/round）**，两项均过原阈值，memory OK，exit 0，未更新 baseline。日志：`artifacts/flat-command-20261010/perf-repeat-20261010-2124.log`。前后 CPU 采样未再见先前的满核 Python 负载，但 Godot 仍活跃；代码未变，不把相对前次的数值回升解释为代码提速。

**覆盖限制**：async Restore 回归没有人为阻塞 worker，不能声称强制覆盖了两个 worker 同时未完成；其数据隔离另由 frozen-state 静态调用链审查支持。多 host 引用 oracle 只对 host0 直接 Create 的自引用断言，不是任意引用图模型。async soak 的跨线程分配计量无效，不采信其分配数字（见 `kb-soak-test.md`）。

### 重构前后交错 A/B（2026-10-10）

**结论**：本轮重构后的端到端吞吐中位数高于重构前：Movement **+10.9%**、Attack **+10.7%**。但旧版波动较大，且不是全机无干扰窗口；这些是本次观察值，不是稳定收益保证，也不能归因于某一项 core 优化。

- A：重构前 `a98f4d3`，独立干净工作树；B：runtime `393ca72`（运行时 HEAD `0a36151`，后续差异仅文档）。
- 同机 AMD Ryzen 7 5700X3D（8 核 / 16 逻辑 CPU）、.NET SDK 8.0.425，22:01–22:08 Europe/Paris。两版先串行 Release build，再按 **A1 → B1 → B2 → A2 → A3 → B3** 各启动独立进程；运行命令为 `dotnet run -c Release --no-build --no-restore --project tools/perf/HeroComing.Perf/HeroComing.Perf.csproj --check-baseline`。
- 每场景仍是 500 players + 500 enemies、100×100 grid、50 轮预热后测量 30 秒。计时包含请求生成、`StepUntilStable()` 和原有每 100 轮诊断输出，不含建世界、spawn 与预热。旧 hierarchy owner 查找到 `ActionOwner` 的业务迁移在比较范围内；B 新增数量/效果守卫在计时外。独立源码核对未发现迁移导致少处理角色或跳过效果的证据。

| 配对编号 | A Movement | B Movement | A Attack | B Attack |
|---|---:|---:|---:|---:|
| 1 | 1884.0 | 2048.3 | 1086.0 | 1202.1 |
| 2（B 先跑） | 2001.0 | 2089.9 | 1140.4 | 1184.8 |
| 3 | 1588.8 | 2135.9 | 964.1 | 1214.5 |
| 中位数 | **1884.0** | **2089.9** | **1086.0** | **1202.1** |

单位均为 rounds/s；变化按 `median(B) / median(A) - 1` 计算。三对 Movement 差异分别 +8.7% / +4.4% / +34.4%，Attack +10.7% / +3.9% / +26.0%，可见幅度并不稳定。未剔除 A3 或选择性保留较快结果。

**门槛与限制**：B 三次全部 exit 0；A1/A2 exit 0，A3 两项低于原阈值 1642/997、exit 1，因此批次控制脚本也返回 1，不称“六次门禁全过”。12 个场景的 memory 均 OK，无异常中断，baseline 未改。每次运行前后采样仍见 Godot、Chrome、claude 活动，A3 前另见 dotnet 活动；采样不是运行全程跟踪，也不覆盖无权限读取 CPU 的进程，无法证明 A3 掉速的具体原因或全机无干扰。

原始日志、各次退出码/时间、前后 CPU 采样、构建日志、DLL SHA256、解析后的 `metrics.json` 与执行脚本保存在本地 `artifacts/flat-command-20261010/ab-2157/`。

### 历史复验快照（2026-08-06）

下表仅记录当时 runtime tree 的结果，不是本轮 flat 迁移的最终验收；本轮结果须以对应工作树和参数的实际门禁输出为准。

| 门禁 | 当时结果 |
|---|---|
| `MiniArch.Tests` Release | 1157 / 1157 PASS |
| `HeroPipeline.Tests` Release | 5 / 5 PASS |
| HeroComing Movement | 1981.2 rounds/s，阈值 1642 |
| HeroComing Attack | 1193.5 rounds/s，阈值 997 |
| HeroComing memory | OK；baseline 未更新 |
| single-host / multi-host soak | 当时未复跑；只作为下节历史证据，不冒充本轮 flat 迁移结果 |

2026-07-15 发布候选的逐次参数、soak 与 perf 证据仍保存在
`docs/plans/2026-07-15-quality-hardening-4-evidence.md`；它只证明当时列出的 commit 和参数。

### 历史压力证据

2026-07-06 的 soak 批次曾覆盖 224 个 diversity seed、最高 5,000,000 帧单次运行，并发现 B1-B6 六类 Submit/Replay 分歧。它证明当时版本在这些样本上通过，不等于当前或未来版本“完备无 bug”。历史 bug 索引见 `kb-code-review-findings.md`，工具和参数见 `kb-soak-test.md`。

## 决策

- **确定性是核心契约**：同样的初始状态、输入序列、组件注册顺序和合法调用时序，Submit 与 Snapshot→Replay 必须得到相同 logical state 与 allocator 演化。
- **用版本化证据，不用绝对措辞**：禁止“完全正确”“已证明不存在同类 bug”“发布级正确性”之类无法由有限测试推出的结论。
- **Checksum 是 oracle，不是事务机制**：它能发现结果分歧，不能撤销已经发生的部分修改。
- **语义校验显式、能力守卫强制**：`CommandStream.Validate()` 显式检查 placeholder/layout、组件 presence，且不改 World；默认 consume 不隐式执行 presence 校验。pending slot、显式/embedded Entity 引用与布局、FrameDelta 预算、async ownership 仍在必须的 consume 边界强制守卫。两者都不是通用 rollback journal。

## 认知模型

把验证理解为持续收紧的反例搜索：

```text
相同输入
  ├─ Submit ─────────────→ source
  └─ Snapshot → Replay ─→ replica
                    ↓
      checksum + validator + diff
```

测试通过表示“在所列范围内没有观察到反例”，不是“所有未来状态都已证明”。

### 明确未覆盖/不承诺

- `CommandStream.Replay` 没有通用事务回滚；不可信 delta 应先 `FrameDelta.Validate()`，但 target-world 兼容性或运行期失败仍可能在 replay 中途留下修改。
- 默认 `Submit`/async consume 不做 component-presence 语义 preflight；未先显式调用 `Validate()` 时，apply-time 违规可能留下前序修改。`Validate()` 也不覆盖 CreateMany 组一致性、pending-slot 状态或 FrameDelta 预算。
- 灾难性 `OutOfMemoryException`、进程终止、硬件故障不保证 rollback。
- `Unsafe*` API 依赖 XML 中的调用方前置条件；违反契约属于未定义/不安全使用。Query Destroy/Clear 都是普通结构变更，会使既有 view/span/accessor 借用失效。
- World mutation 与 `CommandStream` 都不支持并发写；Query 的并行只读迭代必须与结构变更隔离。
- `CanonicalChecksum` 不能替代跨版本 schema 握手：v5 要求两端精确类型 identity 与字段 shape 一致，但 checksum/snapshot 本身不要求相同组件注册顺序。FrameDelta wire 仍使用 runtime component id，交换方必须先建立一致 registry schema。

## 入口

- 完整测试：`dotnet test -c Release miniArch.sln`
- 单 host sweep：`dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 100000`
- 决定性长跑：`dotnet run -c Release --project tools/soak/MiniArch.Soak -- --determinism --frames 200000`
- 多 host：`dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --sweep 8 --frames 10000 --hosts 4`
- 架构性能门禁：`dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
- 已知 bug/非 bug：`kb-code-review-findings.md`

## 坑点

- 不要把不同日期、不同 commit、不同参数的结果合并成一个“证明总量”。
- 不要用单次幸运性能结果闭环；热路径需独立进程、多次运行并看中位数。
- `--no-build` 只适用于确认 profiler 输出已包含当前 `MiniArch.dll`；改 runtime 后先显式 Release build。
- 不要运行 `--update-baseline` 来掩盖回归；刷新阈值需要人工单独确认。
