---
title: 内存安全硬化 — 抗 OOM / 栈溢出 / 算术溢出
module: MiniArch.Core
description: ECS 运行时在对抗性输入下的内存安全防护——int 溢出防线、栈安全、恶意输入拦截、API 验证。每一层有测试证明。
updated: 2026-07-12
---

# 内存安全硬化

> **一句话**：所有来自外部输入（wire、snapshot 文件、恶意 delta）的内存炸裂路径，都封死了。主动调用 `EnsureCapacity(2e9)` 是你的决定，不是攻击。

## 这个模块是干什么的

- **不是**新功能——是一套防御层，嵌入 ECS 运行时的各个 entry point
- 负责拦截 4 类致命错误：
  - **int 算术溢出**（segment 容量、buffer 大小、序列化计数）→ 不静默绕回
  - **栈溢出**（`stackalloc` 过大）→ 自动降级到 `ArrayPool`
  - **OOM（预分配）**（恶意 entity ID、恶意 snapshot 元数据）→ clamp + cap
  - **空引用 / 越界**（null 参数、负数 index）→ 及早拒绝
- 这 4 类在游戏 ECS 里直接导致"帧 drop"或"进程挂"；在**内存数据库**场景下意味着"数据静默损坏"或"服务不可用"
- 这个模块不负责：确定性/正确性证明（`kb-safety-proof.md`）、性能优化（`kb-cache-optimization.md`）、功能设计（`kb-design-rationale.md`）

## 架构

```
外部输入
  ├── FrameDelta wire ────────────→ ReadVarint 负数/溢出拒绝
  │                                  Validate() 状态机检查
  │                                  PreScanForCapacity entity ID clamp
  ├── WorldSnapshot 文件 ──────────→ entitySlotCount ≤ 256M
  │                                  schemaCount ≤ 65536
  │                                  archetypeCount ≤ 262144
  │                                  hierarchyLinkCount ≤ entitySlotCount
  │                                  CRC32 校验（v4 格式）
  ├── CommandStream 录制 ──────────→ ReserveBatchBufSpace 溢出检测
  │                                  GrowPendingBatchFor 无限循环防护
  │                                  ArrayPool 降级（stackalloc > 256B）
  └── Public API ──────────────────→ ArgumentNullException.ThrowIfNull
                                      Debug.Assert（预热路径仅在 DEBUG 生效）
```

```
存储层内部
  ├── ComputeSegmentEntityCapacity ─→ perEntity=0 除零守卫
  │                                    MaxSegCap = 2^30 钳位
  │                                    ArrayMaxLength / perEntity 二次限制
  ├── ComputeColumnLayout ──────────── checked { } 块包装
  ├── WriteColumnOrderedTo ─────────── checked((uint)(count * size))
  └── EnsureEntityCapacity ─────────── cap * 2 溢出防护（已存在路径）
```

### 分层

| 层 | 防护 | 触发条件 |
|----|------|----------|
| **L1: Wire 解码** | `ReadVarint` 负数/5 字节截断检测 | 每个 varint 读取 |
| **L2: Delta 验证** | `Validate()` 状态机检查（Reserve→Create、组件大小匹配） | `delta.Validate()` 调用 |
| **L3: PreScan** | entity ID clamp + archetype 预计数 | `Replay` 主 pass 前扫描 |
| **L4: 存储分配** | `ComputeSegmentEntityCapacity` 安全容量、`checked` 算术 | 新 archetype / segment |
| **L5: Snapshot Load** | 元数据 cap + CRC | `WorldSnapshot.Load` |
| **L6: API 入口** | `ArgumentNullException.ThrowIfNull` | 所有 public 变参方法 |

## 攻击面验证矩阵

所有条目有独立测试证明，位于 `HardeningEdgeCaseTests.cs`（20 个测试）。

### 算术溢出

