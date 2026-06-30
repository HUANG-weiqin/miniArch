# World Data-Structure Optimization Experiments Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 按收益可能性分批实验 MiniArch `World`/`Archetype`/`Chunk`/`CommandBuffer` 数据结构优化，只保留有数据证明的改动。

**Architecture:** 不一次性重构。每个实验都是独立切片：先建立 Release baseline，再做最小实现，再运行指定测试和性能门禁，最后按保留/回退规则决定是否进入下一项。优先优化“热循环少做事”和“批量顺序写”，NativeMemory 作为高天花板但低确定性的后置实验。

**Tech Stack:** .NET 8, C#, xUnit, BenchmarkDotNet, MiniArch perf runners.

---

## 总原则

### 收益可能性排序

当前按“实际项目最可能带来收益”排序：

1. **CommandBuffer 按 archetype 批量 materialize**
2. **Query 快照失效拆分，只在 chunk 非空成员变化时刷新 chunk snapshot**
3. **`EntityRecord` 压缩：`Archetype?` 引用 → archetype id / plus-one id**
4. **NativeMemory + 64B alignment 实验**
5. **MigrationPlan 小优化：direct column lookup + copy entry 排序 + `CopySmall` case 2**
6. **Empty chunk active list / 空 chunk 回收**（只在前面实验暴露空 chunk 扫描成本时做）

### 保留规则

每个实验完成后只能有三种结论：

- **保留**：正确性测试通过；`HeroComing.Perf` 不低于门禁；目标指标中位数改善 ≥3%，或低风险小改改善 ≥1% 且无任何回归。
- **暂存待定**：正确性通过，但性能波动无法判定；记录数据，不进入大重构，先跑下一轮基线或 profile。
- **回退**：测试失败、内存增长、门禁低于阈值、非目标场景回归 >2%、代码复杂度明显不值。

默认不要提交。只有用户明确要求提交时，才按实验粒度 commit。

### 强制验证

所有性能命令必须使用 `-c Release`。

任何改动 `src/MiniArch/` 或 `tests/HeroPipeline.Tests/` 后，最终必须运行：

```bash
dotnet run -c Release --project perf/HeroComing.Perf
```

门禁：Movement ≥496 rounds/s，Attack ≥32 rounds/s；低于阈值直接回退。

---

## Task 0: 建立 baseline 和实验记录

**Files:**
- Read: `AGENTS.md`
- Read: `.knowledge/INDEX.md`
- Read: `.knowledge/kb-core-ecs.md`
- Read: `.knowledge/kb-chunk-storage.md`
- Read: `.knowledge/kb-cache-optimization.md`
- Read: `.knowledge/kb-query-invalidation.md`
- Read: `.knowledge/kb-hero-pipeline-regression.md`
- Create: `artifacts/perf-experiments/2026-06-06-world-data-structure-optimization.md`

**Step 1: 检查工作区**

Run:

```bash
git status --short
```

Expected: 记录现有改动。不要覆盖用户未说明的改动。

**Step 2: 运行正确性 baseline**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet test -c Release tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj
```

Expected: all pass。

**Step 3: 运行性能 baseline，每项至少 3 次取中位数**

Run:

```bash
dotnet run -c Release --project perf/HeroComing.Perf
dotnet run -c Release --project perf/QueryInvalidation.Perf
dotnet run -c Release --project perf/Throughput.Perf
dotnet run -c Release --project perf/GameTickSim.Perf
```

Expected: 记录 Movement/Attack、query refresh、ticks/second、GC/heap delta。

**Step 4: 建立实验记录模板**

写入 `artifacts/perf-experiments/2026-06-06-world-data-structure-optimization.md`：

```markdown
# World Data-Structure Optimization Experiment Log

## Baseline

| Runner | Run 1 | Run 2 | Run 3 | Median | Notes |
|---|---:|---:|---:|---:|---|
| HeroComing Movement | | | | | |
| HeroComing Attack | | | | | |
| QueryInvalidation | | | | | |
| Throughput | | | | | |
| GameTickSim | | | | | |

## Experiments

### Experiment N: name

