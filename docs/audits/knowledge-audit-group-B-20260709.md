# Group B 知识页审计报告 — 2026-07-09

> 审计范围：kb-command-stream.md, kb-deferred-create-design.md, kb-snapshot-persistence.md, kb-lockstep-playbook.md, kb-hierarchy-runtime.md, kb-ecs-diagnostics.md
>
> 方法：逐一核对 API 签名、类型名、文件路径、行号引用、测试方法名、命令、性能数据。只读不修改 .knowledge。

---

## 1. kb-command-stream.md

### 覆盖声明
377 行，涵盖 CommandStream/ParallelCommandStream/FrameDelta 架构、API、坑点、性能数据、Profile 工具。

### 已验证的高风险声明

| 声明 | 验证结果 | 证据 |
|------|---------|------|
| `CommandStreamCore` 是 `public abstract class` | ✅ | `CommandStreamCore.cs:25` |
| `CommandStream` 是 `public sealed class` | ✅ | `CommandStream.cs:18` |
| `ParallelCommandStream` 是 `public sealed class` | ✅ | `ParallelCommandStream.cs:39` |
| 两个子类各有 9 个 public mutator (Create/Track/Add/Set/Remove/Destroy/AddChild/RemoveChild/Clone) | ✅ | `CommandStream.cs:29-125` 分别验证 |
| `CommandStream.cs` 路径 | ✅ | `src/MiniArch/Core/CommandStream.cs` |
| `CommandStreamCore.cs` 路径 | ✅ | `src/MiniArch/Core/CommandStreamCore.cs` |
| `ParallelCommandStream.cs` 路径 | ✅ | `src/MiniArch/Core/ParallelCommandStream.cs` |
| `FrameDelta.cs` 路径 | ✅ | `src/MiniArch/Core/FrameDelta.cs` |
| `CommandStreamTests.cs` 路径 | ✅ | `tests/MiniArch.Tests/Core/CommandStreamTests.cs` |
| `FrameDeltaDeterminismTests.cs` 路径 | ✅ | `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs` |
| `Submit/Snapshot/Replay/SubmitAndSnapshotAsync` 存在 | ✅ | `CommandStreamCore.cs:449, 672, 738, 781` |
| `AsSpan()/FromWire()/Deserialize()` API | ✅ | `FrameDelta.cs:97, 179, 119` |
| `ParallelCommandStream` 不支持 pending source Clone | ✅ | `ParallelCommandStream.cs:215-219` 抛 `NotSupportedException` |
| `profile-commandstream.ps1` 脚本存在 | ✅ | `tools/scripts/profile-commandstream.ps1` |
| CommandStream.Profile 项目存在 | ✅ | `tools/perf/CommandStream.Profile/` |
| `World.ReserveDeferredEntityUnsafe` 存在 | ✅ | `World.EntityLifecycle.cs:466` |
| `World.IsSlotReserved` 存在 | ✅ | `World.cs:1157` |
| 测试方法名全部可找到 | ✅ | 所有 `BUG_` 和常规测试名均被 `rg` 命中 |

### 发现的问题

#### ❌ F1: 过时的行号引用 — `World.ReplayCore` 行号
- **位置**: `kb-command-stream.md:183`
- **原文**: `World.ReplayCore（World.cs:481）`
- **事实**: `ReplayCore` 在 `World.cs:696`
- **推荐操作**: 更新为 `World.cs:696`

#### ❌ F2: 过时的行号引用 — `EnsureReplayReservation` 行号
- **位置**: `kb-command-stream.md:329`
- **原文**: `EnsureReplayReservation（World.EntityLifecycle.cs:451）`
- **事实**: `EnsureReplayReservation` 在 `World.EntityLifecycle.cs:533`
- **推荐操作**: 更新为 `World.EntityLifecycle.cs:533`

