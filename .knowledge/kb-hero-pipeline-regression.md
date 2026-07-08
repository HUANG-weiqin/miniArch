---
title: Hero Pipeline Regression Test
module: HeroComing.Perf
description: First-class regression gate for architecture changes — 30s timed throughput test; includes --compare-old-value-tracking (boundary-diff API vs dense/dict shadow-diff vs explicit dense shadow-diff) and PipelineBenchmarkTests history reference
updated: 2026-07-08 (新增 DenseValueDiff explicit diff 四路对比)
---
# Hero Pipeline Regression Test

## 这个模块是干什么的

- 架构变更的一等回归门禁——改完必须跑它，通过才能提交
- 30 秒固定时长吞吐量测试，覆盖 movement + attack 两条链路
- 检测内存泄漏（heap delta 必须稳定）

## 架构

- `tools/perf/HeroComing.Perf/Program.cs`：单文件控制台应用
- 引用 `tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj` 获取 pipeline 代码
- 500 players + 500 enemies on 100x100 grid
- 默认运行只测量并打印结果，不写 baseline
- `--check-baseline`：读取本页阈值并作为门禁比较，低于阈值时进程返回非 0
- `--update-baseline`：人工确认刷新基线时才写回本页，只替换 baseline/阈值区块
- `--track-observer`：打开 `TrackValueChanges<T>()`，真实读取 `Old/New/Entity.Id` 到 checksum，避免 JIT 删除消费路径
- `--compare-old-value-tracking`：独立对比四种 old/new 追踪策略；不能与 baseline/observer flags 组合：
  - `API`：`TrackValueChanges<T>()`
  - `ManualDense`：`entity.Id -> int[]` dense shadow-diff
  - `ManualDict`：`Dictionary<int,int>` shadow-diff
  - `ExplicitDiff`：`World.CreateDenseValueDiff<TComponent,TValue,TProjector>()` 官方显式 dense shadow-diff（新增）

## 当前 baseline（2026-07-06）

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

### `--compare-old-value-tracking` 设计说明（2026-07-08）

这个模式用于回答两个问题：

- 内建 `TrackValueChanges<T>()` 距离“同一 `Entity.Id` 模型下的最优手写 dense 方案”还有多远。
- 如果用户不能直接依赖稠密 id，只想写更通用的 `Dictionary<int,int>` shadow-diff，API 相对这种方案如何。

- **MiniArch API**：`World.TrackValueChanges<T>()` boundary diff；`Set<T>` / `CommandStream.Set<T>` 热路径不记录 old/new，`.Changes` 读取时扫描当前 world 并与 baseline 对比。
- **ManualDense**：`ManualGenericTracker<T>` 扫描 `FrameView` 的 current values 快照，round 前后各扫一遍并在 round 后 diff 找出变化；用 `entity.Id -> int[]` dense 索引保存 old values。
- **ManualDict**：`ManualDictionaryTracker<T>` 用同样的前后扫描流程，但 old values 存在 `Dictionary<int,int>`，代表不知道 id 稠密范围时的更通用手写方案。

> **关于 slot-port manual tracker 的驳回**：之前实现了 Hero slot port 写入点记录 old/new 的版本（`ManualTrackerPort` 拦截 `IIntSlotPort.TryRead`+`Write`），该版本：
> - **Hero 特有**：依赖 `IIntSlotPort`、`SlotKey`、`ModifierApplySystem` 的 read-before-write 模式。
> - **作弊**：复用 `ModifierApplySystem` 已读取的 old value，无需自己扫描。
> - 被用户驳回，因为可维护的比较应该代表"不能定位/拦截每个写入点"的通用代码。当前 shadow-diff 版本就是这一替代方案。
>
> 四策略的 `Entity.Id` / `Old` / `New` 都消费到 checksum（anti-JIT sink，值不跨策略一致）。

最新运行（2026-07-08，ExplicitDiff API 新增后，未刷新 baseline）：

| Scenario | API rounds/s | ManualDense rounds/s | ManualDict rounds/s | ExplicitDiff rounds/s | Explicit/Dense | Explicit/Dict |
|---|---|---|---|---|---|---|
| Movement | 1569.8 | 1919.3 | 1859.4 | 1925.0 | 1.003 | 1.035 |
| Attack | 977.5 | 1152.8 | 1137.9 | 1126.5 | 0.977 | 0.990 |

注意：Movement 的 `changes/round` 四者都为 500。API 使用 baseline-to-current net diff；`PositionR` 的 no-op 写入不会产出变化条目。ExplicitDiff 使用 dense shadow-diff，语义等价 ManualDense，性能达到 ≥0.977×。

Baseline gate 同时通过（Movement 1983.4 ≥1642, Attack 1193.4 ≥997, 内存 OK），数据仅供门禁验证记录，不更新 baseline。

**结论**：

- **`ExplicitDiff`（`CreateDenseValueDiff<TComponent,TValue,TProjector>()`）达到 ManualDense 的 98–100% throughput**（Explicit/Dense 0.977–1.003），验证了官方显式 dense shadow diff 路线可行且高性能。
- `API`（`TrackValueChanges<T>()`）仍约 0.81–0.84× ManualDense——这是 boundary diff 作为便利/透明 API 的折衷，不是性能路线。价值在于：
  - **统一 API**：覆盖 `World.Set<T>`、`CommandStream.Set<T>`、`GetRef<T>` 直接写和 chunk span 直接写。
  - **Set 热路径隔离**：value tracking on/off 不改变 `Set<T>` / `CommandStream.Set<T>` 写入路径。
  - **net diff 语义**：A→B→A 自动取消，A→B→C 折叠为单条，消费端无需去重。
  - **低/零稳态分配**：预分配数组，稳态无 GC。
  - **无 no-observer 开销**：不调用时 registry 为 null；`Set<T>` 路径无 value-tracker 分支。
- 需要最高吞吐的场景应使用 `CreateDenseValueDiff`（ExplicitDiff）；需要便利/自动语义的使用 `TrackValueChanges`（API）。二者独立共存。

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
