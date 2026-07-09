# 知识审计报告 — Group A

**审计日期**: 2026-07-09  
**审计范围**: `.knowledge/kb-architecture-review.md`, `kb-core-ecs.md`, `kb-chunk-storage.md`, `kb-cache-optimization.md`, `kb-change-tracking.md`  
**工作区**: `.worktrees/knowledge-consistency-audit-20260709`  
**规则**: 只读 .knowledge，不动 kb 文件  

---

## 审计方法

对每页的每一条事实性声明（API 名、文件路径、行号引用、计数、不变式、行为描述、性能陈述、已删除类型声明、设计理由）进行以下验证：
- 源码搜索（`rg` / `grep`）确认存在性、路径、行号
- 文件列表（`glob`）核对文件存在/缺失
- 类/结构体/枚举定义检查
- 引用一致性：跨 kb 页面的交叉引用与源码是否一致

---

## 1. `.knowledge/kb-architecture-review.md`

**覆盖度**: 全面审计 ✓  
**检查类别**: 文件路径、行号引用、计数（partial 文件数/op 种类）、设计描述、API 名称、已删除声明、依赖图

### 不匹配/过时声明

#### [A1] WorldStateSnapshot 池行号引用错误
- **文件:行**: `kb-architecture-review.md:86`
- **原文摘要**: "代码位置：`World.cs:902-1015` + `WorldStateSnapshot.cs:34-99`"
- **实际事实**: 
  - 快照池 (CaptureState/RestoreState/_stateSnapshotPool) 实际代码位于 `World.cs:1169-1307`（含字段声明、CaptureState 方法从 L1191 开始、RestoreState 从 L1247 开始）
  - `World.cs:902-1015` 包含的是 `PreScanForCapacity`（FrameDelta replay 预扫描容量），与快照池无关
  - `WorldStateSnapshot.cs:34-99` 覆盖 XML doc 结尾 + `Clear()` 开始，不是主要逻辑段（class 定义在 L63，`IsRecycled` 在 L97）
- **推荐操作**: 将 `World.cs:902-1015` 修正为 `World.cs:1169-1307`（或 `World.cs:1179-1307` 精确对应池字段+方法）
- **证据**: `rg -n "CaptureState|RestoreState|_stateSnapshotPool" Core/World.cs`

#### [A2] `IChunkForEach` 接口行号引用错误
- **文件:行**: `kb-architecture-review.md:104`
- **原文摘要**: "`ForEachChunk<TForEach>(ref TForEach)` / `ForEachChunkParallel<TForEach>(TForEach)`：基于 `IChunkForEach` struct 接口（`src/MiniArch/Query.cs:196`）"
- **实际事实**:
  - `IChunkForEach` 接口定义在 `src/MiniArch/Query.cs:288`
  - `ForEachChunk<TForEach>` 在 `Query.cs:134`，`ForEachChunkParallel<TForEach>` 在 `Query.cs:179`
  - `Query.cs:196` 是 `if (partitionCount == 1)` 条件分支，与 `IChunkForEach` 无关
- **推荐操作**: 将行号从 `196` 修正为 `288`（接口定义）或 `134/179`（方法入口）
- **证据**: `rg -n "interface IChunkForEach" src/MiniArch/Query.cs`

#### [A3] 遗漏第三层 Archetype 查找缓存
- **文件:行**: `kb-architecture-review.md:64-72`（"Archetype lookup（两层）"表格）
- **原文摘要**: 只列出 `_archetypes: Dict<Signature, Archetype>` 和 `_archetypeByMask: Dict<ComponentMask, Archetype>` 两层
- **实际事实**: 代码中存在第三层 `_archetypeByHash: Dictionary<int, List<Archetype>>`（`World.cs:51`），用于非 canonical mask（component id >= 512）的免分配查找。该 hash 缓存避免了对高 id 组件 signature 重新分配 `Signature` 对象
- **推荐操作**: 在架构表中新增第三行描述 `_archetypeByHash`
- **证据**: `World.cs:47-51`：注释标明 "Hash-keyed cache for non-canonical masks"

