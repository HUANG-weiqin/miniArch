# Knowledge Index

`.knowledge/` 是当前默认的模块知识库。这里按"先读什么、再读什么"的方式组织。

> 架构变更历史和知识库校准记录见 `kb-changelog.md`。

## 模块地图

| 模块 | 知识页 |
|---|---|
| **入门 / 导航** | `kb-design-rationale.md`（**新人必读**：为什么是这样而不是那样）、`kb-glossary.md`（术语表）、`kb-perf-harnesses.md`（4 套 perf harness 消歧）、`kb-lockstep-playbook.md`（帧同步端到端 spine） |
| Workspace（仓库导航/脚本/流程） | `kb-repo-overview.md`、`kb-profiling-workflow.md`、`kb-throughput-workflow.md` |
| MiniArch.Core（ECS 运行时） | `kb-core-ecs.md`、`kb-architecture-review.md`、`kb-chunk-storage.md`、`kb-cache-optimization.md` |
| MiniArch.Core CommandStream | `kb-command-stream.md`、`kb-deferred-create-design.md` |
| MiniArch.Core Query | `kb-query-invalidation.md`、`kb-parallel-query.md` |
| MiniArch.Core Snapshot | `kb-snapshot-persistence.md` |
| MiniArch.Core Hierarchy | `kb-hierarchy-runtime.md` |
| MiniArch.Diagnostics（诊断工具） | `kb-ecs-diagnostics.md`（WorldDiff、WorldValidator、EntityDump、WorldDigest） |
| MiniArch（用户 API 分层） | 合并到 `kb-core-ecs.md` "用户 API 分层" 段 |
| MiniArch.Tests（测试组织） | `kb-test-workflow.md` |
| MiniArch.Benchmarks（对比数据） | `kb-ecs-comparison.md` |
| HeroPipeline.Tests | 合并到 `kb-hero-pipeline-regression.md` "PipelineBenchmarkTests" 段 |
| HeroComing.Perf（回归门禁） | `kb-hero-pipeline-regression.md` |
| GameTickSim.Perf（场景基准） | `kb-gameticksim-scenarios.md` |
| CommandStreamGame.Perf（CommandStream 真实游戏稳态压测） | `kb-commandstream-game-perf.md` |
| samples/BulletLockstep.Demo（多 host 弹幕游戏集成测试） | `kb-bullet-lockstep-demo.md`（9 个 slice 端到端压测库全部公共能力：placeholder lockstep / archetype 迁移 / hierarchy / chunked / 持久化 / 回滚） |

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
- **Cache/内存优化** → `kb-cache-optimization.md`

**帧同步 / 网络**：
- **端到端帧同步指南** → `kb-lockstep-playbook.md`
- **CommandStream** → `kb-command-stream.md`
- **Deferred Create 多 host 设计** → `kb-deferred-create-design.md`
- **Checksum 双模式** → `kb-snapshot-persistence.md`（Checksum 双模式段）
- **状态诊断（diff/校验/探查）** → `kb-ecs-diagnostics.md`

**存储 / 查询**：
- **Chunk 存储** → `kb-chunk-storage.md`
- **Query 失效机制** → `kb-query-invalidation.md`
- **并行 Query** → `kb-parallel-query.md`
- **Hierarchy** → `kb-hierarchy-runtime.md`

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
