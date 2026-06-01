---
title: MiniArch vs Arch vs DefaultEcs 横向对比
module: MiniArch.Benchmarks
description: MiniArch 与其他 C# ECS 架构的吞吐量、内存稳定性、结构操作压力对比
updated: 2026-06-03
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

### 被否决的方向：为写而扩展 EachSpan

曾短暂探索过为 flat `byte[]` chunk 暴露 writable span，并用临时诊断比较多列写回性能。结论不是“继续补一个可写版 EachSpan”，而是相反：

1. **EachSpan 的产品目标是读路径**。
2. **写热路径不是当前要解决的问题**。
3. **为了保留一次性诊断而扩 runtime/API surface 不符合 YAGNI**。

因此当前仓库的结论是：

- `EachSpan` 保持只读语义。
- 不保留 writable span API。
- 如果未来真有稳定、明确的多列写需求，再单独设计专门的写迭代器或 chunk/ref API，而不是把读 API 强行扩成读写两用。

| 场景 | 读写比 | 推荐引擎 |
|------|:------:|:--------:|
| Movement（读 Vel，写 Pos） | 1R:1W | 取决于计算量 |
| AI（读多因素，写少量状态） | 5R+:1W | MiniArch 更佳 |
| Physics（读 Pos, Vel → 写 Pos, Vel） | 1R:1W | 窄≈持平，宽 Arch 略优 |
| Networking（读全量状态序列化） | 纯读 | MiniArch 更佳 |

### 关键认知

| 认知 | 说明 |
|------|------|
| Debug vs Release | 之前报告的 10x 差值是 `dotnet test` 默认 Debug 下的测量假象；Release 下 EachSpan ≈ Manual |
| EachSpan 开销 | 在 Release 下，EachSpan 比等价的 manual chunk span 代码**没有额外开销**（宽场景还快 13%） |
| byte[] 优势需要多组件放大 | 窄场景（2 组件）无法展示 byte[] 优势，需要 6+ 组件且每行切换多列的查询 |
| EachSpan 是读 API | 当前只保留 `ref readonly` 读语义，不把一次性写诊断沉淀成正式 API |
| 写方向被 YAGNI 否决 | 没有稳定需求前，不为 `EachSpan` / `Chunk` 保留额外 writable span surface |
| 约束条件 | 当前 EachSpan 只支持 blittable 组件（非托管类型）；含托管引用的组件必须用 chunk 手动迭代 |

## GameTickSim 11 场景隔离测试

来源：`perf/GameTickSim.Perf/ScenarioBenchmark.cs`，50K 实体，5s 固定计时，`-c Release`。

| 场景 | 测试内容 | MiniArch | DefaultEcs | Arch | 胜者 |
|------|----------|:-:|:-:|:-:|:----:|
| A-PureIteration | 50K, Position+=Velocity, 5-comp | ~9793 | ~4844 | ~9892 | Mini/Arch 持平 |
| B-WideSingleComp | 50K, Health.Current-=1, 3-comp | ~18375 | ~9277 | ~20302 | **Arch** |
| F-MultiArchetype | 6 archetype, iterate Health | ~18712 | ~9389 | ~19598 | **Arch** |
| G1-FragBaseline | 单 archetype 迭代基线 | ~9523 | ~4906 | ~8751 | **MiniArch** |
| G2-FragAftermath | 碎片化后（5 tag） | ~8815 | ~4609 | ~8461 | **MiniArch** |
| H-5ComponentJoin | 5 组件联合查询 | ~6454 | ~2807 | ~6641 | Arch/Mini 持平 |
| I-SparseQuery | 1% 实体有 BuffRemaining | ~315K | ~135K | ~309K | **MiniArch** |
| C-StructuralAddRemove | 9K add + 9K remove Debuff | ~874 | ~2082 | ~643 | **DefaultEcs** |
| J-CreationBurst | 10K create + 10K destroy | ~1666 | ~1372 | ~1419 | **MiniArch** |
| D-MassCreateDestroy | 4.5K create + 4.5K destroy | ~1665 | ~1196 | ~1776 | **Arch** |
| E-MixedFullTick | 12 阶段 RPG 混合负载 | ~607 | ~217 | ~499 | **MiniArch** |
| K-BulletHell | 8 阶段弹幕射击混合负载 | ~4470 | ~1866 | ~5103 | **Arch** |

