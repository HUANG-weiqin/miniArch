# 知识审计清单 — Knowledge Audit Inventory

**生成日期**: 2026-07-09
**工作树**: `knowledge-consistency-audit-20260709` (branch: `audit/knowledge-consistency-20260709`)
**提交**: `246ac7e` (同 main)
**审计范围**: `.knowledge/*.md` 全部 31 个文件（排除 `_template.md`）

---

## Inventory Completeness Evidence

**扫描方法**:
```bash
# Step 1: glob 扫描全部 .knowledge/*.md
fd '\.md$' .knowledge/ --no-ignore

# Step 2: 对每个文件读取 front matter 和行数
# Step 3: 对照 INDEX.md 验证无遗漏 kb-* 文件
```

**结果**:
- `.knowledge/` 共有 32 个 `.md` 文件
- 排除 `_template.md`（格式参考模板，不计入审计目标）
- **审计目标 = 31 个文件**（30 个 `kb-*.md` + 1 个 `INDEX.md`）
- 所有 30 个 `kb-*.md` 在 INDEX.md 中均有引用 ✅ 无孤立知识页
- **Group D 报告**已生成并存在于本审计工作树：`docs/audits/knowledge-audit-group-D-20260709.md` ✅

---

## Audit Targets（修复后行数）

| # | 文件名 | 行数 | front-matter title | module | updated | 状态 |
|---|--------|------|--------------------|--------|---------|------|
| 1 | `INDEX.md` | 89 | Knowledge Index | KnowledgeIndex | 2026-07-09 | ✅ |
| 2 | `kb-architecture-review.md` | 231 | Architecture Mechanistic Review | MiniArch.Core | 2026-07-09 | ✅ |
| 3 | `kb-bullet-lockstep-demo.md` | 117 | BulletLockstep Demo — 多 host 弹幕游戏集成测试 | samples.BulletLockstep.Demo | 2026-07-09 | ✅ |
| 4 | `kb-cache-optimization.md` | 137 | Cache & Memory Optimization Review | MiniArch.Core | 2026-07-09 | ✅ |
| 5 | `kb-change-tracking.md` | 149 | Change Tracking（变更追踪） | MiniArch.Core ChangeTracking | 2026-07-09 | ✅ |
| 6 | `kb-changelog.md` | 379 | Knowledge Base Changelog | Meta | 2026-07-09 | ✅ |
| 7 | `kb-chunk-storage.md` | 195 | Chunk 存储 | MiniArch.Core | 2026-07-09 | ✅ |
| 8 | `kb-code-review-findings.md` | 350 | 代码审阅发现 | Meta | 2026-07-09 | ✅ |
| 9 | `kb-command-stream.md` | 377 | Command Stream Runtime | MiniArch.Core CommandStream | 2026-07-09 | ✅ |
| 10 | `kb-commandstream-game-perf.md` | 71 | CommandStream Game Steady-State Perf | CommandStreamGame.Perf | 2026-07-09 | ✅ |
| 11 | `kb-core-ecs.md` | 177 | MiniArch Core ECS | MiniArch.Core | 2026-07-09 | ✅ |
| 12 | `kb-deferred-create-design.md` | 234 | Deferred Create — Multi-Host Lockstep Design | MiniArch.Core CommandStream | 2026-07-09 | ✅ |
| 13 | `kb-design-rationale.md` | 329 | 设计决策总纲 — 为什么是这样而不是那样 | Meta | 2026-07-09 | ✅ |
| 14 | `kb-ecs-comparison.md` | 74 | MiniArch vs Arch vs DefaultEcs 横向对比 | MiniArch.Benchmarks | 2026-07-09 | ✅ |
| 15 | `kb-ecs-diagnostics.md` | 56 | MiniArch.Diagnostics 诊断工具 | MiniArch.Diagnostics | 2026-07-09 | ✅ |
| 16 | `kb-gameticksim-scenarios.md` | 70 | GameTickSim Scenarios | GameTickSim.Perf | 2026-07-09 | ✅ |
| 17 | `kb-glossary.md` | 71 | Glossary | Meta | 2026-07-09 | ✅ |
| 18 | `kb-hero-pipeline-regression.md` | 75 | Hero Pipeline Regression Test | HeroComing.Perf | 2026-07-09 | ✅ |
| 19 | `kb-hierarchy-runtime.md` | 55 | Hierarchy Runtime | MiniArch.Core Hierarchy | 2026-07-09 | ✅ |
| 20 | `kb-lockstep-playbook.md` | 112 | Lockstep Playbook | Meta | 2026-07-09 | ✅ |
| 21 | `kb-lockstep-soak.md` | 227 | 多 host Lockstep 浸泡测试 — 网络同步收敛证明 | LockstepSoak | 2026-07-09 | ✅ |
| 22 | `kb-parallel-query.md` | 236 | 并行 Query 迭代 | MiniArch.Core Query | 2026-07-09 | ✅ |
| 23 | `kb-perf-harnesses.md` | 70 | Performance Harnesses Disambiguation | Meta | 2026-07-09 | ✅ |
| 24 | `kb-profiling-workflow.md` | 59 | Profiling Workflow | Workspace | 2026-07-09 | ✅ |
| 25 | `kb-query-invalidation.md` | 78 | Query Invalidation System | MiniArch.Core Query | 2026-07-09 | ✅ |
| 26 | `kb-repo-overview.md` | 68 | Repository Overview | Workspace | 2026-07-09 | ✅ |
| 27 | `kb-safety-proof.md` | 216 | MiniArch ECS 库安全证明 | Proof | 2026-07-09 | ✅ |
| 28 | `kb-snapshot-persistence.md` | 135 | Snapshot Persistence | MiniArch.Core Snapshot | 2026-07-09 | ✅ |
| 29 | `kb-soak-test.md` | 121 | 浸泡测试（Soak Test）— 库安全证明 | Soak | 2026-07-09 | ✅ |
| 30 | `kb-test-workflow.md` | 131 | Test Workflow | MiniArch.Tests | 2026-07-09 | ✅ |
| 31 | `kb-throughput-workflow.md` | 50 | Throughput Workflow | Workspace | 2026-07-09 | ✅ |

