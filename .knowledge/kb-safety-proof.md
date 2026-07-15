---
title: MiniArch 正确性验证报告
module: Proof
description: 版本化记录 MiniArch 的测试范围、执行命令、当前证据与明确未覆盖项；不把有限测试表述为绝对安全证明
updated: 2026-07-15
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
| 长程压力 | `MiniArch.Soak`、`MiniArch.LockstepSoak` | ID 复用、free-list、hierarchy、跨 host 是否长期漂移 |
| 性能/内存 | `HeroComing.Perf --check-baseline` | runtime 改动是否跌破发布阈值或持续增内存 |

### 当前复验快照

验证 runtime commit：`21660c3`（2026-07-15）。

| 门禁 | 当前结果 |
|---|---|
| `MiniArch.Tests` Release | 1040 / 1040 PASS |
| `HeroPipeline.Tests` Release | 5 / 5 PASS |
| CommandStream + FrameDelta determinism focused | 158 / 158 PASS（CommandStreamCore 拆分点） |
| HeroComing Movement | 1741.9 rounds/s，阈值 1642 |
| HeroComing Attack | 1041.3 rounds/s，阈值 997 |
| HeroComing memory | OK；baseline 未更新 |

本轮完整 sweep/determinism/lockstep soak 与三次 Hero 结果写入
`docs/plans/2026-07-15-quality-hardening-4-evidence.md`；该文档是本次发布候选的逐次证据源。

### 历史压力证据

2026-07-06 的 soak 批次曾覆盖 224 个 diversity seed、最高 5,000,000 帧单次运行，并发现 B1-B6 六类 Submit/Replay 分歧。它证明当时版本在这些样本上通过，不等于当前或未来版本“完备无 bug”。历史 bug 索引见 `kb-code-review-findings.md`，工具和参数见 `kb-soak-test.md`。

## 决策

- **确定性是核心契约**：同样的初始状态、输入序列、组件注册顺序和合法调用时序，Submit 与 Snapshot→Replay 必须得到相同 logical state 与 allocator 演化。
- **用版本化证据，不用绝对措辞**：禁止“完全正确”“已证明不存在同类 bug”“发布级正确性”之类无法由有限测试推出的结论。
- **Checksum 是 oracle，不是事务机制**：它能发现结果分歧，不能撤销已经发生的部分修改。
- **preflight 只覆盖已建模失败**：CommandStream 在 allocator/materialize 前预检组件 presence、hierarchy overlay、pending slot 与 async handoff；这显著缩小部分提交面，但不是通用 rollback journal。

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

- `World.Replay` 没有通用事务回滚；不可信 delta 应先 `FrameDelta.Validate()`，但 target-world 兼容性或运行期失败仍可能在 replay 中途留下修改。
- 灾难性 `OutOfMemoryException`、进程终止、硬件故障不保证 rollback。
- `Unsafe*` API 与 `Clear(query)` 依赖 XML 中的调用方前置条件；违反契约属于未定义/不安全使用。
- World mutation 不支持并发写；并行录制必须使用单个 `ParallelCommandStream`，consume 仍独占。
- `CanonicalChecksum` 不能替代跨版本 schema 握手；跨进程仍要求一致的组件注册/schema。

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