- Hypothesis:
- Changed files:
- Correctness:
- Perf before:
- Perf after:
- Decision: keep / revert / inconclusive
- Reason:
```

---

## Task 1: CommandBuffer 按 archetype 批量 materialize

**Hypothesis:** 当前 create-heavy 场景最大浪费是每个 created entity 独立 `BuildCreatedEntityComponents` → `ReserveEntity` → 写 record/component。把同 archetype created entities 聚成批次后，可以把 N 次小写变成范围 reserve + 顺序写。

**Files:**
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Test: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Perf: `perf/HeroComing.Perf/Program.cs`（只在需要新增分段指标时改）

**Step 1: 加测试，证明批处理不改变语义**

在 `CommandBufferTests.cs` 增加覆盖：

- 同一 buffer 创建多个相同组件集合 entity，Submit 后所有实体 alive，组件值正确。
- 混合两种 archetype 创建，Submit 后各自组件正确。
- created entity 中有 destroyed/released 的 entity，不能 materialize。
- created entity 后再 AddChild/RemoveChild/Destroy，现有语义不变。

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests
```

Expected: 先通过现有实现；这些测试是防回归，不是必须失败。

**Step 2: 在 World 加最小批量入口**

目标 API 形状建议：

```csharp
internal unsafe void MaterializeReservedEntitiesFast(
    ReadOnlySpan<Entity> entities,
    Archetype archetype,
    ReadOnlySpan<CommandBuffer.CreatedComponent> sortedComponents,
    List<byte[]> slabs,
    ReadOnlySpan<int> entityToComponentOffsetOrFixedLayout)
```

如果所有 entity 同 archetype 且组件类型顺序相同，优先实现固定 layout 版本：

```csharp
internal unsafe void MaterializeReservedEntitiesFastSameLayout(
    ReadOnlySpan<Entity> entities,
    Archetype archetype,
    ReadOnlySpan<CommandBuffer.CreatedComponent> componentsByEntity,
    int componentCount,
    List<byte[]> slabs)
```

实现原则：

- 使用 `archetype.ReserveEntityRanges(entities.Length, ranges)` 一次性保留行。
- 顺序写每个 `chunk.GetReservedEntities(...)`。
- 顺序写 `_records[entity.Id]`。
- 组件写入仍可先沿用 `WriteComponentFromBytes`，不要第一版就重写 column bulk copy。

**Step 3: 在 CommandBuffer.Submit 中分组**

最小可行分组：只合并“连续相同 archetype + 相同 componentCount + 相同 sorted component types”的 created entities。

不要第一版做全局 hash bucket。原因：连续同类创建已经覆盖 HeroPipeline 常见场景，且避免重排引发 hierarchy/side-effect 语义风险。

伪码：

```csharp
for i in created states:
    if destroyed: flush current batch; release
    else if empty: flush current batch; materialize empty or batch empty
    else:
        build sorted components, resolve archetype
        if same layout as current batch:
            append entity + component slice metadata
        else:
            flush current batch
            start new batch
flush at end
```

**Step 4: 正确性验证**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet test -c Release tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj
```

Expected: all pass。

**Step 5: 性能验证**

Run each 3 times:

```bash
dotnet run -c Release --project perf/HeroComing.Perf
dotnet run -c Release --project perf/GameTickSim.Perf
```

Retention gate:

- HeroComing Movement 或 GameTickSim create-heavy 指标中位数 ≥ baseline +3%。
- Attack 不回归 >2%。
- GC/heap delta 不增长。

**Step 6: 决策**

- 保留：记录到 experiment log，继续 Task 2。
- 回退：恢复 `CommandBuffer.cs`/`World.cs`/测试改动，记录原因。

---

## Task 2: Query 快照失效拆分

**Hypothesis:** 当前 `Archetype.Generation` 在 `ReserveEntity`/`RemoveEntity` 时变化，导致 Query 可能因普通 entity count 变化重建 matched archetype/chunk snapshot。实际需要刷新的是“新 archetype 出现”或“chunk 从空变非空/从非空变空”。

**Files:**
- Modify: `src/MiniArch/Core/Archetype.cs`
- Modify: `src/MiniArch/Core/Query.cs`
- Test: `tests/MiniArch.Tests/Core/QueryTests.cs`
- Perf: `perf/QueryInvalidation.Perf/Program.cs`

**Step 1: 写/调整 Query refresh 测试**

覆盖以下语义：

- 同一 matched archetype 内新增 entity，但 chunk 本来非空：`query.RefreshCount` 不增加。
- 同一 matched archetype 内删除 entity，但 chunk 仍非空：`query.RefreshCount` 不增加。
- chunk 从 0→1 或 1→0：chunk snapshot 能正确包含/排除 chunk。
- 新 signature/archetype 创建：matched archetype snapshot 必须刷新。

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~QueryTests
```