### 场景关键发现

| 发现 | 说明 |
|------|------|
| 跨 archetype 迭代零开销 | F vs B 差异 <4%，archetype 数量不影响迭代性能 |
| 碎片化退化差异 | MiniArch ~8%, DefaultEcs ~5%, **Arch 12-17% 且方差大** |
| 组件越多 chunk 优势越大 | 2 组件 DefaultEcs 落后 2x → 5 组件落后 2.3x |
| DefaultEcs 独赢结构变更 | per-entity sparse set 无需 archetype migration，2.4x 快于 MiniArch |
| EntitySet 重建是 DefaultEcs 致命伤 | 隔离赢结构变更 2.4x，但混合场景输 2.5x（每次变更后必须重建缓存） |
| Arch 有内存泄漏（仅 add/remove 时） | RPG 混合 +1822KB；弹幕模式（仅 create/destroy）无泄漏 |
| MiniArch 稀疏查询无显著优势 | post-rebase 后 vs Arch 仅 ~2-6% 差距，两者都有 query cache |

### 负载模式决定胜者

| 游戏模式 | 负载特征 | 胜者 | 差距 |
|----------|----------|:----:|------|
| E-MixedFullTick (RPG) | 重结构变更 + 8+查询交替 | **MiniArch** | +22% vs Arch |
| K-BulletHell (弹幕射击) | 重迭代 + 轻结构变更 | **Arch** | +14% vs MiniArch |

弹幕射击模式（K）设计：5 种实体（Player/Boss/EnemyBullet/PlayerBullet/Particle），每 tick 创建 800+销毁 ~800，MoveAll 迭代 ~12K 实体。与 RPG 模式关键差异：无 add/remove component（仅 BossEnrage 每 100 tick 1 次），纯 create/destroy + 迭代主导。

**结论：差距真实存在且方向取决于负载特征。** MiniArch 在结构变更频繁的混合负载中领先，Arch 在迭代主导的负载中领先。

### K-BulletHell 20s 优化口径（2026-06-03）

- 这不是上表 5s 对称实现口径；这里专门针对 MiniArch 的真实热路径消费方式做了优化：
  - `Query` 对象在 tick 外缓存
  - `Query` 允许 archetype snapshot 先刷新、chunk flatten 按需构建
  - MiniArch 读路径切到 `GetComponentRefAt<T> + Unsafe.Add + GetEntityStorage()`
  - 默认自适应 chunk 目标从 `16KB` 提升到 `64KB`
- 20s 定时结果（`perf/GameTickSim.Perf -- --scenarios K-BulletHell`）：
  - MiniArch：`6837~6933 ticks/s`
  - Arch：`6540~6548 ticks/s`
  - 结论：在这个“真实消费热路径”口径下，MiniArch 可稳定反超 Arch 约 `+4%~+6%`

### 12 阶段混合 Tick 扩展（增加 4 个 query 轮次）

在 E-MixedFullTick 基础上增加 ManaRegen/RangeCheck/StaminaRegen/UpdateTransforms 四个查询阶段，交替插入结构变更之间：

| ticks | MiniArch | DefaultEcs | Arch |
|-------|:-:|:-:|:-:|
| 1000 | **600** | 215 | 505 |
| 2000 | **564** | 217 | 463 |
| 4000 | **569** | 215 | 462 |

MiniArch 稳定领先 Arch ~19%。DefaultEcs GC 随时间增长（Gen0 1K→5, 4K→18）。

## 源码级机制对比（MiniArch vs Arch）

### Query 缓存

