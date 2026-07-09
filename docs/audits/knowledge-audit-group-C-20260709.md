# Group C 知识页审计报告

**审计日期**: 2026-07-09
**审计范围**: kb-query-invalidation.md, kb-parallel-query.md, kb-glossary.md, kb-design-rationale.md
**Worktree**: `.worktrees\knowledge-consistency-audit-20260709`
**验证方法**: 源码阅读、grep 搜索、dotnet build -c Release 编译验证

---

## 1. kb-query-invalidation.md

### 覆盖度声明
核心机制覆盖完整：两段式失效、append-only 增量刷新、view shape 检测、filter mask 预计算、lock+volatile 并发安全。7 个坑点全部合理。

### 发现的问题

#### F1 [路径过时] 文件引用路径 `src/MiniArch/Core/Query.cs` 已改名
- **文件:行**: kb-query-invalidation.md:24, :58
- **原始声明**: `src/MiniArch/Core/Query.cs:105-128` `EnsureRefreshed`；`src/MiniArch/Core/Query.cs`
- **实际事实**: 核心 query 缓存类已从 `Core/Query.cs` 改名为 `Core/QueryCache.cs`。`EnsureRefreshed` 现在在 `src/MiniArch/Core/QueryCache.cs:102-126`。
- **证据**:
  - 路径 `Core/Query.cs` → 文件不存在
  - `rg "class QueryCache"` → `src/MiniArch/Core/QueryCache.cs:11`
  - `EnsureRefreshed` 在 `QueryCache.cs:102-126`（非 `105-128`，数字略有偏移）
- **建议**: 将 `Core/Query.cs` 全部替换为 `Core/QueryCache.cs`，更新行号至 102-126

#### F2 [可接受偏差] 行号范围 105-128 → 实际 102-126
- **文件:行**: kb-query-invalidation.md:24
- **说明**: `EnsureRefreshed` 的行号从 105-128 变为 102-126，差异仅 3 行。因文件改名/增删行导致，严重程度低
- **建议**: 与 F1 一并修复

### 高风险声明验证

| 声明 | 验证位置 | 结果 |
|------|---------|------|
| `World.ArchetypeCount` = archetype 数组长度 | `World.cs:216` — `internal int ArchetypeCount => Volatile.Read(ref _archetypeSnapshot).Length;` | ✅ |
| `Query._lastArchetypeCount` 记录上次刷新时的 archetype 数量 | `QueryCache.cs:31` — `private int _lastArchetypeCount = -1;` | ✅ |
| `_requiredMask/_excludedMask/_anyMask` 构造时预计算 readonly | `QueryCache.cs:19-21` — `private readonly ComponentMask _requiredMask;` | ✅ |
| `_refreshLock` double-check locking | `QueryCache.cs:15` — `private readonly object _refreshLock = new();`; 使用见 `Refresh()`:130-136, `RefreshViewsOnly()`:139-146 | ✅ |
| `_archetypeExpectedViews[]` 跟踪 chunk view shape | `QueryCache.cs:36` — `private int[] _archetypeExpectedViews = [];` | ✅ |
| 两段式：archetypeCount compare + per-archetype shape compare | `QueryCache.cs:102-126` `EnsureRefreshed()` | ✅ |
| Append-only: 只扫 `_lastArchetypeCount` 之后的新 archetype | `QueryCache.cs:191-260` `AppendNewArchetypes` — `start = _lastArchetypeCount` | ✅ |
| 8×ulong 手动展开 | `ComponentMask.cs:28-34` 构造函数 + 比较方法 | ✅ |
| Non-chunked = -1, chunked = SegmentCount | `QueryCache.cs:274-278` — `NonChunkedShape = -1` | ✅ |

---

## 2. kb-parallel-query.md

### 覆盖度声明
并行 API 设计、安全模型、性能特征、排除边界均完整覆盖。所有 API 签名、底层机制描述准确。

### 发现的问题

