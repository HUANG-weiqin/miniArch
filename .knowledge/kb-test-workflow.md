---
title: Test Workflow
module: MiniArch.Tests
description: How the test suite, query profiling, snapshot benchmarks, and structural-change benchmarks are organized and how to run them
updated: 2026-07-09
---
# Test Workflow

## 这个模块是干什么的

- 验证 ECS core 的行为，作为 typed-column / direct-index 重构后的行为回归网
- 覆盖实体生命周期、chunk 存储、结构迁移和 query
- 为 `CreateMany` 单独区分 append-only、recycled ids、mixed ids 三类场景
- 提供 query 采样 profiling 的独立入口、固定时长 throughput runner、snapshot save/load benchmark
- 用复杂 archetype 分布覆盖 query filter + traversal 的热路径

## 测试组织

按目录分组，每组简要描述覆盖范围。具体文件列表会随开发变化，本表只维护分组级别描述，详细文件以 `tests/MiniArch.Tests/` 实际目录为准。

| 分组 | 覆盖范围 |
|---|---|
| **Core/** | |
| `WorldLifecycleTests` | 实体生命周期、version、free-list、EnsureCapacity、CreateMany、带组件 Create\<T...\>、GetSingleton\<T\>() |
| `WorldStructuralChangeTests` | Add/Set/Remove/Destroy 的 structural semantics |
| `WorldStatsTests` | WorldStats / ArchetypeStats 诊断快照 |
| `EntityTests` | Entity 句柄契约 |
| `EntitySlotTests` | EntitySlot 分配/回收契约 |
| `ChunkTests` | 存储密度、并发只读、引用类型列清尾槽位 |
| `ChunkColumnIndexTests` | Column index 查找正确性 |
| `ArchetypeTests` | chunk 复用、non-full chunk tracking、chunked 模式 |
| `ComponentSchemaTests` | ComponentSchema 构造/比较 |
| `ComponentRegistryTests` | Registry 注册/查找 |
| `SignatureTests` | Signature 构造/比较/Contains |
| `QueryTests` | 缓存与并发读取、冷热路径 |
| `QueryFilterTests` | 链式 filter 和 builder 契约 |
| `QueryComponentSetTests` | ComponentSet 创建/排序契约 |
| `ParallelQueryTests` | ForEachChunk / ForEachChunkParallel 安全性与加速比 |
| `EntityAccessorTests` | EntityAccessor ref struct 契约 |
| `CommandStreamTests` | Submit/Snapshot、cross-world replay、concurrent recording、Clone、struct 缩小后的正确性、SwapOutState 字段分类契约 |
| `CommandBufferParityTests` | MiniArch/Arch 共享结构命令 parity（文件名沿用历史，实际比对的是 CommandStream） |
| `CommandBufferGamePerfTests` | 真实游戏循环稳态 perf（CommandStream） |
| `FrameDeltaTests` | FrameDelta 创建、序列化、等号/哈希 |
| `FrameDeltaDeterminismTests` | 跨 world replay 决定性（相同 delta → 相同最终状态）；Submit 与 Replay 收敛 |
| `FrameDeltaAttackSurfaceTests` | 非法/边界 delta 的容错与防御 |
| `SubmitReplayParityTests` | Submit → Replay 字节级 parity |
| `SubmitReplayRestoreParityTests` | 三路收敛（9 种模式）字节级 checksum 一致 |
| `ChangeTrackingInfrastructureTests` | ChangeTracking 内部基础设施契约 |
| `ChangeTrackingReplayTests` | 变化追踪 replay 的正确性 |
| `ChildrenEnumerableTests` | ChildrenEnumerable 枚举契约 |
| `EntityCloneTests` | Clone 语义 |
| `ThroughputRunnerTests` | 参数解析和汇总契约 |
| `QueryProfilingRunnerTests` | Profiling runner 构造契约 |
| `ComplexQueryBenchmarkScenarioTests` | Benchmark world shape 和命中比例 |
| `IntegrationTests` | 最完整的端到端例子 |
| `TrickyEdgeCaseTests` | 边界/边缘情况；DEBUG 安全检查 |
| `RobustnessTests` | 异常路径和错误输入的健壮性 |
| `IntMathTests` | 整数平方根 `IntMath.Isqrt` 边界覆盖 |
| **Persistence/** | |
| `WorldSnapshotTests` | Round-trip、free slot version、unsupported component、Tier 1 in-memory rollback |
| `WorldCloneTests` | 内存直拷克隆 |
| `WorldDiffTests` | World diff 生成/应用契约 |
| `NetworkSyncTests` | 网络同步场景的 delta 交换 |
| `ChangeTrackingSnapshotTests` | 变化追踪 + 快照的组合 |
| **PropertyBased/** | |
| `SerializationRoundtripPropertyTests` | FsCheck 属性测试：Save/Load roundtrip canonical checksum |
| `ReplayConvergencePropertyTests` | FsCheck：随机操作序列 replay 收敛 |
| `KnownLimitationTests` | FsCheck：已知限制的文档化行为 |
| **UserApi/** | |
| `UserQueryTests` | 普通 API 契约、OrderByEntityId/OrderByComponent |
| `WatchApiTests` | Watch/ChangeWatch/TransitionWatch 发布验证 |
| `WatchProjectedTests` | ProjectedChangeWatch 行为契约 |
| `ChangeQueryTests` | ChangeQuery 构造/过滤/迭代 |
| `ChangeQueryFilterTests` | ChangeQuery 链式 filter 契约 |
| **Diagnostics/** | |
| `WorldValidatorTests` | World 一致性校验 |
| `WorldDigestTests` | World digest 计算/比较 |
| `EntityDumpTests` | Entity 内容 dump |
| **根目录** | |
| `PublicApiSentinelTests` | 公共 API 冻结哨兵 |
| `CrossFeatureParityTests` | M3 交叉特性矩阵 |
| ~~`Core/DebugMetricsTests.cs`~~ | **已删除** — DebugMetrics 子系统已移除 |

## PublicApiSentinel

**What failure means:** The test reflects MiniArch's public API surface and compares it against an embedded baseline. A failure means the public API surface has changed (types, methods, properties, fields, etc.).

**Accidental API change => revert source change:** If you did not intend to change the public API, revert the source change that caused the failure.

**Intentional API change requires human approval then regenerate baseline:** If the change is intentional, get human approval, then regenerate the baseline:

1. Set environment variable `GENERATE_API_BASELINE=1`.
2. Run `dotnet test -c Release --filter "PublicApiSentinel"`.
3. The test generates a file `PublicApiBaseline.txt` in the test output directory.
4. Copy the generated baseline content to replace the `EmbeddedBaseline` string in `tests/MiniArch.Tests/PublicApiSentinelTests.cs`.
5. Commit the updated baseline with your intentional API change.

## 决策

- 每个核心概念一个测试文件
- 集成测试只保留一条完整迁移路径
- 结构变化测试必须保留 `Set` 的 in-place 语义断言
- command buffer 测试需要锁定 recording 不提前发布 layout 变化
- CreateMany benchmark 必须把 fresh append-only、recycled ids、mixed ids 分开跑
- query 并发测试必须覆盖热缓存和冷首次 materialize 两类场景
- 零分配测试的 warmup 必须循环 ≥10 次（避免 Tier 1 升级的假分配）

## 认知模型

- 运行时行为的安全网

## 入口

- 第一次读：`IntegrationTests.cs` → `CommandStreamTests.cs` → `WorldStructuralChangeTests.cs`
- 修 bug：对应功能的测试文件
- 运行测试：`tools/scripts/test.ps1` 或 `dotnet test`
- 运行 benchmark：`tools/scripts/benchmark.ps1` — 或 `dotnet run --project tests\MiniArch.Benchmarks -c Release -- command-buffer`

## 坑点

- 只跑局部测试不看整体迁移可能破坏；跑完 `CommandStreamTests` 还必须回归 lifecycle / structural-change / query
- `Remove` 只快不一定是好事——需确认 archetype 在复用已有空 chunk
- `Create` 只看时间不看 entity metadata 扩容分配可能漏掉问题
- `Set` 相关测试要先确认核心是否已切到 typed-column
- 混合 benchmark 必须用固定种子保证 MiniArch 和 Arch 输入一致
- complex query benchmark 如果从空实体逐组件 `Add` 到终态，会残留大量空 archetype
- snapshot benchmark 不能把 world 构建或 byte[] 预生成算进 save/load 时间
- warmed query benchmark 需要 setup 阶段先 materialize 匹配 archetype
- `FrameDelta` 跨 world replay 要求双方从同一初始态按相同 frame 顺序推进
- 测试名要稳定，方便 agent 用 `--filter` 定位
- DebugMetrics 相关测试已全部删除，不应再引用