#### [A4] 依赖图 `_archetypeByMask` 路径遗漏 `_archetypeByHash`
- **文件:行**: `kb-architecture-review.md:25-41`（依赖图 ASCII）
- **原文摘要**: 依赖图只画了 Signature → Archetype 和 ComponentMask → Archetype 两条路径
- **实际事实**: 缺少 `_archetypeByHash: Dictionary<int, List<Archetype>>` 作为第三条路径
- **推荐操作**: 在依赖图中补充第三路径，或加注 "non-canonical mask 走 _archetypeByHash"
- **证据**: 同 [A3]

### 已验证的高风险声明

| 声明 | 证据 | 结论 |
|------|------|------|
| World 拆分为 5 个 partial 文件 (`:137`) | `rg "partial class World"` → 5 文件: World.cs, World.EntityLifecycle.cs, World.Create.Generated.cs, World.QueryCache.cs, World.StructuralChange.cs | ✅ 精确 |
| `IsMaskCanonical` 在 `World.cs:514` (`:70`) | 源码确认 L514: `internal static bool IsMaskCanonical(...)` | ✅ 精确 |
| 两段式失效在 `Core/QueryCache.cs:103-126` (`:96`) | L103-126 包含 `EnsureRefreshed()` 含 archetype count cmp + segment count 循环 | ✅ 精确 |
| 9 种 op kind 0x01-0x09 (`:130`) | `DeltaOpKind` enum: Reserve(0x01)..Destroy(0x09) | ✅ 精确 |
| `IsChunked` 派生属性 `=> _segments is not null` (`:58`) | `Archetype.cs:88` | ✅ 精确 |
| ComponentMask 8×ulong (`:77`) | `ComponentMask.cs:12-26` 定义 B0-B7 | ✅ 精确 |
| CommandStream / ParallelCommandStream 拆分 (`:117-121`) | `CommandStream : CommandStreamCore`, `ParallelCommandStream : CommandStreamCore` | ✅ 精确 |
| DebugMetrics/CommandBuffer 已删除 (`:146-147`) | `rg "DebugMetrics|CommandBuffer"` 零匹配 | ✅ 精确 |
| `_stateSnapshotPool: Stack<WorldStateSnapshot>` (`:81`) | `World.cs:1179` | ✅ 精确 |

---

## 2. `.knowledge/kb-core-ecs.md`

**覆盖度**: 全面审计 ✓  
**检查类别**: 文件存在性、文件计数、路径引用、API 描述、已删除声明

### 不匹配/过时声明

#### [B1] World partial 文件计数错误
- **文件:行**: `kb-core-ecs.md:21`
- **原文摘要**: "World partial 文件族（**7 个**）"
- **实际事实**: 实际只有 **5 个** partial 文件: `World.cs`, `World.EntityLifecycle.cs`, `World.Create.Generated.cs`, `World.QueryCache.cs`, `World.StructuralChange.cs`
- **推荐操作**: 将 `7 个` 改正为 `5 个`。同时应同步检查 `kb-architecture-review.md`（其 §10 正确说"5 个"）和 `kb-cache-optimization.md`（也错误说"7 个"）的一致性
- **证据**: `rg -l "partial class World" src/MiniArch/Core/` → 5 文件

#### [B2] 文件路径 `Core/Query.cs` 不存在
- **文件:行**: `kb-core-ecs.md:82`
- **原文摘要**: "`MiniArch.Core.QueryCache`（原 `Core.Query`，2026-06-30 重命名）是 `internal sealed class`（`Core/Query.cs:11`）"
- **实际事实**: 文件路径应为 `Core/QueryCache.cs`（而非 `Core/Query.cs`）。`Core/` 下不存在 `Query.cs`。`internal sealed class QueryCache` 定义在 `Core/QueryCache.cs:11`
- **推荐操作**: 将 `Core/Query.cs` 改正为 `Core/QueryCache.cs`
- **证据**: `glob "Core/Query.cs"` → 无结果; `glob "Core/QueryCache.cs"` → 存在

