# Knowledge Index

`.knowledge/` 是当前默认的模块知识库。这里按"先读什么、再读什么"的方式组织。

## 模块地图

| 模块 | 知识页 |
|---|---|
| Workspace（仓库导航/脚本/流程） | `kb-repo-overview.md`、`kb-profiling-workflow.md`、`kb-throughput-workflow.md` |
| MiniArch.Core（ECS 运行时） | `kb-core-ecs.md`、`kb-architecture-review.md`、`kb-chunk-storage.md`、`kb-cache-optimization.md` |
| MiniArch.Core CommandStream | `kb-command-stream.md`、`kb-deferred-create-design.md`（DeferredEntities flag：placeholder/immediate 模式切换、多 host lockstep 设计） |
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
| CommandStreamGame.Perf（CommandStream 真实游戏稳态压测） | `kb-commandstream-game-perf.md` |

## 快速入口

- **仓库入口** → `kb-repo-overview.md`
- **CPU 采样定位热点** → `kb-profiling-workflow.md`
- **固定时长吞吐量对比** → `kb-throughput-workflow.md`
- **Chunk 存储** → `kb-chunk-storage.md`
- **ECS 运行时** → `kb-core-ecs.md`
- **整体架构理解** → `kb-architecture-review.md`
- **Cache/内存优化** → `kb-cache-optimization.md`
- **Query 失效机制** → `kb-query-invalidation.md`
- **CommandStream** → `kb-command-stream.md`
- **Deferred Create 多 host 设计** → `kb-deferred-create-design.md`（DeferredEntities flag + ReplayCore placeholder 映射）
- **Archive/Snapshot** → `kb-snapshot-persistence.md`
- **Hierarchy** → `kb-hierarchy-runtime.md`
- **用户 API 分层** → `kb-user-api-layering.md`
- **测试/基准** → `kb-test-workflow.md`、`kb-hero-pipeline-benchmarks.md`、`kb-ecs-comparison.md`、`kb-gameticksim-scenarios.md`
- **CommandStream 真实游戏稳态压测** → `kb-commandstream-game-perf.md`
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

## 重大变更摘要（2026-06-29 DeferredEntities flag）

- **`CommandStream.DeferredEntities` flag**：`false`（默认）时 `Create()`/`Clone()` 分配 real id（单机）；`true` 时返回 placeholder（多 host lockstep）。`Snapshot()` 按 flag 输出 placeholder delta 或 real-id delta。`SubmitAndSnapshotAsync()` 始终输出 real-id delta。
- **删除 `CreateImmediate()` / `CloneImmediate()`**：公共 API 和 `ICommandRecorder` 接口同步移除。`DeferredEntities=false` 时 `Create()`/`Clone()` 即原 immediate 行为。
- **`Snapshot()` 不再泄漏 host world id**：`DeferredEntities=true` 时跳过 `ResolveDeferredCreates()`，delta 保留 placeholder。
- **ReplayCore placeholder→local 映射**：`_replayPlaceholderMap` 按 seq 索引，每帧 `mapLen=0` 重置防 stale。`ResolveReplayEntity` 加 bounds check。
- **`_replayPlaceholderMapLen` 字段删除**：mapLen 现在是 ReplayCore 局部变量，不复用跨帧。
- **WorldSnapshot free list 持久化**：格式 v3 直接序列化 free list 数组到流末尾，不再调用 RebuildFreeIdStack。`WorldClone` 改为 `CopyFreeIdsFrom`。新增 `Save_load_preserves_free_id_allocation_order` 验证测试。
- **`WorldStateSnapshot.cs` 骨架创建**：Tier 1 in-memory rollback snapshot 数据类，尚未接入 World。
- **XML docs 明确区分 `WorldSnapshot` vs `WorldStateSnapshot`**：`WorldSnapshot` 的 doc 写明"NOT for in-memory rollback"，`WorldStateSnapshot` 的 doc 写明"NOT for persistence/network"。`WorldStateSnapshot` 改用 `int[] FreeIds + int[] FreeIdVersions` 避免依赖 `World.RecycledEntity`（private 嵌套类型）。
- **Tier 1 完整实现**：`World.CaptureState()` / `World.RestoreState()` 通过 Array.Copy 在稳态零分配地备份/恢复 Records、FreeIds、per-archetype 数据（非 chunked + chunked）、Hierarchy。`_createArchetypeCacheGeneration++` 使 query cache 失效。预测帧创建的空 archetype count 被置零。4 个端到端测试验证：state preservation、deterministic ids、idempotency、cache invalidation。

## 重大变更摘要（2026-06-28 CommandStream API 统一）

- **CommandStream 并行 API 统一**：删除 `SetConcurrent`/`AddConcurrent`/`RemoveConcurrent` 专用方法，`ParallelRecording=true` 时所有 Record API 透明切换为并发实现。单线程零退化。

## 重大变更摘要（2026-06-22 全库审阅）

- **修复 CommandStream.BuildFromFrozen bug**：`EmitHierarchyToDelta` 被重复调用两次，导致 FrameDelta 中 Link/Unlink 操作重复写入
- **知识库全面更新**：修正过时文件路径（Ecs/Query.cs → Query.cs）、删除不存在的文件引用（SpanQueryIterators.cs、ChunkViewTyped.cs）、修复旧字段名引用（_archetypeVersion → _createArchetypeCacheGeneration）
