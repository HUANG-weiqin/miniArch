# FrameLookup Long-Run Report

## Completion

- M0 新 worktree 与基线确认：done。
- M1 发布 API 契约草案：done，见 `docs/plans/2026-07-11-frame-lookup-api-design.md`。
- M2 Direct ForEach ValueLab：done，新增 `CompactRowDirectForEach` 对比。
- M3 发布形态 Correctness Matrix：done，新增 DirectForEach consistency 与 rebuild publish lifecycle case。
- M4 API Gate Report：done，结论 **Conditional Hold**，见 `docs/plans/2026-07-11-frame-lookup-api-gate-report.md`。
- M5 Production TDD Skeleton：skipped，M4 不是 Go。
- M6 Compact FrameLookup v1：skipped，M5 未进入。
- M7 Rows Builder v1：skipped，M6 未进入。
- M8 Docs, Examples, Perf Gate：skipped，未触及 production。
- M9 Final Review Package：done。

## Test Stats

- Baseline build：`dotnet build -c Release miniArch.sln` pass，0 warnings / 0 errors。
- Baseline tests：`dotnet test -c Release miniArch.sln` pass：`MiniArch.Tests 913 passed`，`HeroPipeline.Tests 5 passed`。
- ValueLab correctness：
  - `dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only` pass。
  - `dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000` pass。
- ValueLab perf：
  - `--quick` pass。
  - `--full` pass。
- `git diff --check`：pass。
- HeroComing.Perf：not applicable，本轮未修改 `src/MiniArch/**` 或 `tests/HeroPipeline.Tests/**`，M4 gate 未进入 production。

## Key Findings

### What worked

- Compact CSR row-ref lookup 本体继续成立。
- Correctness matrix 扩展到 public-shape 语义后仍通过：DirectForEach consistency、Rebuild publishes new result、NoGrow failure 均稳定。
- 高水位 build 分配维持 0B。
- `realistic` 场景中 CompactRowDsl 仍是最强 row-capable 方案：build 0.65ms、totalRow 1.99ms。
- `full-1m` 场景中 CompactRowDsl：build 12.81ms、totalRow 149.48ms，仍优于 Dictionary component path 的 build 20.58ms + total entity+component 177.67ms，且 Dictionary build 分配约 21MB。

### What was skipped and why

- M5-M8 全部跳过，因为 M4 结论是 Conditional Hold，不允许进入 `src/MiniArch/`。
- 未运行 HeroComing.Perf：无 production 架构变更，且 Gate 已停止在 ValueLab/report 层。

### API decisions

- `FrameLookup<TKey>` 定位仍是 **key -> component rows**。
- Snapshot 内 build once、不做 live cache、不做 automatic freshness tracking。
- 不发布逐 row `ForEach(key, consumer)` v1：正确但太慢。
- 不发布 entity-only / hot bucket / low-Q 伪通用 API。
- 不发布 LinkedRow / DenseInt / EntityArray 为通用 v1。

### Performance boundary

Final M4 `--full` DirectForEach 对比：

| Scenario | CopyRowRefs rowComp | DirectForEach rowComp | Direct / Copy |
|---|---:|---:|---:|
| realistic | 0.51ms | 6.87ms | 13.41x |
| hot | 51.63ms | 87.36ms | 1.69x |
| full-1m | 67.81ms | 134.51ms | 1.98x |

这说明 DirectForEach 的 per-row consumer 调用成本吞掉了省下的 row-ref copy。它不能作为发布级 v1。

## Restore Points

- Worktree：`E:\godot\arch\miniArch\.worktrees\frame-lookup-public-api`。
- Branch：`exp/frame-lookup-public-api`。
- Base：`9f0c637 docs: report frame read models value lab verdict`。
- Last safe milestone commit before final report：`7bb9381 docs: gate frame lookup public api`。

Commits created：

1. `80941c4 docs: draft frame lookup api contract`
2. `d38c052 perf: compare frame lookup direct foreach`
3. `9584fc4 test: cover frame lookup public shape correctness`
4. `7bb9381 docs: gate frame lookup public api`

## Next Steps

- Smallest next action：重新设计 consume terminal，不进 production。优先验证 **chunk-run/batched consumer**，用一次 callback 处理同 chunk contiguous row run，避免 per-row interface call。
- 第二候选：把 `CopyRowRefs` 作为 caller-provided span 的低层 terminal 重新审查，名字必须诚实，生命周期必须文档化。
- Risks to address：
  - DirectForEach 如果继续逐 row callback，会把 API 做漂亮但做慢。
  - hot bucket / entity-only 仍不是 compact row-ref 的适用区间。
  - 任何 production 重启都必须先更新 design + correctness + perf gate，再进 `src/MiniArch/`。
