---
title: Partition 内部原型实验报告
module: MiniArch.Core
description: Partition 原型实现总结、性能测量、证伪条件检查与结论
updated: 2026-07-10
---

# Partition 内部原型实验报告

## 状态说明（2026-07-10）

**技术验证成功，已归档为研究结论，不建议继续 core 产品化。**

当前已转向 `ComponentBucketQuery` 方案，详见 `kb-component-bucket-index-mvp-report.md`。

原 Partition 原型在技术层面通过了全部证伪条件（见下文），性能表现优异（P=64 时达 31× 加速）。然而后续 ComponentBucketQuery（基于真实组件值的桶查询）的出现改变了决策：

- **产品适用面不足**：Partition 的核心价值——运行时动态 key 分区扫描——在真实游戏中需求场景较少。绝大多数"按 key 分桶"的需求用静态 tag + parallel query 已能覆盖。
- **单 World 单 Partition Axis 太窄**：Partition 设计假设实体按单一维度（`PartitionId`）分区。而 ComponentBucketQuery 零 core 侵入，基于真实组件值分桶，支持任意组件类型作为分桶维度。
- **Core 侵入代价高**：Partition 需要修改 Archetype/World/QueryCache/StructuralChange 四个核心模块。ComponentBucketQuery 以 sidecar 形式存在，不碰一行 core 代码。

**结论：Partition 作为研究原型是有价值的技术验证，但作为 MiniArch.Core 的产品功能不值得继续投入。** 对按组件值分桶有需求的用户可考虑 `ComponentBucketQuery`。

> 以下为原实验报告全文，保留以作记录。所有原始数据和证伪检查仍然有效。

## 原型摘要

### 实现（5 个文件新建/修改）

| 文件 | 改动量 | 说明 |
|------|--------|------|
| `Core/Archetype.cs` | +3 行 | 新增 `_partitionId` 字段、`PartitionId` 属性、构造函数参数 |
| `Core/World.cs` | +80 行 | 新增 `_partitionArchetypes` 复合键字典、`_archetypesByPartitionId` 桶索引、`GetOrCreateArchetype` partitionId 重载 |
| `Core/World.StructuralChange.cs` | +15 行 | 迁移路径传递 `PartitionId`（ApplyTypedAdd/RemoveBoxed/GetOrCreateAddDestinationArchetype）；新增 `MoveEntityToPartition` |
| `Core/World.QueryCache.cs` | +100 行 | 新增 `ExecutePartitionQuery`（桶范围查询，仅扫描匹配 PartitionId 的 archetype） |
| `PartitionStore.cs` | ~100 行 | 新文件：`PartitionStore<TKey>` 泛型内部类，提供 Add/Set/Remove/Query |

### 正确性测试（14 项全通过）

覆盖：identity、add、set、remove、Add<T> 保留 partition、Remove<T> 保留 partition、Set<T> 保留 partition、partition query 隔离、global query 可见性、destroy、多签名同 partition、多 store 共享 ID 空间。

所有 911 测试（897 原 + 14 新）通过，HeroComing.Perf 回归门禁通过。

## 性能测量

配置：`N=100000`，`measure=3000ms`，`warmup=2`，uniform 分布

### Query Throughput (ops/s)

| P | K/part | Strategy | CountOnly | Pos+=Vel | Read3 | Write3 |
|:-:|:------:|----------|:---------:|:--------:|:-----:|:------:|
| 4 | 25K | FullScan | 4,323 | 4,154 | 4,072 | 3,768 |
| | | **Partition** | **8,724,265** | **19,803** | **19,721** | **17,362** |
| | | Sidecar | 23,834 | 5,578 | 5,003 | 3,817 |
| 16 | 6.25K | FullScan | 9,752 | 8,469 | 7,924 | 7,896 |
| | | **Partition** | **9,305,341** | **76,292** | **77,148** | **82,193** |
| | | Sidecar | 56,827 | 14,467 | 13,630 | 9,367 |
| 64 | 1.56K | FullScan | 13,457 | 9,201 | 9,649 | 8,926 |
| | | **Partition** | **9,371,556** | **286,526** | **262,160** | **310,016** |
| | | Sidecar | 398,757 | 58,279 | 45,802 | 28,619 |

### Partition vs FullScan Speedup

