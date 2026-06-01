# Knowledge Index

`.knowledge/` 是当前默认的模块知识库。这里按“先读什么、再读什么”的方式组织，不按历史写作顺序组织。

## 文件简介

- `kb-repo-overview.md`：仓库导航、协作入口和脚本使用方式
- `kb-profiling-workflow.md`：可复用的 CPU sampling / profiling 方法和命令模板
- `kb-throughput-workflow.md`：可复用的固定时长吞吐量对比方法和命令模板
- `kb-core-ecs.md`：`MiniArch.Core` 的运行时架构说明
- `kb-command-buffer-feasibility.md`：command buffer 的已实现模型、回放顺序、并发 recording 边界、0-GC 优化、FastCommandBuffer arena 优化结果和验证入口
- `kb-debug-metrics.md`：DEBUG-only 内部指标与用户可粘贴诊断报告 API
- `kb-user-api-layering.md`：`MiniArch` 共享 `World/Entity/QueryDescription`、默认 `foreach` 查询，以及 `MiniArch.Core` advanced 边界
- `kb-snapshot-persistence.md`：snapshot 存档格式、运行时桥接点和 load/save 边界
- `kb-hierarchy-runtime.md`：runtime-owned parent-child、级联销毁和 hierarchy snapshot 恢复
- `kb-test-workflow.md`：测试组织、验证方式、性能基准和常见回归点
- `kb-hero-pipeline-benchmarks.md`：Hero 项目 pipeline benchmark 移植到 miniArch 的架构和结果
- `kb-architecture-review.md`：全库机械化拆解（10 模块一句话真相 + 核心循环 + 状态模型 + 数据流）、5 个已知问题、4 个优化方向、3 个设计张力
- `kb-query-invalidation.md`：per-archetype generation 基于版本号的 Query 缓存失效机制
- `kb-ecs-comparison.md`：MiniArch vs Arch vs DefaultEcs 三方性能与内存稳定性对比报告

## 模块地图

- `Workspace` -> `kb-repo-overview.md`
- `Workspace` -> `kb-profiling-workflow.md`
- `Workspace` -> `kb-throughput-workflow.md`
- `MiniArch.Core` -> `kb-core-ecs.md`
- `MiniArch.Core CommandBuffer` -> `kb-command-buffer-feasibility.md`
- `MiniArch.DebugMetrics` -> `kb-debug-metrics.md`
- `MiniArch` -> `kb-user-api-layering.md`
- `MiniArch.Core Snapshot` -> `kb-snapshot-persistence.md`
- `MiniArch.Core Hierarchy` -> `kb-hierarchy-runtime.md`
- `MiniArch.Tests` -> `kb-test-workflow.md`
- `MiniArch.Benchmarks` -> `kb-test-workflow.md`
- `HeroPipeline.Tests` -> `kb-hero-pipeline-benchmarks.md`
- `HeroComing.Perf` -> `kb-hero-pipeline-regression.md`
- `MiniArch.Core ArchitectureReview` -> `kb-architecture-review.md`
- `MiniArch.Core Query` -> `kb-query-invalidation.md`
- `MiniArch.Benchmarks Comparison` -> `kb-ecs-comparison.md`

## 快速入口

- 想先找仓库入口，先看 `kb-repo-overview.md`。
- 想复用采样定位性能热点的方法，先看 `kb-profiling-workflow.md`。
- 想复用固定时长吞吐量对比的方法，先看 `kb-throughput-workflow.md`。
- 想理解 ECS 运行时，先看 `kb-core-ecs.md`。
- 想理解 command buffer 现在已经实现到什么程度、playback/replay 顺序是什么、并发 recording 边界在哪，先看 `kb-command-buffer-feasibility.md`。
- 想定位 overflow、array grow、slab rent、snapshot copy 或 world metadata grow，先看 `kb-debug-metrics.md`。
- 想理解为什么现在只保留一份 `World/Entity/QueryDescription`、默认查询为什么只走 `QueryDescription`，先看 `kb-user-api-layering.md`。
- 想理解存档为什么不能直接复制 chunk 对象，以及 snapshot 怎么重建 world，先看 `kb-snapshot-persistence.md`。
- 想理解 parent-child 为什么不做成组件、destroy 为什么会级联，以及读档后关系怎么恢复，先看 `kb-hierarchy-runtime.md`。
- 想理解测试覆盖、验证方式和性能基准，先看 `kb-test-workflow.md`。
- 想理解“为什么边界这么划”，先看各模块页里的 `决策`。
- 想理解"怎么读这个模块"，先看各模块页里的 `入口`。
- 想从整体上理解 miniArch 的运行机制、已知问题和优化方向，先看 `kb-architecture-review.md`。
- 想理解 Query 失效机制和 per-archetype generation 优化，先看 `kb-query-invalidation.md`。
- 想理解 span-based chunk 迭代器（EachSpan），见 `kb-user-api-layering.md` 的数据流部分。
- 想排查行为偏差，先看各模块页里的 `坑点` 和对应测试文件。
- 新增知识页时，先把它挂到这里，再写模块正文。
