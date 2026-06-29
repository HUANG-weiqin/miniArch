---
title: MiniArch vs Arch vs DefaultEcs 横向对比
module: MiniArch.Benchmarks
description: MiniArch 与其他 C# ECS 架构的吞吐量、内存稳定性、结构操作压力对比
updated: 2026-06-30 (修正 tools/perf 路径; 失效机制改为增量 append)
---
# MiniArch vs Arch vs DefaultEcs 横向对比

## 这个模块是干什么的

- 记录 MiniArch 与 Arch (2.1.0)、DefaultEcs (3.0.0) 的三方对比基准
- 所有数据来自 `tools/perf/HeroComing.Perf` 和 `tools/perf/GameTickSim.Perf`

## 正常负载（~50K 实体，无额外结构操作）

| 指标 | MiniArch | Arch | DefaultEcs |
|---|---|---|---|
| 吞吐量 (rounds/s) | **496** | 386 | 96 |
| 堆内存/tick | **0–3.6 KB** | 1.5–16 KB | ≥6 MB (GC Gen0) |
| 内存波动 | **无** | 持续增长 | 锯齿形 GC 抖动 |

## 结构操作压测

| 结构 ops/tick | MiniArch | Arch | vs Arch | DefaultEcs | vs DefaultEcs |
|---|---|---|---|---|---|
| 40K | **379** | 329 | +15% | 73 | +416% |
| 640K | **28** | 24 | +17% | 5.1 | +444% |
| 10.24M | **1.1** | 1.1 | +2% | 0.19 | +490% |

MiniArch 全程堆内存 delta <4KB，零 GC；Arch 在 5.12M ops 时堆内存飙至 1GB。

## GameTickSim 11 场景

- MiniArch 在结构变更频繁的混合负载中领先（E-MixedFullTick +22% vs Arch）
- Arch 曾是迭代主导负载的领先者（旧版 K-BulletHell），但 **Archetype 扁平化后 MiniArch 反超 Arch +70~74%**（每 tick 200 实体创建 + 200 毁灭 + 15-30K 运动迭代 + 碰撞扫描）
- 实测验证：`ChunksOf<T>` + 直接 `Span<T>` 迭代 vs `EachSpan<T>` 在 K-BulletHell 下性能差距在 0.2% 噪声内，说明 `EachSpan` 的 ref struct 包装已被 JIT 完全内联消除

## 源码级机制对比（MiniArch vs Arch）

| 方面 | MiniArch | Arch |
|---|---|---|---|
| Query 缓存粒度 | 预计算 `Archetype[]` + `ChunkView[]` 快照 | `Archetype[]` 列表 |
| 失效机制 | archetype 数量 + per-archetype segment 数（两段式） | Archetypes 集合 hash code | archetype 数量比较 |
| 组件拷贝 | 仅重叠组件 | 全量拷贝 |
| Set 快路径 | 已有组件时 in-place set | 总是迁移 |

> 注：上表"失效机制"行的"重建策略"维度见 `kb-query-invalidation.md`——MiniArch 是**增量 append**（只扫新增 archetype），不是全量重建。

## 为什么 MiniArch 胜出

- 512-bit bitmask 匹配 vs hash-based full rescan（Arch 每次重新计算 all）
- 实体迁移只拷贝重叠组件（遍历 destination signature 逐个匹配）
- Set 快路径避免不必要的 archetype migration
- 所有操作不产生临时对象
- Archetype 扁平化消除了 multi-chunk 开销

## 关键理解

- MiniArch 优势在结构操作适中时最大（+34% vs Arch）
- 极端负载下差异被带宽压平，但内存确定性成立
- 对游戏服务器和帧率敏感场景 = 同性能 + 零 GC 暂停

## FrifloGameScenarios S10 MixedLoad 排查结论

- 旧版 S10 使用 `List<Entity>.RemoveAt(0)` 做 FIFO 删除，会把 benchmark 变成句柄数组搬迁压测，而不是 ECS 混合负载压测。
- 隔离实验结果：
  - `List+Random`：MiniArch 2491 ops/s，Friflo 1121 ops/s，复现约 2.2x 差距。
  - `ListNoRand`：MiniArch 2760 ops/s，Friflo 1170 ops/s，去掉随机读后差距仍在，随机读不是主因。
  - `Queue+Rand`：MiniArch 13517 ops/s，Friflo 13596 ops/s，改用 O(1) FIFO 后差距消失。
  - `QueueNoRand`：MiniArch 31589 ops/s，Friflo 30404 ops/s，真实 create/query/destroy 混合负载接近。
- 机制：`RemoveAt(0)` 每轮 200 次、列表约 15K 元素，约移动 300 万个 entity handle。MiniArch `Entity` 是两个 `int`（8B）；Friflo `Entity` 源码注明 16B（`EntityStore` 引用 + raw id/revision）。因此 Friflo 在这个 benchmark 里搬迁字节量约为 MiniArch 的 2 倍。
- 结论：S10 原始 2.2x 差距是 benchmark 容器选择造成的伪影；用于 ECS 对比时应使用 `Queue<T>` 或 ring buffer 表达 FIFO 生命周期，不应使用 `List.RemoveAt(0)`。
