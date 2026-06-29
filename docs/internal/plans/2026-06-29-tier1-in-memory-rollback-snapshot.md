# Tier 1 In-Memory Rollback State Snapshot

Date: 2026-06-29

> **Status: IMPLEMENTED** — 2026-06-29. `World.CaptureState()` / `World.RestoreState()` working with 4 passing tests. 413/413 MiniArch + 5/5 HeroPipeline.

## 结论

在 World 上添加 `CaptureState()` / `RestoreState()` 一对方法，通过 Array.Copy 备份/恢复所有 mutable 数组实现零分配的 GGPO 风格回滚。`WorldStateSnapshot.cs` 骨架（数据类）已创建，尚未接入 World。

## 目标

- 支持 GGPO 风格 rollback：save → simulate predicted frame → restore → re-simulate
- 回滚后 id 分配完全确定（`_freeIds[]` + `_freeIdCount` 已由 #1 修复）
- 稳态零 GC 分配（双缓冲：snapshot 实例在 Capture/Restore 之间交替复用）
- 非 chunked 和 chunked 两种 Archetype 存储模式都支持

## 非目标

- 不做世界状态 diff（两个 World 状态的差异）
- 不做部分/过滤快照（interest management）
- 不做持久化序列化（WorldSnapshot 已覆盖）
- 不做跨进程/跨机器复制（WorldSnapshot 已覆盖）

## 架构

```
CaptureState() → WorldStateSnapshot (opaque handle, pre-allocated arrays)
                    ├── Records[] + EntitySlotCount
                    ├── FreeIds[] + FreeIdVersions[] + FreeIdCount
                    ├── per-Archetype ArchStateBackup
                    │     ├── Entities[] + Count
                    │     ├── Data[] (full copy)
                    │     ├── SegmentEntities[][] (chunked)
                    │     ├── SegmentData[][] (chunked)
                    │     └── SegmentCounts[] (chunked)
                    └── Hierarchy
                          ├── ParentByChild[], FirstChild[]
                          ├── ChildEntity[], ChildNext[]
                          └── ChildSlotCount, ChildFreeList
```

RestoreState 反向操作。snapshot 实例在 Restore 后回收为 spare，下一次 CaptureState 复用以避免分配。

## 需要修改的文件

| 文件 | 改动 |
|---|---|
| `WorldStateSnapshot.cs` | 现有骨架，已创建。需要调整为 ArchetypeStateBackup struct （当前是 class，改为 struct 避免 per-archetype 分配） |
| `World.cs` | 加 `CaptureState()`/`RestoreState()` + `_stateSnapshotSpare` 字段 |
| `Archetype.cs` / `Archetype.Storage.cs` | 加 `CaptureState(ArchStateBackup)` / `RestoreState(ArchStateBackup)` / `ResetCount()` |
| `HierarchyTable.cs` | 加 `CaptureState(WorldStateSnapshot)` / `RestoreState(WorldStateSnapshot)` |
| 测试文件 | 加 Rollback + re-simulate 端到端测试 |

## 性能注意

- 所有数据拷贝通过 `Array.Copy` 完成，一次 memcpy 调用
- 预分配的 backup 数组通过 `EnsureCapacity` 模式增长，稳态不再分配
- 对于格斗游戏规模（<1000 实体），Capture + Restore 应在 <0.1ms 内完成
- 不需要修改 CommandStream / FrameDelta / ReplayCore

## 验证

- `Save_load_preserves_free_id_allocation_order` 已通过（#1 修复验证）
- 新增测试：`CaptureState_RestoreState_preserves_entity_state` — 创建实体，Capture，修改世界，Restore，验证世界还原
- 新增测试：`Rollback_replay_produces_deterministic_ids` — Capture → 模拟 → Restore → 重放 → id 分配序列一致
- 新增测试：`Rollback_replay_with_invalidated_caches_still_correct` — 验证 cache 重建不影响结果

## 实施步骤

### Task 1: 重构 WorldStateSnapshot 数据类

**Files:** `src/MiniArch/Core/WorldStateSnapshot.cs`

- 将 `ArchetypeStateBackup` 从 class 改为 struct（避免 per-archetype 堆分配）
- 将 `ArchetypeBackups` 从 `Dictionary<Archetype, ArchStateBackup>` 改为 `List<(Archetype, ArchStateBackup)>`（字典在 Archetype 数量少时是 overkill）
- 为 chunked segment 数据添加正确的字段

验证：build 通过

### Task 2: Archetype CaptureState / RestoreState

**Files:** `src/MiniArch/Core/Archetype.Storage.cs`