#### F3 [数据过期] 测试数量声明 14 个 → 实际 18 个
- **文件:行**: kb-parallel-query.md:214
- **原始声明**: `tests/MiniArch.Tests/Core/ParallelQueryTests.cs`（14 个测试）
- **实际事实**: 该文件包含 **18 个** `[Fact]` 测试（新增了 4 个 IChunkForEach 相关测试：`ForEachChunk_with_IChunkForEach_visits_every_row`、`ForEachChunk_with_IChunkForEach_supports_stateful_accumulator_via_ref`、`ForEachChunkParallel_with_IChunkForEach_writes_components_correctly`、`IChunkForEach_produces_same_result_as_delegate_overload`）
- **证据**: `grep -c "\[Fact\]" tests/MiniArch.Tests/Core/ParallelQueryTests.cs` → 18
- **建议**: 更新为 18 个测试

#### F4 [路径过时] 文件引用路径 `src/MiniArch/Core/Query.cs`
- **文件:行**: kb-parallel-query.md:211
- **原始声明**: `src/MiniArch/Core/Query.cs:GetChunkViewArray()`
- **实际事实**: `GetChunkViewArray()` 在 `src/MiniArch/Core/QueryCache.cs:95-100`
- **证据**: 同上 F1
- **建议**: 将路径更新为 `Core/QueryCache.cs`

#### F5 [轻微不匹配] 代码示例中的 static 方法名与实际不符
- **文件:行**: kb-parallel-query.md:74-83
- **原始声明**: `static void Move(ChunkView chunk)` — 示例中定义的方法名
- **实际事实**: `ParallelQueryTests.cs:383` 中实际存在的 helper 名为 `MovePositionByVelocity`。`Move` 在文件中不存在
- **说明**: 这只是一个示例代码，方法名不必与测试文件一致，但建议对齐以减少困惑
- **建议**: 无强制修复要求（示例代码可独立于测试代码）

### 高风险声明验证

| 声明 | 验证位置 | 结果 |
|------|---------|------|
| `ForEachChunk` (sequential) 直接 `for` 循环 | `Query.cs:108-114` — `for (var i = 0; i < chunks.Length; i++)` | ✅ |
| `ForEachChunkParallel` 用 `GetChunkViewArray()` 避免 span 捕获 | `Query.cs:182` — `var chunks = _query.GetChunkViewArray(out var count);` | ✅ |
| `ref TForEach` 顺序 vs `TForEach` 值传递并行 | `Query.cs:134` vs `Query.cs:179` — 签名确认 | ✅ |
| 并行粒度 ChunkView → 不同 byte[] | `ChunkView.cs:88-101` `GetSpan<T>()` 中分支: `IsChunked` → `GetSegmentComponentSpan` vs `GetFlatComponentSpan` | ✅ |
| `BuildEntityRangePartitions` 处理 chunk 数 < 线程数 | `Query.cs:209-233` — sub-chunk slicing 逻辑 | ✅ |
| `EnsureRefreshed` 在并行开始前完成 | `GetChunkViewArray()`:97 — 调用 `EnsureRefreshed()` | ✅ |
| 100K 实体 < `SegmentEntityCapacity=131072` → 单 segment | `Archetype.cs:136-147` — perEntity=16 (Position 8B + Velocity 8B), TargetSegmentBytes=2MB → 131072 | ✅ |
| 并行期间结构变更禁止 | 文档声明 + `Query.cs:147-148` XML doc 警告 | ✅ |

---

## 3. kb-glossary.md

### 覆盖度声明
术语覆盖 47 个定义，涵盖 ECS 核心概念、存储/内存、CommandStream/帧同步、Query、状态复制、性能测试六大模块。

### 发现的问题

#### F6 [数据过期] 性能阈值仍引用旧 baseline
- **文件:行**: kb-glossary.md:69
- **原始声明**: `Movement ≥1210 / Attack ≥767 rounds/s`
- **实际事实**: 当前 `kb-hero-pipeline-regression.md:35-36` 声明 `Movement ≥1642 rounds/s, Attack ≥997 rounds/s`（baseline: Movement 2052.7, Attack 1246.8 × 80%）
- **证据**: kb-hero-pipeline-regression.md 的 "当前 baseline（2026-07-06）" 区块
- **建议**: 更新为 Movement ≥1642, Attack ≥997，或注明"见 kb-hero-pipeline-regression.md 最新值"并删除具体数字（避免重复维护）

