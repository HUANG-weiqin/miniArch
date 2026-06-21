---
title: 并行 Query 设计（待实现）
module: MiniArch.Core Query
description: ChunkView 作为并行工作单元的并行 query 迭代设计，含 API、安全模型、实现路径和 benchmark 目标
updated: 2026-06-21 (初稿，待实现)
---
# 并行 Query 设计（待实现）

## 这个模块是干什么的

- 这个模块负责：
  - 把 query 匹配到的 ChunkView 切片分给多个线程并行处理
  - 提供安全的"只读 + 组件值写入"并行入口
  - 明确什么能在并行体内做、什么不能
- 这个模块不负责：
  - System 调度（YAGNI，用户自己组织调用顺序）
  - Job 依赖链 / JobHandle（YAGNI，`Parallel.For` 已经够用）
  - 自动 Read/Write 冲突检测（Arch/Friflo 都没做，证明不痛）
  - 并行结构变更（Add/Remove/Create/Destroy 仍走主线程 `CommandStream`）

## 背景：为什么现在做

- Arch 和 Friflo 都有并行 query（详见对比表）
- MiniArch 当前完全单线程，50K 实体的 query 跑满一个核、其余闲着
- 已有的 `Query.GetChunkViewSpan()` 返回 `ChunkView[]`，每个 `ChunkView` 的 `GetSpan<T>()` 返回不相交的 `Span<T>`——**天然就是并行工作单元**，不需要重新设计存储
- 实施 ROI 高：~150-200 行代码，预期 4x+ 加速

## 架构

### 核心思路

```
Query.GetChunkViewSpan()  →  ChunkView[0]  ChunkView[1]  ...  ChunkView[N-1]
                                │              │                   │
                                ▼              ▼                   ▼
                            Thread 0       Thread 1            Thread N-1
                                │              │                   │
                                ▼              ▼                   ▼
                          chunk.GetSpan<T>() 返回不相交的 Span<T>
                          用户在 action 体内自由读写组件值
```

- **并行粒度**：ChunkView（一个 archetype 的一段，或非分段模式下整个 archetype）
- **不相交保证**：不同 ChunkView 引用不同 `byte[]`（分段模式）或同 `byte[]` 不同列偏移范围（非分段模式）—— 两者都是不相交内存区域，并发写安全
- **同实体不被两个线程处理**：`Parallel.For` 按 chunk 索引分区，一个 chunk 只给一个线程

### API 设计

在 `public readonly struct Query`（`src/MiniArch/Query.cs`）上新增：

```csharp
/// <summary>
/// Iterates matched chunks sequentially. Zero-alloc if the delegate is cached by the caller.
/// </summary>
public void ForEachChunk(ChunkAction action)
{
    var chunks = _query.GetChunkViewSpan();
    for (var i = 0; i < chunks.Length; i++)
        action(chunks[i]);
}

/// <summary>
/// Iterates matched chunks in parallel across worker threads.
/// Safe for component value reads/writes via chunk.GetSpan&lt;T&gt;().
/// NOT safe for structural changes (Add/Remove/Create/Destroy) — collect entity ids
/// and apply via CommandStream after this call returns.
/// </summary>
public void ForEachChunkParallel(ChunkAction action)
{
    var chunks = _query.GetChunkViewSpan();
    Parallel.For(0, chunks.Length, i => action(chunks[i]));
}

/// <summary>Delegate for chunk-level iteration.</summary>
public delegate void ChunkAction(ChunkView chunk);
```

### 用户侧使用方式

```csharp
// 1. 纯值更新（最常见的场景）
var desc = new QueryDescription().With<Position>().With<Velocity>();
var query = world.Query(in desc);

// 缓存 delegate 避免每帧 lambda 分配
static void Move(ChunkView chunk)
{
    var positions = chunk.GetSpan<Position>();
    var velocities = chunk.GetSpan<Velocity>();
    for (var i = 0; i < chunk.Count; i++)
        positions[i] = new Position(positions[i].X + velocities[i].X,
                                    positions[i].Y + velocities[i].Y);
}

query.ForEachChunkParallel(Move);

// 2. 需要结构变更的场景：并行收集，主线程提交
var toDestroy = new ConcurrentBag<Entity>();
query.ForEachChunkParallel(static chunk =>
{
    var healths = chunk.GetSpan<Health>();
    var entities = chunk.GetEntities();
    for (var i = 0; i < chunk.Count; i++)
        if (healths[i].Value <= 0)
            toDestroy.Add(entities[i]);
});

using var cs = new CommandStream(world);
foreach (var e in toDestroy)
    cs.Destroy(e);
cs.Submit();
```

