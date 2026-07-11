---
title: Knowledge Index
module: KnowledgeIndex
description: .knowledge 知识库导航、模块地图与必读入口
updated: 2026-07-10
---

# Knowledge Index

`.knowledge/` 是当前默认的模块知识库。这里按"先读什么、再读什么"的方式组织。

> 架构变更历史和知识库校准记录见 `kb-changelog.md`。

## 模块地图

| 模块 | 知识页 |
|---|---|
| **入门 / 导航** | `kb-design-rationale.md`（**新人必读**：为什么是这样而不是那样）、`kb-glossary.md`（术语表）、`kb-perf-harnesses.md`（perf harness 消歧）、`kb-lockstep-playbook.md`（帧同步端到端 spine） |
| Workspace（仓库导航/脚本/流程） | `kb-repo-overview.md`、`kb-profiling-workflow.md`、`kb-throughput-workflow.md` |
| MiniArch.Core（ECS 运行时） | `kb-core-ecs.md`、`kb-architecture-review.md`、`kb-chunk-storage.md`、`kb-cache-optimization.md`、`kb-partition-prototype-report.md` |
| MiniArch.Core CommandStream | `kb-command-stream.md`、`kb-deferred-create-design.md` |
| MiniArch.Core Query | `kb-query-invalidation.md`、`kb-parallel-query.md` |
| MiniArch.Core ChangeTracking | `kb-change-tracking.md`（World.Watch pull-event 模型：ChangeWatch/TransitionWatch Snapshot+Diff；为什么不是 push event） |
| MiniArch.Core Snapshot | `kb-snapshot-persistence.md` |
| MiniArch.Core Hierarchy | `kb-hierarchy-runtime.md` |
| MiniArch.Diagnostics（诊断工具） | `kb-ecs-diagnostics.md`（WorldDiff、WorldValidator、EntityDump、WorldDigest） |
| MiniArch（用户 API 分层） | `kb-component-bucket-index-mvp-report.md`（ComponentBucketQuery MVP 最终报告——确定性 per-key scan、调用者提供 span 模式、零 core 入侵、正确性模型与性能矩阵）、`kb-frame-read-models.md`（Frame Read Models ValueLab / FrameLookup API Gate：compact CSR 本体成立；逐 row DirectForEach 发布形态 Conditional Hold，hot bucket/entity-only 禁用区间）、`kb-managed-entity-sidecar-evaluation.md`（Entity -> managed object sidecar 价值验证 No-Go：打败 dictionary 但未优于 competent dense user，serialization 不进 v1） |
| MiniArch.Tests（测试组织） | `kb-test-workflow.md` |
| MiniArch.Benchmarks（对比数据） | `kb-ecs-comparison.md` |
| HeroPipeline.Tests | 合并到 `kb-hero-pipeline-regression.md` "PipelineBenchmarkTests" 段 |
| HeroComing.Perf（回归门禁） | `kb-hero-pipeline-regression.md` |
| **浸没测试（必读）** | **`kb-soak-test.md`**（**🚨 这个测试存在！所有 miniArch 改动后应运行** — 长周期随机操作正确性验证器，已发现 6 个 Submit/Replay 不一致 bug（B1-B6），另有 B7-B16 来自代码审阅） |
| **多 host 同步浸没测试** | **`kb-lockstep-soak.md`**（🚨 N host placeholder lockstep 收敛证明 — 补齐单 host soak 无法覆盖的 DeferredEntities=true 多 host 交错收敛性） |
| **安全证明报告** | **`kb-safety-proof.md`**（2026-07-06 正式库安全证明——224 seed × 5M 帧全 PASS，15 条代码路径审计零分歧） |
| **确定性证明（选型必读）** | **`kb-determinism-proof.md`**（2026-07-10 确定性审计报告——9 维度全通过，LayoutKind.Auto 拦截修复，含与其他 ECS 库的确定性对比和引用措辞） |
| GameTickSim.Perf（场景基准） | `kb-gameticksim-scenarios.md` |
| CommandStreamGame.Perf（CommandStream 真实游戏稳态压测） | `kb-commandstream-game-perf.md` |
| WatchApi.Perf（Watch API 专项吞吐/分配） | `kb-change-tracking.md`、`kb-perf-harnesses.md` |
| samples/BulletLockstep.Demo（多 host 弹幕游戏集成测试） | `kb-bullet-lockstep-demo.md`（8 个 slice（2-9）端到端压测库全部公共能力：placeholder lockstep / archetype 迁移 / hierarchy / chunked / 持久化 / 回滚） |

