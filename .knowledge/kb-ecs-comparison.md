---
title: MiniArch vs Arch vs DefaultEcs 横向对比
module: MiniArch.Benchmarks
description: MiniArch 与其他 C# ECS 架构的吞吐量、内存稳定性、结构操作压力对比
updated: 2026-06-01
---
# MiniArch vs Arch vs DefaultEcs 横向对比

## 这个模块是干什么的

- 记录 MiniArch 与 Arch (2.1.0)、DefaultEcs (3.0.0) 的三方对比基准
- 所有数据来自 `perf/HeroComing.Perf` 和 `perf/GameTickSim.Perf`

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
- Arch 在迭代主导的负载中领先（K-BulletHell +14% vs MiniArch）
- MiniArch 在 K-BulletHell 优化口径（cached query + lazy snapshot + direct-ref row loop）下可反超 +4%~+6%

## 源码级机制对比（MiniArch vs Arch）

| 方面 | MiniArch | Arch |
|---|---|---|
| Query 缓存粒度 | 预计算 `Chunk[]` 快照 | `Archetype[]` 列表 |
| 失效机制 | per-archetype `Generation` (long) | 全局 `GetHashCode()` (recompute all) |
| 组件拷贝 | 仅重叠组件 | 全量拷贝 |
| Set 快路径 | 已有组件时 in-place set | 总是迁移 |

## 为什么 MiniArch 胜出

- generation-based partial rebuild vs hash-based full rescan
- 实体迁移只拷贝重叠组件
- Set 快路径避免不必要的 archetype migration
- 所有操作不产生临时对象

## 关键理解

- MiniArch 优势在结构操作适中时最大（+34% vs Arch）
- 极端负载下差异被带宽压平，但内存确定性成立
- 对游戏服务器和帧率敏感场景 = 同性能 + 零 GC 暂停