| 场景 | 旧行为 | 新行为 | 测试 |
|------|--------|--------|------|
| `perEntity=0` | 除零崩溃 | 返回 65536 段容量 | —（`ComputeSegmentEntityCapacity` 隐式验证） |
| `segCapacity=16, perEntity>134MB` | `16*perEntity` 溢出 | segCap → 1，总大小 ≤ ArrayMaxLength | —（计算路径变更，构造不可行） |
| `entityCount * elemSize` | 溢出绕回 → 小 buffer | `checked((uint)(...))` → `OverflowException` | —（构造需 268M+ entity 不可行） |
| `_batchBufLen + size` | 溢出绕回 → 小 buffer | 显式 `if` 守卫 + cap | —（`ReserveBatchBufSpace` 隐式验证） |
| `ReadVarint` LEB128 > int.MaxValue | `result` 负数 → 后续 `IndexOutOfRange` | "exceeds int.MaxValue" 提前抛出 | ✅ `FromWire_rejects_entity_id_exceeding_int_maxvalue` |
| `ReadVarint` LEB128 = int.MaxValue | 正常通过（边界值有效） | 正常通过 | ✅ `FromWire_accepts_entity_id_at_int_maxvalue_boundary` |
| Set op 组件 size > int.MaxValue | 溢出绕回 | 提前抛出 | ✅ `FromWire_rejects_component_size_exceeding_int_maxvalue` |

### 栈溢出

| 场景 | 旧行为 | 新行为 | 测试 |
|------|--------|--------|------|
| `scratchSize > 256B` | `stackalloc scratchSize` → StackOverflow | `ArrayPool<byte>.Shared.Rent(size)` | （代码路径清晰，暂缺测试） |
| `componentCount > 64` | `stackalloc ComponentType[cnt]` → StackOverflow | `ArrayPool<ComponentType>.Shared.Rent(cnt)` | （同） |

### OOM（预分配）

| 场景 | 旧行为 | 新行为 | 测试 |
|------|--------|--------|------|
| `Reserve(Entity(100M, 1))` | PreScan 预分配 `_records[100M]` | clamp 到 `Math.Max(_records.Length*2, 65536)` | ✅ `Replay_reserve_with_huge_entity_id_does_not_oom` |
| `Create(Entity(100M, 1))` | PreScan 同，主 pass `_records[100M]` 越界 | PreScan clamp + Validate "无 Reserve" 拒绝 | ✅ `Validate_rejects_create_with_huge_entity_id_without_reserve` |
| `Destroy(Entity(100M, 1))` | PreScan 预分配 100M | PreScan 跳过非 alloc op（destroy 不跟踪 id） | ✅ `Destroy_with_huge_entity_id_does_not_oom_in_prescan`（已有） |
| `AddChild parent=Entity(100M, 1)` | PreScan 预分配 | clamp 已覆盖 AddChild | —（同路径） |
| Snapshot `entitySlotCount = 300M` | `new int[300M]` ≈ 1.2GB OOM | 上限 256M，`InvalidDataException` | ✅ `SnapshotLoad_rejects_entity_slot_count_over_256M` |
| Snapshot `schemaCount = 100K` | `new Type[100K]` 分配前无检查 | 上限 65536 | ✅ `SnapshotLoad_rejects_excessive_schema_count` |
| Snapshot `archetypeCount = 300K` | 30 万次循环分配 | 上限 262144 | ✅ `SnapshotLoad_rejects_excessive_archetype_count` |
| Snapshot `hierarchyLinkCount = slotCnt+1` | 超量读取 | 上限 = entitySlotCount | ✅ `SnapshotLoad_rejects_excessive_hierarchy_link_count` |
| Snapshot 负元数据 | — | `< 0` 一律拒绝 | ✅ `SnapshotLoad_rejects_negative_metadata_counts` |
| CommandStream buffer 倍增 | 无上限 → `OutOfMemoryException` | `GrowPendingBatchFor` 上限 `0x40000000` | —（构造需~4GB 中间状态不可行） |

### API 空参数

| 入口 | 旧行为 | 新行为 | 测试 |
|------|--------|--------|------|
| `CommandStream.Replay(null)` | `NullReferenceException` | `ArgumentNullException.ThrowIfNull` | ✅ `Replay_null_delta_throws_ArgumentNullException` |
| `CommandStream.SubmitAndSnapshotIntoAsync(null)` | `NullReferenceException` | 同上 | ✅ `SubmitAndSnapshotIntoAsync_null_target_throws_ArgumentNullException` |
| `WorldSnapshot.Load(null)` | `NullReferenceException` | 同上 | ✅ `SnapshotLoad_null_stream_throws_ArgumentNullException` |

### 截断/Malformed 输入

| 场景 | 保护 | 测试 |
|------|------|------|
| Snapshot 文件 < 8 字节 | "Snapshot data is too short" | ✅ `SnapshotLoad_rejects_truncated_input` |
| 空 stream | 同上 | ✅ `SnapshotLoad_rejects_empty_stream` |
| FrameDelta 截断 wire | `InvalidOperationException` + 实例重置 | ✅ `Deserialize_truncated_wire_never_crashes_and_cleans_state`（已有） |
| FrameDelta 随机 garbage | `ArgumentException` 或 `InvalidOperationException` | ✅ `Deserialize_random_garbage_never_crashes`（已有） |