Expected: 新测试在现有实现下可能失败，尤其 refresh count 断言。

**Step 2: 拆版本号**

在 `Archetype` 中保留或替换：

```csharp
internal long EntityGeneration { get; private set; }      // 如仍需调试可保留
internal long NonEmptyChunkVersion { get; private set; }  // 只有 chunk 非空成员变化时递增
```

关键逻辑：

- `ReserveEntity`：如果目标 chunk `beforeCount == 0` 且 `afterCount > 0`，递增 `NonEmptyChunkVersion`。
- `ReserveEntityRanges`：对每个 touched chunk 检测 0→非0。
- `RemoveEntity`：如果目标 chunk `beforeCount > 0` 且 `afterCount == 0`，递增 `NonEmptyChunkVersion`。
- 新 chunk 添加但仍空，不应让 query chunk snapshot 刷新；首次写入该 chunk 时刷新。

**Step 3: Query 只在必要时刷新**

设计：

- matched archetypes 只受 `_world.ArchetypeVersion` 影响。
- chunk snapshot 受 matched archetypes 的 `NonEmptyChunkVersion` 影响。
- `_snapshotGenerations` 改为记录 `NonEmptyChunkVersion`，并且只用于 chunk snapshot。

不要让普通 count 变化触发 `BuildMatchingArchetypeSnapshot()`。

**Step 4: 正确性验证**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~QueryTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj
```

Expected: all pass。

**Step 5: 性能验证**

Run each 3 times:

```bash
dotnet run -c Release --project perf/QueryInvalidation.Perf
dotnet run -c Release --project perf/HeroComing.Perf
dotnet run -c Release --project perf/GameTickSim.Perf
```

Retention gate:

- QueryInvalidation 中 refresh/rebuild 次数下降，吞吐中位数 ≥ baseline +3%。
- HeroComing 与 GameTickSim 不回归 >2%。

---

## Task 3: `EntityRecord` 压缩为无引用元数据

**Hypothesis:** `_records` 是随机 entity access 的桥。把 `Archetype?` 引用从每条 record 中移除，可把 record 从约 24B 压到 16B，并让 `_records` 不含 GC 引用，改善随机 `Get/Set/Destroy/IsAlive`。

**Files:**
- Modify: `src/MiniArch/Core/EntityRecord.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/WorldClone.cs`
- Modify: `src/MiniArch/Core/WorldSnapshot.cs`
- Test: `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`
- Test: `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`
- Test: `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`
- Test: `tests/MiniArch.Tests/Persistence/WorldCloneTests.cs`

**Step 1: 设计 record 形状，避免 default 误判 occupied**

推荐：

```csharp
internal struct EntityRecord
{
    public int Version;
    public int ArchetypeIndexPlusOne; // 0 = unoccupied/default
    public int ChunkIndex;
    public int RowIndex;

    public readonly bool IsOccupied => ArchetypeIndexPlusOne != 0;
}
```

不要用 `-1 = empty`，因为 `EntityRecord[]` 默认全 0，会让初始化成本变高。

**Step 2: 给 World 增加 archetype id side table**

在 `World` 中新增：

```csharp
private Archetype[] _archetypesByIndex = [];
private int _archetypeCount;
```

新增 helper，集中访问：

```csharp
private Archetype GetRecordArchetype(in EntityRecord record)
    => _archetypesByIndex[record.ArchetypeIndexPlusOne - 1];

private int GetArchetypeIndexPlusOne(Archetype archetype)
    => archetype.StorageIndex + 1;
