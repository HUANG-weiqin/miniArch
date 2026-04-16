# MiniArch Component Query Hotspot Design

Date: 2026-04-16

## 结论

- 当前 `MiniArch` 在 `query-with-all-component-span` workload 上仍明显落后 `Arch`，这已经是 query 读路径的主要剩余性能缺口。
- 这轮不直接猜测具体 runtime 优化点，而是先补齐一条针对 `component-consuming query` 的统一观测链路：`benchmark -> throughput -> profiling`。
- 推荐方案是先扩展 profiling 和对比入口，让 `component span` workload 可以被稳定复跑、采样和对比，再根据采样结果决定后续是优化 `Chunk.GetComponentSpan<T>()`、列定位，还是更上层的 query 遍历包装。
- 本轮的完成目标不是“追平 Arch”，而是“定位热点并沉淀一份可执行实施计划和最小验证矩阵”。

## 目标

- 把 `component-consuming query` 的性能问题从“已知慢”推进到“已知慢在什么层级、接下来先改哪里”。
- 为后续 runtime 优化提供稳定的诊断入口，避免每次临时拼 workload 或混用 entity-only 结果。
- 让 profiling、throughput 和 BenchmarkDotNet 对同一类 component span workload 说同一种语言。

## 非目标

- 本轮不直接重写 query API。
- 不引入新的系统层 typed query public API。
- 不在没有采样证据的情况下直接修改 `Chunk` / `Query` 热路径实现。
- 不把这轮目标扩大成 “component span 一步追平 Arch”。

## 当前事实

- `.knowledge/kb-throughput-workflow.md` 已记录：`query-with-all-component-span` 在 `100000` entity 档位下，`MiniArch` 平均 `9615.78 ops/s`，`Arch` 平均 `17691.85 ops/s`，`MiniArch` 落后约 `45.65%`。
- entity-only steady-state query 吞吐已经领先 `Arch`，说明 query 统一 generation 和 entity span 路径不是当前主矛盾。
- `benchmarks/MiniArch.Benchmarks/QueryBenchmarks.cs` 已经有 component row-wise / component span benchmark。
- `benchmarks/MiniArch.Benchmarks/ThroughputRunner.cs` 已经有 `query-with-all-component-span` workload。
- 但 `benchmarks/MiniArch.Benchmarks/QueryProfilingRunner.cs` 目前只执行 entity checksum 路径，无法直接对 `component span` workload 做 sampling。
- `src/MiniArch/Core/Chunk.cs` 的 `GetComponentSpan<T>(ComponentType)` 每个 chunk 仍会做：
  - `GetComponentIndex(component)`
  - `GetComponentSpanAt<T>(columnIndex)`
  - typed column 运行时类型检查
- 当前还没有证据证明主要瓶颈一定落在上述任意一步；它也可能在更上层的 query/chunk 遍历包装。

## 方案比较

### 方案 A：观测先行，统一 component workload 入口（推荐）

做法：

- 扩展 `QueryProfilingRunner` 和 `scripts/profile-query.ps1`，显式支持 `component span` workload。
- 让 profiling runner 可以区分：
  - entity-only 遍历
  - component row-wise
  - component span
- 保持 `QueryBenchmarks` 和 `ThroughputRunner` 的 component workload 定义与 profiling runner 对齐。
- 先用采样确认热点集中在哪一层，再决定 runtime 优化切入点。

优点：

- 证据链完整，后续优化不会建立在猜测上。
- 改动风险低，能快速形成稳定的诊断基线。
- 与本轮“定位热点 + 产出执行方案”的完成标准完全一致。

缺点：

- 本轮不会直接缩小性能差距。
- 需要先投入一轮可观测性建设。

### 方案 B：直接优化 `Chunk.GetComponentSpan<T>()`

做法：

- 直接围绕 `GetComponentSpan<T>()` / `GetComponentSpanAt<T>()` 做低层优化。
- 优先考虑减少每 chunk 的列定位和运行时类型检查。

优点：

- 如果猜对热点，见效最快。

缺点：

- 当前 profiling 不覆盖 component span，容易在证据不足时修错地方。
- 一旦主要成本不在 `Chunk`，就会出现回头补诊断入口的二次返工。

### 方案 C：系统层 typed query 帮助器

做法：

- 在 query 或系统层引入更高层的 typed 遍历帮助器，把组件列定位和 span 提取提前到外层。

