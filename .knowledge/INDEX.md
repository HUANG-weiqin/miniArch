# Knowledge Index

`.knowledge/` 是当前默认的模块知识库。这里按"先读什么、再读什么"的方式组织。

> 架构变更历史和知识库校准记录见 `kb-changelog.md`。

## 模块地图

| 模块 | 知识页 |
|---|---|
| **入门 / 导航** | `kb-glossary.md`（术语表）、`kb-perf-harnesses.md`（4 套 perf harness 消歧）、`kb-lockstep-playbook.md`（帧同步端到端 spine） |
| Workspace（仓库导航/脚本/流程） | `kb-repo-overview.md`、`kb-profiling-workflow.md`、`kb-throughput-workflow.md` |
| MiniArch.Core（ECS 运行时） | `kb-core-ecs.md`、`kb-architecture-review.md`、`kb-chunk-storage.md`、`kb-cache-optimization.md` |
| MiniArch.Core CommandStream | `kb-command-stream.md`、`kb-deferred-create-design.md` |
| MiniArch.Core Query | `kb-query-invalidation.md`、`kb-parallel-query.md` |
| MiniArch.Core Snapshot | `kb-snapshot-persistence.md` |
| MiniArch.Core Hierarchy | `kb-hierarchy-runtime.md` |
| MiniArch（用户 API 分层） | 合并到 `kb-core-ecs.md` "用户 API 分层" 段 |
| MiniArch.Tests（测试组织） | `kb-test-workflow.md` |
| MiniArch.Benchmarks（对比数据） | `kb-ecs-comparison.md` |
| HeroPipeline.Tests | 合并到 `kb-hero-pipeline-regression.md` "PipelineBenchmarkTests" 段 |
| HeroComing.Perf（回归门禁） | `kb-hero-pipeline-regression.md` |
| GameTickSim.Perf（场景基准） | `kb-gameticksim-scenarios.md` |
| CommandStreamGame.Perf | `kb-commandstream-game-perf.md` |

## 快速入口

**新人 / 第一次接触**：
- **术语不认识** → `kb-glossary.md`（GGPO/SoA/LEB128/Tier 等）
- **仓库入口** → `kb-repo-overview.md`
- **整体架构理解** → `kb-architecture-review.md`
- **ECS 运行时** → `kb-core-ecs.md`
- **用户 API 分层** → `kb-user-api-layering.md`

**性能 / 回归**：
- **不知道该跑哪个 perf 工具** → `kb-perf-harnesses.md`（消歧矩阵）
- **CPU 采样定位热点** → `kb-profiling-workflow.md`
- **固定时长吞吐量对比** → `kb-throughput-workflow.md`
- **回归门禁** → `kb-hero-pipeline-regression.md`
- **Cache/内存优化** → `kb-cache-optimization.md`

**帧同步 / 网络**：
- **端到端帧同步指南** → `kb-lockstep-playbook.md`
- **CommandStream** → `kb-command-stream.md`
- **FrameDelta.Merge** → `kb-command-stream.md`（Merge 段）
- **Deferred Create 多 host 设计** → `kb-deferred-create-design.md`
- **Checksum 双模式** → `kb-snapshot-persistence.md`（Checksum 双模式段）

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
- **架构变更历史** → `kb-changelog.md`
- **新增知识页** → 先看 `.knowledge/_template.md` 模板，再挂到上面的地图，再写模块正文