**汇总**: 31 个审计目标文件，总计 **4,739 行**。

- Minimum: `kb-throughput-workflow.md` (50 行)
- Maximum: `kb-changelog.md` (379 行)
- Median: ~117 行
- Average: ~153 行

---

## Metadata Issues

审计时发现的 front matter / metadata 问题；M1/M2/M3/M4 全部已解决，M5 保持设计决策：

| # | 文件 | 问题 | 严重程度 | 状态 |
|---|------|------|----------|------|
| M1 | `INDEX.md` | **无 front matter**。不符合 `_template.md` 模板要求（缺少 `title`/`module`/`description`/`updated`）。INDEX.md 是知识库索引，按项目约定 INDEX.md 不需要严格套用模板（它是指南而非知识页），但建议在 front matter 中至少注明 `module: KnowledgeIndex` 和 `updated` 以保持一致。 | 低 | ✅ lead 已补充 |
| M2 | `kb-architecture-review.md` | `updated` 值包含非日期注解文本：`2026-07-09 (修正: World partial 5 文件, ...)`。模板要求 `updated` 应为 ISO 日期。该注解实际是变更日志，可移至正文。 | 低 | ✅ 已修复 |
| M3 | `kb-change-tracking.md` | module 声明为 `MiniArch.Core`，但实际描述的是 "MiniArch.Core ChangeTracking" 子系统。INDEX.md 将其放在 "MiniArch.Core Change Tracking" 模块。建议统一 module 值为 `MiniArch.Core ChangeTracking`（或 INDEX.md 对应合并到 MiniArch.Core）。 | 信息性 | ✅ lead 已统一为 `MiniArch.Core ChangeTracking` |
| M4 | `kb-change-tracking.md` | `description` 字段包含代码类型名跨度过长：`"旧 TrackValueChanges/TransitionLog/DenseValueDiff/IValueProjector/IValueChangeSink 已删除"`——可读性差，建议仅概述功能而非列出已删除类型。 | 低 | ✅ lead 已精简 description |
| M5 | `kb-architecture-review.md` | module 为 `MiniArch.Core`，但 INDEX.md 将其归类在 `MiniArch.Core（ECS 运行时）`。两者一致，但 `kb-architecture-review` 跨模块涵盖整个架构，module 值建议考虑 `Meta` 或 `Architecture` 以反映其全局性。 | 信息性 | ⏳ 当前值有效，设计决策保持 |

