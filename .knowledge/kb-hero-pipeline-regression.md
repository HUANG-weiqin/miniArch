---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes — 30s timed throughput test; PipelineBenchmarkTests history reference (old --track-observer / --compare-old-value-tracking removed with old change tracking API)
updated: 2026-08-15
---
# Hero Pipeline Regression Test

## 这个模块是干什么的

- 架构变更的一等回归门禁——改完必须跑它，通过才能提交
- 30 秒固定时长吞吐量测试，覆盖 movement + attack 两条链路
- 检测内存泄漏（heap delta 必须稳定）

## 已知的残余回退归因（2026-08-05，已修正）

> **修正**：早先二分定位 `0fca3eb preflight embedded deferred placeholders`（2026-07-25）造成 ~13-16% 回退，后续发现该结论被 Windows Defender（`msmpeng` 周期性扫描）污染——同代码在不同时段测量波动可达 ±10-35%，`0fca3eb` 的"坏"测量恰落在污染窗口。当前认知：HEAD 相对 07-06 baseline 存在 ~5-10% 量级的真实差距，但精确量化在本机不可靠（需排除 repo 目录或等待干净窗口）。

**已实施的优化（placeholder 校验语义保持、Remove-only 契约修正；2026-08-05 经 Advisor 两轮审阅，门控 A/B 实测 +7%/+13%）**：record 阶段在批写入咽喉点 `CommitBatchComponent` 和 store 路径（`CommandStream.Add/Set`、`ParallelCommandStream.Add/Set`）探测值是否可能含 placeholder；命中或布局不可验证（Entity-bearing Auto-layout 或真正 unresolved layout）时置**帧级单调 flag**，preflight 首行读 flag，0 直接 return，1 跑原全量逻辑。探测只吞已知布局异常（Unknown 也置位，由 preflight 在原有时机抛）。store 的 preflight/ReplacePlaceholders 延迟到首个 payload entry 才取 offsets，Remove-only/空 store 不被同帧无关 placeholder 或 deferred Create 拖累（P2）。Remove-only 不再触发布局扫描属有意行为变更（有契约测试）。`GetOffsets` 缓存发布顺序修复（slot 先写、外层后发，slot 读写改 Volatile）。`dotnet test` 1134/1134 通过，fuzz 11/11，门禁通过。

> **性能结论口径（2026-08-05 门控 A/B 实测后更新）**：msmpeng 门控、同窗口交错测量（每版本 2 轮）：优化后相对未优化 HEAD **+7%/+13%**（Movement 1873-1922 vs 1768-1798；Attack 1110-1127 vs 988-1001）；相对 07-06 baseline 仍 -9%/-12%（baseline 2092-2104/1261-1290）。残余差距两部分：record 探测开销 ~4.6%（每组件一次 flag volatile 读 + offsets 数组查表）+ 269 提交系列其他成本 ~5%（E1 全移除 preflight 后仍比 baseline 低 ~5%）。

> **性能结论口径（2026-08-06 零校验重构最终态，门控交错 A/B）**：T2.8（`Validate()` 抽取 + `FieldKinds<T>` 守卫）3 轮中位 **Movement 2032 / Attack 1205**；同窗口 baseline `c79f464`（零检查参考）2099/1278——守卫成本 **-3.2%/-5.7%**；相对 07-25 未优化 HEAD（1768-1798/988-1001）快 **13-15%**；相对 08-05 优化版 main（1873-1922/1110-1127）快 **5-8%**。门禁 80% 阈值（1642/997）余量 **+23%/+21%**。

> **成本分解**：守卫（FieldKinds 探测 + flag 扫描器）~1-2%（每值 1 static 读 + 分支，仅 flagged 帧跑扫描）；`Validate()`/存在性校验抽离后 Submit 零语义校验的净收益 ~2-5%。验收线口径：07-06 baseline（2052.7/1246.8）是**含隐式 preflight 时代**的历史测量，任何"零语义校验 + 占位符守卫"实现都无法回到该值（T2 纯零检查单测 ~2044，仍在 -0.4% 噪声带内）——当前验收以门控 80% 阈值 + 同窗口 A/B 为准。

## 运行前提（重要）

**跑门禁前必须确认没有并发高 CPU 进程**（其他 perf 基准、`testhost`、`dotnet run` 的其它基准项目等），否则吞吐量会虚低 30-50%，造成假 FAIL。

- 判断方法：`Get-Counter '\Process(*)\% Processor Time'` 检查非 idle 的高占用进程，或看任务管理器。
- **`msmpeng`（Windows Defender 引擎）是本机周期性干扰源**（实测 34% CPU，周期几分钟，可造成同代码 ±10-35% 波动）：跑门禁前/中检查 `Get-Counter '\Process(msmpeng)\% Processor Time'`，>5% 就等 20-30s。
- 2026-08-05 实测：`testhost`（139% CPU）并发时 HEAD 测出 1137-1183 / 616-667 rounds/s（假 FAIL）；进程消失后同一代码测得 1748-1753 / 1027 rounds/s，门禁 PASS。
- 门禁输出出现"中途掉速"（前 1000 轮 ~1750 rounds/s 后段跌到 ~620-1000）也是并发干扰的信号，先清场再重跑。