#### ❌ F3: 过时的方法名引用 — `SortTypesAndOffsets` + `DeduplicateSortedSpans`
- **位置**: `kb-command-stream.md:177`
- **原文**: "`hasLargeIds` 分支显式 `SortTypesAndOffsets` + `DeduplicateSortedSpans`（`CommandStream.cs:755-756`）"
- **事实**: 这两个方法在当前代码中**不存在**。当前实现为 `SortAndDeduplicateComponents`（`CommandStreamCore.cs:1892`）。`CommandStream.cs` 仅 126 行（因代码重构，逻辑移入 `CommandStreamCore.cs`）。原来的排序+去重逻辑被合并为一个 `SortAndDeduplicateComponents` 方法。
- **推荐操作**: 更新方法名和文件/行号引用

#### ❌ F4: 过时的方法名引用 — `MaskToTypes`
- **位置**: `kb-command-stream.md:177`
- **原文**: "mask 分支通过 `MaskToTypes` 按位序升序枚举（`CommandStream.cs:1113`）"
- **事实**: `MaskToTypes` 在当前代码中**不存在**。`CommandStream.cs` 仅 126 行。
- **推荐操作**: 删除或更新为当前实现描述

#### ❌ F5: 过时文件名引用 — `World.Checksum.cs`（首次出现在此页）
- **位置**: `kb-command-stream.md:183`（在 `World.ReplayCore` 引用附近未直接出现，但此页引用 `kbsnapshot-persistence.md` 涉及此文件）
- 详细见 kb-snapshot-persistence.md 的 F6。

---

## 2. kb-deferred-create-design.md

### 覆盖声明
234 行，涵盖 DeferredEntities flag 设计、Producer/Consumer 端实现、EntitySlot、决策。

### 已验证的高风险声明

| 声明 | 验证结果 | 证据 |
|------|---------|------|
| `DeferredEntities` 属性存在 | ✅ | `CommandStreamCore.cs:144` |
| `EntitySlot` 是 `public readonly struct` | ✅ | `EntitySlot.cs:19` |
| `EntitySlot` 有 `implicit operator Entity` | ✅ | `EntitySlot.cs:70` |
| `EntityFieldResolver` 是 `internal static class` | ✅ | `EntityFieldResolver.cs:21` |
| `World.TryResolvePlaceholder` 已改为 `internal`（不再是公共 API） | ✅ | 搜索未找到 public 声明 |
| `_replayPlaceholderMap` 是 `Entity[]` | ✅ | `World.cs` 搜索匹配 |
| `SubmitAndSnapshotAsync` 始终输出 real-id delta（忽略 flag） | ✅ | `CommandStreamCore.cs:771-773, 826-829` |
| `Snapshot()` 在 `DeferredEntities=true` 时检测 immediate entity 并抛异常 | ✅ | `CommandStreamCore.cs:753-754` |
| Lockstep soak 工具存在 | ✅ | `tools/lockstep-soak/MiniArch.LockstepSoak/` |

### 发现的问题

本次审计未发现 kb-deferred-create-design.md 中的事实性错误。

---

## 3. kb-snapshot-persistence.md

### 覆盖声明
135 行，涵盖 WorldSnapshot/WorldClone/WorldStateSnapshot 三套机制、Checksum 双模式、CRC32 v4 格式。

### 已验证的高风险声明

| 声明 | 验证结果 | 证据 |
|------|---------|------|
| `WorldSnapshot.Save/Load` 存在 | ✅ | `WorldSnapshot.cs:35, 99` |
| `World.Clone()` 存在 | ✅ | `World.cs:477` |
| `World.CaptureState/RestoreState` 存在 | ✅ | `World.cs` (via `_stateSnapshotPool`) |
| `_stateSnapshotPool` 是 `Stack<WorldStateSnapshot>` | ✅ | `World.cs:1179` |
| `IsRecycled` 属性存在 | ✅ | `WorldStateSnapshot.cs:97` |
| `WorldStateSnapshot` 池化设计 | ✅ | `World.cs:1194-1195, 1306` |
| CRC32 尾部校验（v4 格式, `FormatVersion=4`） | ✅ | `WorldSnapshot.cs:24-25, 48-93` |
| CRC 错误时抛出 `InvalidDataException("CRC mismatch at offset ...")` | ✅ | `WorldSnapshot.cs:140-141` |
| v3 格式可读且跳过 CRC | ✅ | `WorldSnapshot.cs:146` 注释 |
| `CollectPersistedArchetypes` 存在 | ✅ | `WorldSnapshot.cs:345` |
| `WriteArchetype` 存在 | ✅ | `WorldSnapshot.cs:405` |

