---
title: 并行 Query 迭代
module: MiniArch.Core Query
description: ChunkView 作为并行工作单元的并行 query 迭代 API、安全模型、性能特征
updated: 2026-07-09
---
# 并行 Query 迭代

## 这个模块是干什么的

- 这个模块负责：
  - 把 query 匹配到的 `ChunkView` 数组切片分给多个线程并行处理
  - 提供安全的"只读 + 组件值写入"并行入口（`ForEachChunkParallel`）
  - 提供对照的单线程版本（`ForEachChunk`）作为基准和零分配快速路径
- 这个模块不负责：
  - System 调度（YAGNI，用户自己组织调用顺序）
  - Job 依赖链 / JobHandle（`Parallel.For` 已经够用）
  - 自动 Read/Write 冲突检测（Arch/Friflo 都没做，证明不痛）
  - 并行结构变更（Add/Remove/Create/Destroy 仍走主线程 `CommandStream`）

## 架构

### 核心思路

```
Query.GetChunkViewArray()  →  ChunkView[0]  ChunkView[1]  ...  ChunkView[N-1]
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

### API

在 `public readonly struct MiniArch.Query`（`src/MiniArch/Query.cs`）上：

```csharp
public delegate void ChunkAction(ChunkView chunk);

// Delegate-based（缓存 delegate 时零分配）：
public void ForEachChunk(ChunkAction action);          // 顺序迭代
public void ForEachChunkParallel(ChunkAction action);  // 并行迭代，组件值读写安全

// IChunkForEach-based（始终零分配 + JIT 特化去虚化）：
public interface IChunkForEach {
    void OnChunk(ChunkView chunk);
}
public void ForEachChunk<TForEach>(ref TForEach forEach)
    where TForEach : IChunkForEach;            // 顺序；ref 支持有状态 job
public void ForEachChunkParallel<TForEach>(TForEach forEach)
    where TForEach : IChunkForEach;            // 并行；struct 被捕获进 Parallel.For 委托，所有 worker 共享同一实例
```

实现侧的关键决策：
- `ForEachChunk` 直接对 `_query.GetChunkViewSpan()` 跑 `for` 循环，没有跨 lambda 分配。
- `ForEachChunkParallel` 用 `_query.GetChunkViewArray(out var count)`（新增内部 API）拿到 underlying `ChunkView[]` 数组+count，因为 `ReadOnlySpan<T>` 无法被 lambda 捕获。
- 并行入口：`Parallel.For(0, count, i => action(chunks[i]))`。
- 共享 `BuildEntityRangePartitions` helper 处理"chunk 数 < 线程数"时的 entity 子区间拆分（delegate 和 IChunkForEach 路径共用）。

### 典型用法

```csharp
// 1. 纯值更新（最常见）
var desc = new QueryDescription().With<Position>().With<Velocity>();
var query = world.Query(in desc);

static void Move(ChunkView chunk)
{
    var positions = chunk.GetSpan<Position>();
    var velocities = chunk.GetSpan<Velocity>();
    for (var i = 0; i < positions.Length; i++)
        positions[i] = new Position(positions[i].X + velocities[i].X,
                                    positions[i].Y + velocities[i].Y);
}

query.ForEachChunkParallel(Move);  // 缓存 Move 避免每帧 lambda 分配

// 2. 需要结构变更：并行收集 → 主线程提交
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
- 实测在 chunk 数 ≥ 核心数时性能足够好
- 如果未来 profiling 显示 `Parallel.For` 的 partitioner 开销大，再换——先证明收益再优化

### 为什么提供两套 API（delegate + IChunkForEach）

- **delegate 路径**：保留是为了简洁。`Move` 是 static method 时 `query.ForEachChunkParallel(Move)` 编译器生成 static delegate，零分配。适合一次性脚本/原型。
- **IChunkForEach 路径**：解决 inline lambda 不缓存场景的隐藏分配。用户每帧写：
  ```csharp
  query.ForEachChunk(chunk => { /* ... */ });  // ← 每次 call 都 new Delegate
  ```
  换成 struct + interface 后 JIT 跨泛型特化，零分配 + 内层调用去虚化。
- **设计取舍**：没有像 Arch/Friflo 那样提供 `IForEach<T1,T2>` typed span 接口。ChunkView 内部的 `GetSpan<T>()` 已经只做 1 次 colIdx 查找 + 1 次 IsChunked 分支（每 chunk 而非每 entity），typed 接口能省的常数有限。YAGNI：用户用例出现 typed 收益后再加。
- **顺序 vs 并行的参数模式**：顺序用 `ref TForEach`（job 字段跨 chunk 可见，支持 accumulator）；并行用 by-value `TForEach`（struct 被值捕获进 `Parallel.For` 闭包——所有 worker 共享同一 captured copy。在 `OnChunk` 中 mutate struct 字段对闭包 copy 构成 data race，且调用方变量不会被更新。要产生可见结果，应写到外部共享引用状态、线程本地状态并显式合并，或使用线程安全 collector。并行不能用 `in`/`ref`，因为 `Parallel.For` 的 lambda 无法捕获 ref-like 参数）。

### 历史：为什么用 delegate 起步

- Arch 走 `IForEach` struct 是为了 JIT 内联，避开虚方法调度
- 但 MiniArch 的 chunk API 让用户自己写内层 `for` 循环（遍历 `Span<T>`），delegate 调用只发生在外层（每 chunk 一次），不是热路径
- IForEach 模式要求用户为每种组件组合定义 struct，API 复杂度高
- **2026-06-30 落地**：profiling 在 inline-lambda-不缓存的真实使用模式下显示 delegate 分配是 GC 噪声来源，加 `IChunkForEach` 路径消除该路径上的全部分配