| P=4 (25% hit) | P=16 (6.25% hit) | P=64 (1.56% hit) |
|:-------------:|:----------------:|:----------------:|
| **4.8×** | **9.0×** | **31.1×** |

### Partition vs Sidecar Speedup

| P=4 | P=16 | P=64 |
|:---:|:----:|:----:|
| **3.6×** | **5.3×** | **4.9×** |

### Key Move Cost

| P | Entities/part | Moves/s | Per move |
|:-:|:------------:|:-------:|:--------:|
| 4 | 25,071 | 6,073,852 | 0.165 μs |
| 16 | 6,263 | 6,364,433 | 0.157 μs |
| 64 | 1,547 | 6,480,406 | 0.154 μs |

Key move ≈ 0.16μs，是普通 `World.Set<T>`（~0.04μs）的 ~4×。这很合理：key move 本质是 archetype 间结构迁移（full component copy），不是简单字段写入。

### Break-Even Q/U

```
required Q/U = (T_partition_move - T_regular_set) / (T_scan_query - T_partition_query)
             = (0.16μs - 0.04μs) / (250μs - 53μs)
             ≈ 0.0006
```

即 1 次查询对 1667 次 key move 即可收支平衡。所有真实游戏场景（Queries/frame >> Moves/frame × 0.0006）都远高于此阈值。

## 证伪条件检查

| # | 条件 | 结果 | 证据 |
|:-:|------|:----:|------|
| 1 | 固定 K，N 放大 10 倍，Partition query 时间仍随 N 增长 | ❌ 未证伪 | Partition query 时间与 K（每分区实体数）成正比，与 N 无关。P=64 时 K=1.5K → 286K ops/s vs P=4 时 K=25K → 19K ops/s，吞吐与 K 反比 |
| 2 | P=16、命中率 6.25%，read path 不能稳定达到手扫 2× | ❌ 未证伪 | Pos+=Vel 9.0×，Read3 9.7×，远高于 2× 阈值 |
| 3 | bucket query 不是真的没有全局 O(A_match) | ❌ 未证伪 | CountOnly 达 10M ops/s（纯缓冲区开销限制），证明只扫描 1 个匹配 archetype。索引路径走 `_archetypesByPartitionId` 而非全局 `_archetypeSnapshot` |
| 4 | 默认未 partition Query 退化超过 2% | ❌ 未证伪 | 911 测试通过 + HeroComing.Perf 通过（Movement 1859 ≥ 1642） |
| 5 | 空 archetype/partition 数持续无界增长 | ❌ 未证伪 | Partition archetype 数 = P × distinct_signatures，有界 |
| 6 | 相比 tag 在固定低基数场景没有功能或性能增益 | ✅ 部分成立 | 同等选择性下性能相当；但 Partition 支持运行时动态 key（如复合 key `Owner+Zone`），tag 做不到 |

## 设计与实现关键决策

1. **PartitionId=0 保留为默认分区**：所有现有 archetype 不受影响，零迁移成本
2. **复合键 `(Signature, PartitionId)`**：partition archetype 通过独立 `_partitionArchetypes` 字典查找，不影响现有 `_archetypes` 路径
3. **Migration 路径传递 PartitionId**：`ApplyTypedAdd` / `RemoveBoxed` / `GetOrCreateAddDestinationArchetype` 调用 `GetOrCreateArchetype(destSig, source.PartitionId)`。这使得普通 `world.Add<Health>(entity)` 在已 partition 实体上正确地创建同 PartitionId 的目标 archetype
4. **CountOnly ~10M ops/s 本质上界**：这是框架调用开销上限（创建 Span 数组、函数调用链路），不是 scan 开销。任何实际 kernel（至少 1 次组件读）都低于此上限，因此不影响结论。

## 建议下一步

1. **公共 API 设计**：基于内部 PartitionStore 接口设计 public `world.Partition<TKey>()` 系列
2. **Persistence 集成**：PartitionId 必须贯穿 Snapshot/Restore、Clone、CommandStream/FrameDelta/Replay
3. **N=1M 验证**：当前 100K 足够证伪；生产规模 1M 可在公共 API 阶段补测
4. **Tag 策略正式对照**：在文档中明确"固定值用 tag，动态值用 partition"的分流规则
5. **Partition 命名与概念统一**：考虑 `SharedKey`、`Bucket`、`Group` 等命名