```

`Archetype` 可增加 `internal int StorageIndex { get; set; } = -1;`，由 `World.GetOrCreateArchetype` 分配。测试直接 new `Archetype` 时保持 `-1`，不依赖它。

**Step 3: 替换所有 `record.Archetype` 读写**

重点位置：

- `TryGetLocation`
- `GetRequiredLocation`
- `CreateInArchetype`
- `WriteCreatedEntitiesAndLocations`
- `DestroySingle` moved entity record update
- `MoveEntityCore` / `FinishMoveEntity`
- snapshot/clone restore location

原则：只有 helper 能把 record id 转成 `Archetype`，不要到处直接索引 side table。

**Step 4: 正确性验证**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~WorldLifecycleTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~WorldStructuralChangeTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~WorldSnapshotTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~WorldCloneTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj
```

Expected: all pass。

**Step 5: 性能验证**

Run each 3 times:

```bash
dotnet run -c Release --project perf/Throughput.Perf
dotnet run -c Release --project perf/GameTickSim.Perf
dotnet run -c Release --project perf/HeroComing.Perf
```

Retention gate:

- random entity access / mixed tick 场景中位数 ≥ baseline +3%。
- Pure chunk iteration 不回归 >2%。
- HeroComing 门禁通过。

**Step 6: 特别检查**

- 确认 `Dispose()` 清空 `_archetypesByIndex`。
- 确认 `Reset()` 重置 archetype id side table。
- 确认 snapshot import 后 record 的 archetype index 正确。

---

## Task 4: NativeMemory + 64B alignment 实验

**Hypothesis:** 单纯 `byte[]` → native pointer 不一定提升；真正收益来自 64B 对齐、减少 GC 扫描/LOH 压力，以及为 SIMD 做准备。该实验高风险，只有在前面实验后迭代仍是瓶颈时才做。

**Files:**
- Modify: `src/MiniArch/Core/Chunk.cs`
- Modify: `src/MiniArch/Core/Archetype.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Test: `tests/MiniArch.Tests/Core/ChunkTests.cs`
- Test: `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1: 先做 alignment-only 低风险子实验**

在 `Chunk.CreateStorage()` 中先把列起始偏移改为 64B 对齐：

```csharp
totalBytes = AlignUp(totalBytes, 64);
```

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~ChunkTests
dotnet run -c Release --project perf/GameTickSim.Perf
dotnet run -c Release --project perf/HeroComing.Perf
```

Decision:

- 如果无收益且内存变大，回退 alignment-only。
- 如果后续 SIMD 计划明确，可保留为 NativeMemory 前置条件。

**Step 2: NativeMemory 只在独立切片实现**

不要抽象出大接口。只在 `Chunk` 内替换 `_data` 存储。

推荐字段：

```csharp
private unsafe byte* _data;
private int _dataLength;
private bool _ownsData;
```

使用：

```csharp
NativeMemory.AlignedAlloc((nuint)byteCount, 64);
NativeMemory.AlignedFree(_data);
```

**Step 3: 增加释放链**

必须新增：

- `Chunk.Dispose()`：释放 native buffer。
- `Archetype.Dispose()`：dispose all chunks。
- `World.Dispose()`：dispose all archetypes before clearing dictionaries。
- `Chunk.EnsureCapacity()`：分配新 native buffer、按列 copy、释放旧 buffer。

注意：如果用户不 dispose world，native memory 会泄漏。这是该实验的最大风险。必须在文档和 tests 中明确。

**Step 4: 修改所有 data access**

替换：

```csharp
MemoryMarshal.GetArrayDataReference(_data)
_data.AsSpan(...)
```

为 native pointer/ref helper：

```csharp
private unsafe ref byte DataRef => ref *_data;
```

`GetColumnBytes()` 如果还需要 `Span<byte>`：

```csharp
return new Span<byte>(_data + offset, length);
```

**Step 5: 正确性和泄漏验证**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~ChunkTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

额外手动检查：

- 连续跑 `HeroComing.Perf` 3 次，进程 working set 不持续增长。
- `World.Dispose()` 后大 world 的 working set 有下降或至少不继续增长。

Retention gate:

- GameTickSim 或 chunk-heavy benchmark 中位数 ≥ baseline +5%。
- 无 unmanaged memory leak。
- HeroComing 不回归。

如果只提升 <3%，回退。NativeMemory 复杂度不允许小收益保留。

---

## Task 5: MigrationPlan 低风险小优化

**Hypothesis:** 结构变更搬迁共享组件时，按 source column 顺序拷贝更 cache-friendly；build 阶段也可用 direct map 替代 O(n*m)。这是低风险补丁，但收益通常小。

**Files:**
- Modify: `src/MiniArch/Core/MigrationPlan.cs`
- Modify: `src/MiniArch/Core/Chunk.cs`
- Test: `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`
- Test: `tests/MiniArch.Tests/Core/ArchetypeTests.cs`

**Step 1: 修改 Build**

把内层 source component 线性搜索改成：

```csharp
if (source.TryGetComponentIndex(destinationComponent, out var sourceColumnIndex))
{
    sharedCopies[copyCount++] = new CopyEntry(
        sourceColumnIndex,
        destination.GetComponentIndexFast(destinationComponent),
        destination.GetElementSize(destinationIndex));
}
```

**Step 2: 排序 CopyEntry**

在 `Build` 完成后：

```csharp
Array.Sort(sharedCopies, 0, copyCount, CopyEntrySourceComparer.Instance);
```

如果 `copyCount != sharedCopies.Length`，先 trim 或只排序有效范围。

**Step 3: 补 `CopySmall` case 2**

在 `Chunk.CopySmall` 加：

```csharp
case 2:
    Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<short>(ref source));
    return;