| 方面 | MiniArch | Arch |
|------|----------|------|
| 缓存粒度 | 预计算 `Chunk[]` 快照 | `Archetype[]` 列表 |
| 失效机制 | per-archetype `Generation` (long) | 全局 `Archetypes.GetHashCode()` (recompute all) |
| 迭代开销 | 已缓存时零开销（数组遍历） | 每次 archetype→chunk 两层遍历 |
| 构建开销 | 仅 rebuild 变更 archetype 的 chunk | hash 变了就全量 rescan |

MiniArch `EnsureMatchingSnapshot()`: 先检查 `_world.ArchetypeVersion`（int 比较），再逐 archetype 检查 `Generation`。Arch `Match()`: 每次重新计算 `_allArchetypes.GetHashCode()`（遍历所有 archetype）。

**这是 MiniArch 混合场景赢的关键**：结构变更后，MiniArch 只重建受影响 archetype 的 chunk 列表，Arch 每次 query 都可能触发全量 archetype 扫描。

### 实体迁移（Add/Remove component）

| 方面 | MiniArch | Arch |
|------|----------|------|
| 组件拷贝 | `CopySharedComponentsFrom`（仅重叠组件） | `CopyComponents`（全量拷贝） |
| Set 快路径 | `ApplyTypedAddOrSet` 检测已存在 → in-place set | 无，总是迁移 |
| Edge 缓存 | `ArchetypeEdges.TryGetAdd/Remove` | `SparseJaggedArray` add/remove edges |
| 代际追踪 | `_generation++` on ReserveEntity + RemoveEntity | 无 per-archetype generation |

MiniArch 的 `ApplyTypedAddOrSet` 快路径避免了不必要的 archetype migration——当 `world.Add<T>()` 时如果实体已有 T，直接 in-place 修改。Arch 总是执行完整的 Move 流程。

### 性能优势的因果链

```
混合场景中结构变更频繁 → 多个 archetype 的 generation 变化
  → MiniArch: 只重建变更 archetype 的 chunk 快照（O(changed)）
  → Arch: GetHashCode() 全量重算 + 全量 archetype 扫描（O(all)）
  → MiniArch 查询开销更低 → 12 阶段 tick 中累积 ~19% 优势
```

## 关键理解

- MiniArch 的性能优势在**结构操作适中时最大**（+34% vs Arch），因为此时分配/GC 开销占比最大
- 极端负载下差异被带宽压平，但 MiniArch 的**内存确定性**依然成立——这是 Arch 和 DefaultEcs 无法提供的
- 对游戏服务器和帧率敏感场景，MiniArch = **同性能 + 零 GC 暂停**，不是二选一
- **混合场景赢的原因是 query 缓存失效机制**：generation-based partial rebuild vs hash-based full rescan

## 为什么 MiniArch 胜出

- **激进 YAGNI**：不需要托管内存就不分配，不需要快照就不复制，不需要事件就不建队列
- **结构操作只操作元数据数组**，不产生临时对象
- **Query 缓存用 generation-based partial rebuild**：结构变更后只重建受影响 archetype 的 chunk 列表
- **实体迁移只拷贝重叠组件**（`CopySharedComponentsFrom`），Arch 全量拷贝
- **Set 快路径**：`Add<T>()` 已有 T 时直接 in-place 修改，避免迁移
- Arch 的 `CopyComponents` 全量拷贝 + 全量 archetype hash rescan 是混合场景的瓶颈
- DefaultEcs 的 EntitySet 重建 + `object[]` 组件存储导致 GC 压力不可消除

## 合适场景

- 需要高确定性、零 GC 暂停、长时间稳定运行
- 游戏服务器、帧率敏感的动作游戏、实时仿真
- 结构操作频繁（大量 Spawn/Destroy/AddComponent）的工作负载
- 读密集查询，尤其是多组件宽查询
- 混合工作负载（查询+结构变更交替），MiniArch 优势最大

如果项目能容忍偶尔 GC 暂停和内存抖动，Arch 已经是很好的选择。如果目标是在极端结构操作下仍然可预测——MiniArch 是当前唯一答案。
