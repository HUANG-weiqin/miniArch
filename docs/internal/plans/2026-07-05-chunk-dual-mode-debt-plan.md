# Chunk 存储 dual-mode 结构债偿还执行计划

**起草日期**: 2026-07-05
**目标仓库**: `miniArch`（主仓）
**总体目标**: 消除 `Archetype` dual-mode（flat / chunked）的所有 caller-side dual-mode 知识，把不变式收敛到 `Archetype.Storage.cs` / `ChunkView.cs` / `QueryCache.cs` 三处内部，让后续新增跨 archetype 操作 / 单点访问 / snapshot 路径不再需要写 `if (!archetype.IsChunked)` 二分。
**不在范围**: 不改对外 public API 形状（`Query.ForEachChunk` / `GetChunks` / `ChunkView` 全部不变）；不做 small-chunk / always-chunked PoC（需另起 benchmark-first 决策）；不做 `_flatEntitiesGeneration` token 化（blast radius 大且不真正消除纪律）。

---

## 0. 前置准备（开工前 0.5 天）

### 0.1 通读约束
1. 读 `AGENTS.md` 全文，特别注意：
   - **§5 架构变更回归门禁**：任何 `src/MiniArch/` 改动提交前必须跑
     ```powershell
     dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
     ```
     阈值：Movement ≥1210 rounds/s, Attack ≥767 rounds/s，内存不增长，不崩溃。低于阈值 → `git stash` 回退。
   - **§5a 确定性豁免**：本计划所有 Task **都不符合豁免条件**（不是纯文档/死代码/partial 迁移/重命名 private/空白），**全部要跑门禁**。
2. 读 `.knowledge/INDEX.md` 和与本计划相关：
   - `.knowledge/kb-chunk-storage.md`（dual-mode 现状、不变式）
   - `.knowledge/kb-architecture-review.md` §2（存储架构）
   - `.knowledge/kb-ecs-comparison.md`（性能 baseline——保护 `+70~74% vs Arch` 不能回吐）
   - `.knowledge/kb-code-review-findings.md:45`（同族 bug 历史）
3. 读 `.knowledge/kb-hero-pipeline-regression.md`，确认当前 baseline 数字。

### 0.2 分支与基线
1. 从 main 拉新分支：`git checkout -b refactor/chunk-dual-mode-debt`。
2. 跑一次门禁记录改动前 baseline（不改 baseline 文件，只看 stdout 数字），存档作为本分支"零点"。
3. **额外 baseline**：跑 `dotnet run -c Release --project tools/perf/GameTickSim.Perf`，记录 K-BulletHell / E-MixedFullTick 等典型场景 vs Arch 的对比数字。本计划保护这些数字不退化。
4. **每个 Task 独立 commit**，commit message 模板：`refactor(chunk): <what> (#TaskN)`。失败时单 Task `git reset` 回退。

### 0.3 验收的硬标准（终极 DoD）

完成所有 Task 后，全文 grep（**只针对 dual-mode 知识，不含 ChunkView 的私有字段**）：
```powershell
rg -n "archetype\.IsChunked|arch\.IsChunked|archetype\.SegmentCount|arch\.SegmentCount" src/ tests/ tools/ samples/
```

**允许的命中位置**（白名单，附理由）：

| 文件 | 哪部分 | 理由 |
|---|---|---|
| `src/MiniArch/Core/Archetype.Storage.cs` | 全文 | dual-mode owner，理所应当 |
| `src/MiniArch/Core/Archetype.TestHooks.cs` | `ForceChunkedForTesting` | 测试工具自有合理 |
| `src/MiniArch/Core/ChunkView.cs` | 全文 | 公共 chunk 抽象的合理住所，按设计就是要在内部二分 |
| `src/MiniArch/Core/QueryCache.cs` | 构造 ChunkView 处 | 物理：必须知道一个 archetype 产几个 ChunkView |
| `src/MiniArch/Core/World.EntityLifecycle.cs:282` | `WriteCreatedEntitiesAndLocations` 的 flat-fast-path dispatch | 显式批写 fast path，详见 Task 2 / Appendix C "不做的事"——是性能取舍不是债 |
| `src/MiniArch/Core/World.cs:1054` 及附近 | `CaptureState` 给 `ArchetypeBackupEntry` 分发到 `CopyFromNonChunked/CopyFromChunked` | snapshot 是 byte 流路径，类 B（结构性写入），dual-mode 知识住所合理；与 §2 同理 |
| `src/MiniArch/Core/WorldStateSnapshot.cs` | `CopyFromChunked`、`RestoreTo` 等内部对 `arch.SegmentCount` 等的读取 | snapshot encoding/decoding 必须知道 segment 数量做 allocate/复制/校验；类 B 内部职责 |
| `tests/**` | `Assert.True/False(archetype.IsChunked)` 形式的断言 | 测试**应该**验证 storage mode 行为——这是验证 invariant 不是债 |
| `tests/**` | fuzz 测试里的 `while (!archetype.IsChunked) { 加 entity }` 强制晋升 | 测试构造场景，合理 |