### 发现的问题

#### ❌ F6: 引用不存在的文件 `World.Checksum.cs`
- **位置**: `kb-snapshot-persistence.md:74, 75, 92`
- **原文**: 
  - `world.Checksum()`（`World.Checksum.cs:11` → `WorldSnapshot.cs:159`）
  - `world.CanonicalChecksum()`（`World.Checksum.cs:20` → `WorldSnapshot.cs:221`）
  - `src/MiniArch/World.Checksum.cs`（入口段）
- **事实**: `World.Checksum.cs` **不存在**。`Checksum()` 方法在 `World.cs:1316`，`CanonicalChecksum()` 在 `World.cs:1325`。两个方法都委托到 `WorldSnapshot` 中的 `ComputeChecksum`（行 221）和 `ComputeCanonicalChecksum`（行 **300**）。
- **行号错误汇总**:
  - 声称 `World.Checksum.cs:11` → 真实 `World.cs:1316`
  - 声称 `WorldSnapshot.cs:159` → 真实 `ComputeChecksum` 在 `WorldSnapshot.cs:221`
  - 声称 `World.Checksum.cs:20` → 真实 `World.cs:1325`
  - 声称 `WorldSnapshot.cs:221` → 真实 `ComputeCanonicalChecksum` 在 `WorldSnapshot.cs:300`
  - 声称 `WorldSnapshot.cs:159-271` 范围 → 实际 `ComputeChecksum:221` + `ComputeCanonicalChecksum:300`
- **推荐操作**: 删除对 `World.Checksum.cs` 的所有引用。更新行号。

#### ❌ F7: 过时的 `_deferredSeq` 文件/行号引用
- **位置**: `kb-snapshot-persistence.md:130`
- **原文**: "`CommandStream.cs:1436`"
- **事实**: `_deferredSeq = 0` 在 `CommandStreamCore.cs:2074`。`CommandStream.cs` 仅 126 行。
- **推荐操作**: 更新为 `CommandStreamCore.cs:2074`

---

## 4. kb-lockstep-playbook.md

### 覆盖声明
112 行，端到端 lockstep 流程导航页，链接 5 个 kb 页。

### 已验证的高风险声明

| 声明 | 验证结果 | 证据 |
|------|---------|------|
| `CommandStream(world)` 构造模式 | ✅ | `CommandStream.cs:18` |
| `stream.DeferredEntities = true` | ✅ | `CommandStreamCore.cs:144` |
| `stream.Snapshot()` 返回 FrameDelta | ✅ | `CommandStreamCore.cs:672` |
| `stream.Clear()` 存在 | ✅ | `CommandStreamCore.cs` |
| `delta.AsSpan()` 存在 | ✅ | `FrameDelta.cs:97` |
| `FrameDelta.FromWire()` 存在 | ✅ | `FrameDelta.cs:179` |
| `world.Replay(delta)` 存在 | ✅ | `World.cs` + `CommandStreamCore.cs:738` |
| `world.Checksum()` 存在 | ✅ | `World.cs:1316` |
| 跨页链接指向正确 | ✅ | 逐一检查均存在 |

### 发现的问题

kb-lockstep-playbook.md 是纯导航页，没有原始代码行号/类名/方法名的具体引用，所有声明均已验证为真。

---

## 5. kb-hierarchy-runtime.md

### 覆盖声明
55 行，简洁描述 hierarchy 运行时。

### 已验证的高风险声明

| 声明 | 验证结果 | 证据 |
|------|---------|------|
| `HierarchyTable.cs` 存在 | ✅ | `src/MiniArch/Core/HierarchyTable.cs` |
| `ChildrenEnumerable.cs` 存在 | ✅ | `src/MiniArch/Core/ChildrenEnumerable.cs` |
| `ChildrenEnumerable` 是 `public readonly struct` | ✅ | `ChildrenEnumerable.cs:10` |
| `World.AddChild/RemoveChild/TryGetParent/EnumerateChildren/HasChildren` | ✅ | `World.cs:277-318` |
| `WorldLifecycleTests.cs` 路径 | ✅ | `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs` |

