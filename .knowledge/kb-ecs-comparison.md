---
title: 跨 ECS 性能对比
module: MiniArch.Benchmarks
description: 版本化记录 MiniArch 与 Friflo、Arch、DefaultEcs 的场景测量；区分当前结果、历史数据与可比性限制
updated: 2026-10-10
---
# 跨 ECS 性能对比

## 这个模块是干什么的

- 记录同一 harness、同一场景内的跨库测量，不把不同工具或日期的数据混算。
- 当前 Friflo 复测显示各有强项，不能概括 MiniArch 全面领先。以下单轮数据不是稳定收益保证，也不是重构前后 A/B。
- `HeroComing.Perf` 是单库架构门禁，不是跨库比较；其结果见 `kb-hero-pipeline-regression.md`、`kb-safety-proof.md`。

## 架构

### 当前 FrifloGameScenarios 复测（2026-10-10）

- 入口：`tools/perf/FrifloGameScenarios.Perf`；MiniArch runtime `393ca72`（测量 HEAD `2e342c8`，后续仅文档），Friflo **3.6.0**、Arch **2.1.0**，不是对竞品最新版本的声明。
- AMD Ryzen 7 5700X3D（8 核 / 16 逻辑 CPU）、.NET SDK 8.0.425。Release build 0 warnings / 0 errors；默认完整一轮 **15 场景 × 3 库**，45 项齐全，结束标记存在、exit 0，无运行异常。
- 每个实例先构造，再预热 10 秒、强制 GC，随后测量 10 秒 `RunIteration()`；各场景固定 **MiniArch → Friflo → Arch**，库间强制 GC。不是随机顺序或多轮统计；构造成本不在吞吐内。

单位为各场景的 **轮/s**（程序列名 `Ops/s`），不是底层 entity 操作数。末列按 `MiniArch / Friflo - 1` 计算。

| 场景 | MiniArch | Friflo | Arch | Mini vs Friflo |
|---|---:|---:|---:|---:|
| S1 BulletHell | 19311.5 | 18420.1 | 16296.8 | +4.8% |
| S2 MMOZone | 57921.5 | 60612.1 | 52153.2 | -4.4% |
| S3 WaveSpawner | 9864.6 | 8363.9 | 7901.4 | +17.9% |
| S4 BuffSystem | 6546.0 | 6703.2 | 4928.3 | -2.3% |
| S5 FullGameLoop | 24275.9 | 25372.1 | 24651.0 | -4.3% |
| S6 RPGStats | 26325.3 | 21628.4 | 26029.1 | +21.7% |
| S7 ConditionalEffects | 72724.1 | 72602.9 | 68865.9 | +0.2% |
| S8 AIStateMachine | 1287.9 | 1397.8 | 928.7 | -7.9% |
| S9 TeamAlternation | 64006.1 | 61096.3 | 54990.2 | +4.8% |
| S10 MixedLoad | 35893.0 | 30337.1 | 23849.1 | +18.3% |
| S11 RandomEntityAccess | 11647.9 | 9205.3 | 9774.2 | +26.5% |
| S12 FollowTheLeader | 9836.1 | 13347.6 | 8343.6 | -26.3% |
| S13 FragmentedQueries | 29988.3 | 27799.7 | 24043.3 | +7.9% |
| S14 ParallelBulletHell | 30132.9 | 24147.4 | 30916.9 | +24.8% |
| S15 SeqBeatPar（顺序 vs 并行） | 8147.8 | 22123.9 | 32959.5 | -63.2% |

**读数**：以同场景 Friflo 为参照，常规 S1–S13 有 5 项差异 >+5%、2 项差异 <-5%，其余 6 项在 ±5% 内；这里的 5% 只是展示区间，不是显著性阈值。最明显弱项是 S12 跨实体跟随，其次 S8 状态切换。S15 必须按下节的策略差异解读，名字不代表本轮顺序版本获胜。

**GC**：S1–S13 三库测量窗口均为 `0/0/0`。S14 MiniArch `26/0/0`、Friflo `0/0/0`、Arch `27/0/0`；S15 分别 `0/0/0`、`0/0/0`、`29/0/0`。没有采集分配字节数或长期 heap 曲线，不据此声称零分配、无泄漏或确定性内存占用。

**环境与证据**：22:39–22:54 Europe/Paris；前后采样仍见 Chrome、claude 等活动，开始前见约 99% 单逻辑 CPU 的 dotnet 活动，未确定来源。没有全程 CPU 跟踪，也不覆盖不可读取 CPU 的受保护进程，不能称全机无干扰。完整原始结果、构建/环境/CPU 日志及 `metrics.json` 在本地 `artifacts/flat-command-20261010/friflo-2235/`；原始负载、依赖版本和 Hero baseline 均未改。

### 历史页内记录（本轮未复测）

以下旧数字与机制解释仅保留为历史参考，不代表当前源码语义、当前性能保证或上述新数据的归因。

#### 正常负载（~50K 实体，无额外结构操作）

| 指标 | MiniArch | Arch | DefaultEcs |
|---|---|---|---|
| 吞吐量 (rounds/s) | **496** | 386 | 96 |
| 堆内存/tick | **0–3.6 KB** | 1.5–16 KB | ≥6 MB (GC Gen0) |
| 内存波动 | **无** | 持续增长 | 锯齿形 GC 抖动 |

#### 结构操作压测

| 结构 ops/tick | MiniArch | Arch | vs Arch | DefaultEcs | vs DefaultEcs |
|---|---|---|---|---|---|
| 40K | **379** | 329 | +15% | 73 | +416% |
| 640K | **28** | 24 | +17% | 5.1 | +444% |
| 10.24M | **1.1** | 1.1 | +2% | 0.19 | +490% |