优点：

- 长期上限更高。
- 可以减少业务侧重复样板。

缺点：

- 设计面太大，不适合这轮“先定位热点”。
- 会把问题从性能诊断扩大成 API 演进。

## 推荐方案

推荐方案 A：`观测先行，统一 component workload 入口`。

理由：

- 现有仓库已经有 component benchmark 和 throughput 口径，缺的不是“是否值得看”，而是“如何稳定看到它为什么慢”。
- 当前最明显的断层在 profiling：entity-only 有独立 runner，component-consuming query 没有。
- 先补齐观测链路后，后续 runtime 优化可以更明确地在以下方向里二选一：
  - `Chunk` 级 typed column 读取优化
  - `Query` 级遍历包装与每 chunk 固定成本优化

## 设计细节

### 1. 统一 workload 语言

- 为 profiling 引入与 benchmark/throughput 对齐的 workload 维度，而不是只保留 `scenario + temperature`。
- 推荐新增三类 execution mode：
  - `entity`
  - `component-row-wise`
  - `component-span`
- 这样能保证三条验证路径在讨论同一 workload：
  - `QueryBenchmarks`
  - `ThroughputRunner`
  - `QueryProfilingRunner`

### 2. profiling runner 扩展

- `QueryProfilingRunner` 需要能够复用 `BenchmarkWorldFactory.CreateMiniComplexQueryWorld(...)` 里已有的：
  - `WithAllQuery`
  - `PositionType`
  - `VelocityType`
- 在 execute 阶段新增：
  - entity checksum 路径
  - component row-wise checksum 路径
  - component span checksum 路径
- 采样默认仍应保留 `hot/cold` 区分：
  - `hot` 看 steady-state component traversal
  - `cold` 看 fresh query + component traversal 的混合成本

### 3. runtime 诊断假设边界

- 本轮只定义待验证假设，不提前承诺哪个必然成立。
- 优先验证的三个假设：
  - 假设 1：主要成本在 `Chunk.GetComponentSpan<T>()` 的每 chunk 固定开销。
  - 假设 2：主要成本在 component span 读循环本身，与 Arch 的 typed span 暴露方式存在差异。
  - 假设 3：主要成本不在 `Chunk`，而在 query/chunk 外层包装和数据组织。

### 4. 验收输出

- 这轮设计完成后，应能明确回答：
  - 用哪条命令看 component span throughput。
  - 用哪条命令采样 component span 热点。
  - 后续 runtime 优化应先看哪几个函数。
- 后续实现完成后，应能从 profiling topN 和 throughput 结果中判断：
  - 热点是否真的集中到了目标函数。
  - 优化是否改善了真实 steady-state `ops/s`，而不只是短 benchmark 均值。

## 风险

- 如果 profiling workload 与 throughput workload 不完全同构，后续结论可能互相打架。
- 如果 component profiling 只保留 span、不保留 row-wise，对比维度会不完整，难以判断“慢在 span 提取”还是“慢在组件读取本体”。
- 如果本轮直接把 runtime 优化混进观测改造，后续很难区分“工具变了”还是“实现变了”。

## 最小验证矩阵

第一阶段：观测链路打通

- `powershell -ExecutionPolicy Bypass -File scripts\throughput.ps1 -Workload query-with-all-component-span -DurationSeconds 3 -RepeatCount 3`
  - 目标：确认 component span 吞吐口径仍可复跑。
- `dotnet run --project .\benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj -c Release --filter "*Components_Execute_Warmed*" -j short`
  - 目标：确认 component row-wise / span benchmark 口径都仍存在。
- `powershell -ExecutionPolicy Bypass -File scripts\profile-query.ps1 ...`
  - 目标：新增 component workload 参数后，能稳定采到 component span 热点。

第二阶段：热点定位

- `component-row-wise` 与 `component-span` 都跑一遍 `hot`
  - 目标：先判断差距主要来自 span 提取还是组件读取本体。
- 对 `component-span` 跑一次 `cold`
  - 目标：确认 query refresh/build 是否只占小头，避免误把冷路径当主矛盾。

## Definition Of Done

- `docs/plans` 下存在这份设计文档和对应 implementation plan。
- 设计明确给出推荐路线、非目标、风险和验证矩阵。
- implementation plan 能直接指导下一轮实现，不需要再次重做需求澄清。