**不允许的命中**（= 债未还干净）：
- 新加的"批量读取遍历"路径在 `Query.cs` / `World*.cs` 里走 `if (!archType.IsChunked)` 二分（应走 `AsChunkViews()`）—— 具体：`Query.cs:611` 修完应消失
- 任何 production 控制流路径在 `CommandStream.cs` / `WorldSnapshot.cs` / `tools/` / `samples/` 内做 `IsChunked` 判断

**确认形式**：grep 后人工审一遍每个命中点，确认在白名单内或可解释；不在白名单内的命中点必须在对应 Task 里 fix 或显式说明理由后增到 allowlist。**不靠纯 grep 二值判定**——验收时人 + grep 一起看。

### 0.4 任务门禁矩阵

| Task | 改 src/MiniArch? | 触发 §5 门禁? | 备注 |
|---|---|---|---|
| 1 DEBUG invariant 固化 | 是 | **是**（DEBUG IL 变化仍要跑确认 Release 不退化） | 0 运行时改动，但 Conditional DEBUG 在 Release 编译下完全消失，门禁应无差异 |
| 2 删 ThrowIfChunked API 家族 | 是 | **是** | 改 caller 9+ 处，热路径 `OrderedComponentEnumerator.Initialize`，重点观察 |
| 3 layout-agnostic 拷贝原语 | 是 | **是** | 3 个 copy 函数重构，结构变更热路径，重点观察 |
| 4 集中 cache invalidation | 是 | **是** | `_flatEntitiesGeneration` 重构 |
| 5 跨模式 snapshot 断言 | 是 | **是**（DEBUG 路径，Release 应无差异） | 加 Debug.Assert，确认 Release 不退化 |

---

## Phase 1 — DEBUG invariant 固化（前置安全网，~1.5 人日，Task 1）

> 目的：在动任何运行时代码前先建一个"违反 invariant 立刻崩"的安全网。后续 4 个 Task 任何 refactor 后跑 DebugAlwaysValid 即知有没破。

### Task 1 — 集中 DEBUG invariant 断言

**Why**: 当前 6 个 segment shape invariant 全靠隐式维持：`_count == Σ seg.Count`、除末段全满、`_columnByteOffsets` 关联 `_segmentCapacity`、`_segmentIndex = -1` sentinel 等。后续 Task 2-5 重构会触发各种 mutation，没有集中 assert 就静默 corrupt。前置固化是后续 4 项的安全网。

**Files**:
- `src/MiniArch/Core/Archetype.Storage.cs`（加 `[Conditional("DEBUG")] AssertSegmentInvariants()` 方法，在一些关键 mutation 末尾调用）
- `src/MiniArch/Core/ChunkView.cs`（命名常量 `NonChunkedSegmentIndex = -1` 替换字面量 + DEBUG 有效性断言）

**Steps**:

1. 在 `Archetype.Storage.cs` 加（不在 Release 编译）：
   ```csharp
   [Conditional("DEBUG")]
   private void AssertSegmentInvariants()
   {
       if (!IsChunked) return;
       var total = 0;
       for (var i = 0; i < _segmentCount; i++)
       {
           ref var seg = ref _segments[i];
           Debug.Assert(seg.Entities.Length == _segmentCapacity,
               $"Segment {i} entity capacity ({seg.Entities.Length}) != _segmentCapacity ({_segmentCapacity}).");
           // 除末段外应满；末段可为 0..=_segmentCapacity
           if (i < _segmentCount - 1)
               Debug.Assert(seg.Count == _segmentCapacity,
                   $"Non-last segment {i} count ({seg.Count}) != _segmentCapacity ({_segmentCapacity}).");
           Debug.Assert((uint)seg.Count <= (uint)seg.Entities.Length,
               $"Segment {i} count ({seg.Count}) exceeds capacity ({seg.Entities.Length}).");
           total += seg.Count;
       }
       Debug.Assert(total == _count,
           $"Sum of segment counts ({total}) != _count ({_count}).");
   }
   ```
2. 在以下 mutation 方法**末尾**调用 `AssertSegmentInvariants()`：
   - `ConvertToChunked`（已有 `AssertConvertedInvariants`，可整合或保留两者）
   - `GrowChunked`
   - `AllocateRows`（chunked 分支末尾）
   - `WriteEntityAt`（chunked 分支末尾——但这是热路径，`[MethodImpl(AggressiveInlining)]` 下 assert 会被 Conditional 吃掉，安全）
   - `RemoveAt`（chunked 分支两个 return 点）
   - `RestoreFlatBackup`（chunked 分支末尾）
3. 在 `ChunkView.cs` 顶部加：
   ```csharp
   private const int NonChunkedSegmentIndex = -1;
   ```
   把 `_segmentIndex` 初始化、构造函数里的 `-1` 字面量替换为 `NonChunkedSegmentIndex`。
