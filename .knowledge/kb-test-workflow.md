---
title: Test Workflow
module: MiniArch.Tests
description: How the test suite, query profiling, snapshot benchmarks, and structural-change benchmarks are organized and how to run them
updated: 2026-06-08 (DebugMetricsTests 已删除、CommandBufferTests 大幅扩展、ThroughputRunner 已移除)
---
# Test Workflow

## 这个模块是干什么的

- 验证 ECS core 的行为，作为 typed-column / direct-index 重构后的行为回归网
- 覆盖实体生命周期、chunk 存储、结构迁移和 query
- 为 `CreateMany` 单独区分 append-only、recycled ids、mixed ids 三类场景
- 提供 query 采样 profiling 的独立入口、固定时长 throughput runner、snapshot save/load benchmark
- 用复杂 archetype 分布覆盖 query filter + traversal 的热路径

## 测试组织

| 测试文件 | 覆盖范围 |
|---|---|
| `Core/WorldLifecycleTests.cs` | 实体生命周期、version、free-list、EnsureCapacity、CreateMany、带组件 Create<T...>、GetFirst<T>() |
| `Core/WorldStructuralChangeTests.cs` | Add/Set/Remove/Destroy 的 structural semantics |
| `Core/EntityTests.cs` | Entity 句柄契约 |
| `Core/ChunkTests.cs` | 存储密度、并发只读、引用类型列清尾槽位 |
| `Core/ChunkColumnIndexTests.cs` | Column index 查找正确性 |
| `Core/ArchetypeTests.cs` | chunk 复用、non-full chunk tracking |
| `Core/CommandBufferTests.cs` | Submit/Snapshot/Merge、cross-world replay、concurrent recording、Clone、struct 缩小后的正确性 |
| `Core/CommandBufferParityTests.cs` | MiniArch/Arch 共享结构命令 parity |
| `Core/QueryTests.cs` | 缓存与并发读取、冷热路径 |
| `Core/QueryFilterTests.cs` | 链式 filter 和 builder 契约 |
| `Core/QueryComponentSetTests.cs` | ComponentSet 创建/排序契约 |
| `Core/EntityAccessorTests.cs` | EntityAccessor ref struct 契约 |
| `Core/IntegrationTests.cs` | 最完整的端到端例子 |
| ~~`Core/DebugMetricsTests.cs`~~ | **已删除** — DebugMetrics 子系统已移除 |
| `Core/ThroughputRunnerTests.cs` | 参数解析和汇总契约 |
| `Core/QueryProfilingRunnerTests.cs` | Profiling runner 构造契约 |
| `Core/ComplexQueryBenchmarkScenarioTests.cs` | Benchmark world shape 和命中比例 |
| `Core/SignatureTests.cs` | Signature 构造/比较/Contains |
| `Core/ComponentRegistryTests.cs` | Registry 注册/查找 |
| `Core/EntityCloneTests.cs` | Clone 语义 |
| `Core/TrickyEdgeCaseTests.cs` | 边界/边缘情况 |
| `Persistence/WorldSnapshotTests.cs` | Round-trip、free slot version、unsupported component |
| `Persistence/WorldCloneTests.cs` | 内存直拷克隆 |
| `UserApi/UserQueryTests.cs` | 普通 API 契约、OrderBy |

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

- 第一次读：`IntegrationTests.cs` → `CommandBufferTests.cs` → `WorldStructuralChangeTests.cs`
- 修 bug：对应功能的测试文件
- 运行测试：`scripts/test.ps1` 或 `dotnet test`
- 运行 benchmark：`scripts/benchmark.ps1` — 或 `dotnet run --project benchmarks\MiniArch.Benchmarks -c Release -- command-buffer`

## 坑点

- 只跑局部测试不看整体迁移可能破坏；跑完 `CommandBufferTests` 还必须回归 lifecycle / structural-change / query
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