```

**Step 4: 验证**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~WorldStructuralChangeTests
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Retention gate:

- 无正确性回归。
- HeroComing 不回归。
- 结构变更 benchmark 有 ≥1% 改善即可保留；否则因为改动小，也可保留 `case 2`，但排序/direct lookup 若无收益可回退。

---

## Task 6: Empty chunk active list / 回收实验（条件执行）

**Only run if:** Task 2 后仍观察到 query 扫描大量 empty chunk，或 GameTickSim/HeroComing 记录显示 empty chunks 累积。

**Hypothesis:** 高 churn 后 `Archetype._chunks` 包含很多 empty chunk，query 每帧跳过空 chunk 造成浪费。维护 non-empty chunk list 可以减少扫描。

**Files:**
- Modify: `src/MiniArch/Core/Archetype.cs`
- Modify: `src/MiniArch/Core/Query.cs`
- Test: `tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1: 加 debug/perf 观测**

先只统计：

- archetype chunk count
- non-empty chunk count
- empty chunk count

不要先实现 active list。

**Step 2: 如果 empty ratio 高，再实现**

实现 `Archetype.GetNonEmptyChunkSpan()` 或 query build 时使用 `_nonEmptyChunkIndexes`。

维护点：

- chunk 0→1：加入 non-empty set。
- chunk 1→0：移除 non-empty set。
- swap-remove 不改变 chunk count，但可能触发 1→0。

**Step 3: 验证**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~QueryTests
dotnet run -c Release --project perf/GameTickSim.Perf
dotnet run -c Release --project perf/HeroComing.Perf
```

Retention gate:

- empty chunk 高比例场景明显改善 ≥3%。
- 普通场景不回归 >2%。

---

## Task 7: 最终文档与知识库更新

**Files:**
- Modify: `.knowledge/kb-cache-optimization.md`
- Modify: `.knowledge/kb-core-ecs.md` if entity/archetype record layout changed
- Modify: `.knowledge/kb-chunk-storage.md` if NativeMemory or alignment retained
- Modify: `.knowledge/kb-query-invalidation.md` if query invalidation retained
- Modify: `.knowledge/INDEX.md` only if new knowledge page is added

**Step 1: 写入实验结论**

每个 retained/reverted 实验都写：

- 实施内容
- 目标指标 baseline vs after
- 保留/回退原因
- 坑点

**Step 2: 最终全量验证**

Run:

```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj
dotnet test -c Release tests/HeroPipeline.Tests/HeroPipeline.Tests.csproj
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected:

- all tests pass
- HeroComing Movement ≥496 rounds/s
- HeroComing Attack ≥32 rounds/s
- no memory growth

**Step 3: 最终汇报格式**

向用户汇报：

```text
保留的实验：
1. ... baseline -> after, +X%

回退的实验：
1. ... 原因

最终验证：
- MiniArch.Tests: pass
- HeroPipeline.Tests: pass
- HeroComing.Perf: Movement X, Attack Y

修改文件：
- ...
```

不要声称“更快”而不附数据。