4. 在 `ChunkView` 构造函数加 `[Conditional("DEBUG")] AssertValid()`：
   ```csharp
   [Conditional("DEBUG")]
   private void AssertValid()
   {
       Debug.Assert(_archetype is not null);
       if (_segmentIndex >= 0)
           Debug.Assert(_archetype.IsChunked, "Positive segment index requires chunked archetype.");
       else
           Debug.Assert(_segmentIndex == NonChunkedSegmentIndex);
       Debug.Assert(_rowCount >= -1);
       Debug.Assert(_startRow >= 0);
   }
   ```
   两个构造函数末尾调一次。
5. 加 flat-cache 内容校验增强：扩展现有 `AssertFlatCacheConsistent`，在 generation 匹配时抽查前 N 个 entity id 与 `_segments[0].Entities[0..min(N,_segmentCapacity)]` 一致（N=32，固定成本）。
6. `dotnet build -c Debug` + `dotnet test`（Debug 模式激活所有新 assert，全测试通过证明不破坏现有不变式）。
7. `dotnet build -c Release` + `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`（确认 Release 模式下 Conditional 消失，性能不退化）。

**Definition of Done (DoD)**:
- [ ] `AssertSegmentInvariants` / `AssertValid` 添加并挂在所有 mutation / 构造路径
- [ ] `NonChunkedSegmentIndex` 常量替换所有 `-1` 字面量
- [ ] `AssertFlatCacheConsistent` 增强内容抽查
- [ ] `dotnet test` Debug 模式全绿（无 assert 触发）
- [ ] `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` 通过，与零点 baseline 差异在 ±2% 噪声内
- [ ] `dotnet run -c Release --project tools/perf/GameTickSim.Perf` 通过，K-BulletHell / E-MixedFullTick vs Arch 对比数字不退化

---

## Phase 2 — 删撒谎 API + caller 走 ChunkView（核心，~3 人日，Task 2）

> 目的：消除 caller-side dual-mode 知识。所有"批量读取遍历" caller 走 ChunkView 单一路径，dual-mode 知识收敛在 ChunkView 内部。

### Task 2 — 删 ThrowIfChunked API 家族 + caller 改造

**Why**: `GetComponentRef<T>` / `GetComponentSpanAt<T>` / `GetComponentSpan<T>` / `GetReservedEntities` 假装通用，实际 chunked 模式下 `throw InvalidOperationException`。已被同族 bug `BUG_order_by_component_supports_chunked_archetypes`（`kb-code-review-findings.md:45`）咬过一次，合同靠 runtime throw 不靠 static，必再发生。

**Files** (要改的 caller，从 §0.3 grep 结果收集):
- `src/MiniArch/Core/Archetype.Storage.cs`（重命名 + assert）
- `src/MiniArch/Core/ChunkView.cs`（flat 分支不再调撒谎 API，改为内部直接访问）
- `src/MiniArch/Query.cs`（`OrderedComponentEnumerator.Initialize` 的 `if (!archetype.IsChunked)` 二分要消失）
- `src/MiniArch/Core/World.EntityLifecycle.cs`（`WriteCreatedEntitiesAndLocationsFlat` + `GetReservedEntities` 一是删 call，要么改用 `WriteEntityAt` 统一路径）
- `src/MiniArch/Core/World.cs:1054`（`if (!arch.IsChunked)` 在 `CaptureState` 分发到 `CopyFromNonChunked` / `CopyFromChunked`——这条**例外保留**：这是 WorldStateSnapshot 内部分发，`Archetype.IsChunked` 由 `WorldStateSnapshot` 知道是合理住所内的；但要确认在 §0.3 grep 是否预期 exception 列表中——本 plan 把 `WorldStateSnapshot` 列为允许出现 IsChunked 的 4 个文件之一）
- `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/ThroughputRunner.cs`（bench harness 不能走撒谎 API，改为 ChunkView）
- `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/QueryProfilingRunner.cs`（同上）
- `tests/MiniArch.Benchmarks/QueryBenchmarks.cs`（同上）
- `tests/MiniArch.Tests/Core/ArchetypeTests.cs` / `ChunkTests.cs` / `QueryTests.cs` / `WorldLifecycleTests.cs`（单元测试可保留 flat 路径，要么走 ChunkView，要么用新命名的 `GetFlat*`）

**Steps**:

1. **重构 Archetype 内部撒谎 API**：
   - `GetComponentRef<T>(colIdx)` → 重命名 `GetFlatComponentRef<T>(colIdx)` + 加 `Debug.Assert(!IsChunked)`，移除 `ThrowIfChunked`
   - `GetComponentSpanAt<T>(colIdx)` → `GetFlatComponentSpanAt<T>(colIdx)` + assert
   - `GetComponentSpan<T>(component)` → `GetFlatComponentSpan<T>(component)` + assert
   - `GetReservedEntities(start, count)` → `GetFlatReservedEntities(start, count)` + assert
   - 保留 `GetComponentRefAt<T>(col, row)` 双模式不变（带 row 参数，物理 chunked 安全）
   - 删除 `ThrowIfChunked` 方法（被 assert 替代后无用）

   **Release 下的诚实度变化**（需在 commit message 说明）：原 `ThrowIfChunked` 在 Release 也 `throw InvalidOperationException`；新 `Debug.Assert(!IsChunked)` 在 Release 被 Conditional 吃掉——调用方在 chunked archetype 上误调 `GetFlat*` 时，Release 会经 `MemoryMarshal.GetArrayDataReference(_data)`（`_data` 在 chunked 后为 `null!`）→ **NullReferenceException** 而非 `InvalidOperationException`。仍 loud 但 message 不友好。判断这个可接受的理由：
   - 所有 production caller 改完后**不应再调** `GetFlat*` on chunked archetype——这条路径只在 DEBUG 测试时被 assert 抓
   - 上线 Debug 编译 vs 上线 Release 编译两种 production 配置之一发生时，C# 行业做法是 "tests run in Debug"，所以 DEBUG assert 在 CI 阶段就 grab 此种误用
   - 保留 `throw` 也行，但与 plan "更安全健壮" 形态不符——assert 是更诚实的选择，plan 必须在不丧失 loudness 的前提下接受 message 退化

   如果团队觉得 NRE-not-nice-message 不可接受，回退方案：保留 `ThrowIfChunked` 内的 throw + 加 `Debug.Assert(!IsChunked)` 双保险。Task executor 与 plan owner 沟通后选其一。

2. **重构 `ChunkView.cs` 内部**：flat 分支不再通过撒谎 API，改为直接访问 Archetype 的内部存储：
   ```csharp
   public Span<T> GetSpan<T>() where T : unmanaged
   {
       var colIdx = _archetype.GetComponentIndexFast(Component<T>.ComponentType);
       Span<T> full = _archetype.IsChunked
           ? _archetype.GetSegmentComponentSpan<T>(_segmentIndex, colIdx)
           : _archetype.GetFlatComponentSpan<T>(colIdx);   // flat-only，命名诚实
       return _rowCount >= 0 ? full.Slice(_startRow, _rowCount) : full;
   }
   ```
   同样改 `GetComponentSpanAt<T>` 和 `GetEntities` 的 flat 分支。ChunkView 自己用 `IsChunked` 是合理住所内的，不违反 §0.3。

3. **`OrderedComponentEnumerator.Initialize` (`Query.cs:569-635`)** 重写——消除 `if (!archetype.IsChunked)` 二分：
   ```csharp
   for (var i = 0; i < archetypeCount; i++)
   {
       var archetype = archetypes[i];
       var rowCount = archetype.EntityCount;
       if (rowCount == 0) continue;

       var entitySpan = archetype.GetEntityStorageUnsafe();  // 已是 dual-mode 透明
       entitySpan.AsSpan(0, rowCount).CopyTo(entities.AsSpan(index));

       // 唯一路径走 ChunkView：需要 Archetype 暴露 AsChunkViews() 内部 helper
       var columnIndex = archetype.GetComponentIndex(componentType);
       var valueIndex = index;
       foreach (var chunk in archetype.AsChunkViews())
       {
           var componentSpan = chunk.GetComponentSpanAt<T>(columnIndex);
           componentSpan.CopyTo(values.AsSpan(valueIndex));
           valueIndex += componentSpan.Length;
       }

       index += rowCount;
   }
   ```
   需在 Archetype 加 internal helper `AsChunkViews()`：
   ```csharp
   internal ChunkViewSpan AsChunkViews()
   {
       if (!IsChunked)
       {
           // 单个 ChunkView 包整个 archetype，segmentIndex = NonChunkedSegmentIndex
           // 返回单元素 view（用 stackalloc + ChunkView 单一实例 或 cached _singleView 字段）
       }
       else
       {
           // 多 segment view
       }
   }
   ```
   **注意**：这是个 struct enumerator 或 cached array，避免每次 alloc。具体实现见 step 3b。

3b. **`AsChunkViews` 实现方案**：在 `Archetype` 加 internal `ChunkViewEnumerator AsChunkViews()` 返回 struct enumerator：
   ```csharp
   internal ChunkViewEnumerator AsChunkViews() => new(this);
   
   internal struct ChunkViewEnumerator
   {
       private readonly Archetype _archetype;
       private int _index;   // -1 inf 起步，0..SegmentCount
       
       internal ChunkViewEnumerator(Archetype archetype) { _archetype = archetype; _index = -1; }
       
       public ChunkView Current => _archetype.IsChunked
           ? new ChunkView(_archetype, _index)
           : new ChunkView(_archetype);
       
       public bool MoveNext()
       {
           if (!_archetype.IsChunked) { if (_index == -1) { _index = 0; return true; } return false; }
           _index++;
           return _index < _archetype.SegmentCount;
       }
   }
   ```
   这样 caller 用 `foreach (var chunk in archetype.AsChunkViews())` 零分配、JIT-friendly。

4. **`World.EntityLifecycle.WriteCreatedEntitiesAndLocations` 重构**：`if (archetype.IsChunked)` 分发 to Flat/Chunked 两个方法是显式 fast path（保留），但 `WriteCreatedEntitiesAndLocationsFlat` 的 `GetReservedEntities` 调用要改：
   - 选项 A（推荐）：把 `GetFlatReservedEntities` 保留给这个唯一 caller，但加 `Debug.Assert(!IsChunked)`，确保只用于 flat fast path
   - 选项 B：删 `GetReservedEntities`，`WriteCreatedEntitiesAndLocationsFlat` 也改走 `WriteEntityAt` per-row（损失批量 span 写入性能）
   - **先选 A**——保留 flat fast path，但 API 名字诚实化（`GetFlat*`）
   - 测试确认 `Web`, `WorldLifecycleTests` 不退化

