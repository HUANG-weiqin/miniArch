# Knowledge Index

`.knowledge/` 是当前默认的模块知识库。这里按"先读什么、再读什么"的方式组织。

## 模块地图

| 模块 | 知识页 |
|---|---|
| Workspace（仓库导航/脚本/流程） | `kb-repo-overview.md`、`kb-profiling-workflow.md`、`kb-throughput-workflow.md` |
| MiniArch.Core（ECS 运行时） | `kb-core-ecs.md`、`kb-architecture-review.md`、`kb-chunk-storage.md`、`kb-cache-optimization.md` |
| MiniArch.Core CommandBuffer / CommandStream | `kb-command-buffer-feasibility.md` |
| MiniArch.Core Query | `kb-query-invalidation.md`、`kb-parallel-query.md` |
| MiniArch.Core Snapshot | `kb-snapshot-persistence.md` |
| MiniArch.Core Hierarchy | `kb-hierarchy-runtime.md` |
| MiniArch.Core DebugMetrics | `kb-debug-metrics.md`（已删除，保留页作为历史记录） |
| MiniArch（用户 API 分层） | `kb-user-api-layering.md` |
| MiniArch.Tests（测试组织） | `kb-test-workflow.md` |
| MiniArch.Benchmarks（对比数据） | `kb-ecs-comparison.md` |
| HeroPipeline.Tests | `kb-hero-pipeline-benchmarks.md` |
| HeroComing.Perf（回归门禁） | `kb-hero-pipeline-regression.md` |
| GameTickSim.Perf（场景基准） | `kb-gameticksim-scenarios.md` |
| CommandBufferGame.Perf（CommandBuffer 真实游戏稳态压测） | `kb-commandbuffer-game-perf.md` |

## 快速入口

- **仓库入口** → `kb-repo-overview.md`
- **CPU 采样定位热点** → `kb-profiling-workflow.md`
- **固定时长吞吐量对比** → `kb-throughput-workflow.md`
- **Chunk 存储** → `kb-chunk-storage.md`
- **ECS 运行时** → `kb-core-ecs.md`
- **整体架构理解** → `kb-architecture-review.md`
- **Cache/内存优化** → `kb-cache-optimization.md`
- **Query 失效机制** → `kb-query-invalidation.md`
- **CommandBuffer / CommandStream** → `kb-command-buffer-feasibility.md`
- **Archive/Snapshot** → `kb-snapshot-persistence.md`
- **Hierarchy** → `kb-hierarchy-runtime.md`
- **用户 API 分层** → `kb-user-api-layering.md`
- **测试/基准** → `kb-test-workflow.md`、`kb-hero-pipeline-benchmarks.md`、`kb-ecs-comparison.md`、`kb-gameticksim-scenarios.md`
- **CommandBuffer 真实游戏稳态压测** → `kb-commandbuffer-game-perf.md`
- **回归门禁** → `kb-hero-pipeline-regression.md`
- **排查行为偏差** → 各模块页的 `坑点` + 对应测试文件
- **理解"为什么边界这么划"** → 各模块页的 `决策`
- **新增知识页** → 先挂到这里，再写模块正文

## 重大变更摘要（2026-06-08 大重构）

- **World 拆分为 partial 文件**：World.cs + World.EntityLifecycle.cs + World.Create.Generated.cs + World.QueryCache.cs + World.StructuralChange.cs
- **Archetype 拆分为 partial 文件**：Archetype.cs（字段/metadata）+ Archetype.Storage.cs（存储操作）
- **Edge cache 使用直索引 `Archetype?[]`**（按 componentId 直索引，O(1) 查找）
- **DebugMetrics 整个子系统已删除**（kb-debug-metrics.md 保留作为历史记录）
- **FrameDelta 热路径 struct 大幅缩小**（Movement +50% / Attack +29%）
- **ComponentMask 扩展为 512-bit**（8 × `ulong`），覆盖 component id 0..511 的快速匹配
- **新增分段存储模式**：Archetype 超过阈值后自动切换为多 Segment 模式（详见 `kb-chunk-storage.md`）