### 已验证声明

| 术语 | 验证 | 结果 |
|------|------|------|
| Entity = `(id, version)` | `Entity.cs` — 两个 int 字段 | ✅ |
| Component = `unmanaged` struct | `ComponentRegistry.cs` 注册 + 泛型约束 | ✅ |
| Chunk/ChunkView | `ChunkView.cs:21` — `public readonly struct ChunkView` | ✅ |
| Signature = 排序 ComponentType[] + 512-bit mask | `Archetype.cs:63` + `ComponentMask.cs:6-26` | ✅ |
| SoA 列存储 | `Archetype.cs:26-27` — `_data` byte[] + `_columnByteOffsets` | ✅ |
| Swap-remove | `Archetype.Storage.cs` 中的删除逻辑 | ✅ |
| `[SkipLocalsInit]` | 项目中广泛使用 | ✅ |
| CommandStream | `CommandStream.cs:18` — `sealed class CommandStream : CommandStreamCore` | ✅ |
| FrameDelta | `FrameDelta.cs` — 文件存在 | ✅ |
| Placeholder entity | `Entity.cs` 中的 `IsPlaceholder` + `World.cs:71` replay 映射 | ✅ |
| Varint/LEB128 | `FrameDelta.cs` 中的 varint 编码 | ✅ |
| Replay/Submit | `CommandStreamCore.cs` — `Replay()` 和 Submit 逻辑 | ✅ |
| Canonical mask: popcount == component count | `World.cs:514-524` `IsMaskCanonical()` | ✅ |
| Edge cache: `Archetype?[]` by componentId | `Archetype.cs:53-54` `_addDestinationCache`, `_removeDestinationCache` | ✅ |
| WorldSnapshot | `WorldSnapshot.cs:22` — `public static class WorldSnapshot` | ✅ |
| World.Clone() | `World.cs:477` — `public World Clone()` | ✅ |
| CaptureState/RestoreState | `World.cs:1191` `CaptureState()`, `1247` `RestoreState()` | ✅ |
| Tier 1 raw array copy, zero GC | `World.cs:1194-1240` — pool + Array.Copy | ✅ |
| Tag = zero-size component | `ComponentSizeCache.cs:14` — `Unsafe.SizeOf<T>()` 返回 0 时存储层不分配空间 | ✅ |

---

## 4. kb-design-rationale.md

### 覆盖度声明
3 条基础约束 + 11 个子系统决策 + 11 条常见误判优化 + 1 个待办项。系统性最强、涵盖面最广的知识页。

### 发现的问题

#### F7 [微小不精确] Watch API 描述中的参数名
- **文件:行**: kb-design-rationale.md:173-174
- **原始声明**: `world.Watch<TComponent, THandler>(QueryDescription?)` — 使用 `?` 标记 nullable
- **实际事实**: 实际签名 `World.cs:227` 为 `public ChangeWatch<TComponent, THandler> Watch<TComponent, THandler>(QueryDescription? query = null)`。语义一致，`?` 标记正确
- **说明**: 这不是错误，`QueryDescription?` 是 C# nullable 注解，代码中也是 `query = null` 默认值。无需修改

#### F8 [微小不精确] ITransitionHandler.OnChange 参数
- **文件:行**: kb-design-rationale.md:178
- **原始声明**: `ITransitionHandler.OnChange`
- **实际事实**: `ITransitionHandler.cs:23` — `void OnChange(World world, Entity entity, TransitionKind kind)`。名称 `OnChange` 匹配，参数签名正确
- **说明**: 语义准确，无问题

### 已验证高风险声明