5. **bench harness 改造**（`ThroughputRunner`, `QueryProfilingRunner`, `QueryBenchmarks`）：
   - 这些是 baseline 计时，对 Arch 直接用 Arch 自己的 raw API（`Arch.World` 上各 archetype 拿 array）；对 MiniArch 也直接用 `archetype.GetComponentSpan<T>`。**改这一点会改变 benchmark 自身定义**——MiniArch 与 Arch 的对比可能不再 apples-to-apples
   - **关键**：Benchmark 改动前，必须先在同一访问模式下取 baseline 数字（已有 §0.2 的零点 baseline）。改动后跑同一基准对比，差异在 ±2% 噪声内可接受
   - 改为通过 `Query.ForEachChunk` 调用，与典型用户代码对齐——这才是真正的"MiniArch 吞吐量"，不是绕过 ChunkView 的内部 fast path
   - **不可比的风险**：Arch 没有等价的 ChunkView 抽象——它直接遍历 archetype 的 typed arrays。MiniArch 改走 `ForEachChunk` 后衡量的是"MiniArch 的公共 API"vs"Arch 的 raw access"。两种解读都合理，但**plan owner 显式选择"应衡量公共 API 路径"**，理由是：用户 controller 写的就是公共 API，rolling-vs-raw 用 raw 测出来更乐观但偏差于 production
   - commit message **必须**显式说明 baseline 定义变化，附 before/after 两组数字到 PR description，方便后续审计对比

   **退化回退方案**：如果 MiniArch `ForEachChunk` 路径在某个 GameTickSim 场景下比 raw path 退化 > 5%（vs 零点），先排查是不是 ChunkView 在那个场景没被 JIT 内联——把 `GetComponentSpanAt` 等内部方法加 `[MethodImpl(AggressiveInlining)]` 一次。如果仍退化 > 5%，回退该 bench 改造，保留 raw path bench、新增 `ForEachChunk` bench 并行存在 —— 这等价于"benchmark 自身定义分层"，不悔改原 raw path。本回退不阻塞 main Task 完成，但 plan owner 必须被告知决策。

6. **单元测试改造**：单元测试可以直接调 `GetFlat*` API（它们不需要 chunked 行为，测试的就是 flat 路径），或改走 ChunkView。两种都可，倾向后者以体现"调用方都不该走撒谎 API"的精神。

7. 跑全测试 + 门禁。

**Definition of Done**:
- [ ] 4 个撒谎 API 重命名为 `GetFlat*` + `Debug.Assert(!IsChunked)`
- [ ] `ThrowIfChunked` 方法删除
- [ ] `ChunkView` flat 分支用 `GetFlat*` 命名
- [ ] `Query.cs:611` 的 `if (!archetype.IsChunked)` 二分消失，统一走 `AsChunkViews()`
- [ ] `AsChunkViews()` struct enumerator 实现 + 零分配测试
- [ ] `World.EntityLifecycle` 的 flat fast path 保留，但用 `GetFlatReservedEntities` 命名
- [ ] bench harness 3 处改走 `ForEachChunk`，commit message 说明定义变化
- [ ] §0.3 grep 验收：`archetype.IsChunked` 在 `Query.cs` / `World.cs`（除 `WorldStateSnapshot` 操作）/ `World.EntityLifecycle.cs`（除 flat fast path 内的命名 assert）/ tests/ / tools/ 全部消失
- [ ] `dotnet test` Debug + Release 全绿
- [ ] `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` 通过
- [ ] `dotnet run -c Release --project tools/perf/GameTickSim.Perf` 通过，vs Arch 对比数字不退化（±5% 内）

---

## Phase 3 — layout-agnostic 拷贝原语（核心，~2 人日，Task 3）

> 目的：消除 `CopyComponent` / `CopyColumnFrom` / `CopyComponentRaw` 的 4 分支组合爆炸（src×dst chunked?），收敛到单点。

### Task 3 — 抽 layout-abstraction 单点拷贝

**Why**: 3 个跨 archetype copy 函数各自 src.Chunked × dst.Chunked = 4 分支。新加跨 archetype 操作自动 ×4。冷组合（src.chunked-dst.flat / src.flat-dst.chunked）只在大 archetype + 结构变更生产场景触发，单元测试常覆盖不全。

**前置**: 必须先加 4 模式组合测试覆盖（已在 Task 2 DoD 隐含，这里显式）。

**Files**:
- `tests/MiniArch.Tests/Core/ArchetypeTests.cs` 加测试：4 种 src×dst chunked 组合，包括 large archetype（强制 chunked）跨向 small archetype（flat）的迁移
- `src/MiniArch/Core/Archetype.Storage.cs` 抽出 `LayoutAccess` 机制 + 重构 3 个 copy 函数