- 添加 `internal void CaptureState(ref ArchetypeStateBackup backup)`
  - non-chunked: copy `_entities[0.._count)`, `_data` (full), `_count`
  - chunked: copy per-segment data + `_segmentCount`
- 添加 `internal void RestoreState(ref ArchetypeStateBackup backup)`
  - non-chunked: Array.Copy back, restore `_count`
  - chunked: per-segment restore, restore `_segmentCount`
- 添加 `internal void ResetCount()` — 将 `_count` 置零（用于预测帧创建的新 archetype）

验证：`dotnet test -c Release` ALL PASS

### Task 3: Hierarchy CaptureState / RestoreState

**Files:** `src/MiniArch/Core/HierarchyTable.cs`

- 添加 `internal void CaptureState(WorldStateSnapshot snapshot)` — 复制全部 4 个数组 + 2 个 int
- 添加 `internal void RestoreState(WorldStateSnapshot snapshot)` — Array.Copy 写回
- 注意：数组只扩容不缩容，Restore 时只需要拷贝 `childSlotCount_pre` 个元素

验证：`dotnet test -c Release` ALL PASS

### Task 4: World CaptureState / RestoreState

**Files:** `src/MiniArch/Core/World.cs`

- 添加 `_stateSnapshotSpare: WorldStateSnapshot?` 字段
- 添加 `public WorldStateSnapshot CaptureState()`
  - 取 spare 或 new → 清空旧数据 → 填充
  - Records、FreeIds、EntitySlotCount、FreeIdCount
  - 遍历 `_archetypes`，调用每个 archetype 的 CaptureState
  - Hierarchy.CaptureState
- 添加 `public void RestoreState(WorldStateSnapshot snapshot)`
  - 反向操作 + 将 snapshot 存入 `_stateSnapshotSpare`
  - 递增 `_createArchetypeCacheGeneration`（使所有 query cache 失效）
  - 将所有「预测帧创建的空 archetype」的 count 置零

验证：`dotnet test -c Release` ALL PASS

### Task 5: 端到端测试

**Files:** `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`

```csharp
[Fact]
public void Capture_and_restore_preserves_full_state()
{
    var world = new World();
    // create entities, add components, destroy some, link some
    var snapshot = world.CaptureState();
    // mutate world heavily (create, destroy, add/remove components)
    world.RestoreState(snapshot);
    // verify state matches snapshot
}

[Fact]
public void Rollback_and_replay_produces_deterministic_ids()
{
    var world = new World();
    var snapshot = world.CaptureState();
    // predicted frame: Create entities → gets ids from free list
    world.RestoreState(snapshot);
    // re-simulate with same operations: must get identical ids
    // Assert.Equal on ids
}

[Fact]
public void Capture_restore_twice_is_idempotent()
{
    var world = new World();
    world.Create(new Position(1, 2));
    var s1 = world.CaptureState();
    world.RestoreState(s1);
    var s2 = world.CaptureState();
    // hash-based comparison
}
```

验证：`dotnet test -c Release` ALL PASS

### Task 6: Benchmark（可选）

**Files:** `tools/perf/HeroComing.Perf` 新增 rollback 场景

- 测量 100/500/1000 实体下 Capture + Restore 耗时
- 目标：100 实体 < 50μs，500 实体 < 200μs，1000 实体 < 1ms

## 坑点

- **chunked/split archetype 模式**：Tier 1 快照必须正确处理 `Segment[]`，否则回滚后会有数据残留。Segment 容量的对齐（`_segmentEntityCapacity`）是固定的，所以 segment 内 `Entities` + `Data` 数组大小在不同帧之间不变（除非 segment 数变化），降低了快照复杂度。
- **`_archetypes` 集合**：预测帧可能创建新 archetype。Restore 后这些 archetype 还在集合里，但 count = 0。WorldSnapshot.Save 跳过空 archetype，所以 checksum 不受影响。但是 Restore 后如果遍历 `_archetypes` 来执行某些操作，空 archetype 会产生额外的空迭代。这是可接受的（YAGNI）。
- **`_addDestinationCache` / `_removeDestinationCache`**：这些是 archetype 转换缓存，Restore 后可能指向错误的目标（因为预测帧改变了 archetype 空间）。缓存失效通过 `_createArchetypeCacheGeneration++` 触发重建。
- **`_records` 长度**：`EntitySlotCount` 在 Restore 后恢复为旧值。如果预测帧分配了新 slot（`_entitySlotCount++`），Restore 后 `_records` 数组末尾仍有新 slot 的残留数据，但 `_entitySlotCount` 已恢复为旧值，所以这些 slot 不可达。