**修复后 metadata 状态**: 31/31 (100%) 文件 front matter 完整有效；1 个信息性设计建议 (M5, kb-architecture-review.md module 字段 — 当前值有效，不属于 metadata 错误)；M1/M3/M4 已由 lead 补充/统一/精简。

---

## Proposed Verification Groups（修复后）

基于模块凝聚力和页面数量平衡，将 31 个审计目标划分为 **6 个独立验证组**：

### Group A: Core ECS Runtime (5 页, 889 行)
| 文件 | 行数 | 模块 |
|------|------|------|
| `kb-architecture-review.md` | 231 | MiniArch.Core |
| `kb-core-ecs.md` | 177 | MiniArch.Core |
| `kb-chunk-storage.md` | 195 | MiniArch.Core |
| `kb-cache-optimization.md` | 137 | MiniArch.Core |
| `kb-change-tracking.md` | 149 | MiniArch.Core ChangeTracking |
| **合计** | **889** | |

**验证重点**: ECS 运行时核心设计是否与代码一致；chunk 存储不变式；缓存优化陈述；变更追踪 API 契约。

### Group B: CommandStream, Persistence & Lockstep (6 页, 969 行)
| 文件 | 行数 | 模块 |
|------|------|------|
| `kb-command-stream.md` | 377 | MiniArch.Core CommandStream |
| `kb-deferred-create-design.md` | 234 | MiniArch.Core CommandStream |
| `kb-snapshot-persistence.md` | 135 | MiniArch.Core Snapshot |
| `kb-lockstep-playbook.md` | 112 | Meta |
| `kb-hierarchy-runtime.md` | 55 | MiniArch.Core Hierarchy |
| `kb-ecs-diagnostics.md` | 56 | MiniArch.Diagnostics |
| **合计** | **969** | |

**验证重点**: CommandStream API 签名；FrameDelta wire format；Snapshot/Replay 契约；lockstep 端到端指南；hierarchy runtime 行为；诊断工具 API。

### Group C: Query, Parallel & Glossary (4 页, 714 行)
| 文件 | 行数 | 模块 |
|------|------|------|
| `kb-query-invalidation.md` | 78 | MiniArch.Core Query |
| `kb-parallel-query.md` | 236 | MiniArch.Core Query |
| `kb-glossary.md` | 71 | Meta |
| `kb-design-rationale.md` | 329 | Meta |
| **合计** | **714** | |

**验证重点**: Query 失效机制（两段式）；并行 API 安全模型；术语表准确性；设计决策与代码的实际一致性。

### Group D: Soak, Proof & Safety (4 页, 914 行)
| 文件 | 行数 | 模块 |
|------|------|------|
| `kb-soak-test.md` | 121 | Soak |
| `kb-safety-proof.md` | 216 | Proof |
| `kb-lockstep-soak.md` | 227 | LockstepSoak |
| `kb-code-review-findings.md` | 350 | Meta |
| **合计** | **914** | |

**验证重点**: 浸泡测试矩阵数据；安全证明声明的准确性；锁步 soak 证明结果；code review 发现条目的当前状态。