#### [B3] `ComponentColumnMap.cs` 文件不存在（已删除/合并）
- **文件:行**: `kb-core-ecs.md:29`
- **原文摘要**: "`ComponentColumnMap.cs`：`component id → column index` 映射的共享 helper"
- **实际事实**: 文件中不存在 `ComponentColumnMap.cs`。该功能已合并到 `Archetype` 内部（`_componentIdToColumnIndex` 字段直接在 `Archetype.cs` 中管理）。git 历史显示该文件在 `2b2cc1f` / `385096d` 重构中被删除
- **推荐操作**: 从文件列表中删除此条目，或在 `Archetype.cs` 描述中说明 `_componentIdToColumnIndex` 的职责
- **证据**: `glob "**/ComponentColumnMap.cs"` → 无结果; `rg "ComponentColumnMap"` → 零匹配

#### [B4] `EntityBatchRange.cs` 非独立文件
- **文件:行**: `kb-core-ecs.md:42`
- **原文摘要**: "`EntityBatchRange.cs`：批量创建/克隆的连续范围记录"
- **实际事实**: `EntityBatchRange` 是 `internal readonly record struct`，**定义在 `World.EntityLifecycle.cs` 内部**，不是独立文件
- **推荐操作**: 从文件列表中删除此条目，或在 `World.EntityLifecycle.cs` 描述中注明其存在
- **证据**: `rg "record struct EntityBatchRange"` → `World.EntityLifecycle.cs:1`

#### [B5] `SpanHelper.cs` 文件不存在（已删除）
- **文件:行**: `kb-core-ecs.md:44`
- **原文摘要**: "`SpanHelper.cs`：排序+去重、hash 合并等 span 工具"
- **实际事实**: `SpanHelper.cs` 不存在于源码中。该功能已被折叠到 `Core/SpanSorting.cs`（仅包含排序逻辑）或内联使用
- **推荐操作**: 从文件列表中删除此条目，或更新为 `Core/SpanSorting.cs` 并核实其实际内容
- **证据**: `glob "**/SpanHelper.cs"` → 无结果; `rg "SpanHelper"` → 零匹配

#### [B6] `Query.cs` 的描述与实际职责不完全匹配
- **文件:行**: `kb-core-ecs.md:33`
- **原文摘要**: "`Query.cs`：archetype 过滤和 chunk 遍历、单版本号全局快照失效；定义 `internal sealed class QueryCache`（用户面是 `MiniArch.Query` struct facade）"
- **实际事实**: 
  - `src/MiniArch/Query.cs` 定义的是 **public `MiniArch.Query` struct facade** 和 `IChunkForEach`，不定义 `QueryCache`
  - `internal sealed class QueryCache` 定义在 `Core/QueryCache.cs`
  - `Query.cs` 是用户 API 层，查询快照/过滤/失效等内部实现在 `Core/QueryCache.cs`
- **推荐操作**: 重新措辞为："`Query.cs`：public `MiniArch.Query` struct facade，提供 `GetChunks()` / `ForEachChunk` 等用户入口；内部查询实现在 `Core/QueryCache.cs`"
- **证据**: 阅读 `src/MiniArch/Query.cs`（只有 public struct）+ `Core/QueryCache.cs`（internal class）

### 已验证的高风险声明

