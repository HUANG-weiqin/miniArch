---
title: Frame Read Models ValueLab
module: MiniArch（用户 API 分层）
description: Snapshot 内 Build 一次、查询多次的帧内派生索引 ValueLab 结论、适用区间、禁用区间与后续产品化边界
updated: 2026-07-11
---
# Frame Read Models ValueLab

## 这个模块是干什么的

- 这个模块负责：
  - 记录 Frame Read Models ValueLab 的实测结论。
  - 判断 Snapshot 内派生索引是否值得进入下一阶段产品化。
  - 固化适用区间、禁用区间、布局裁决。
- 这个模块不负责：
  - 记录未实测的设计猜想。
  - 定义生产 public API。
  - 替代 `World.Query` 或自动 freshness tracking。

## 架构

- 核心组成：
  - `tools/perf/FrameReadModels.ValueLab/`：独立实验项目，不进入生产 API。
  - `CompactRowLookup<TKey>`：两遍 CSR，保存 `(chunkIndex,rowIndex)`。
  - `LinkedRowLookup<TKey>`：单遍 linked rows，对照布局。
  - `EntityArrayLookup<TKey>`：entity-only baseline。
  - `DenseIntCompactLookup`：有界 int key 特化候选。
- 数据流 / 控制流：
  - 稳定 Snapshot → `world.Query(desc).GetChunks()` → Build lookup → 多次按 key 只读查询。
  - 组件读取从 `ChunkView.GetSpan<T>()` + row ref 取值，不复制组件 payload。

## 决策

- **Conditional Go。** uniform / 高基数 / 组件读取场景值得继续产品化 compact CSR row-ref lookup。
- **不进 Core。** ValueLab 没有修改 `World/Archetype/QueryCache/CommandStream`；后续产品化仍应保持 0 core intrusion。
- **不做完整关系代数 API。** 下一步只允许 1-3 组件 Rows、一个 Where、一个 KeyBy、一个 FrameLookup terminal。
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

## 坑点

- 不要把 ValueLab 原型直接复制进 public API；产品化必须重新 TDD。
- `TryBuildNoGrow` 失败必须保持目标为空，不能发布部分结果。
- AutoGrow 后旧 view/span/enumerator 全部失效。
- `ComponentBucketQuery` 是 per-key scan 基线；在 Q 达到上万时会回到 `Q × N`。
- Dictionary baseline 会分配大量 `List<Entity>`，不能作为唯一胜利证据。
- `EntityArrayLookup` 很适合 entity-only/hot baseline，但不满足 chunk-row component span 读取目标。