**Steps**:

1. **加测试覆盖** 先于重构：
   ```csharp
   [Fact] public void CopyComponent_flat_src_flat_dst() { ... }
   [Fact] public void CopyComponent_chunked_src_flat_dst() { ... }
   [Fact] public void CopyComponent_flat_src_chunked_dst() { ... }
   [Fact] public void CopyComponent_chunked_src_chunked_dst() { ... }
   ```
   用 `ForceChunkedForTesting`（`Archetype.TestHooks.cs:5`）强制 chunked。验证 4 组合下数据正确传输。

2. **设计 layout-abstraction** 的核心约束（从 §决策"不做什么"借鉴）：
   - 不加 virtual call（用 struct + generic 或 inlined 静态方法 + JIT 特化）
   - 不加 runtime bounds check（DEBUG assert 已足够）
   - 不丢 `CopySmall` inlining（原方法 `[MethodImpl(AggressiveInlining)]`）
   - 一个 LayoutAccess 持有 archetype 引用 + 一个内部 bool chunked flag（或用 IsChunked 直接读）

   推荐方案：**static helper method 接 Archetype ref**，避免 struct 装箱：
   ```csharp
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   private static ref byte GetColumnRef(Archetype arch, int col, int row)
   {
       if (!arch.IsChunked)
       {
           return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(arch._data),
               arch._columnByteOffsets[col] + row * arch._elementSizes[col]);
       }
       var (segIdx, localRow) = arch.GetSegmentAndLocal(row);
       return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(arch._segments[segIdx].Data),
           arch._columnByteOffsets[col] + localRow * arch._elementSizes[col]);
   }
   ```
   注意：`_data` / `_segments` / `_columnByteOffsets` / `_elementSizes` 都是 `Archetype` 私有字段——`GetColumnRef` 必须是 `Archetype` 内 `private static` 方法或 `partial` 同类内 `private static`。同 partial class 内私有访问没问题。

3. 重构 3 个 copy 函数用 `GetColumnRef`：
   ```csharp
   private void CopyComponent(Archetype source, int srcCol, int srcRow, int dstCol, int dstRow)
   {
       var size = _elementSizes[dstCol];
       ref var srcRef = ref GetColumnRef(source, srcCol, srcRow);
       ref var dstRef = ref GetColumnRef(this, dstCol, dstRow);
       CopySmall(ref dstRef, ref srcRef, size);
   }
   ```
   `CopyColumnFrom` 和 `CopyComponentRaw` 同样重构。每个函数内部 4 分支收敛到 1 个 `GetColumnRef` 调用 —— `GetColumnRef` 自己内部还是 2 分支（src 是 chunked 还是 flat），但这是单点 dual-mode 知识。

4. **关键验收**：跑 `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`——结构变更热路径（add/set/remove 触发 archetype 迁移）走 CopyComponent。如果 `CopySmall` 内联被破坏或加了 virtual call，会立刻看到运动场景吞吐退化。

5. 跑 4 模式组合测试 全绿。

**Definition of Done**:
- [ ] 4 个 src×dst chunked 组合测试已加，全绿
- [ ] `GetColumnRef` 单点 helper 实现，`[MethodImpl(AggressiveInlining)]`
- [ ] `CopyComponent` / `CopyColumnFrom` / `CopyComponentRaw` 4 分支收敛为 1 个 `GetColumnRef` 调用
- [ ] `dotnet test` 全绿
- [ ] `HeroComing.Perf` 门禁通过，结构变更密集场景吞吐与零点 baseline 差异 ≤ ±2%
- [ ] `GameTickSim.Perf` E-MixedFullTick（结构变更频繁的混合负载）不退化

---

## Phase 4 — 集中 cache invalidation（单点，~1.5 人日，Task 4）

> 目的：把 `_flatEntitiesGeneration` 的 8 个递增点收敛到单点 `InvalidateFlatEntityCache()`，加 DEBUG 内容校验抓漏 bump。

### Task 4 — `InvalidateFlatEntityCache` 集中 + DEBUG 内容校验

**Why**: 当前 8 个 mutation 点各自 `_flatEntitiesGeneration++`。漏 bump 静默提供给 `QueryEnumerator.MoveNext()` 热路径陈旧 entity id，无任何 Debug assert 抓得住。集中只减少"递增写错"概率（写错集中为 missing-call）+ 加内容抽查抓漏调用。

**注意**（来自 advisor）：**集中不真正消除纪律**——8 个 mutation site 仍要每个调一次。但这仍是改进：减少"递增表达式的写法"这类错误类型，并且 namespaced 调用点更易 lint/audit。完整消除纪律需要减少 mutation site，不在本 plan 范围。

**Files**:
- `src/MiniArch/Core/Archetype.Storage.cs`

**Steps**:

1. 加 internal 方法：
   ```csharp
   [MethodImpl(MethodImplOptions.AggressiveInlining)]
   private void InvalidateFlatEntityCache()
   {
       _flatEntitiesGeneration++;
   }
   ```