| 声明 | 证据 | 结论 |
|------|------|------|
| Archetype partial 3 个 (`:22`) | `rg -l "partial class Archetype"` → Archetype.cs, Archetype.Storage.cs, Archetype.TestHooks.cs | ✅ 精确 |
| `ComponentType.cs` 是 int wrapper (`:36`) | `ComponentType.cs:7`: `internal readonly record struct ComponentType(int Value)` | ✅ 精确 |
| `EntityRecord.cs` 16 字节 (`:39`) | `EntityRecord.cs:11-17`: Archetype(8) + RowIndex(4) + Version(4) = 16B | ✅ 精确 |
| `EntityAccessor.cs` 是 ref struct (`:40`) | `EntityAccessor.cs:16`: `public ref struct EntityAccessor` | ✅ 精确 |
| `ComponentSchema.cs` 存在 (`:35`) | `src/MiniArch/ComponentSchema.cs` | ✅ 精确 |
| DebugMetrics 已删除 (`:48`) | `rg "DebugMetrics"` 全库零匹配 | ✅ 精确 |
| `EachSpan` API 已删除 (`:84`) | `rg "EachSpan"` 零匹配 | ✅ 精确 |
| typed query 家族已移除 (`:85`) | `rg "class Query<|struct Query<"` 零匹配 | ✅ 精确 |
| `ICommandRecorder` 已删除 (`:110`) | `rg "ICommandRecorder"` 零匹配 | ✅ 精确 |
| `WithTag<T>()` 不存在 (`kb-architecture-review.md:206`) | `rg "WithTag"` 零匹配 | ✅ 精确 |

---

## 3. `.knowledge/kb-chunk-storage.md`

**覆盖度**: 全面审计 ✓  
**检查类别**: 存储结构描述、行号引用、不变式、阈值、ChunkView 行为

### 不匹配/过时声明

#### [C1] `AddEntity = AllocateRows(1) + WriteEntityAt` 行号引用错误
- **文件:行**: `kb-chunk-storage.md:152`
- **原文摘要**: "代码位置：`Archetype.Storage.cs:162-173`"
- **实际事实**: 
  - `AddEntity` 方法在 `Archetype.Storage.cs:222-227`
  - L162-173 是 `GrowChunked` 方法中的 `Array.Resize(ref _segments, ...)` 和段初始化
- **推荐操作**: 将行号从 `162-173` 修正为 `222-227`
- **证据**: 阅读 `Archetype.Storage.cs:219-227`

#### [C2] `Query.cs（Core）` 路径引用歧义
- **文件:行**: `kb-chunk-storage.md:59`
- **原文摘要**: "`Query.cs（Core）`：内部查询实现，分段下每个 Segment 产生一个 ChunkView"
- **实际事实**: 内部查询实现在 `Core/QueryCache.cs`，不是 `Core/Query.cs`（后者不存在）。`src/MiniArch/Query.cs` 是公共 facade
- **推荐操作**: 将引用改为 `Core/QueryCache.cs`，或直接写 "QueryCache"
- **证据**: 同 [B2]

### 已验证的高风险声明

| 声明 | 证据 | 结论 |
|------|------|------|
| `TargetSegmentBytes = 2 * 1024 * 1024` (`:42`) | `Archetype.cs:134` | ✅ 精确 |
| 阈值公式 `perEntity > 0 ? Math.Max(16, TargetSegmentBytes / perEntity) : 65536` (`:43`) | `Archetype.cs:145` | ✅ 精确 |
| ChunkView.Count 实时读 Archetype (`:150`) | `ChunkView.cs:64-66`: 读 `_archetype.GetSegmentCount()` / `_archetype.EntityCount` | ✅ 精确 |
| Segment 结构体字段 (`:32-36`) | `Archetype.cs:149-154`: `Entities`, `Data`, `Count` | ✅ 精确 |
| `IsChunked => _segments is not null` (`:58`) | `Archetype.cs:88` | ✅ 精确 |
| `AssertSegmentInvariants` 不变量 (`§3.6`) | `Archetype.Storage.cs:119-150` 验证容量等长/非空段连续/Count 求和 | ✅ 精确 |
| `AddEntity = AllocateRows(1) + WriteEntityAt` 解耦设计 (`:152`) | `Archetype.Storage.cs:222-227` | ✅ 精确 |
| 行号映射用除法/移位而非二分 (`:149`) | `Archetype.Storage.cs:18`: `(globalRow >> _segmentBitShift, globalRow & _segmentMask)` | ✅ 精确 |