## 快速入口

**第一次接触**：
1. **先读 `kb-design-rationale.md`** — 10 分钟理解 "为什么长这样"，以及为什么常见优化提案都已被拒绝
2. **术语不认识** → `kb-glossary.md`
3. **仓库入口** → `kb-repo-overview.md`
4. **整体架构** → `kb-architecture-review.md`
5. **ECS 运行时** → `kb-core-ecs.md`

**在想"能不能这样改"之前**：先查 `kb-design-rationale.md` §2（子系统的替代方案）和 §3（常见误判优化），很大概率已经被评估过并拒绝了。

**性能 / 回归**：
- **不知道该跑哪个 perf 工具** → `kb-perf-harnesses.md`（消歧矩阵）
- **CPU 采样定位热点** → `kb-profiling-workflow.md`
- **固定时长吞吐量对比** → `kb-throughput-workflow.md`
- **多 host 弹幕游戏集成测试** → `kb-bullet-lockstep-demo.md`
- **回归门禁** → `kb-hero-pipeline-regression.md`
- **Watch API 专项吞吐/分配** → `kb-change-tracking.md`（WatchApi.Perf 段）
- **Cache/内存优化** → `kb-cache-optimization.md`

**确定性 / 选型**：
- **确定性审计报告（选型必读）** → `kb-determinism-proof.md`（9 维度审计 + 实证数据 + 引用措辞）
- **ECS 横向对比** → `kb-ecs-comparison.md`
- **设计决策总纲** → `kb-design-rationale.md`

**帧同步 / 网络**：
- **端到端帧同步指南** → `kb-lockstep-playbook.md`
- **多 host lockstep 收敛证明** → `kb-lockstep-soak.md`
- **CommandStream** → `kb-command-stream.md`
- **Deferred Create 多 host 设计** → `kb-deferred-create-design.md`
- **Checksum 双模式** → `kb-snapshot-persistence.md`（Checksum 双模式段）
- **状态诊断（diff/校验/探查）** → `kb-ecs-diagnostics.md`

**存储 / 查询**：
- **Chunk 存储** → `kb-chunk-storage.md`
- **Query 失效机制** → `kb-query-invalidation.md`
- **并行 Query** → `kb-parallel-query.md`
- **Hierarchy** → `kb-hierarchy-runtime.md`

**反应式 / 变更追踪**：
- **变更追踪（渲染层反应式）** → `kb-change-tracking.md`（World.Watch pull-event 模型：ChangeWatch/TransitionWatch Snapshot+Diff；为什么不是 push event 见 `kb-design-rationale.md` §3.10）

**持久化 / 状态复制**：
- **Archive/Snapshot** → `kb-snapshot-persistence.md`

**测试**：
- **测试组织** → `kb-test-workflow.md`
- **ECS 对比基准** → `kb-ecs-comparison.md`、`kb-gameticksim-scenarios.md`、`kb-commandstream-game-perf.md`

**通用**：
- **排查行为偏差** → 各模块页的 `坑点` + 对应测试文件
- **理解"为什么边界这么划"** → 各模块页的 `决策`

- **架构变更历史** → `kb-changelog.md`（2026-07-01 代码硬化：isqrt / wire 预算 / CRC32 / PBT / Conditional / Entity.IsPlaceholder / SpanFeeder struct）
- **新增知识页** → 先看 `.knowledge/_template.md` 模板，再挂到上面的地图，再写模块正文