### 为什么 `ForEachChunkParallel` 内部用 `GetChunkViewArray` 而不是 `GetChunkViewSpan`

- C# 限制：`ReadOnlySpan<T>`（ref struct）无法被 lambda 捕获
- `Core.Query` 已持有 `ChunkView[] _snapshotChunkViews`，新增 `GetChunkViewArray(out int count)` 直接返回该数组+有效长度，零分配
- 用户文档仍然只看到 `Query.ForEachChunkParallel` 公共 API

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

- `ForEachChunkParallel` 调用 `GetChunkViewArray()` 时会触发 `EnsureRefreshed()`——必须在并行开始前完成
- 并行期间不递增 `_flatEntitiesGeneration`（前提：用户不违规做结构变更）
- `ChunkView` 是 `readonly struct`，不可变，多个线程持有同一引用安全

## 性能特征（实测，4 核 8 线程 i5-1135G7）

工作负载：`Position += Velocity`（16 bytes/entity），单 archetype。

| 实体数 | ForEachChunk (顺序) | ForEachChunkParallel | 加速比 | 说明 |
|---|---|---|---|---|
| 10K | 56 us | 77 us | 0.73x | 单 chunk，并行退化 |
| 50K | 127 us | 96 us | 1.33x | 多 segment，开始有收益 |
| 100K | 258 us | 284 us | 0.91x | 单 segment（100K < SegmentEntityCapacity=131072），并行退化 |

**关键观察**：

- **加速依赖 chunk 数 ≥ 核心数**。Position+Velocity（16 bytes/entity）的 `SegmentEntityCapacity = 131072`，所以 100K 实体在单 archetype 下只占 1 个 segment = 1 个 chunk = 没有并行空间。
- **memory-bound 工作负载收益小**：`Position += Velocity` 是纯顺序读写，单核就能跑满内存带宽。计算密集或随机访问模式才能从并行获益。
- **Arch 对照**：在同样工作负载下 Arch 的并行版本比顺序还慢（chunk 数少 + `Parallel.For` 开销 + GC 压力），MiniArch 的 `ForEachChunkParallel` 不输 Arch。

### 何时并行有收益

- ✅ 实体数 >> `SegmentEntityCapacity / componentSizeBytes`（多个 segment）
- ✅ 多 archetype 匹配（每个 archetype 至少一个 chunk）
- ✅ 每 chunk 工作量大（例如复杂计算、非线性内存访问）
- ❌ 单 archetype + 实体数 < `SegmentEntityCapacity`（只有 1 chunk）
- ❌ 工作负载是纯顺序 memory streaming（单核已饱和带宽）

### 如何验证并行实际启动

参考 `tests/MiniArch.Tests/Core/ParallelQueryTests.cs::ForEachChunkParallel_may_use_multiple_threads`——通过 chunked 模式强制生成 16+ segments，观察 `Environment.CurrentManagedThreadId` 出现多个不同值。

## 入口

- **API 实现**：`src/MiniArch/Query.cs` 的 `ForEachChunk` / `ForEachChunkParallel` / `ChunkAction`
- **底层 chunk 数组访问**：`src/MiniArch/Core/QueryCache.cs:GetChunkViewArray()`（新增，给并行入口用）
- **chunk 视图来源**：`src/MiniArch/Core/QueryCache.cs:GetChunkViewSpan()` 和 `EnsureRefreshed()`
- **不相交内存保证依据**：`src/MiniArch/Core/ChunkView.cs:GetSpan<T>()` + `kb-chunk-storage.md`
- **正确性/竞态测试**：`tests/MiniArch.Tests/Core/ParallelQueryTests.cs`（18 个测试）
- **vs Arch 对比 benchmark**：`tests/MiniArch.Benchmarks/ParallelQueryBenchmarks.cs`

## 坑点

- **历史上容易出问题的地方**：
  - 试图把 `ReadOnlySpan<ChunkView>` 直接捕获进 `Parallel.For` 的 lambda——C# 不允许 ref struct 跨 lambda 边界，必须用 underlying `ChunkView[]`
  - 误以为"50K 实体一定能 4x 加速"——chunk 数决定并行度，单 archetype + 单 segment 时无并行空间
- **容易误判的地方**：
  - "并行一定比单线程快" —— chunk 数少或每 chunk 工作量小时并行开销可能超过收益
  - "delegate 一定有分配" —— 缓存后零分配（实测 `ForEachChunk` 仅 134 B 来自 `Parallel.For` 的固定开销，用户缓存的 static delegate 本身零分配）
  - "并行期间能 Add 组件" —— 错，结构变更必须延迟到并行结束后，否则 swap-remove 会破坏其他 chunk 引用

## 不做的事（明确排除）

| 能力 | 理由 |
|---|---|
| System 调度器 | YAGNI，用户自己组织 5 行代码 |
| `IJob` / `JobHandle` 依赖链 | `Parallel.For` 足够；自定义线程池是另一个项目 |
| 自动 Read/Write 冲突检测 | Arch/Friflo 都没做，痛度不够 |
| 并行结构变更 | 复杂度爆炸，收益为负（cache 争用） |
| `IForEach<T1,T2,...>` typed span 接口 | ChunkView.GetSpan 已是 per-chunk 而非 per-entity 开销；先证明 typed 收益再加 |
| SIMD query 匹配 | query 匹配已经用 512-bit mask，瓶颈不在这 |
