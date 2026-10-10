---
title: 并行 Query 迭代
module: MiniArch.Core Query
description: ChunkView 并行迭代的零分配边界、调用状态所有权、同步完成与嵌套安全模型
updated: 2026-10-11
---
# 并行 Query 迭代

## 这个模块是干什么的

- `ForEachChunkParallel` 同步完成一批不相交的组件行工作；正常调用使用 .NET ThreadPool，调用线程也参与。
- **稳态零分配**：缓存 `ChunkAction` 与 struct `IChunkForEach` 两条路径均复用库内分发状态。前提是调用线程、job 类型、嵌套深度、分区/worker 容量及运行时线程池已预热，回调自身不分配。
- 首次使用、容量增长、异常，以及用户回调或其 `ExecutionContext` 变更通知的分配不在上述承诺内。
- 不负责 System 调度、JobHandle/依赖图、自动读写冲突检测或并行结构变更；不新增公共 Job API，也不自建永久线程池。

## 架构

### 单一入口与调用状态

```text
Query.ForEachChunkParallel(ChunkAction)
    → ActionChunkForEach adapter
    → ForEachChunkParallel<TForEach>
    → ParallelQueryRunner.Run
    → borrow query views / build entity-range views
    → lease state, capture context, queue reusable work items
    → caller + workers consume disjoint partitions
    → join and acknowledge final notification
    → clear borrowed references and release state
```

- `Query.cs` 只负责公共入口与 delegate adapter；`ParallelQueryRunner.cs` 是唯一分区、分发及 lease owner。
- `Core.Query.GetChunkViewArray(out count)` 返回缓存数组及有效长度；不能把数组容量当作 live count，也不能清空借来的数组。
- 状态按**调用线程 + job 类型**缓存。同线程同类型重入租用链中的下一个空闲状态；不同调用不共用活动分区、job 或完成信号。
- 直接 chunk 路由只借用 query 数组；实体子区间路由使用状态自己拥有的 split buffer。归还时清空 owned split views、job/delegate、context、异常引用，并断开 borrowed array 引用；缓存不能长期保活上一轮 World。

### 分区与工作线程

- 匹配 chunk 数不少于处理器数时，每个 chunk 是一个工作单元。
- chunk 较少时，沿各 chunk 的实体区间切成不重叠子视图，因此**单 archetype / 单 chunk 也能并行**。
- caller 保留第 0 分区；其余分区通过共享递增索引领取，每行只属于一个分区。
- 工作项是可复用的 `IThreadPoolWorkItem`，交给原生 ThreadPool；worker 数受分区、处理器和可用线程池容量约束。并行度与回调顺序不作固定保证。
- callback 内嵌套的已建 query 使用 **0-worker 路由**，仍走同一准备、执行、上下文恢复及清理流程，避免线程池 worker 全部等待自己排入的内层工作。内层不保证另起并行。

### 完成边界

- 成功或异常退出前，必须等所有已提交 worker 完成回调，不允许调用方已开始结构变更而旧 worker 仍在访问 World。
- `_pending == 0` 或 `ManualResetEventSlim.IsSet` 都不足以许可状态复用：`Set()` 内部会先发布 `IsSet`，再完成通知。
- 最后一个 worker 在 `Set()` 返回后发布尾部 acknowledgement。caller 看到它后才能清理状态、下次 `Reset()` 或归还 lease。
- 部分提交失败也要等待已经提交的 worker；等待中的 `ThreadInterruptedException` 先记录，完成 join 后再传播。
- 回调失败可停止尚未开始的分区；已完成的组件写入不回滚，不承诺事务。

### 上下文与值语义

- 正常 flow 下，多分区执行用 `ExecutionContext.Capture()` 与缓存的 `ContextCallback`，caller 和 worker 都经 `ExecutionContext.Run()`；保留 `AsyncLocal` / culture 的流动及修改后的恢复。不会为此每轮构造 closure。
- 调用方显式 `ExecutionContext.SuppressFlow()` 时，Capture 为 null，不向 workers 流动上下文；caller 上执行的 callback 修改可留在调用方。这与旧 TPL 的可达行为一致，不新增“抑制 flow 仍保证 caller 隔离”的承诺。成功/异常及 CPU1 的对照证据在 `baseline/suppress-flow/`，见缺陷索引的非 bug 条目。
- 保留原路由边界：仅“chunk 少于处理器数、拆分后恰好一分区”的快路直接调用，异常及 ambient 修改直接传播。`count >= processorCount` 路由仍聚合异常，并在正常 flow 下隔离上下文；这也包括单 CPU 上的一个 chunk。
- 并行 struct 参数按值传入，但**同一次调用中的所有 callback 共享这一份副本**，不是每个 worker 一份。并发修改其字段需外部同步；调用方原变量不回写。
- 不承诺沿用旧 TPL 的 `TaskScheduler.Current`、Task identity 等实现细节。

## 决策

- 原 `Parallel.For` 每次调用创建内部调度对象；外部缓存 delegate 或换成 struct job，都不能消除这些对象。捕获 lambda 还使空查询/单分区提前返回时分配约 40 B。
- 因此不在各调用方加缓存补丁：在同步并行入口的自然 owner 替换分发机制，使用平台工作项能力并复用本次调用状态。
- 不改存储、query matching、公开签名及顺序 `ForEachChunk`；不为这个问题引入新线程池、依赖链或公共调度器。
- nested 只是 workerCount 为 0 的同一执行流程，不另建一套 runner、异常处理或上下文模型。
- 保留 delegate 的便利性与 struct 的 JIT 特化；typed entity/ref Query 的独立设计取舍见 [设计理由](kb-design-rationale.md)。

## 认知模型

把它看成一次**借用稳定视图的同步批处理**，而不是后台 Job 生命周期：