### 发现的问题

#### ⚠️ F8: 小偏差 — `ChildrenEnumerable` 描述
- **位置**: `kb-hierarchy-runtime.md:30`
- **原文**: "`public readonly struct + struct enumerator（MiniArch 命名空间）`"
- **事实**: `ChildrenEnumerable` 确实是 `public readonly struct`（`ChildrenEnumerable.cs:10`），命名空间是 `MiniArch.Core`，不是 `MiniArch`。虽然 `command-stream.md` 的入口段标题标的是 `MiniArch.Core Hierarchy`，但 HierarchyTable.cs 和 ChildrenEnumerable.cs 都在 `namespace MiniArch.Core` 下。
- **推荐操作**: 如果严谨修改命名空间，可改为 `MiniArch.Core`。但 `MiniArch` 命名空间可能是旧称，此偏差小。

本次审计未发现事实性错误。

---

## 6. kb-ecs-diagnostics.md

### 覆盖声明
56 行，涵盖 WorldDiff/WorldValidator/EntityDump/WorldDigest 四个诊断工具。

### 已验证的高风险声明

| 声明 | 验证结果 | 证据 |
|------|---------|------|
| `WorldDiff.cs` 存在 | ✅ | `src/MiniArch/Diagnostics/WorldDiff.cs` |
| `WorldValidator.cs` 存在 | ✅ | `src/MiniArch/Diagnostics/WorldValidator.cs` |
| `EntityDump.cs` 存在 | ✅ | `src/MiniArch/Diagnostics/EntityDump.cs` |
| `WorldDigest.cs` 存在 | ✅ | `src/MiniArch/Diagnostics/WorldDigest.cs` |
| `WorldDiff.Compare()` 是 `static` | ✅ | `WorldDiff.cs:35` |
| `WorldValidator.Validate()` 是 `static` | ✅ | `WorldValidator.cs:26` |
| `EntityDump.Describe()` 是 `static` | ✅ | `EntityDump.cs:14` |
| `WorldDigest.Compute()` 是 `static` | ✅ | `WorldDigest.cs:15` |
| `EntityReport` 是 `public readonly struct` | ✅ | `EntityDumpResult.cs:8` |
| `WorldDiffResult` 是 `public sealed class` | ✅ | `WorldDiff.cs:264` |
| `HashBuilder` 类存在 | ✅ | `WorldDigestResult.cs:57` |
| `HashBuilder` 使用 `SHA256.HashData(Stream)` | ✅ | `WorldDigestResult.cs:75` |
| `Signature` 没有 `[i]` 索引器 | ✅ | `Signature.cs` 无 `this[...]` |
| `Signature` 有 `.AsSpan()` | ✅ | `Signature.cs:59` |
| `Position`/`Velocity` 在诊断测试中是 `file readonly record struct` | ✅ | 在 `EntityDumpTests.cs`、`WorldDigestTests.cs`、`WorldValidatorTests.cs` 中均声明为 `file readonly record struct` |
| `WorldValidator` 对 pending 保留输出 Warning | ✅ | `WorldValidator.cs:211-213` |
| `WorldDiff.Compare` 在有 pending 时抛 `InvalidOperationException` | ✅ | `WorldDiff.cs:29-33, 40-41` |

### 发现的问题

#### ❌ F9: `WorldDiffResult` 不在独立文件
- **位置**: `kb-ecs-diagnostics.md:31`
- **原文**: "所有结果类型在各自独立的 `*Result.cs` 文件中"
- **事实**: `WorldDiffResult` 定义在 `WorldDiff.cs:264`，不在独立的 `WorldDiffResult.cs` 文件中。其他三个（`ValidationResult`→`WorldValidatorResult.cs`、`EntityReport`→`EntityDumpResult.cs`、`WorldDigestResult`→`WorldDigestResult.cs`）确实在独立文件中。
- **推荐操作**: 修正为"大部分结果类型在各自独立的 `*Result.cs` 文件中，WorldDiffResult 在 WorldDiff.cs 内"。