## 决策

### 为什么选 ChunkView 作为并行单元（而不是 entity）

- Archetype 的存储就是按 chunk 组织的，`GetChunkViewSpan()` 已存在，零新基础设施
- 按 chunk 分区天然保证同一 entity 不会被两个线程处理
- 按 entity 分区需要额外的 entity→thread 映射，违反 YAGNI

### 为什么用 `Parallel.For` 而不是自定义线程池

- `Parallel.For` 是 .NET 内置，零依赖
- 自定义线程池（如 Arch 的 `ZeroAllocJobScheduler`）是另一个子系统的工程量
- 实测 `Parallel.For` 在 chunk 数 ≥ 核心数时性能足够好
- 如果未来 profiling 显示 `Parallel.For` 的 partitioner 开销大，再换——先证明收益再优化

### 为什么用 delegate 而不是 `IForEach` struct 接口

- Arch 走 `IForEach` struct 是为了 JIT 内联，避开虚方法调度
- 但 MiniArch 的 chunk API 让用户自己写内层 `for` 循环（遍历 `Span<T>`），delegate 调用只发生在外层（每 chunk 一次），不是热路径
- IForEach 模式要求用户为每种组件组合定义 struct，API 复杂度高
- **未来可选**：如果 profiling 显示 delegate 调用是瓶颈，再加 `IForEachChunk` struct 重载

### 为什么不做并行结构变更

- 结构变更涉及 swap-remove + `_records` 写入 + archetype 迁移，多线程同时做这个需要锁或 CAS，复杂度爆炸
- Arch 也不支持（明确文档警告）
- Friflo 用 `CommandBuffer.Synced` 支持，但文档自己说"通常比单线程慢"（CPU cache 跨核争用）
- **替代方案**：并行收集要变更的 entity id → 主线程走 `CommandStream` 批量提交。这才是真实游戏的正确模式

### 为什么不做 Read/Write 注解

- Unity ECS / Bevy 有 `[ReadOnly]` / `[WriteOnly]` + 自动依赖图
- Arch 和 Friflo 都没做——证明在 C# ECS 圈子这个痛度不够
- 用户自己保证不写冲突（同 entity 同组件不会被两个 chunk 同时处理，所以天然安全）

## 安全模型

### 允许在 `ForEachChunkParallel` 体内做的事

| 操作 | 安全性 | 说明 |
|---|---|---|
| `chunk.GetSpan<T>()` 读 | ✅ 安全 | 不同 chunk 的 span 不相交 |
| `chunk.GetSpan<T>()` 写 | ✅ 安全 | 同上，且同 entity 只在一个 chunk 内 |
| `chunk.GetEntities()` 读 | ✅ 安全 | 只读 |
| `chunk.TryGetComponentIndex<T>()` | ✅ 安全 | 只读 archetype 元数据 |
| 收集 entity id 到线程安全容器 | ✅ 安全 | `ConcurrentBag<T>` 等 |

### 禁止在 `ForEachChunkParallel` 体内做的事

| 操作 | 后果 | 替代方案 |
|---|---|---|
| `world.Add/Set/Remove/Create/Destroy` | 数据竞争 + 可能崩（swap-remove 改变其他 chunk 引用） | 收集 entity id，结束后走 `CommandStream` |
| `world.Get/GetRef/TryGet` | 安全但每次查 `_records[]` 有 false sharing 风险 | 用 `chunk.GetSpan<T>()` 代替 |
| 修改 `QueryDescription` 或新建 query | 死锁或数据不一致 | 在并行前完成所有 query 创建 |

### 实现侧的保证

- `ForEachChunkParallel` 调用 `GetChunkViewSpan()` 时会触发 `EnsureRefreshed()`——必须在并行开始前完成
- 并行期间不递增 `_flatEntitiesGeneration`（前提：用户不违规做结构变更）
- `ChunkView` 是 `readonly struct`，不可变，多个线程持有同一引用安全

