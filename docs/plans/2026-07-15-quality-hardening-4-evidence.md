# MiniArch 4.0 Quality Hardening Evidence

## 结论

这是质量硬化分支的证据账本，不是性能 baseline。起点 Release build 与全部测试通过；CommandStream 六个场景已完成三次采样。HeroComing 在同一代码上出现一红两绿，证明当前机器噪声足以跨越门槛，最终闭环必须要求连续多次通过，不能使用单次幸运结果。

当前文档只记录起点。`pre-split`、最终结果、API diff、soak 与 lockstep 证据将在对应任务完成后追加；禁止用本文件运行或暗示 `--update-baseline`。

## 环境与代码点

- Date: 2026-07-15
- Runtime: .NET SDK 8.0.423
- OS: Microsoft Windows 11 Pro for Workstations 10.0.26100
- CPU: AMD Ryzen 7 5700X3D 8-Core Processor
- Branch: `codex/quality-hardening-4`
- Baseline commit: `5b9ca0cb48a250dc3be3e1b7a6039eab78e41357`
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

Pending. 行为修复全部 green、任何 partial 拆分发生前填写：

- 六场景三次中位数；
- `Submit`、component record/apply 等关键方法 IL hash；
- `existing-set` / `existing-add-remove` 的 JIT inline 摘要。

## 最终验证

Pending. 最终 commit 上填写：

- focused/full tests；
- single-host soak；
- determinism soak；
- multi-host lockstep soak；
- CommandStream before/pre-split/after；
- HeroComing 连续三次门禁；
- public API diff；
- 已修复 bug、文档修正、保留 unsafe 契约和明确非目标。
