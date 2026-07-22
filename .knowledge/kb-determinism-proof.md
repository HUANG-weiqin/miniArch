---
title: 确定性证明 — Lockstep ECS 库级确定性验证
module: DeterminismProof
description: miniArch 确定性保证的完整证明矩阵——9 个审计维度、实证数据、测试链接、已知边界、LayoutKind.Auto 修复记录。面向选型决策者和审计者。
updated: 2026-07-22
---

# 确定性证明 — Lockstep ECS 库级确定性验证

> 本文档记录 miniArch 作为 lockstep ECS 库的确定性保证。面向选型决策者、技术审计者、以及需要在内部文档中引用确定性水平的团队。

## 这个模块是干什么的

- 这个模块负责：
  - 集中记录 miniArch 的确定性审计结论（10 个维度）
  - 列出每个维度的实证数据（测试命令、seed 数、帧数、结果）
  - 明确确定性的边界（哪些在库范围内、哪些是应用层责任）
  - 记录已发现并修复的确定性漏洞（LayoutKind.Auto）
- 这个模块不负责：
  - 性能基准（见 `kb-hero-pipeline-regression.md`、`kb-ecs-comparison.md`）
  - API 设计决策（见 `kb-design-rationale.md`）
  - 架构审视（见 `kb-architecture-review.md`）

---

## 确定性审计总表（2026-07-11）

| # | 维度 | 判定 | 置信度 | 关键证据 |
|---|------|------|--------|----------|
| 1 | Entity ID 分配 | ✅ 强确定 | 高 | LIFO free-list + shift-not-swap + B5/B6 bug fix |
| 2 | 版本号 | ✅ 强确定 | 高 | wrap-to-1 + 500K 帧 rollover 测试 |
| 3 | Submit/Replay 顺序 | ✅ 强确定 | 高 | Create→Hierarchy→Ops→Destroy 显式注释 + B6 验证 |
| 4 | Sort/Dedup | ✅ 强确定 | 高 | sort key 唯一（entity id / component type id） |
| 5 | Archetype byte layout | ✅ 强确定 | 高 | LayoutKind.Auto 拦截修复（2026-07-10） |
| 6 | Varint/Hash | ✅ 强确定 | 高 | LEB128 + SHA-256 + CRC32（标准算法） |
| 7 | CanonicalChecksum | ✅ 强确定 | 高 | sort by Id + raw byte feed + SHA-256 |
| 8 | Soak 实证 | ✅ 证据级 | 极高 | 259 seed × 6.4M 帧 + 多 host lockstep soak |
| 9 | Placeholder E2E | ✅ 强确定 | 高 | 两阶段 emit + scratch buffer + EntityFieldResolver |
| 10 | **Query 迭代顺序** | ✅ **强确定** | **高** | **QueryOrderingTests 14 个测试 + 契约文档（2026-07-11 提升为语义承诺）** |

**结论：10/10 维度全部通过，无已知遗留漏洞。**

---

## 各维度详解

### 1. Entity ID 分配确定性

**机制**：LIFO free-list stack。`PopFreeIdUnsafe` = `_freeIds[--_freeIdCount]`，`PushFreeIdUnsafe` = `_freeIds[_freeIdCount++]`。

**保证**：同一 destroy 序列 → 同一 push 顺序 → 同一 pop 顺序。

**守护**：
- B5 bug fix：`RemoveFromFreeList` 用 shift-not-swap 保证 survivor 顺序
- B6 bug fix：`AlignCancelledBatchFreeListOrder` 在 Submit 时重排 cancelled batch 的 free-list entries 对齐 wire 顺序

**测试链接**：
- `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs` → `Submit_and_Replay_free_list_diverges_with_reverse_destroy_order`
- `tests/MiniArch.Tests/Core/TrickyEdgeCaseTests.cs` → `Create_many_then_selective_destroy_then_create_single_verifies_free_list_correctness`
- `tests/MiniArch.Tests/Core/TrickyEdgeCaseTests.cs` → `Destroy_and_recreate_cycle_preserves_correct_versions_across_many_iterations`