2. 把所有 `_flatEntitiesGeneration++;` 替换为 `InvalidateFlatEntityCache();`。

   **找位置的命令**（Task 1-3 已改 Archetype.Storage.cs，行号会失效——以 identifier 为准）：
   ```powershell
   rg -n "_flatEntitiesGeneration\+\+" src/MiniArch/Core/Archetype.Storage.cs
   ```
   当前快照参考：`GrowChunked`、`AllocateRows`(chunked 末)、`WriteEntityAt`(chunked 末)、`RemoveAt`(chunked 多个 return 点)、`RebuildFlatEntities`、`RestoreFlatBackup`(chunked 末)。**执行时以 grep 结果为准**，不要按 plan 里写的行号找。

3. 增强 `AssertFlatCacheConsistent`（Task 1 已加内容抽查 N=32，这里确认为同一机制；不重复加）。

4. **加抓漏 bump 的间接 assert**：在 `GetEntityStorageUnsafe` 中，DEBUG 下每次 cache 命中（即 generation 匹配）时，抽查 cache 的前 N=32 entity id 与 `_segments[0..min(N, SegmentCount)]` 的相应实体一致：
   ```csharp
   [Conditional("DEBUG")]
   private void AssertFlatCacheContentMatchesSegments()
   {
       if (!IsChunked || _cachedFlatEntities is null) return;
       if (_cachedFlatEntitiesGeneration != _flatEntitiesGeneration) return;
       // 抽查：cache 前 N 项与各 segment 顺序读取的前 N 项一致
       var check = Math.Min(32, _count);
       var flatIdx = 0;
       var segIdx = 0;
       var localIdx = 0;
       for (var i = 0; i < check; i++)
       {
           while (localIdx >= _segments[segIdx].Count)
           {
               segIdx++;
               localIdx = 0;
               Debug.Assert(segIdx < _segmentCount,
                   "Segment sum ran out before reaching flat cache check length — " +
                   "indicates segment invariant already broken (caught in this assert helper).");
           }
           Debug.Assert(_cachedFlatEntities[flatIdx++].Equals(_segments[segIdx].Entities[localIdx++]));
       }
   }
   ```
   这能抓"漏 bump 导致 cache 不刷新"——如果 generation 没递增但 segment 内容变了，抽查会失败（变更在前 32 个 entity）。

5. 跑 `dotnet test` Debug 模式（所有新 assert 触发场景都正确，无 assert 失败）+ Release 门禁。

**Definition of Done**:
- [ ] `InvalidateFlatEntityCache()` 实现 + `[AggressiveInlining]`
- [ ] 全部 8 处 `_flatEntitiesGeneration++` 替换为集中调用
- [ ] `AssertFlatCacheContentMatchesSegments` 实现并集成到 `GetEntityStorageUnsafe` DEBUG 路径
- [ ] `dotnet test` Debug 全绿（无 assert 触发——证明现有 8 处都正确递增了）
- [ ] `HeroComing.Perf` 门禁通过
- [ ] `GameTickSim.Perf` 通过

---

## Phase 5 — 跨模式 snapshot 断言（收尾，~0.5 人日，Task 5）

> 目的：补 `RestoreFlatBackup` 跨模式翻译的对称断言，把已隐式成立的不变式固化。

### Task 5 — 补 flat→flat / flat→chunked restore 的 Debug.Assert

**Why**: 当前 chunked→chunked restore 有 `Debug.Assert(SegmentCount <= arch.SegmentCount)`，flat→flat / flat→chunked 缺等价断言。已隐式成立但不固化则后续 refactor 易破。

**Files**:
- `src/MiniArch/Core/WorldStateSnapshot.cs:252-278`（`RestoreTo` non-chunked 分支）
- `src/MiniArch/Core/Archetype.Storage.cs:779`（`RestoreFlatBackup`）

**Steps**:

1. 在 `RestoreTo` non-chunked 分支加断言：
   ```csharp
   if (!IsChunked)
   {
       Debug.Assert(SourceCapacity > 0, "Non-chunked backup must have a positive SourceCapacity.");
       Debug.Assert(Count >= 0 && Count <= arch.Capacity, 
           $"Backup Count ({Count}) must fit in archetype capacity ({arch.Capacity}).");
       Debug.Assert(ColumnByteOffsets.Length == 0 || ColumnByteOffsets.Length == arch.ComponentTypes.Count,
           "Backup column offset count must match archetype component count.");
       
       // 跨模式翻译约束：flat backup → chunked arch 是合法的（RestoreFlatBackup 处理），
       // 但 chunked backup → flat arch 是非法的（晋升单向）—— 已由 IsChunked 分发保证不可能
       arch.RestoreFlatBackup(Entities, Data, ColumnByteOffsets, Count);
   }
   ```

2. 在 `RestoreFlatBackup` flat→flat 路径加：
   ```csharp
   if (!IsChunked)
   {
       Debug.Assert(count <= _capacity, 
           $"Backup count ({count}) exceeds archetype capacity ({_capacity}).");
       Array.Copy(srcEntities, _entities, count);
       // ... (现有 col-by-col 复制)
   }
   ```