## 实现计划

### 文件改动

| 文件 | 改动 |
|---|---|
| `src/MiniArch/Query.cs` | 新增 `ForEachChunk` / `ForEachChunkParallel` / `ChunkAction` delegate |
| `tests/MiniArch.Tests/Core/ParallelQueryTests.cs` | 新增：正确性、安全性、边界测试 |
| `tests/MiniArch.Benchmarks/ParallelQueryBenchmarks.cs` | 新增：vs Arch/Friflo 并行 query 对比 |

### 实施步骤

1. **写 API + 单线程版本**（`ForEachChunk`）：保证正确性，作为基准
2. **加并行版本**（`ForEachChunkParallel`）：`Parallel.For` 包装
3. **写正确性测试**：
   - 单组件写、双组件写、读后写、chunk 边界（多 archetype、多 segment）
   - 大规模 fuzz：10K entity 并行更新后值正确
4. **写 benchmark**：
   - 50K 实体 Position+Velocity，1/2/4/8 线程对比
   - 对比 Arch `InlineParallelChunkQuery` 和 Friflo `QueryJob.RunParallel`
5. **写文档**：在 `docs/README.md` 加并行使用说明

### 验证标准

| 指标 | 目标 |
|---|---|
| 50K 实体 Position+Velocity query，8 核 vs 1 核 | ≥ 4x 加速 |
| GC 分配 | 0/0/0（用户缓存 delegate 时） |
| HeroComing.Perf Movement | ≥ 866 rounds/s 不退化 |
| MiniArch.Tests 全部通过 | 是 |

## 认知模型

- 理解这个模块时，应该把它看成：
  - **"把已有的 chunk 切片分给线程池"** —— 不是新存储模型，不是新调度框架
- 这个模块里最重要的抽象是：
  - `ChunkView` 作为并行工作单元（已有，不新增）
  - `ChunkAction` delegate 作为用户代码入口（新增）
- 常见误解：
  - "并行 query 就是多线程 foreach entity" —— 错，是按 chunk 分区，不是按 entity
  - "并行期间能 Add 组件" —— 错，结构变更必须延迟到并行结束后
  - "需要自己加锁保护组件写入" —— 错，不同 chunk 的 span 不相交，天然安全

## 入口

- 第一次读或加功能，先看：
  - `src/MiniArch/Query.cs`：`ForEachChunk` / `ForEachChunkParallel` 的实现位置
  - `src/MiniArch/Core/Query.cs:GetChunkViewSpan()`：chunk 视图来源
  - `src/MiniArch/ChunkView.cs`：`GetSpan<T>()` 的不相交保证依据
- 修 bug，先看：
  - `ParallelQueryTests.cs`：复现并行竞态
  - `Core/Query.cs:EnsureRefreshed()`：刷新时机问题

## 坑点

- 历史上容易出问题的地方（实施时预期）：
  - `Parallel.For` 的闭包捕获：确保 `chunks` 数组在闭包内是只读引用，不修改
  - 用户在并行体内违规做结构变更：考虑加 `Debug.Assert` 检测（例如检测 `_flatEntitiesGeneration` 在并行前后是否变化）
  - chunk 数 < 核心数时并行退化：文档说明 + benchmark 验证
- 容易误判的地方：
  - "delegate 一定有分配" —— 缓存后零分配；`Parallel.For` 内部分配有固定成本但不影响正确性
  - "并行一定比单线程快" —— chunk 数少或每 chunk 工作量小时并行开销可能超过收益

## 不做的事（明确排除）

| 能力 | 理由 |
|---|---|
| System 调度器 | YAGNI，用户自己组织 5 行代码 |
| `IJob` / `JobHandle` 依赖链 | `Parallel.For` 足够；自定义线程池是另一个项目 |
| 自动 Read/Write 冲突检测 | Arch/Friflo 都没做，痛度不够 |
| 并行结构变更 | 复杂度爆炸，收益为负（cache 争用） |
| `IForEach<T1,T2,...>` struct 接口 | 未来可选，v1 先用 delegate 验证收益 |
| SIMD query 匹配 | query 匹配已经用 512-bit mask，瓶颈不在这 |