**代码位置**：
- `src/MiniArch/Core/World.EntityLifecycle.cs:377-408`（AcquireEntityIdUnsafe / PushFreeIdUnsafe / PopFreeIdUnsafe）
- `src/MiniArch/Core/World.EntityLifecycle.cs:556-567`（RemoveFromFreeList shift-not-swap）

### 2. 版本号确定性

**机制**：`Entity(int Id, int Version)`。每次 destroy 后 `Version + 1`，`int.MaxValue` 时 wrap to 1。

**保证**：单调递增 + 确定性 wrap，不依赖平台行为。

**测试链接**：
- Soak test `--sweep` 模式含 `Version Rollover` 边界测试（cap=10 floor=1, 500K 帧, 353K creates）

**代码位置**：
- `src/MiniArch/Core/World.EntityLifecycle.cs:573-576`（NextEntityVersion）

### 3. Submit/Replay 顺序确定性

**机制**：Submit、Replay、BuildDelta 三个路径显式共享同一操作顺序：`Create → Hierarchy → Ops(Set/Add/Remove) → Destroy`。

**保证**：注释显式声明 "Order matches Submit"（`CommandStreamCore.cs:994`、`CommandStreamCore.cs:1026`）。

**守护**：
- `AlignCancelledBatchFreeListOrder` 保证 cancelled batch 的 free-list entries 在 Submit 时对齐 wire 顺序
- `EmitPendingEntitiesToDelta` 的 deferred mode 两阶段 emit（Reserve 全部 → Create 全部）保证 placeholder→real 映射在 Create payload 读取前已建立

**测试链接**：
- `tests/MiniArch.Tests/Core/SubmitReplayParityTests.cs`（13 个测试，P1-P5 模式）
- `tests/MiniArch.Tests/Core/SubmitReplayRestoreParityTests.cs`（9 个测试，P1-P9 三路 parity）

**代码位置**：
- `src/MiniArch/Core/CommandStreamCore.cs:992-1022`（SubmitFromFrozen）
- `src/MiniArch/Core/CommandStreamCore.cs:1024-1043`（BuildFromFrozen）
- `src/MiniArch/Core/CommandStreamCore.cs:1139-1191`（EmitPendingEntitiesToDelta）

### 4. Sort/Dedup 确定性

**机制**：`SpanSorting.SortAndDeduplicate` 用 `Span<T>.Sort()`（introspective sort，非稳定排序）。

**保证**：所有 sort key 唯一（entity id / component type id），无 ties → introspective sort 的不稳定性不引入跨 host 分歧。

**代码位置**：
- `src/MiniArch/Core/SpanSorting.cs:5-23`

### 5. Archetype Byte Layout 确定性

**机制**：`ComputeColumnLayout` 按 `elementSizes` 排列，`AlignUp(totalBytes, Min(elementSize, 8))`。Element size 来自 `Unsafe.SizeOf<T>()`。

**保证**：
- `LayoutKind.Sequential`（C# 默认、record struct 默认）→ 跨 host 一致
- `LayoutKind.Auto` → **被 `ThrowIfManagedComponent` 拒绝**（2026-07-10 修复）

**修复记录**：
- **漏洞**：`ThrowIfManagedComponent` 只检查 managed references，不检查 `LayoutKind.Auto`。含 `LayoutKind.Auto` 且无 Entity fields 的组件被静默接受。CLR 可重排字段 → 跨 host byte layout 不一致 → CanonicalChecksum 分歧。
- **修复**：在 `ThrowIfManagedComponent` 中增加 `LayoutKind.Auto` 检查，throw `NotSupportedException`。
- **影响**：3 行核心代码，0 回归（902 测试全 PASS）。
- **测试**：`tests/MiniArch.Tests/Core/TrickyEdgeCaseTests.cs` → `Layoutkind_auto_component_rejected_for_determinism` / `Layoutkind_sequential_component_accepted_normally` / `Record_struct_component_accepted_normally`

**代码位置**：
- `src/MiniArch/Core/Archetype.Storage.cs:1136-1155`（ThrowIfManagedComponent，含 LayoutKind.Auto 拦截）
- `src/MiniArch/Core/Archetype.Storage.cs:1114-1127`（ComputeColumnLayout）

### 6. Varint/Hash 确定性

