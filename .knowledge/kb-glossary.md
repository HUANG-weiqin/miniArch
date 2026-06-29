---
title: Glossary
module: Meta
description: Terms and acronyms used across the miniArch knowledge base
updated: 2026-06-30 (补 HeroComing.Perf 参数语义)
---
# Glossary

> 这个页面定义 miniArch 知识库中使用的术语和缩写。

## ECS 概念

| 术语 | 定义 |
|------|------|
| **ECS** | Entity-Component-System 架构模式。实体是 ID，组件是数据，系统是逻辑。 |
| **Archetype** | 拥有相同组件集合的所有实体的分组。miniArch 按 archetype 做 SoA 存储。 |
| **Entity** | `(id, version)` 二元组。`id` 是 slot 索引，`version` 用于区分同 slot 的不同代际实体。 |
| **Component** | `unmanaged` struct 数据。miniArch 只支持值类型组件（含托管引用的组件在构造时 fail fast）。 |
| **Chunk / ChunkView** | Archetype 存储的 public readonly struct 视图。单块模式下 ChunkView 包裹整个 Archetype；分段模式下每个 Segment 一个 ChunkView。 |
| **Query** | 按组件集合过滤 archetype 的机制。`QueryDescription`（filter）→ `Query`（archetype+chunk 快照）→ 迭代。 |
| **Signature** | 排序的 `ComponentType[]` + 缓存的 512-bit mask。作为 archetype 字典 key。 |

## 存储与内存

| 术语 | 定义 |
|------|------|
| **SoA** | Structure of Arrays。每个组件列存储在独立的连续 byte 区域，而不是 per-entity struct（AoS）。提高 cache 局部性。 |
| **Swap-remove** | 删除行时把最后一行移到被删位置（O(1)），保持数据紧凑。需要同步更新 `EntityRecord.RowIndex`。 |
| **Padding 字节** | C# struct 在内存中为对齐而填充的空隙字节。miniArch 用 `GC.AllocateArray`（零初始化）保证 padding = 0，使 checksum 跨 peer 一致。 |
| **LOH** | Large Object Heap。.NET 中 ≥85000 字节的对象分配在此堆，不压缩易导致碎片化。 |
| **`[SkipLocalsInit]`** | C# 属性，跳过局部变量零初始化，消除 JIT 生成的 `init` 指令开销。 |
| **`AggressiveInlining`** | `MethodImpl` 选项，提示 JIT 内联薄转发方法。遗漏会导致迭代退化 41%。 |

## CommandStream / 帧同步

| 术语 | 定义 |
|------|------|
| **CommandStream** | miniArch 唯一的延迟命令录制器。append-only typed store，按组件类型分片记录。 |
| **FrameDelta** | 帧增量数据，packed `byte[]` + varint op 流。buffer 本身就是 wire format，零拷贝网络发送。 |
| **Lockstep** | 帧同步：所有 peer 执行相同的命令序列，状态在确定性下保持一致。miniArch 提供录制+序列化+重放+checksum，不提供传输层。 |
| **GGPO** | Good Game Peaceful Online — 乐观回滚网络架构。本地预测执行，冲突时回滚到权威状态重放。miniArch 的 `CaptureState/RestoreState` 支持此模式。 |
| **Placeholder entity** | `Entity(-1, seq)` — 多 host lockstep 下 `DeferredEntities=true` 时 `Create()` 返回的临时 id。replay 端各自建 `seq→local real` 映射。 |
| **Varint / LEB128** | 可变长度整数编码。小数值用 1 字节，大数值用更多字节。entity id 通常 < 65536 → 2-3 字节代替 8 字节。 |
| **Replay** | 在目标 world 上按字节流时序执行 `FrameDelta` 中的所有操作。 |
| **Submit** | 在源 world 上直接执行 `CommandStream` 录制的命令（不走 delta 序列化）。 |
| **Tier 1** | `CaptureState/RestoreState` 的第一阶段实现：内存原地 raw 数组拷贝，稳态零 GC。无 Tier 2/3 计划（YAGNI）。 |

## Query

| 术语 | 定义 |
|------|------|
| **两段式失效** | Query 快照的两阶段增量检查：快路径 `archetypeCount == _lastArchetypeCount`（int compare）；慢路径 per-archetype segment count 变化时只重建 ChunkView。详见 `kb-query-invalidation.md`。 |
| **Tag（标签/标记）** | MiniArch 中没有独立的"标签"或"标记"概念。标签就是零大小的 `unmanaged` 组件（如 `readonly record struct PlayerTag`）。用 `With<T>()` 即可查询，无需 `WithTag<T>()`。零大小组件在 `ComponentSizeCache` 中 size=0，存储层不为其分配列空间。 |
| **canonical mask** | `ComponentMask` 的 popcount == component count 的状态（所有 id < 512）。只有 canonical mask 进 `_archetypeByMask` 字典。 |
| **Edge cache** | Archetype 上按 componentId 直索引的 `Archetype?[]`，缓存 Add/Remove 操作的目标 archetype。O(1) 查找，稀疏 id 时数组可能膨胀。 |

## 状态复制

| 术语 | 定义 |
|------|------|
| **WorldSnapshot** | 二进制序列化字节流，支持跨进程持久化/网络传输。 |
| **World.Clone()** | 创建全新独立 `World`（新 archetype cache、新 capacity）。用于分支模拟，**不是**高频回滚工具。 |
| **CaptureState/RestoreState** | 绑定源 World 的 opaque 句柄，raw 数组拷贝，稳态零 GC。用于 GGPO 式高频原地回滚。 |

## 性能测试

| 术语 | 定义 |
|------|------|
| **HeroComing.Perf** | 唯一的回归门禁工具。30s 固定时长吞吐量测试；门禁用 `--check-baseline`，人工刷新基线用 `--update-baseline`。Movement ≥1210 / Attack ≥767 rounds/s。 |
| **rounds/s** | 每秒完整游戏帧数（500 players + 500 enemies，含 query+command+submit 全链路）。 |
| **cycles/s** | PipelineBenchmarkTests 的 per-operation cycle 计数。与 rounds/s 是不同 harness，不可跨工具比较。 |