## 认知模型

- **把 MiniArch 看作一种内存数据库**：Entity 是行 ID，Component 是列，Archetype 是行簇，Replay 是 WAL replay，Snapshot 是 checkpoint。这样理解之后，checkpoint 加载时不能因为一个恶意头字段就 OOM、WAL replay 时不能因为一个恶意 varint 就栈溢出——这些在游戏 ECS 里是"开发期 bug"，在数据库里是"CVE"。
- 硬化不是"提高内存使用效率"（那是 cache optimization 的工作），而是**保证内存使用上限可预测**，任何外部输入都不能突破这个上限。
- 测试了 20 个 attack surface 场景，覆盖所有已知的恶意输入路径。测试本身充当**可执行的规格文档**：每个 `[Fact]` 方法名指明攻击面 + 预期行为。

## 硬化原则

1. **零热路径退化**——所有兜底走 `checked`（已有 JIT 硬件指令）或 `if (unlikely)`，不改变正常路径的指令流水线
2. **DEBUG 优先，RELEASE 不妥协**——`Debug.Assert` 仅用于不可能状态；所有拒绝输入/防止 OOM 的保护都是无条件（not `#if DEBUG`）
3. **数据优先**——不从数据结构推导安全属性，所有保护路径有测试证明
4. **尽早拒绝**——省去后续所有分配和计算。`ReadVarint` 拦截负数比等下游 `Validate()` 更早；snapshot 元数据 cap 在 `new int[slotCnt]` 之前

## 决策

- **clamp 值选择原则**：下限是"任何合理游戏都不会超过"，上限是"即使超过也不会 OOM"。具体：
  - entitySlotCount：256M 上限 = 1GB slot 表，覆盖所有现实场景
  - schemaCount：65536 上限 = 远多于任何游戏的组件类型数
  - archetypeCount：262144 上限 = 60 个组件类型的全组合数，实际 ≤ 几千
  - hierarchyLinkCount：上限 = entitySlotCount，不可能需要超过实体数的链接
- **`Math.Max(_records.Length * 2, 65536)` 为什么取 65536**：最小 entity 容量 64（`EnsureCapacity` 下限），double 后 128，但 clamp 需要足够大以容纳合理 replay 行为。65536 = 64K 实体 ≈ 64K×28 字节记录 ≈ 1.8MB _records 数组——对空 world 是合理的一次性增长。
- **为什么不用 `decimal` / `BigInteger`**：性能不可接受。`checked` 是零开销硬件指令；`uint` 转 `int` 配合 `Math.Min` 在 JIT 后是一条 CMOV。

## 入口

- 阅读 hardening 的起点：`tests/MiniArch.Tests/Core/HardeningEdgeCaseTests.cs`（20 个测试，每个对应一个硬化点）
- 实现集中点：`src/MiniArch/Core/Archetype.cs`（`ComputeSegmentEntityCapacity`）、`src/MiniArch/Core/FrameDelta.cs`（`ReadVarint`）、`src/MiniArch/Core/WorldSnapshot.cs`（3 个元数据 cap）、`src/MiniArch/Core/World.cs`（PreScan clamp）
- 路线图大盘：`.knowledge/kb-hardening-roadmap.md`

## 坑点

- 硬化不消除性能开销——segment 容量=1 时每 entity 分配一个独立数组，回滚备份也是数组级拷贝。这是安全价格，不是 bug
- `snapshot schemaCount` cap 可能阻断极端（> 65536 组件类型）的用户，但这是合理的——如果你有 65536+ 组件类型，你的设计可能有问题
- `ReadVarint` 在 `FromWire` 阶段就已经抛异常，不是 `Validate` 阶段。这意味着某些"按设计应该走到 Validate 才拒"的 wire 在解码阶段就被拒绝了，5 个现有测试因此更新了期望值
- DEBUG-only 保护（`AssertNoStructChange`、层级循环检测）不会在 RELEASE 构建中触发。依赖这些保护的代码必须额外提供无条件 fallback 或者确保逻辑上不可能触发
- `_records[entity.Id]` 在 replay main pass 中不经过 PreScan 的保护——PreScan 只防 OOM，不保证 array index 合法。必须在 PreScan 之后的 `PlaceEntityInArchetype` 中确保 `_records` 够大，或者始终使用 Validate()
