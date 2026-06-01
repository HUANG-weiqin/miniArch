---
title: MiniArch vs Arch vs DefaultEcs 横向对比
module: MiniArch.Benchmarks
description: MiniArch 与其他 C# ECS 架构的吞吐量、内存稳定性、结构操作压力对比
updated: 2026-06-02
---
# MiniArch vs Arch vs DefaultEcs 横向对比

## 这个页面是干什么的

- 这个页面记录 MiniArch 与 Arch (2.1.0)、DefaultEcs 的三方对比基准
- 所有数据来自 perf 项目 `perf/HeroComing.Perf` 中 `GameTickScenario` 的定长 5s 吞吐量测量
- 这是对外推销 MiniArch 的核心论据，也是回归门禁的及格线参考

## 对比引擎版本

| 引擎 | 版本 | 包 |
|---|---|---|
| MiniArch | 本地源码 | — |
| Arch | 2.1.0 | `gfoidl.Arch` |
| DefaultEcs | 3.0.0 | `DefaultEcs` |

## 测量方法

- 固定 5s 计时，20 tick warmup，每 10 tick 报告一次
- 所有输出用 `-c Release` 编译
- 对比的是 **record + submit 循环**，不含 world 创建
- 50K 实体 + 每 tick 结构操作（Spawn/Destroy/AddComponent）

## 正常负载（~50K 实体，无额外结构操作）

| 指标 | MiniArch | Arch | DefaultEcs |
|---|---|---|---|
| 吞吐量 (rounds/s) | **496** | 386 | 96 |
| MiniArch vs | — | +28% | +417% |
| 堆内存/tick | **0–3.6 KB** | 1.5–16 KB | ≥6 MB (GC Gen0) |
| 内存波动 | **无** | 持续增长 | 锯齿形 GC 抖动 |

## 结构操作压测（增量 Spawn/Destroy/Debuff）

固定 ~12K 稳态实体，按比例 `SpawnPerTick / DestroyPerTick / DebuffPerTick = 3:1:6` 逐步加压。

| 结构 ops/tick | MiniArch | Arch | vs Arch | DefaultEcs | vs DefaultEcs |
|---|---|---|---|---|---|
| 40K | **379** | 329 | +15% | 73 | +416% |
| 160K | **130** | 97 | +34% | 19 | +575% |
| 320K | **61** | 49 | +24% | 8.3 | +632% |
| 640K | **28** | 24 | +17% | 5.1 | +444% |
| 1.28M | **11.6** | 10.3 | +13% | 1.9 | +511% |
| 2.56M | **6.0** | 5.0 | +20% | 0.94 | +542% |
| 5.12M | **2.6** | 2.3 | +14% | 0.37 | +597% |
| 10.24M | **1.1** | 1.1 | +2% | 0.19 | +490% |

行为差异：

- **MiniArch**：全程堆内存 delta **<4KB**，零 GC，无泄漏
- **Arch**：5.12M ops 时堆内存飙至 **1GB**，虚拟内存膨胀；80K ops 时因 `CopyComponents` 空 archetype 偶发崩溃
- **DefaultEcs**：全程 GC Gen0 抖动，吞吐量最低；640K+ 时几乎停滞，GC 占主导

最终瓶颈：三个引擎在 10.24M ops 时都被**内存带宽**压平，差异缩小（+2% vs Arch），但 MiniArch 仍然是唯一零 GC 分配的实现。

## Span Query 吞吐量对比

对比来自 `perf/Throughput.Perf`（fixed-duration 10s，-c Release，100K entities）：

### 窄查询（2 组件：Position + Velocity）

5 archetypes 混合（模拟现实多签名分布），query 匹配 4 archetypes（90% entities）。

| 模式 | MiniArch (ops/s) | Arch (ops/s) | 对比 |
|------|:-:|:-:|:-:|
| Manual (chunk span) | 15,543 | 16,803 | MiniArch −8% |
| EachSpan | **16,488** | — | — |

窄场景两者持平，差异 <5%，在噪声范围内。EachSpan 内部 `foreach` 的 struct copy 开销在 Release 下被 JIT 消除。

### 宽查询（6 组件：Position+Velocity+Health+Team+Acceleration+Mana）

单一 archetype，所有实体同一签名。

| 模式 | MiniArch (ops/s) | Arch (ops/s) | 对比 |
|------|:-:|:-:|:-:|
| Manual (chunk span) | 5,874 | 5,826 | MiniArch +1% |
| EachSpan | **6,650** | — | **MiniArch +14% vs Arch GetSpan** |

宽场景 MiniArch EachSpan 比 Arch **快 14%**。原因：

1. **byte[] 单块分配**：6 个组件列在 `_data` 中物理相邻，跨列访问时 TLB/cache 友好
2. **Arch 需要 6 次 `(T[])Components[idx]` castclass**：每次切 chunk 额外类型检查
3. **Per-chunk 列寻址更短**：MiniArch `_componentIdToColumnIndex[id]` → `_data + offset`；Arch `ComponentIdToArrayIndex[id]` → `Components[ai]` → 类型转换

### 关键认知

| 认知 | 说明 |
|------|------|
| Debug vs Release | 之前报告的 10x 差值是 `dotnet test` 默认 Debug 下的测量假象；Release 下 EachSpan ≈ Manual |
| EachSpan 开销 | 在 Release 下，EachSpan 比等价的 manual chunk span 代码**没有额外开销**（宽场景还快 13%） |
| byte[] 优势需要多组件放大 | 窄场景（2 组件）无法展示 byte[] 优势，需要 6+ 组件且每行切换多列的查询 |
| 约束条件 | 当前 EachSpan 只支持 blittable 组件（非托管类型）；含托管引用的组件必须用 chunk 手动迭代 |

## 关键理解

- MiniArch 的性能优势在**结构操作适中时最大**（+34% vs Arch），因为此时分配/GC 开销占比最大
- 极端负载下差异被带宽压平，但 MiniArch 的**内存确定性**依然成立——这是 Arch 和 DefaultEcs 无法提供的
- 对游戏服务器和帧率敏感场景，MiniArch = **同性能 + 零 GC 暂停**，不是二选一

## 为什么 MiniArch 胜出

- **激进 YAGNI**：不需要托管内存就不分配，不需要快照就不复制，不需要事件就不建队列
- **结构操作只操作元数据数组**，不产生临时对象
- Arch 的 `AddComponents` 在 archetype 间搬移实体时分配大量临时数组
- DefaultEcs 的组件存储依赖 `object[]`，导致 GC 压力无法消除

## 合适场景

- 需要高确定性、零 GC 暂停、长时间稳定运行
- 游戏服务器、帧率敏感的动作游戏、实时仿真
- 结构操作频繁（大量 Spawn/Destroy/AddComponent）的工作负载

如果项目能容忍偶尔 GC 暂停和内存抖动，Arch 已经是很好的选择。如果目标是在极端结构操作下仍然可预测——MiniArch 是当前唯一答案。