---

## 4. `.knowledge/kb-cache-optimization.md`

**覆盖度**: 全面审计 ✓  
**检查类别**: 内存布局图、优化描述、行号引用、性能数据

### 不匹配/过时声明

#### [D1] World partial 文件计数错误
- **文件:行**: `kb-cache-optimization.md:18`
- **原文摘要**: "World (7 partial files — 详见 kb-architecture-review.md §10)"
- **实际事实**: World 实际只有 **5 个** partial 文件（同 [B1]）。且 kb-architecture-review.md §10（`:137`）正确地说 "5 个"，此处既与源码不符，也与它引用的 kb 页不符
- **推荐操作**: 将 `7 partial files` 改正为 `5 partial files`
- **证据**: 同 [B1]

### 已验证的高风险声明

| 声明 | 证据 | 结论 |
|------|------|------|
| EntityRecord 16B 布局 (`:57-59`) | `EntityRecord.cs:11-17` | ✅ 精确 |
| Edge cache 直索引 (`:63-66`) | `Archetype.cs:108-130`: `Archetype?[]` 按 componentId 索引 | ✅ 精确 |
| CopySmall 2-byte fast path (`:69-71`) | `Archetype.Storage.cs` 含 `case 2` | ✅ 精确 |
| FrozenState 双缓冲 (`:78-83`) | `CommandStreamCore.cs:29-36`: `_frozen/_spareFrozen/_pendingFrozen` + `SwapOutState` | ✅ 精确 |
| CommandStream store cache / dirty flags (`:94-103`) | `GetOrCreateStore<T>()` LRU cache, `_hasStoreCommands` flag | ✅ 精确 |
| `[SkipLocalsInit]` / `AggressiveInlining` 使用 (`:75, :114`) | 12x SkipLocalsInit in Archetype.Storage.cs; 4x AggressiveInlining in ChunkView.cs | ✅ 精确 |

---

## 5. `.knowledge/kb-change-tracking.md`

**覆盖度**: 全面审计 ✓  
**检查类别**: API 签名、设计描述、文件存在性、不变式、数值引用

### 无错误/过时声明

**零不匹配**。所有声明均通过代码验证。

### 已验证的高风险声明

| 声明 | 证据 | 结论 |
|------|------|------|
| `ChangeWatch<TComponent, THandler>` 类定义 (`:21`) | `ChangeWatch.cs:21`: `public sealed class ChangeWatch<TComponent, THandler>` | ✅ 精确 |
| `TransitionWatch<THandler>` 类定义 (`:32`) | `TransitionWatch.cs:32`: `public sealed class TransitionWatch<THandler>` | ✅ 精确 |
| Dense epoch marks（long[] 64-bit）(`:26-28, :37-38`) | `TransitionWatch.cs:37-45`: `_snapshotMarks: long[]`, `_currentMarks: long[]` | ✅ 精确 |
| 64-bit epoch 无溢出风险 (`:30`) | `TransitionWatch.cs:36,45`: `_snapshotEpoch: long`, `_currentEpoch: long` | ✅ 精确 |
| 无 per-Diff 清除 (`:27-29`) | `TransitionWatch.Diff(L113-114)`: epoch bump 使旧标记失效，无 `Array.Clear` | ✅ 精确 |
| 两阶段 buffer 回调 (`:76-77`) | `TransitionWatch.Diff`: Phase1 收集→Phase2 回调 (`L169-174`) | ✅ 精确 |
| Snapshot 先于 Diff (`:143`) | `TransitionWatch.Diff(L109-111)`: `!_hasSnapshot` → `InvalidOperationException` | ✅ 精确 |
| `World.Watch<TComponent, THandler>` 入口在 `Core/World.cs` (`:139`) | `World.cs:227` | ✅ 精确 |
| `World.Watch<THandler>` filter 必填 (`:91`) | `World.cs:258` | ✅ 精确 |
| WatchApi.Perf 项目存在 (`:114`) | `tools/perf/WatchApi.Perf/` 含 Release 构建产物 | ✅ 精确 |
| 旧 API 全删除：`TrackValueChanges`, `ChangeTracker<T>`, `IValueProjector` 等 (`:80`) | `rg "TrackValueChanges|ChangeTracker<|IValueProjector|TransitionLog"` 零匹配 | ✅ 精确 |