#### ⚠️ F10: `IncrementalHash` 移除描述可能不准确
- **位置**: `kb-ecs-diagnostics.md:34`
- **原文**: "使用 `System.Security.Cryptography.SHA256.HashData(Stream)` 替代 `IncrementalHash`（后者在 System.IO.Hashing 8.x 中被移除）"
- **事实**: `IncrementalHash` 属于 `System.Security.Cryptography` 命名空间，**从未**存在于 `System.IO.Hashing` 中。`System.IO.Hashing` 包含 `Crc32`、`XxHash64` 等。`IncrementalHash.CreateHash` 仍是 `System.Security.Cryptography.IncrementalHash` 的有效 API（如在 `WorldSnapshot.cs:303` 中使用）。`HashBuilder` 只是提供了一种替代方法，并非因为 `IncrementalHash` 被移除。
- **推荐操作**: 删除或修正关于 `System.IO.Hashing` 移除的陈述。建议改为："`HashBuilder` 使用 `MemoryStream` + `SHA256.HashData(Stream)` 聚合计算，避免热路径依赖。`IncrementalHash`（`System.Security.Cryptography`）仍可用，但在诊断场景中 `HashBuilder` 更简洁。"

---

## 汇总表

| 知识页 | 总问题数 | 严重问题 | 轻微问题 | 完全正确 |
|--------|---------|---------|---------|---------|
| kb-command-stream.md | 5 (F1-F5) | 3 (行号/方法名过期) | 2 | 大部分正确 |
| kb-deferred-create-design.md | 0 | - | - | ✅ 全部正确 |
| kb-snapshot-persistence.md | 2 (F6-F7) | 1 (文件引用不存在) | 1 | 大部分正确 |
| kb-lockstep-playbook.md | 0 | - | - | ✅ 全部正确 |
| kb-hierarchy-runtime.md | 1 (F8) | 0 | 1 | 全部正确 |
| kb-ecs-diagnostics.md | 2 (F9-F10) | 1 (事实错误) | 1 | 大部分正确 |

### 严重问题分类

| ID | 问题类型 | 位置 | 说明 |
|----|---------|------|------|
| F3+F4 | 过时方法名 | kb-command-stream.md:177 | `SortTypesAndOffsets`/`DeduplicateSortedSpans`/`MaskToTypes` 在当前代码中不存在 |
| F1 | 过时行号 | kb-command-stream.md:183 | `World.ReplayCore` 声称在 `World.cs:481`，实际在 `:696` |
| F2 | 过时行号 | kb-command-stream.md:329 | `EnsureReplayReservation` 声称在 `World.EntityLifecycle.cs:451`，实际在 `:533` |
| F6 | 文件不存在 | kb-snapshot-persistence.md:74,75,92 | `World.Checksum.cs` 不存在，且 `WorldSnapshot.cs:159`/`:221` 行号全部偏小 |
| F7 | 过时行号 | kb-snapshot-persistence.md:130 | 声称 `CommandStream.cs:1436`，实际在 `CommandStreamCore.cs:2074` |
| F9 | 事实偏差 | kb-ecs-diagnostics.md:31 | `WorldDiffResult` 不在独立的 `*Result.cs` 文件中 |

### 建议优先级

1. **立即修复**：F6（`World.Checksum.cs` 不存在 → Checksum 双模式的入口段完全失效）
2. **立即修复**：F3+F4（过时方法名引用 → 读者会困惑找不到代码）
3. **修复**：F1+F2+F7（过时行号 → 定位失效）
4. **修复**：F9（事实偏差 → 不一致）
5. **可选**：F8+F10（轻微不准确）

---

## 审计命令记录