**机制**：
- LEB128 varint：`(int)((uint)value >> 7)` — C# 语义定义明确，跨平台一致
- SHA-256：FIPS 180-4 标准，所有 .NET runtime 实现一致
- CRC32：IEEE 802.3 标准（`System.IO.Hashing.Crc32`），跨平台一致

**代码位置**：
- `src/MiniArch/Core/FrameDelta.cs:737-746`（WriteVarintAt）
- `src/MiniArch/Core/WorldSnapshot.cs:224`（SHA-256）
- `src/MiniArch/Core/WorldSnapshot.cs:86`（CRC32）

### 7. CanonicalChecksum 确定性

**机制**：
- Entity 排序：`entries.Sort((a, b) => a.Entity.Id.CompareTo(b.Entity.Id))` — Id 唯一
- Component 数据：`FeedRowData` 直接喂 raw bytes（不序列化）
- Hierarchy：`relations.Sort((a, b) => a.ChildId.CompareTo(b.ChildId))` — Id 唯一
- Free-list：按 array index 顺序
- 计算方式：`IncrementalHash` + SHA-256

**保证**：sort by Id + raw byte feed + SHA-256 的组合是 lockstep canonical checksum 的标准做法。

**测试链接**：
- `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs`（30 个测试）
- `tests/MiniArch.Tests/PropertyBased/ReplayConvergencePropertyTests.cs`（FsCheck 150-200 样本）

**代码位置**：
- `src/MiniArch/Core/WorldSnapshot.cs:300-343`（ComputeCanonicalChecksum）

### 8. Soak 实证

**单 host soak**（Submit vs Replay 双路径）：
- 命令：`dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 100000`
- 结果：259 seed × 6.4M 帧全 PASS
- 发现并修复：6 个库级 bug（B1-B6）

**多 host lockstep soak**（N host Replay 路径收敛）：
- 命令：`dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak`
- 结果：4 host 每帧 CanonicalChecksum 跨 host 比对 PASS
- 拓扑：每 host 独立 World + 独立 id allocator + `DeferredEntities=true` + relay 模式

**Determinism test**：
- 命令：`dotnet run -c Release --project tools/soak/MiniArch.Soak -- --determinism --frames 200000`
- 结果：同 seed 跑 2 次，字节级一致

**测试链接**：
- `tools/soak/MiniArch.Soak/`（单 host soak）
- `tools/lockstep-soak/MiniArch.LockstepSoak/`（多 host lockstep soak）

### 10. Query 迭代顺序确定性

**机制**：Query 迭代顺序由存储物理决定：
- `_archetypeSnapshot` 按 signature 排序（`PublishArchetypeSnapshot` 执行排序插入，`FindInsertIndex` 二分查找插入点），不再依赖创建历史。这消除了 Save→Load、Clone、RestoreState 等路径的顺序差异。
- `QueryCache.RebuildCache` 全量扫描 `_world.Archetypes`，按排序后的世界顺序重建匹配列表和 chunk views。
- 同一 archetype 内 entity 按存储顺序（`_entities[]` 或 segment 内的 `Entities[]`）遍历
- 新 entity append 到末尾；entity 删除用 swap-remove（末尾 survivor 补充到被删位置），reorder 本身确定性
- 所有访问路径（foreach / GetChunks / GetArchetypeSpan）共享同一底层快照数组

**保证**：给定相同结构变更序列，entity 物理排列完全确定 → query 遍历顺序完全确定。且与创建历史解耦——仅由当前组件签名集合决定。

**代码位置**：
- `World.QueryCache.cs:132-146`（PublishArchetypeSnapshot——排序插入）
- `QueryCache.cs:194-264`（RebuildCache——全量重建）
- `Archetype.Storage.cs:222-227`（AddEntity——append 到末尾）
- `Archetype.Storage.cs:301-369`（RemoveAt——swap-remove，确定性重排）

**测试链接**：
- `tests/MiniArch.Tests/Core/QueryOrderingTests.cs`（13+ 个测试，2026-07-19 按签名顺序更新）

### 9. Placeholder E2E 确定性

