---
title: Frame Read Models ValueLab
module: MiniArch（用户 API 分层）
description: Snapshot 内 Build 一次、查询多次的帧内派生索引 ValueLab 与 public API gate 结论、适用区间、禁用区间与后续产品化边界
updated: 2026-07-11
---
# Frame Read Models ValueLab

## 这个模块是干什么的

- 这个模块负责：
  - 记录 Frame Read Models ValueLab 的实测结论。
  - 记录 FrameLookup public API gate 的进入/暂停结论。
  - 判断 Snapshot 内派生索引是否值得进入下一阶段产品化。
  - 固化适用区间、禁用区间、布局裁决。
- 这个模块不负责：
  - 记录未实测的设计猜想。
  - 定义最终生产 public API。
  - 替代 `World.Query` 或自动 freshness tracking。

## 架构

- 核心组成：
  - `tools/perf/FrameReadModels.ValueLab/`：独立实验项目，不进入生产 API。
  - `CompactRowLookup<TKey>`：两遍 CSR，保存 `(chunkIndex,rowIndex)`。
  - `CompactRowLookup<TKey>.ForEach(...)`：public API consume 形态探针；正确性通过但性能未过线。
  - `CompactRowLookup<TKey>.ForEachRun(...)`：chunk-run/batched consume 探针；正确性通过但通用场景性能 No-Go。
  - `LinkedRowLookup<TKey>`：单遍 linked rows，对照布局。
  - `EntityArrayLookup<TKey>`：entity-only baseline。
  - `DenseIntCompactLookup`：有界 int key 特化候选。
- 数据流 / 控制流：
  - 稳定 Snapshot → `world.Query(desc).GetChunks()` → Build lookup → 多次按 key 只读查询。
  - 组件读取从 `ChunkView.GetSpan<T>()` + row ref 取值，不复制组件 payload。

## 决策

- **Conditional Go。** uniform / 高基数 / 组件读取场景值得继续产品化 compact CSR row-ref lookup。
- **Public API Gate: Conditional Hold（2026-07-11）。** compact CSR 本体继续成立，但逐 row `ForEach(key, consumer)` 发布形态性能不过线，暂不进入 `src/MiniArch/`。
- **不进 Core。** ValueLab 没有修改 `World/Archetype/QueryCache/CommandStream`；后续产品化仍应保持 0 core intrusion。
- **不做完整关系代数 API。** 下一步只允许 1-3 组件 Rows、一个 Where、一个 KeyBy、一个 FrameLookup terminal。
- **Direct ForEach 不作为 v1 形态。** full run 中 DirectForEach row component consume 慢于 `CopyRowRefs + manual consume`：realistic 约 13.41×，full-1m 约 1.98×。
- **Chunk-run / batched consumer 不作为通用 v1 形态。** on-the-fly run 合并正确但 No-Go：realistic 中同 key rows 不连续，RunForEach 比 CopyRowRefs 慢约 34.15×；full-1m 慢约 1.94×。它只在 hot bucket 改善 DirectForEach，而 hot bucket 不是目标区间。
- **Linked rows 不产品化。** Build 快但读取跳跃，未找到胜出区间。
- **hot bucket 不是已解决场景。** 单 key 大桶重复查询时 row-ref lookup 被重复大桶遍历拖垮；需要禁用说明或单独 entity-only specialization。

## 认知模型

- 理解这个模块时，应该把它看成：
  - “每个 Snapshot 先按 key 整理一次索引；本 Snapshot 内多次查询复用索引”。
- 这个模块里最重要的抽象是：
  - `RowRef(chunkIndex,rowIndex)`：Snapshot 行位置。
  - `FrameLookup<TKey>`：最近一次成功 Build 的派生结果，不是自动缓存。
- 常见误解：
  - “它会跟着 World 修改自动刷新” → 不会，Snapshot 查询阶段不能修改。
  - “hot bucket 也一定快” → 不一定，大桶重复遍历会吞掉收益。

## 入口

- 第一次读或加功能，先看：
  - `docs/plans/2026-07-11-frame-read-models-report.md`：实测结论和数据。
  - `tools/perf/FrameReadModels.ValueLab/FrameReadModelBenchmarks.cs`：性能矩阵。
  - `tools/perf/FrameReadModels.ValueLab/FrameReadModelCorrectness.cs`：正确性矩阵。
- 产品化前先看：
  - `docs/plans/2026-07-11-frame-read-models.md`：原始边界、Go 门槛、判死线。
  - `docs/plans/2026-07-11-frame-lookup-api-design.md`：M1 public API 契约草案（已被 M4 gate 部分否决）。
  - `docs/plans/2026-07-11-frame-lookup-api-gate-report.md`：M4 Conditional Hold 决策与 DirectForEach 数据。
  - `docs/plans/2026-07-11-frame-lookup-chunk-run-report.md`：chunk-run / batched consumer No-Go 数据。

## 坑点

- 不要把 ValueLab 原型直接复制进 public API；产品化必须重新 TDD。
- 不要把逐 row DirectForEach 当成已验证发布形态；它正确但太慢，必须先重设计 consume 形态。
- 不要把 chunk-run 当成自然解：uniform/high-cardinality 下同 key rows 分散，run 数接近 row 数，callback 仍近似 per-row。
- `TryBuildNoGrow` 失败必须保持目标为空，不能发布部分结果。
- AutoGrow 后旧 view/span/enumerator 全部失效。
- `ComponentBucketQuery` 是 per-key scan 基线；在 Q 达到上万时会回到 `Q × N`。
- Dictionary baseline 会分配大量 `List<Entity>`，不能作为唯一胜利证据。
- `EntityArrayLookup` 很适合 entity-only/hot baseline，但不满足 chunk-row component span 读取目标。