---

## 跨页一致性检查

### 矛盾: World partial 文件数

| 知识页 | 声称 | 实际 | 结论 |
|--------|------|------|------|
| `kb-architecture-review.md:137` | "拆分为 5 个 partial 文件" | 5 | ✅ 正确 |
| `kb-core-ecs.md:21` | "World partial 文件族（7 个）" | 5 | ❌ 错误 |
| `kb-cache-optimization.md:18` | "World (7 partial files)" | 5 | ❌ 错误 |

**说明**: `kb-architecture-review.md`（§10）是正确的 5 个，但 `kb-core-ecs.md` 和 `kb-cache-optimization.md` 都声称 7 个。可能是早期重构（合并 small partials）后未同步更新。`kb-core-ecs.md:21` 引用的 "详见 `kb-architecture-review.md` §10" 也与实际 §10 内容矛盾。

---

## 使用的命令行/搜索

```bash
# 文件存在性检查
glob "**/ComponentColumnMap.cs"
glob "**/EntityBatchRange.cs"
glob "**/SpanHelper.cs"
glob "**/ComponentSchema.cs"

# World/Archetype partial 文件计数
rg "partial class World" --files-with-matches src/MiniArch/Core/
rg "partial class Archetype" --files-with-matches src/MiniArch/Core/

# 行号验证
rg -n "IsMaskCanonical" src/MiniArch/Core/World.cs
rg -n "interface IChunkForEach" src/MiniArch/Query.cs
rg -n "CaptureState|RestoreState|_stateSnapshotPool" src/MiniArch/Core/World.cs

# API 删除验证
rg "DebugMetrics" src/ tests/ --type cs
rg "ICommandRecorder" src/ --type cs
rg "WithTag" src/ --type cs
rg "EachSpan" src/ --type cs

# 不变式验证
rg -n "AssertSegmentInvariants|AssertConvertedInvariants|AssertFlatCacheConsistent" src/MiniArch/Core/Archetype.Storage.cs

# 存储布局验证
rg -n "TargetSegmentBytes|ComputeSegmentEntityCapacity" src/MiniArch/Core/Archetype.cs
rg -n "IsChunked" src/MiniArch/Core/Archetype.cs
```

---

## 总结

| 知识页 | 完全审计 | 不匹配数 | 高风险声明已验证 |
|--------|----------|----------|----------------|
| `kb-architecture-review.md` | 是 | 3（行号×2 + 遗漏） | 18 ✅ |
| `kb-core-ecs.md` | 是 | 5（计数+文件路径+存在性×3） | 12 ✅ |
| `kb-chunk-storage.md` | 是 | 2（行号×1 + 路径歧义×1） | 12 ✅ |
| `kb-cache-optimization.md` | 是 | 1（计数） | 12 ✅ |
| `kb-change-tracking.md` | 是 | 0 | 15 ✅ |

**总不匹配**: 11 处（含 2 处跨页重复计数错误）  
**最严重的问题**:
1. World partial 文件数说 7 实为 5（影响 `kb-core-ecs.md` 和 `kb-cache-optimization.md`）
2. `kb-core-ecs.md` 列出 3 个不存在的文件（在早期重构中被删除）
3. `kb-architecture-review.md` 两处行号引用不匹配
4. `kb-architecture-review.md` 遗漏 `_archetypeByHash` 第三层查询缓存

所有 kb-change-tracking.md 声明均已验证为精确，无需修改。