### Group E: Test, Benchmarks & Perf (5 页, 421 行)
| 文件 | 行数 | 模块 |
|------|------|------|
| `kb-test-workflow.md` | 131 | MiniArch.Tests |
| `kb-ecs-comparison.md` | 74 | MiniArch.Benchmarks |
| `kb-gameticksim-scenarios.md` | 70 | GameTickSim.Perf |
| `kb-commandstream-game-perf.md` | 71 | CommandStreamGame.Perf |
| `kb-hero-pipeline-regression.md` | 75 | HeroComing.Perf |
| **合计** | **421** | |

**验证重点**: 测试组织；竞品对比数据；场景基准描述；perf harness 消歧；回归门禁阈值。

### Group F: Workspace, INDEX & BulletLockstep (7 页, 825 行)
| 文件 | 行数 | 模块 |
|------|------|------|
| `INDEX.md` | 89 | *(索引)* |
| `kb-repo-overview.md` | 68 | Workspace |
| `kb-profiling-workflow.md` | 59 | Workspace |
| `kb-throughput-workflow.md` | 50 | Workspace |
| `kb-perf-harnesses.md` | 70 | Meta |
| `kb-changelog.md` | 379 | Meta |
| `kb-bullet-lockstep-demo.md` | 117 | samples.BulletLockstep.Demo |
| **合计** | **832** | |

**验证重点**: INDEX.md 模块地图完整性；仓库导航路线；profiling/throughput 流程；perf harness 矩阵；changelog 更新记录；bullet demo 决策。

---

## Summary Statistics

| 维度 | 数值 |
|------|------|
| 审计目标总数 | 31 |
| 总行数 | **4,739**（lead 清理后） |
| 组数 | 6 |
| 每组最小页数 | 4 |
| 每组最大页数 | 7 |
| 每组平均页数 | ~5.2 |
| 每组最小行数 | 421 |
| 每组最大行数 | 969（Group F 升为 832） |
| 完全通过 metadata 检查 | 31/31 (100%) |
| Metadata 问题（低严重） | 0（全部已解决） |
| Metadata 信息性设计建议 | 1 (M5 — 设计决策保留，非错误) |
| 已生成的审计报告 | 6 组全部（A/B/C/D/E/F）|

---

## 修复后行数变化汇总

对比修复前的旧行数（2026-07-09 初版审计）：

| 文件 | 旧行数 | 审计修复后 | lead 清理后 | 最终变化 |
|------|--------|-----------|------------|---------|
| `INDEX.md` | 82 | 82 | 89 | +7（lead 补充 front matter） |
| `kb-architecture-review.md` | 230 | 231 | 231 | +1 |
| `kb-bullet-lockstep-demo.md` | 115 | 117 | 117 | +2 |
| `kb-changelog.md` | 379 | 379 | 379 | 0 |
| `kb-core-ecs.md` | 179 | 177 | 177 | -2 |
| `kb-ecs-comparison.md` | 72 | 74 | 74 | +2 |
| `kb-gameticksim-scenarios.md` | 72 | 70 | 70 | -2 |
| `kb-glossary.md` | 71 | 71 | 71 | 0 |
| `kb-hero-pipeline-regression.md` | 73 | 75 | 75 | +2 |
| `kb-perf-harnesses.md` | 70 | 70 | 70 | 0 |
| `kb-test-workflow.md` | 102 | 131 | 131 | +29 |

行数变化范围小（-2 到 +29，外加 code-review 页新增 2 行验证计数策略说明）。审计修复后总行数为 4,730；lead 清理后 INDEX.md 增加 7 行 front matter，并补充 code-review 验证计数策略，最终总行数为 **4,739**。

---

## Next Step Recommendation

1. **所有 6 组审计已完成** → 进入修复验证阶段。
2. **修复已应用**（见 `knowledge-audit-repair-plan-20260709.md` 更新版），lead 已补充 INDEX.md front matter、统一 kb-change-tracking metadata、修正 kb-repo-overview 措辞。
3. **最终验证**：由 lead 运行 `dotnet build -c Release` + `dotnet test -c Release` + `HeroComing.Perf --check-baseline` 确认修复无回归。