MiniArch 全程堆内存 delta <4KB，零 GC；Arch 在 5.12M ops 时堆内存飙至 1GB。

#### GameTickSim 13 场景

- MiniArch 在结构变更频繁的混合负载中领先（E-MixedFullTick +22% vs Arch）
- Arch 曾是迭代主导负载的领先者（旧版 K-BulletHell），但 **Archetype 扁平化后 MiniArch 反超 Arch +45~47%**（每 tick 200 实体创建 + 200 毁灭 + 15-30K 运动迭代 + 碰撞扫描，2026-07 实测 GameTickSim vs Arch 基线）
- 实测验证：`GetChunks() + GetSpan<T>()` vs `EachSpan<T>` 在 K-BulletHell 下性能差距在 0.2% 噪声内，说明 `EachSpan` 的 ref struct 包装已被 JIT 完全内联消除

#### 历史机制解释（MiniArch vs Arch，未按当前源码复核）

| 方面 | MiniArch | Arch |
|---|---|---|---|
| Query 缓存粒度 | 预计算 `Archetype[]` + `ChunkView[]` 快照 | `Archetype[]` 列表 |
| 失效机制 | archetype 数量 + per-archetype segment 数（两段式） | Archetypes 集合 hash code | archetype 数量比较 |
| 组件拷贝 | 仅重叠组件 | 全量拷贝 |
| Set 快路径 | 已有组件时 in-place set | 总是迁移 |

> 注：上表"失效机制"行的"重建策略"维度见 `kb-query-invalidation.md`——MiniArch 是**增量 append**（只扫新增 archetype），不是全量重建。

#### 旧场景的机制概括

- 512-bit bitmask 匹配 vs hash-based full rescan（Arch 每次重新计算 all）
- 实体迁移只拷贝重叠组件（遍历 destination signature 逐个匹配）
- Set 快路径避免不必要的 archetype migration
- 所有操作不产生临时对象
- Archetype 扁平化消除了 multi-chunk 开销

#### 旧场景的结论

- MiniArch 优势在结构操作适中时最大（+34% vs Arch）
- 极端负载下差异被带宽压平，但内存确定性成立
- 对游戏服务器和帧率敏感场景 = 同性能 + 零 GC 暂停

## 决策

- S1–S13 用来观察各自负载下的当前实现，不以单轮近似值宣称全面领先或显著差异。
- S14 三库计算式与 100K 实体规模一致，但分区不同：MiniArch 的 `Query.ForEachChunkParallel` 会在 chunk 少于 CPU 时切为实体子区间；Friflo 使用 `ParallelJobRunner`；Arch harness 对预收集的 `Chunk[]` 做 `Parallel.For`。这是当前策略对比，不是相同调度器/分区的实验。
- S15 明确是 MiniArch 顺序版本对 Friflo/Arch 并行版本，单独列示；不能将该差距表述为同策略内核速度差距。
- 独立只读核对未见 S13 漏查询、S10 少新增/少销毁等明确跳工作证据；这不等于完整跨库结果等价证明。发现差距后先做专项或分段测量，再解释原因。

## 认知模型

- 计时循环累计的 checksum 随迭代次数变化，不能直接比较跨库相等性；它不是功能正确性 oracle。
- `GC Gen0/1/2` 是收集次数，不是分配字节数。尤其不能把 `0/0/0` 转写成“零分配”。
- 场景名是负载标签；不同场景一轮的工作量不同，不能相加吞吐量生成“总分”，也不与 Hero 的 rounds/s 混比。

## 入口

- 跨 Friflo / Arch 场景：`tools/perf/FrifloGameScenarios.Perf/Program.cs`、`ArchScenarios.cs`、对应 `.csproj`。
- 默认完整运行：`dotnet run -c Release --project tools/perf/FrifloGameScenarios.Perf`。
- 首个参数是场景名前缀；只跑一个场景宜传完整名称，例如 `-- S12-FollowTheLeader`。`S1` 会同时匹配 S10–S15，不是仅 S1。
- 其他工具与单位消歧：`kb-perf-harnesses.md`；历史 GameTickSim 数据：`kb-gameticksim-scenarios.md`。

## 坑点

### FrifloGameScenarios S10 MixedLoad 排查结论

> 以下数据来自 2026-06 历史排查，为 benchmark 伪影分析记录。当前代码可能产生不同数值，此处仅保留作为方法论参考，不反映当下性能状态。

- 旧版 S10 使用 `List<Entity>.RemoveAt(0)` 做 FIFO 删除，会把 benchmark 变成句柄数组搬迁压测，而不是 ECS 混合负载压测。
- 隔离实验结果：
  - `List+Random`：MiniArch 2491 ops/s，Friflo 1121 ops/s，复现约 2.2x 差距。
  - `ListNoRand`：MiniArch 2760 ops/s，Friflo 1170 ops/s，去掉随机读后差距仍在，随机读不是主因。
  - `Queue+Rand`：MiniArch 13517 ops/s，Friflo 13596 ops/s，改用 O(1) FIFO 后差距消失。
  - `QueueNoRand`：MiniArch 31589 ops/s，Friflo 30404 ops/s，真实 create/query/destroy 混合负载接近。
- 机制：`RemoveAt(0)` 每轮 200 次、列表约 15K 元素，约移动 300 万个 entity handle。MiniArch `Entity` 是两个 `int`（8B）；Friflo `Entity` 源码注明 16B（`EntityStore` 引用 + raw id/revision）。因此 Friflo 在这个 benchmark 里搬迁字节量约为 MiniArch 的 2 倍。
- 结论：S10 原始 2.2x 差距是 benchmark 容器选择造成的伪影；用于 ECS 对比时应使用 `Queue<T>` 或 ring buffer 表达 FIFO 生命周期，不应使用 `List.RemoveAt(0)`。