```bash
# 文件存在性验证
ls -la src/MiniArch/Core/CommandStreamCore.cs
ls -la src/MiniArch/Core/CommandStream.cs
ls -la src/MiniArch/Core/ParallelCommandStream.cs
ls -la src/MiniArch/Core/FrameDelta.cs
ls -la src/MiniArch/Core/HierarchyTable.cs
ls -la src/MiniArch/Core/ChildrenEnumerable.cs
ls -la src/MiniArch/Core/EntitySlot.cs
ls -la src/MiniArch/Core/WorldClone.cs
ls -la src/MiniArch/Core/WorldStateSnapshot.cs
ls -la src/MiniArch/Diagnostics/WorldDiff.cs
ls -la src/MiniArch/Diagnostics/WorldValidator.cs
ls -la src/MiniArch/Diagnostics/EntityDump.cs
ls -la src/MiniArch/Diagnostics/WorldDigest.cs
ls -la src/MiniArch/Diagnostics/WorldDigestResult.cs
ls -la src/MiniArch/Diagnostics/WorldValidatorResult.cs
ls -la src/MiniArch/Diagnostics/EntityDumpResult.cs
# World.Checksum.cs — 不存在！

# 类/方法声明验证
rg -n "public abstract class CommandStreamCore" src/MiniArch/
rg -n "public sealed class CommandStream[^C]" src/MiniArch/
rg -n "public sealed class ParallelCommandStream" src/MiniArch/
rg -n "class FrameDelta" src/MiniArch/
rg -n "internal static class WorldClone" src/MiniArch/
rg -n "public sealed class WorldStateSnapshot" src/MiniArch/
rg -n "public*Compare\(World" src/MiniArch/Diagnostics/WorldDiff.cs
rg -n "public*Validate\(World" src/MiniArch/Diagnostics/WorldValidator.cs
rg -n "public*Describe\(World" src/MiniArch/Diagnostics/EntityDump.cs
rg -n "public*Compute\(World" src/MiniArch/Diagnostics/WorldDigest.cs
rg -n "Checksum\(\)|CanonicalChecksum" src/MiniArch/Core/World.cs

# 行号验证
rg -n "ReplayCore" src/MiniArch/Core/World.cs
rg -n "EnsureReplayReservation" src/MiniArch/Core/World.EntityLifecycle.cs
rg -n "_deferredSeq" src/MiniArch/Core/CommandStreamCore.cs
rg -n "SortAndDeduplicateComponents" src/MiniArch/Core/CommandStreamCore.cs

# 测试方法名验证
rg -n "Submit_on_source_equals_Replay" tests/MiniArch.Tests/
rg -n "Submit_link_and_set_on_same_child" tests/MiniArch.Tests/
rg -n "BUG_stale_existing_entity_set_is_skipped" tests/MiniArch.Tests/
rg -n "BUG_existing_entity_that_becomes_stale" tests/MiniArch.Tests/
rg -n "Pending_cancel_after_later_create" tests/MiniArch.Tests/
rg -n "Interleaved_pending_creates" tests/MiniArch.Tests/

# 签名验证
rg -n "this\[" src/MiniArch/Core/Signature.cs
rg -n "AsSpan" src/MiniArch/Core/Signature.cs
```

## 审计结论

1. **kb-deferred-create-design.md** 和 **kb-lockstep-playbook.md** 完全正确，无需修改。
2. **kb-hierarchy-runtime.md** 全部正确，仅一个可选命名空间小偏差。
3. **kb-command-stream.md** 有 3 处严重过期行号/方法名引用和 2 处轻微问题，主要是代码重构（`CommandStream.cs` 拆分为 `CommandStreamCore.cs` 主实现 + `CommandStream.cs` 瘦封装）后未同步更新。
4. **kb-snapshot-persistence.md** 有 2 处问题，最严重的是引用已不存在的 `World.Checksum.cs` 文件和错误的行号，导致 Checksum 双模式段落的入口指引完全失效。
5. **kb-ecs-diagnostics.md** 有 2 处问题，其中 `WorldDiffResult` 文件位置描述不准确，以及 `IncrementalHash` 移除背景的不准确表述。

**总计 10 个问题（含严重 6 个，轻微 4 个），需要在下次知识页维护时修复。**