3. 在 `RestoreFlatBackup` flat→chunked 路径加：
   ```csharp
   // Chunked: zero all existing segment counts, then distribute the flat
   // backup across segments using the current segment-capacity offsets.
   for (var i = 0; i < _segmentCount; i++)
       _segments[i].Count = 0;
   
   Debug.Assert(count <= (_segmentCount * _segmentCapacity),
       $"Backup count ({count}) exceeds chunked capacity ({_segmentCount * _segmentCapacity}).");
   // ... (现有分配逻辑)
   ```

4. 同步增强 `AssertSegmentInvariants`（Task 1 加的）在 `RestoreFlatBackup` 末尾调用——确认 restore 后 segment 不变式仍成立。

5. 跑全测试 + 门禁。

**Definition of Done**:
- [ ] 3 处新增 `Debug.Assert` 已加
- [ ] `AssertSegmentInvariants` 在 `RestoreFlatBackup` 末尾调用
- [ ] `dotnet test` Debug 全绿（无 assert 触发——证明现有 restore 路径不变式都正确维持）
- [ ] `HeroComing.Perf` 门禁通过
- [ ] `GameTickSim.Perf` 通过

---

## 附录 A: antagonist 风险登记

按 risk 概率排序：

1. **Task 2 bench harness 改造导致 baseline "假退化"或"假进步"**：bench 自身定义变了，vs Arch 对比失去基准。**Mitigation**：§0.2 已要求改动前取同一访问模式 baseline，改动后跑同 baseline 对比，差异超 ±5% 必须回到 plan 师确认。commit message 显式说明定义变化。

2. **Task 3 layout-abstraction 破坏 `CopySmall` inlining**: 4 个分支收敛到 1 个 `GetColumnRef` 调用——如果 JIT 没有把它 inline，热路径结构变更会立即退化。**Mitigation**: `[MethodImpl(AggressiveInlining)]` + `dotnet build -c Release` 后用 BenchmarkDotNet 或 ILSpy 验证 `CopyComponent` 仍内联了 `GetColumnRef`。HeroComing.Perf 门禁天然抓这个（结构变更密集场景）。

3. **Task 2 `WriteCreatedEntitiesAndLocationsFlat` 退化**：选 A（保留 `GetFlatReservedEntities`）应该零退化，但万一 `Debug.Assert(!IsChunked)` 在 Release 下被 Conditional 吃掉但条件不满足（即 flat fast path 进了 chunked archetype），会调到不应调的 API。**Mitigation**: caller 端 `if (archetype.IsChunked)` 分发已保证不会进入，但加 assert 是第二道防线。forestalled by Task 1 invariant。

4. **过度 invalidate `_cachedFlatEntities`**：Task 4 集中后不会出问题，但**新增 mutation site 时漏加 `InvalidateFlatEntityCache()`** 仍可能。**Mitigation**: Task 1 的 `AssertFlatCacheContentMatchesSegments` 在 DEBUG 下抓"漏 call"。后续新增 mutation 必须显式调 Invalidate——这是纪律，但比"递增表达式写错"更易 audit/grep。

5. **`AsChunkViews()` struct enumerator 性能未达预期**：如果 JIT 没有特化好，foreach 路径可能比原 `if (!IsChunked)` 二分慢。**Mitigation**: `BenchmarkDotNet` 测一下 `OrderedComponentEnumerator.Initialize` 改前/改后 `5K / 50K / 200K` 实体的耗时，差异超 ±5% 重新评估。

## 附录 B: 跨 task 依赖

- Task 1 → 2 / 3 / 4 / 5 都依赖：Task 1 提供的 invariant assert 是后续 refactor 的安全网。**Task 1 必须先做**。
- Task 2 → 3 弱依赖：Task 2 改测试 baseline，Task 3 加 4 模式测试更方便。**可同 phase 做**。
- Task 4 → 5 独立：Task 4 是 cache invalidation，Task 5 是 snapshot assert，互不影响。
- Task 5 可在 Task 1 完成后任何时候做（DEBUG assert 增强）。

## 附录 C: 不做的事

- 不做 token 化 cache invalidation：blast radius 大且不真正消除纪律
- 不做 `ConvertToChunked` 短路 `count==0`：sloppy but cold，引入新 sub-state 比现状更糟
- 不做 small-chunk PoC：bench-first 决策，需另起 task
- 不动 public API 形状：用户调用代码零改动
- 不删 `WriteCreatedEntitiesAndLocationsFlat` / `...Chunked` 二分：是显式 flat batch fast path，保留
- 不重构 `WorldStateSnapshot.ArchetypeBackupEntry.CopyFromNonChunked` / `CopyFromChunked`：snapshot 是 byte 流路径，类 B（结构性写入），dual-mode 知识住所合理

## 附录 D: 参考

- `kb-chunk-storage.md` 全文（现状基础架构）
- `kb-architecture-review.md` §2 存储子系统
- `kb-ecs-comparison.md` 性能 baseline 保护
- `kb-code-review-findings.md` `BUG_order_by_component_supports_chunked_archetypes` 同族 bug 史
- `kb-hero-pipeline-regression.md` 门禁阈值与 baseline