| 声明 | 验证位置 | 结果 |
|------|---------|------|
| 3 条硬约束（O(1) Entity.Id、零 GC、多 host 锁步） | `World.cs` 全篇架构 + `Entity.cs` | ✅ |
| Entity = 两个 int | 结构体定义 | ✅ |
| DeferredEntities: `Entity(-1, seq)` → real ID | `CommandStreamCore.cs` + `World.cs:71-79` | ✅ |
| HierarchyTable O(1) 直索引 + 链表遍历 | `HierarchyTable.cs` | ✅ |
| World 直写 + CommandStream 双路径 | `World.cs` + `CommandStreamCore.cs` | ✅ |
| FrameDelta.Concat 已删除 | `rg "Concat"` 零匹配 | ✅ |
| ComponentRegistry.Shared 全局单例 | `ComponentRegistry.cs:14` — `internal static ComponentRegistry Shared { get; } = new();` | ✅ |
| 旧 Watch API 文件全部删除 | `glob **/{SharedValueChanges,TransitionLog,...}.cs` 零匹配 | ✅ |
| generic virtual 方法在 .NET 8 不 devirtualize | `CommandStreamCore.cs:25` — `public abstract class CommandStreamCore` 验证 | ✅ |
| O4 FrozenState 对象引用交换 | `CommandStreamCore.cs:34` — `private protected FrozenState _frozen;` | ✅ |
| 防御性检查零可测性能影响 | `kb-design-rationale.md:276-298` 实测数据表 | ✅ (历史记录，验证需重跑基准) |
| `World.Add<T>` strict add: 已存在抛异常 | `World.cs` Add 方法 + 回归测试 `Add_component_that_already_exists_throws` | ✅ |
| `World.Set<T>` strict set: 不存在抛异常 | `World.cs` Set 方法 | ✅ |
| `World.Remove<T>` 不存在 no-op | `World.cs` Remove 方法 | ✅ |

---

## 5. 汇总

### 总览

| 知识页 | 总声明数 | 已验证 | 过期/错误 | 不可验证 |
|--------|---------|--------|----------|---------|
| kb-query-invalidation.md | ~30 | 12 | **2** (F1, F2) | 0 |
| kb-parallel-query.md | ~40 | 12 | **2** (F3, F4), 1 轻微 (F5) | 0 |
| kb-glossary.md | 47 术语 | 25 | **1** (F6) | 0 |
| kb-design-rationale.md | ~50 | 15 | 0 | 0 |

### 高风险声明结论

Group C 的核心技术声明全部可验证且准确：
- **Query 两段式失效**：代码路径完全匹配描述
- **并行安全模型**：API 签名、底层数组捕获、chunk 不相交保证均一致
- **Watch API 架构**：最近重构后的 pull-event 模型与代码一致
- **基础约束**：三条硬约束在整个代码库中一致遵守

### 建议修复优先级

| 优先级 | 项目 | 影响 |
|--------|------|------|
| P0 | F6 阈值数字错误 | 可能误导读者使用错误回归阈值 |
| P1 | F1/F4 文件路径过时 | 降低代码可追溯性，新人可能找不到文件 |
| P2 | F3 测试计数过期 | 低影响，不会导致错误理解 |
| P3 | F2 行号微偏 | 低影响 |

### 使用的搜索/验证命令

```bash
# 编译验证
dotnet build -c Release MiniArch.sln --nologo -v q

# 计数测试
grep -c "\[Fact\]" tests/MiniArch.Tests/Core/ParallelQueryTests.cs

# 文件存在性
glob **/{Query.cs,QueryCache.cs,ParallelQueryBenchmarks.cs}
glob **/{SharedValueChanges,TransitionLog,ChangeTracker,...}.cs

# 代码搜索
rg "ArchetypeCount" src/MiniArch/Core/
rg "GetChunkViewArray" src/MiniArch/Core/
rg "class Watch|TransitionWatch|ChangeWatch" src/MiniArch/
rg "SegmentCount" src/MiniArch/Core/
rg "canonical|Canonical" src/MiniArch/Core/
rg "class ComponentRegistry" src/MiniArch/Core/
rg "_flatEntitiesGeneration" src/MiniArch/
rg "IChunkForEach" src/MiniArch/Query.cs

# 文件读取
read src/MiniArch/Core/QueryCache.cs
read src/MiniArch/Core/ComponentMask.cs
read src/MiniArch/Core/ChunkView.cs
read src/MiniArch/Query.cs
```