1. 调用方预先创建 query，保证执行期间无结构变更。
2. 一次调用租用独立状态；它可以被多个线程读写，但不能同时属于下一次调用。
3. 组件数据归 World，视图只是借用；分区、完成信号及 callback 副本归本次调用。
4. 最后一次通知真正结束，才允许释放借用、清理引用和复用状态。

### 公共用法

```csharp
ChunkAction move = Move;
query.ForEachChunkParallel(move);     // Reuse the delegate across frames.

var job = new MoveJob();
query.ForEachChunkParallel(job);      // One shared by-value copy per call.
query.ForEachChunk(ref job);          // Sequential; field changes reach the caller.
```

使用 chunk 的 span 在内部循环中访问组件。若要累计结果，使用调用方拥有的线程安全 collector 或不冲突的输出区域；collector 的分配不是库内零分配的一部分。

### 安全边界

- 可读写本分区 `chunk.GetSpan<T>()`，可读 entities/span 与 archetype 元数据。
- 不相交分区只保证这一轮按分区访问不冲突；任意实体访问、外部共享状态或内外 query 重叠写入仍需调用方协调。
- 不得并发 `Add/Remove/Create/Destroy/Clone/Restore` 等结构变更，也不得在 worker 中首次创建 query cache。预建内层 query 后可以嵌套读取。
- 结构命令先收集到线程安全的调用方存储，等本调用返回后再由主线程录入/提交 `CommandStream`；不要让 workers 共写一个 stream。

## 入口

- 公共入口与 adapter：`src/MiniArch/Query.cs`。
- 调用状态与分发：`src/MiniArch/ParallelQueryRunner.cs`。
- 缓存视图：`src/MiniArch/Core/QueryCache.cs`。
- 区间与 span：[chunk storage](kb-chunk-storage.md)、`src/MiniArch/Core/ChunkView.cs`。
- 原有覆盖：`tests/MiniArch.Tests/Core/ParallelQueryTests.cs`，包括每行覆盖、组件写入和同线程嵌套不覆写的历史 witness。
- 零分配及生命周期：`tests/MiniArch.Tests/Core/ParallelQueryAllocationTests.cs`，包括跨线程字节、跨 T nested、异常后复用、并发调用、满 max1/max2 pool root、共享 struct copy、EC 读写恢复及弱引用回收。分配用例由 `tests/MiniArch.Tests/Program.cs` 的独立进程入口执行；流程见 [测试工作流](kb-test-workflow.md)。
- 真正的 S14：`dotnet run -c Release --project tools/perf/FrifloGameScenarios.Perf -- S14`；比较数字及限制集中在 [ECS 对比](kb-ecs-comparison.md)。
- 缺陷索引：[代码审阅发现](kb-code-review-findings.md)。

## 验证口径

- 并行工作必须量 `GC.GetTotalAllocatedBytes(true)`，只量调用线程会漏 worker 分配；0 次 GC 也不能证明 0 B。
- 2026-10-10 原实现独立探针：100K 同形 S14 每次约 3.8–4.3 KB 全线程分配；顺序 caller 为 0 B。根因是 closure/TPL，不是 S14 已缓存的 callback。
- 修复后的公开入口独立探针覆盖默认及 `AsyncLocal=42` / `fr-FR`，cached delegate 与 struct 各 10K/20K 次；所有窗口 caller 和全线程均为原始 0 B。单 CPU 的公开路径 10K 次也为 0 B，未扣除控制组常数。
- 9 个分配回归在不运行 VSTest 宿主的独立进程中执行：预热 4096 次，再 `Collect → WaitForPendingFinalizers → Collect → WaitForPendingFinalizers`；度量区不输出、不构造 delegate、不做断言，32/16 次调用后仍严格断言全进程总字节为 0。VSTest 只检查子进程完成/退出码，语义用例仍在原宿主；不以 caller-only 计数漏掉 worker。
- 2026-10-11 发布复验中，原宿主内 empty/struct 用例仍出现一次 352 B，具体分配栈未捕获；静态空路径不进入分发，独立 empty 与相邻 no-op 窗口均为 0 B。禁止把未归因的全宿主字节硬算成每次库分配，也不放宽 0 B；改为隔离宿主后，旧库的 9 个用例仍全部只因分配非零失败。证据：`artifacts/release-7.0.0/tests.log` 与 `artifacts/parallel-zeroalloc-20261010/child-process-20261011/`。
- 历史短窗只预热 128 次且未排空 finalizer，曾出现位置和值均漂移的数百到数千字节；不能归因为固定 per-call 成本，也不能用任意容忍量或挑最小样本掩盖。修复的是稳态准备，不是 0 B 断言。
- 证据目录：`artifacts/parallel-zeroalloc-20261010/`。真实 S14 与全套回归必须覆盖最终集成状态，不能只接受相似原型。

## 坑点

- 缓存 delegate / 使用 struct 只消除其自身的包装分配，不能推导底层调度也零分配。
- 匹配了空 archetype 不等于没有匹配 view：原 `count >= processorCount` 路由会回调空 view，本次保留；不要随意统一成零回调。
- ThreadStatic 只隔离线程，不自动隔离同线程重入；活动状态必须持有 lease。
- 完成计数、唤醒等待者、许可复用是三个观察时点；不要用早期 `IsSet` 替代最后一个时点。
- pooled state 必须清引用，但不得清空借来的 QueryCache 数组。
- 并行不一定更快：小工作量、内存带宽饱和或线程池容量受限时，顺序可能更合适。用相同负载的 Release 测量，不按实体数直接推算加速比。
- 这不是“整个库每个 API 都零分配”的声明；返回新对象、显式异步 `Task` 等接口有自己的分配边界。