## 架构

- `tools/perf/HeroComing.Perf/Program.cs`：单文件控制台应用
- 引用 `tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj` 获取 pipeline 代码
- 500 players + 500 enemies on 100x100 grid
- 默认运行只测量并打印结果，不写 baseline
- `--check-baseline`：读取本页阈值并作为门禁比较，低于阈值时进程返回非 0
- `--update-baseline`：人工确认刷新基线时才写回本页，只替换 baseline/阈值区块
- ~~`--track-observer`~~（已删除——旧 `TrackValueChanges<T>()` API 随 Watch 重构移除）
- ~~`--compare-old-value-tracking`~~（已删除——旧 `CreateDenseValueDiff<TComponent,TValue,TProjector>()` 四路对比随 Watch 重构移除）

## 当前 baseline（2026-07-06，截至本日期未更新——需人工 `--update-baseline` 刷新）

| 链路 | 吞吐量 rounds/s | 平均耗时 ms/round | 总轮数 | 内存稳定性 |
|---|---|---|---|---|
| Movement（无 collision） | 2052.7 | 0.5 | 61582 | 稳定 |
| Attack（含 collision） | 1246.8 | 0.8 | 37404 | 稳定 |

### 回归阈值

- Movement 吞吐量：≥1642 rounds/s（baseline 的 80%）
- Attack 吞吐量：≥997 rounds/s（baseline 的 80%）
- 内存：heap delta 不能持续增长（允许 ±10% 波动）
### 如果失败

吞吐量低于阈值 → 用 `kb-profiling-workflow.md` 的 CPU 采样流程定位热点：

```powershell
# 冷路径（query refresh/matching）：
dotnet run -c Release --project tests\MiniArch.Benchmarks -- profile-query --scenario with-all --temperature cold --entity-count 100000 --duration 8 --warmup 1

# 热路径（steady-state traversal）：
dotnet run -c Release --project tests\MiniArch.Benchmarks -- profile-query --scenario with-all --temperature hot --entity-count 100000 --duration 8 --warmup 1
```

已知热点路径见 `kb-cache-optimization.md` 热路径分析表 + `kb-query-invalidation.md`（`EnsureRefreshed` 快路径 vs `AppendNewArchetypes` 慢路径）。

> **阈值说明**：baseline × 80% 四舍五入。随 baseline 刷新同步（当前：2052.7 × 80% ≈ 1642，1246.8 × 80% ≈ 997）。

### 旧 `--compare-old-value-tracking` 设计说明（2026-07-08，部件已删除）

> 旧比较模式（`TrackValueChanges` vs `ManualDense` vs `ManualDict` vs `ExplicitDiff`）随 2026-07-09 Watch API 重构全部删除。旧 API `TrackValueChanges<T>()`、`CreateDenseValueDiff<TComponent,TValue,TProjector>()`、`IValueProjector`、`IValueChangeSink` 已不存在。新 Watch API 是唯一变更追踪入口，不再需要跨策略对比。
>
> 历史数据（2026-07-08）：ExplicitDiff 达到 ManualDense 的 0.977–1.003× throughput，验证了 dense shadow diff 路线可行。此结论已融入 `ChangeWatch` 实现。<br/>
> 当时 baseline：Movement 1983.4 / Attack 1193.4，内存 OK。

### PipelineBenchmarkTests（历史参考，非门禁）

这是 per-operation cycle 计数的微基准（门禁是 HeroComing.Perf 的 rounds/s，详见 `kb-perf-harnesses.md`）。

| Benchmark | Cycles/sec |
|-----------|-----------|
| Movement | 48,883 |
| Simple Attack | 25,946 |
| Attack + Trigger | 17,320 |
| Full Card Play + Collision | 13,678 |
| Full Card Play to Armor | 13,685 |

**架构**：`tests/HeroPipeline.Tests/PipelineBenchmarkTests.cs` + `Fixtures/CoreTestFixture.cs`。源码按原始命名空间（`Hero.*`）原样拷贝，使用 `Microsoft.NET.Sdk` 而非 `Godot.NET.Sdk`。数据日期 2026-05-29。**不跨工具比较 cycles/s 与 rounds/s**。

> 注：上表各测试命名仍为 `_20Seconds`（如 `Movement_20Seconds`），但实际运行时长约 3 秒（`sw.ElapsedMilliseconds < 3000`）。该命名是历史遗留，数值无变化时仍可用于 before/after 对比，但不应理解为 20 秒测量周期。