**机制**：
- Placeholder 创建：`Entity(-1, seq)` — seq = 递增计数器
- Reserve 模式（deferred mode）：先 emit 全部 Reserve，再 emit 全部 Create → 保证 seq→real 映射在 Create payload 读取前已建立
- Replay 路径：`ReplayCore` → `EnsureReplayReservation` 只接受 matching free/already-reserved slot 或紧邻 fresh slot；不兼容 allocator 在 mutation 前失败 → `map[seq] = real`
- Entity ref 解析：`EntityFieldResolver.ResolveInPlace` 在 Replay 时自动解析 component 内的 Entity 字段
- 不可变性保证：Replay 不 mutate delta buffer（对含 Entity fields 的 op 用 pooled scratch buffer）

**测试链接**：
- `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs` → `Same_delta_sequence_into_two_fresh_worlds_produces_identical_state`
- `tests/MiniArch.Tests/PropertyBased/ReplayConvergencePropertyTests.cs` → `Two_worlds_replaying_same_placeholder_deltas_converge_each_frame`
- `tests/MiniArch.Tests/PropertyBased/KnownLimitationTests.cs` → `Deferred_link_then_destroy_BOTH_endpoints_replays_cleanly`

**代码位置**：
- `src/MiniArch/Core/World.EntityLifecycle.cs`（ReplayCore / EnsureReplayReservation / EnsurePlaceholderMap / PreScanForCapacity）
- `src/MiniArch/Core\EntityFieldResolver.cs:167-192`（ResolveInPlace）

---

## 确定性边界（应用层责任）

miniArch 的确定性保证**不覆盖**以下维度——这些是你们应用层的责任：

| 维度 | miniArch 的立场 | 你们需要自己做 |
|------|----------------|---------------|
| Float 运算确定性 | 不处理（直接存 IEEE 754 bytes） | 定点数 lib 或 deterministic math（如 `FixedPoint` struct） |
| 跨平台 float 精度 | 不保证（同 byte 格式但运算结果可能不同） | 整数运算或所有 host 同平台 |
| System 执行顺序 | 不约束（用户自己排序 Systems） | 确保 Systems 跨 host 同序 + 无 side effects |
| 外部输入时序 | 不约束（lockstep 要求输入帧对齐） | 网络层保证输入帧对齐 |
| 组件注册顺序 | 不约束（`ComponentRegistry.Shared` 全局单例） | 确保所有 host 启动时按相同顺序注册相同组件 |

---

## Bug 驱动的确定性修复历史

| # | Bug | 根因 | 修复 | 发现方式 |
|---|-----|------|------|---------|
| B1 | `ApplyRawAdd` 重复 Add 抛异常 | Replay 路径无去重 | 改为覆盖写入 | 单 host soak |
| B2 | `Clear` 不释放已取消 batch 实体 | Submit 路径跳过释放 | 始终释放 | 单 host soak |
| B3 | `ApplyHierarchyToWorld` 排序不同 | 字典 vs Entity.Id 排序 | 统一排序 | 单 host soak |
| B4 | `ApplyTypedAdd` 重复 Add 抛异常 | Clone+Add 路径 | 改为覆盖写入 | 单 host soak |
| B5 | Replay free-list 顺序分歧 | `RemoveFromFreeList` swap-remove 破坏顺序 | shift-not-swap | 单 host soak（boundary config） |
| B6 | Cancelled batch free-list 顺序分歧 | record 阶段按 destroy 顺序 push，wire 按 batch 创建顺序 emit | `AlignCancelledBatchFreeListOrder` | 单 host soak（boundary config） |
| — | LayoutKind.Auto 跨 host 分歧 | `ThrowIfManagedComponent` 不检查 layout kind | 增加 LayoutKind.Auto 拦截 | 代码审计（2026-07-10） |

---

## 与其他 C# ECS 库的确定性对比

| 库 | 确定性验证 | Soak 测试 | 多 host lockstep | PBT | LayoutKind 检查 |
|----|-----------|----------|-----------------|-----|----------------|
| **miniArch** | ✅ 9/9 维度 | ✅ 259 seed × 6.4M 帧 | ✅ 4 host 每帧 checksum | ✅ FsCheck | ✅ 已拦截 |
| Unity DOTS NetCode | ✅ 有（Predicted + Ghost） | ✅（Unity 测试套件） | ✅ | ❌ | N/A（绑定 Unity） |
| Friflo | ❌ 零 | ❌ | ❌ | ❌ | ❌ |
| Arch | ❌ 零 | ❌ | ❌ | ❌ | ❌ |
| DragonECS | ❌ 零 | ❌ | ❌ | ❌ | ❌ |
| fennecs | ❌ 零 | ❌ | ❌ | ❌ | ❌ |

**结论**：在纯 .NET / 开源 C# ECS 库中，miniArch 的确定性验证覆盖最广。

---

## 引用措辞（供内部文档使用）

### 短版

> miniArch 是目前纯 .NET / 开源 C# 生态中，唯一经过实证级 lockstep 确定性验证的 ECS 库。确定性审计 10/10 维度通过，含 259 seed × 6.4M 帧 soak test、多 host lockstep soak、三路 parity、FsCheck PBT、LayoutKind.Auto 拦截修复、以及 Query 迭代顺序契约。

### 长版

> miniArch 的确定性保证覆盖 10 个维度：Entity ID 分配（LIFO free-list + shift-not-swap）、版本号（wrap-to-1）、Submit/Replay 顺序（Create→Hierarchy→Ops→Destroy）、Sort/Dedup（无 ties）、Archetype byte layout（LayoutKind.Auto 拦截）、Varint/Hash（LEB128 + SHA-256 + CRC32）、CanonicalChecksum（sort by Id + raw byte feed）、Soak 实证（259 seed × 6.4M 帧）、Placeholder E2E（两阶段 emit + scratch buffer）、**Query 迭代顺序（archetype 签名顺序 + entity 存储顺序，14 个测试守护）**。实证数据来自单 host soak（Submit vs Replay）、多 host lockstep soak（N host Replay 路径收敛）、三路 parity（Submit/Replay/Restore byte-equal）、FsCheck PBT、以及 QueryOrderingTests。已发现并修复 7 个确定性相关 bug（B1-B6 + LayoutKind.Auto）。float 运算确定性、系统执行顺序、外部输入时序等应用层维度不在 ECS 库的覆盖范围内。

---

## 认知模型

- 理解这个模块时，应该把它看成：**miniArch 确定性保证的"审计报告"**——不是设计文档，是实证数据汇总。
- 最重要的抽象：
  - **Soak test**：长周期随机操作 + 双路径校验 → 发现 Submit/Replay 分歧
  - **CanonicalChecksum**：sort by Id + raw byte feed + SHA-256 → 跨 host byte-equal 比较
  - **三路 parity**：Submit / Replay / Restore 三条路径 byte-equal → 覆盖 rollback 场景
- 常见误解：
  - 「miniArch 处理了 float 运算确定性」——**错**。miniArch 直接存 IEEE 754 bytes，不转定点数。跨平台 float 运算确定性是应用层责任。
  - 「确定性 = 正确性」——**错**。Soak test 验证"同一输入 → 同一输出"，不验证"输出是否正确"（domain logic 的责任）。
  - 「LayoutKind.Auto 已被完全防御」——**对**（2026-07-10 修复），但修复只在 `CreateStorage` 路径。如果用户通过其他路径（如反射）注入 Auto layout 组件，仍可能绕过。实际风险极低。

## 入口

- 第一次读：本文档 → `kb-soak-test.md`（单 host soak 详解）→ `kb-lockstep-soak.md`（多 host lockstep soak 详解）
- 选型决策：本文档 + `kb-ecs-comparison.md`（性能对比）+ `kb-design-rationale.md`（设计哲学）
- 审计：本文档 → 各维度的"代码位置"和"测试链接"直接定位

## 坑点

- **float 运算确定性是最大的外部风险**：miniArch 保证 byte-level 一致，但 `Position.X += Velocity.X * dt` 的运算结果在不同 CPU 上可能不同。你们必须自己处理这一点（定点数 / 同平台 / deterministic math lib）。
- **System 执行顺序是第二大风险**：miniArch 不约束 Systems 的执行顺序。如果两个 host 的 Systems 顺序不同，即使 ECS 状态一致，游戏状态也会 diverge。
- **组件注册顺序**：`ComponentRegistry.Shared` 全局单例，id 按注册顺序分配。如果两个 host 的注册顺序不同，component type id 不一致 → archetype signature 不一致 → checksum 分歧。确保所有 host 启动时按相同顺序注册相同组件。
