---
title: 代码审阅发现清单
module: Meta
description: 历次代码审阅中产生过的猜想与结论。真 bug 索引 + 已排除的非 bug 猜想。AI 审阅前必读，避免重复验证已知结论。
updated: 2026-07-05 (3 真 bug 已修复 + 2026-07-05 全面 chunk 审阅，新增 A7-A12、W7-W8、WC1-WC2、Q5-Q6 共 10 条非 bug 猜想)
---
# 代码审阅发现清单

> **审阅前必读**。这个文档只记录**结论 + 指路**，不重复推理过程。
> 真 bug 由 `BUG_` 前缀测试证明（本文件只放索引）；非 bug 猜想按模块归档。

## 这个模块是干什么的

- 沉淀代码审阅中产生过的**所有有价值猜想**（位置 + 猜想 + 结论 + 验证方式）
- 让下一次审阅者（人或 AI）跳过已知结论，专注发现新问题
- 真 bug 不在这里展开——它们以 `BUG_` 前缀测试的形式留在测试套件，本文件只放索引
- 与 `kb-design-rationale.md §3 常见误判优化` 的边界：
  - §3 回答"能不能改成 X"（架构层优化提案）
  - 本文件回答"这段代码是不是有 bug"（代码层审阅猜想）

## 如何使用

1. 审阅前先扫一遍本文件
2. 产生新猜想时，先查本文件——若已被记录为非 bug，直接跳过
3. 若新猜想经验证非 bug，append 到对应模块段
4. 若新猜想经验证是真 bug，写 `BUG_` 前缀测试 + 加到"真 bug 索引"
5. 任何条目若被新审阅推翻（从非 bug 变真 bug），从下方清单移除并改写到真 bug 索引

---

## 已确认的真 bug → 已修复（`BUG_` 测试转为回归测试）

> 三条 bug 均已修复，`BUG_` 前缀测试现在通过，充当回归守卫。

| 测试名 | 位置 | 一句话描述 | 修复 |
|---|---|---|---|
| `BUG_capture_nonchunked_then_promote_then_restore_crashes` | `WorldStateSnapshot.cs:RestoreTo` | CaptureState 备份非 chunked archetype，prediction 期间 archetype 被 promote 为 chunked 后，RestoreState 走非 chunked 分支调用 `arch.CopyDataFrom`/`GetEntityStorageUnsafe`，撞上 `_data=null`/零长度缓存数组崩溃 | `RestoreTo` 检测 backup 非_chunked 但 arch 已 chunked 时，调用新增 `Archetype.RestoreFlatBackup`，按列从 flat backup（backup-time offsets）拆分到 segments（current segment-capacity offsets）；chunked→chunked 路径改用 `ResetCount` 清零多余 segment count |
| `BUG_parallel_append_concurrent_resize_is_not_atomic` | `CommandStream.cs:AppendConcurrent` | `ParallelRecording=true` 下并发 append 时，`_data`/`_entities`/`_kinds` 三个独立 `Array.Resize` 非原子；等待循环只看 `_data.Length` 作闸门，外部线程读到 `_data` 已扩容就退出，写 `_entities[slot]`/`_kinds[slot]` 越界或写入废弃数组 | `AppendConcurrent` 改为在 `_resizeLock` 内完成 ensure+write（与 `AppendDestroyConcurrent` 同模式），消除三数组观测不一致和数据丢失（resize Array.Copy 与延迟写竞争）|
| `BUG_capacity_above_segment_capacity_corrupts_row_mapping_on_promote` | `Archetype.Storage.cs:ConvertToChunked` | 当 non-chunked `_capacity > _segmentCapacity` 时（构造大 capacity 或 `EnsureCapacity` doubling 分支 `newCapacity=max(required, _capacity*2)` 把容量顶过 segment 阈值），promote 路径把超长 buffer 直接当 segment[0]，之后 `GetSegmentAndLocal` 按 `_segmentMask` 切分会把 `globalRow ≥ _segmentCapacity` 的数据映射到不存在的 segment | 删除 `NormalizeForChunked`，`ConvertToChunked` 统一负责 canonical split：`_capacity == _segmentCapacity` 时 fast-path wrap（正常 doubling 阈值 promote 零拷贝），否则 general path 按 `_segmentCapacity` 分割为 N 个标准 segment 并 rebase column offsets（提取 `ComputeColumnLayout` 纯函数复用）|

---

## 已排除的非 bug 猜想

> 每条 4 字段：**位置 / 猜想 / 结论 / 验证**。验证段写"怎么确认的"（读哪段代码/跑哪个测试），不写推理过程本身。

### 存储 / Archetype

#### A1. `AddEntity` 中 `EnsureCapacity` 后再次检查 `if (!_isChunked)`
- **位置**：`Archetype.Storage.cs` AddEntity
- **猜想**：EnsureCapacity 后又判一次 `_isChunked`，看似冗余
- **结论**：非冗余。EnsureCapacity 内部 `_capacity * 2 > _segmentCapacity` 会触发 `NormalizeForChunked + ConvertToChunked`，转换后必须重新判分支
- **验证**：读 `EnsureCapacity` 第一个 if 分支

#### A2. `ConvertToChunked` 后 `_capacity` 字段 stale（保留 doubling 值）
- **位置**：`Archetype.cs` ConvertToChunked + Capacity getter
- **猜想**：转换后 `_capacity` 不更新，似乎会乱
- **结论**：无害。chunked 模式下 `Capacity` getter 走 `ComputeChunkedCapacity()`（累加 segment.Entities.Length），从不读 `_capacity`；EnsureCapacity/ReserveRows 也都走 getter
- **验证**：读 `Capacity` getter 和所有读 `_capacity` 的位置（ConvertToChunked 内部使用，无外部读；`NormalizeForChunked` 已删除）

#### A3. `GrowChunked` 中 `if (_segments.Length == newIdx) Array.Resize`
- **位置**：`Archetype.Storage.cs` GrowChunked
- **猜想**：ConvertToChunked 创建 `new Segment[1]` 且 `_segmentCount=1`，第一次 Grow 时 newIdx=1 似乎越界
- **结论**：正确。newIdx==Length==1 时先 resize 到 `Math.Max(1*2,4)=4`，再写 `_segments[1]`
- **验证**：手算 ConvertToChunked 后第一次 GrowChunked(1) 的 `_segments.Length` 与 `_segmentCount` 关系

#### A4. `AddEntityChunked` while 循环中每次重新取 `ref var lastSeg = ref _segments[lastSegIdx]`
- **位置**：`Archetype.Storage.cs` AddEntityChunked
- **猜想**：循环内反复取 ref，看似低效
- **结论**：必须如此。GrowChunked 可能 `Array.Resize(_segments)`（替换数组），旧 ref 失效
- **验证**：读 GrowChunked 中 `Array.Resize(ref _segments, ...)`

#### A5. `ReadColumnFrom` chunked 模式下空 segment 似乎死循环（take=0, remaining 不变）
- **位置**：`Archetype.Storage.cs` ReadColumnFrom
- **猜想**：`take = Math.Min(remaining, seg.Count)`，seg.Count=0 时 take=0，死循环？
- **结论**：正常流程不产生空 segment。ReserveRows 总是 `lastSeg.Count += take`，segment 0..N-1 满、segment N 部分满。Load 路径下 archetype 是新创建的，ReserveRows 后无空 segment
- **验证**：读 ReserveRows 和 GrowChunked，确认不产生 `Entities.Length>0 && Count==0` 的 segment

#### A6. `MoveEntityCore` catch rollback 中 `destination.RemoveAt(rowIdx, out _)` 忽略 movedEntity
- **位置**：`World.StructuralChange.cs` MoveEntityCore
- **猜想**：swap-remove 可能移动别的 entity 到 rowIdx，但 `out _` 不更新它的 record → corruption
- **结论**：安全。`AddEntity` 总返回末尾 row（非chunked 是 `_count`、chunked 是 lastSeg 末尾），RemoveAt(rowIdx) 时 `row == lastGlobalRow` 不 swap，movedEntity 是 default
- **验证**：读 AddEntity/AddEntityChunked 返回值，确认必为末尾；读 RemoveAt 中 `if (row == lastGlobalRow) return false` 分支
- **⚠ 隐式假设**：未来若 AddEntity 改为从中间空槽分配，此 rollback 会出 bug

### World / 实体生命周期

#### W1. `Destroy` 的 `if (++_destroyCurrentGen == 0)` 溢出处理
- **位置**：`World.EntityLifecycle.cs` Destroy
- **猜想**：`++ == 0` 看似永远不触发
- **结论**：正确。`_destroyCurrentGen` 从 0 开始 ++，溢出 int.MaxValue 后变 int.MinValue，继续 ++ 直到 -1++=0 触发 reset。需要 2^32 次 Destroy，实际不可能
- **验证**：手算 unchecked int wrap-around 序列

#### W2. `EnsureReplayReservation` 三分支（free-list / fresh slot / fallback throw）
- **位置**：`World.EntityLifecycle.cs` EnsureReplayReservation
- **猜想**：第三分支 fallback 后比较 `reserved != entity` 抛异常，似乎过于严格
- **结论**：strict 设计。real-id mode replay 要求 client 与 server 的 free-list 状态一致（文档明确"replay every frame since frame 0"）。三个分支覆盖所有合法 case，非法 case fail-fast
- **验证**：读 `FrameDelta` XML doc 的"Mirror clients must have synchronized id allocators"

#### W3. `PreScanForCapacity` 不追踪 Release/RemoveChild/Destroy 的 entity id
- **位置**：`World.cs` PreScanForCapacity
- **猜想**：注释 "Do NOT track maxEntityId" 看似遗漏
- **结论**：故意设计，防 OOM。注释明确"prevents OOM from malicious Destroy(Entity(100M,1)) pre-growing _records"。这些 op 的 entity 必须已存在，不需要预分配
- **验证**：读 PreScanForCapacity switch 注释

#### W4. `PreScanForCapacity` 中 AddChild 只追踪 parent 不追踪 child
- **位置**：`World.cs` PreScanForCapacity
- **猜想**：child 不更新 maxEntityId，似乎遗漏
- **结论**：非遗漏。child 必须先被 Reserve 或 Create（这两个 case 已更新 maxEntityId），AddChild 本身不创建 entity
- **验证**：读 EmitPendingEntitiesToDelta 确认 Reserve 总在 AddChild 之前 emit

#### W5. `NextEntityVersion` wrap 到 1（int.MaxValue → 1）
- **位置**：`World.EntityLifecycle.cs` NextEntityVersion
- **猜想**：version 回到 1 可能产生 ABA（旧 handle version==1 误匹配新 entity）
- **结论**：理论可能，实际不可能。需要 2^31 次 destroy/create 同一 slot
- **验证**：算 wrap 所需次数

#### W6. `DestroySingle` 中 `record = default; record.Version = nextVersion;` 顺序
- **位置**：`World.EntityLifecycle.cs` DestroySingle
- **猜想**：先 default 清空再设 Version，似乎 RowIndex=0 会被误用
- **结论**：正确。default 后 Archetype=null（IsOccupied=false）、RowIndex=0。设 Version=nextVersion。IsAlive 检查 IsOccupied，free 实体 not alive，RowIndex 不被读
- **验证**：读 IsAlive 实现 `record.IsOccupied && record.Version == entity.Version`

### CommandStream / 并发录制

> **C1、C2 已移除**：`AppendConcurrent` 在 `BUG_parallel_append_concurrent_resize_is_not_atomic`（见真 bug 索引）修复后不再使用外层 `while` + `lock` + `break` 结构，改为读写全在 `lock` 内一次完成。C1 的 grow 对比和 C2 的 break-in-lock 语义均不再适用。

#### C1. `MaterializeFromBatchBuffer` 链表迭代 + id>=512 线性去重
- **位置**：`CommandStream.cs` MaterializeFromBatchBuffer
- **猜想**：id < 512 用 bit set O(1) 去重，id >= 512 用线性扫描 O(n)，似乎不平衡
- **结论**：正确。hasLargeIds 时最后再 `SortTypesAndOffsets + DeduplicateSortedSpans` 兜底。重复 Add 由 `MarkBatchComponentRemoved` 标记旧条目 Removed、新条目独立处理
- **验证**：读 MaterializeFromBatchBuffer 的 hasLargeIds 分支

#### C2. `Submit` finally 中 `Clear(releaseReserved: !submitted)`
- **位置**：`CommandStream.cs` Submit
- **猜想**：异常路径下已 materialize 的 entity 留在 World，似乎清理不完整
- **结论**：设计选择。World 直写路径本就无 undo。finally 只释放 reserved id；已 materialize 的 entity 靠 `TryReleaseReserved` 返回 false（IsOccupied=true）跳过，不重复释放。文档未明确"异常后状态不确定"，但符合 World 直写语义
- **验证**：读 Clear 中 `if (releaseReserved) _world.TryReleaseReserved(entity)` 与 TryReleaseReserved 的 IsOccupied 检查

#### C3. `CancelPendingDescendants` 中 foreach + 修改 Dictionary
- **位置**：`CommandStream.cs` CancelPendingDescendants / EnqueueAllChildren
- **猜想**：foreach 期间修改 Dictionary 会抛 InvalidOperationException
- **结论**：正确。`EnqueueAllChildren` 内部 foreach 只读；修改（`CancelPendingEntity` 的 Remove、`_frozen.HierarchyByChild.Remove`）在 foreach 外的 while 循环里。每次 foreach 开始时 Dictionary version 已稳定
- **验证**：读 EnqueueAllChildren 和外层 while 的调用顺序

#### C4. `ResolveTrackedSlots` hasReal false 时 slot.Entity 不更新但 Next 清空
- **位置**：`CommandStream.cs` ResolveTrackedSlots
- **猜想**：placeholder 未解析时（resolveMap[seq].Id < 0），slot.Entity 保持 placeholder 似乎不对
- **结论**：正确。用户可通过 `slot.Value.IsPlaceholder` 检测未解析。`slots[seq]=null` 和 `s.Next=null` 总执行，避免 Slot 残留链表指针
- **验证**：读 EntitySlot.Value 实现

### FrameDelta / wire format

#### F1. `ReadVarint` 5 字节限制 + continuation 检查
- **位置**：`FrameDelta.cs` OpDecoder.ReadVarint
- **猜想**：for 循环 5 次后抛异常，似乎过早
- **结论**：正确。LEB128 32-bit varint 最多 5 字节（5 × 7 = 35 位足够覆盖 32 位）。第 5 字节后 continuation bit 还 set 表示 > 32 位，是 corrupt 或 version mismatch
- **验证**：算 LEB128 编码 32 位所需最大字节数

#### F2. `WriteEntity` 中 encId 负数处理
- **位置**：`FrameDelta.cs` EncodeEntityIdV2/DecodeEntityIdV2
- **猜想**：`id == int.MaxValue` 时 `id+1` 溢出，似乎编码错误
- **结论**：正确往返。`(uint)(int.MaxValue+1)` unchecked = 0x80000000。VarintSize 对负 int 返回 5。Decode 时 `(uint)ReadVarint()` = 0x80000000，`raw-1` = 0x7FFFFFFF = int.MaxValue
- **验证**：手算 unchecked 算术

#### F3. `ValidateHeader` 对 legacy 格式（无 "MF" magic）跳过验证
- **位置**：`FrameDelta.cs` ValidateHeader
- **猜想**：legacy delta 跳过 magic/version/endianness 检查，似乎不安全
- **结论**：向后兼容设计。legacy delta 来自旧版本库，无 header；新格式强制带 header。跨版本兼容性靠格式版本号，legacy 跳过是已知边界
- **验证**：读 DataStart 属性 `buf[0]=='M' && buf[1]=='F' ? HeaderSize : 0`

#### F4. `Deserialize` 在 `wire.ToArray()` 之后才调 `ValidateHeader`
- **位置**：`FrameDelta.cs` Deserialize
- **猜想**：header 无效时 buffer 已分配，小内存浪费
- **结论**：可接受。MaxFrameBytes 检查在 ToArray 之前防 OOM。Deserialize 是冷路径（网络接收），一次小分配无影响
- **验证**：读 Deserialize 中 MaxFrameBytes 检查的顺序

### Hierarchy

#### H1. `RemoveChild` 中 `if (parent.Id >= 0 && parent.Id < _firstChild.Length)` 为假时跳过 RemoveChildFromParent
- **位置**：`HierarchyTable.cs` RemoveChild
- **猜想**：child 的 parent 指针清空但 parent 的 children list 不移除，inconsistency
- **结论**：defensive，正常流程不触发。AddChildCore 中 EnsureCapacity 同时 grow `_parentByChild` 和 `_firstChild` 到 `parent.Id+1`，所以 parent.Id < _firstChild.Length 总成立
- **验证**：读 AddChildCore 的 EnsureCapacity 调用

#### H2. `CollectDestroySubtree` 用 `_destroyTraversalStack`（List<(Entity,bool)>）模拟递归 DFS
- **位置**：`HierarchyTable.cs` CollectDestroySubtree
- **猜想**：(entity, expanded) 元组迭代式 DFS 看似复杂易错
- **结论**：正确。模拟后序遍历（children 先 destroyOrder.Add，root 最后）。`RemoveAt(lastIndex)` 是 O(1)。finally 中 Clear 保证跨调用清洁
- **验证**：手算一个 3 层树的遍历顺序

#### H3. `RemoveDestroyed` 中 `var next = _childNext[slot]; ... FreeChildSlot(slot); slot = next`
- **位置**：`HierarchyTable.cs` RemoveDestroyed
- **猜想**：FreeChildSlot 修改 `_childNext[slot]`，似乎破坏遍历
- **结论**：正确。`next` 已在 FreeChildSlot 调用前读出，遍历继续用 next
- **验证**：读 FreeChildSlot `_childNext[slot] = _childFreeList`

### Query / QueryCache

#### Q1. `EnsureRefreshed` 两段式失效无锁读字段
- **位置**：`QueryCache.cs` EnsureRefreshed
- **猜想**：多线程同时 EnsureRefreshed 似乎 race
- **结论**：正确。Refresh/RefreshViewsOnly 内部有 `_refreshLock` 串行执行。`_lastArchetypeCount` 用 Volatile.Read/Write。失败方重试时 _lastArchetypeCount 已更新，append-only 扫描不会重复处理
- **验证**：读 Refresh/RefreshViewsOnly 的 lock 块

#### Q2. QueryCache `_snapshotArchetypes` 等字段发布可见性
- **位置**：`QueryCache.cs` AppendNewArchetypes
- **猜想**：字段发布似乎需要 volatile
- **结论**：正确。`Volatile.Write(ref _matchedArchetypeCount, ai)` 在末尾发布。配合 .NET CLR 在 x86/x64 的 strong memory model（load 是 acquire），读 `_matchedArchetypeCount` 后读 `_snapshotArchetypes` 看到一致快照
- **验证**：读 AppendNewArchetypes 末尾的三个 Volatile.Write

#### Q3. `ForEachChunkParallel` 期间 `RefreshViewsOnly` 可能覆盖 chunks 数组
- **位置**：`Query.cs` ForEachChunkParallel + `QueryCache.cs` RefreshViewsOnly
- **猜想**：并行 worker 持有 chunks 数组引用，期间 RefreshViewsOnly 从 ci=0 覆盖 `_snapshotChunkViews[ci++]`，race
- **结论**：由用户契约覆盖。RefreshViewsOnly 由 segment count 变化触发（chunked 增长），必然由 Create 实体（结构变更）引起。ForEachChunkParallel 文档明确"NOT safe for structural changes"
- **验证**：读 ForEachChunkParallel XML doc

#### Q4. `Matches` 中 Any 过滤的冗余线性扫描
- **位置**：`QueryCache.cs` Matches
- **猜想**：fast check（mask Intersects）失败后，slow check 线性扫描所有 any 组件（包括 id<512 的），冗余
- **结论**：冗余但不错误。fast check 已确认 id<512 的不在 archetype 中，线性扫描相当于只查 id>=512 的。any 列表通常很短
- **验证**：读 Matches 的 any 分支

### 持久化 / Snapshot / RestoreState

#### P1. `CaptureState` 只 backup 非空 archetype
- **位置**：`World.cs` CaptureState / RestoreState
- **猜想**：空 archetype 不 backup，似乎丢失
- **结论**：正确。RestoreState 中 `foreach arch ResetCount()` 清零所有 archetype（包括 prediction 新建的），然后只 RestoreTo backup 中的。prediction 新 archetype reset 到 count=0，data 保留但被 _count 限制访问
- **验证**：读 RestoreState 注释 "This handles prediction-created archetypes that have no backup"

#### P2. `RestoreTo` chunked 分支假设 `backup.SegmentCount <= arch.SegmentCount`
- **位置**：`WorldStateSnapshot.cs` RestoreTo chunked 分支
- **猜想**：如果 backup.SegmentCount > arch.SegmentCount 似乎越界
- **结论**：不可能。archetype 的 segment 单向增长（GrowChunked 只增不减），所以 capture 时 SegmentCount <= restore 时 SegmentCount。RestoreTo 循环 0..backup.SegmentCount-1，多余 segment 的 Count 被 ResetCount 设为 0
- **验证**：读 GrowChunked（无 shrink 路径）+ 已有测试 `Capture_restore_round_trip_is_chunked_aware_after_segment_growth_during_prediction`
- **⚠ 注意**：此条只覆盖 backup.IsChunked==true 的情况。backup.IsChunked==false 但 arch 已 chunked 是真 bug（见真 bug 索引）

#### P3. `RestoreState` 只覆盖前 `snapshot.EntitySlotCount` 个 record
- **位置**：`World.cs` RestoreState
- **猜想**：prediction 期间创建的高 id entity 的 record 保留，似乎可被访问
- **结论**：正确。`_entitySlotCount = snapshot.EntitySlotCount` 限制访问，IsAlive 检查 `(uint)id >= (uint)_entitySlotCount` 返回 false，prediction entity 不可访问
- **验证**：读 IsAlive 的边界检查

#### P4. `RestoreState` 中 `_parentByChild` 可能缩短（重新分配到 snapshot 长度）
- **位置**：`World.cs` RestoreState + `HierarchyTable.cs` RestoreState
- **猜想**：prediction 期间 grow 的高 id entity 访问 `_parentByChild[id]` 越界
- **结论**：正确。这些 entity 的 record 已被 `_entitySlotCount` 限制（IsAlive false），不会进入 HierarchyTable 访问。HasAnyRelations 等也有 length 检查
- **验证**：读 HasAnyRelations 的 `(uint)entity.Id >= (uint)_parentByChild.Length` 检查

#### P5. `ComputeChecksum` 不 hash free-list
- **位置**：`WorldSnapshot.cs` ComputeChecksum / ComputeCanonicalChecksum
- **猜想**：两个 world free-list 不同但 checksum 相同，似乎漏检
- **结论**：设计选择。free-list 不是 logical state，间接通过后续 entity id 分配影响 checksum（如果 free-list 不同导致后续 Create 拿到不同 id，会在后续帧 checksum 中暴露）。canonical checksum 才直接 hash free-list（id + version）。两个 checksum 用途不同
- **验证**：对比 ComputeChecksum 与 ComputeCanonicalChecksum 的 XML doc

#### P6. `EmitHierarchyToDelta` 排序但 `ApplyHierarchyToWorld` 不排序
- **位置**：`CommandStream.cs` EmitHierarchyToDelta / ApplyHierarchyToWorld
- **猜想**：Submit 路径（不排序）与 Replay 路径（排序）hierarchy apply 顺序不一致
- **结论**：不影响最终状态。Dictionary 中同一 child 只有一个 entry（后者覆盖），Apply 顺序不影响每个 child 独立的关系建立。EmitHierarchyToDelta 排序是为了 lockstep delta 的跨 host 确定性
- **验证**：读 HierarchyByChild 的索引语义（child 为 key，唯一）

### 并发 / 内存模型

#### CC1. `GetOrCreateStoreParallel` double-check locking
- **位置**：`CommandStream.cs` GetOrCreateStoreParallel
- **猜想**：第一层 `_frozen.Stores[id]` 在 lock 外读，似乎 race
- **结论**：正确。引用类型赋值在 .NET 是 atomic（ECMA-335 Partition I），读到的要么 null 要么完整引用。null 时进入 lock 重新读 + 创建
- **验证**：查 ECMA-335 引用类型赋值原子性保证

#### CC2. `GetOrCreateStoreParallel` 中 Stores 数组 resize 与读的 race
- **位置**：`CommandStream.cs` GetOrCreateStoreParallel
- **猜想**：resize 替换 Stores 数组，外部读旧引用越界
- **结论**：正确。Array.Resize 在 lock 内，退出 lock 有 memory barrier。读字段在 lock 外，但读到旧引用时 id < 旧长度（否则进入 lock resize），读到新引用时 id < 新长度。两种情况都安全
- **验证**：读 double-check 的两个分支条件

#### CC3. `AppendDestroyConcurrent` resize + write 都在 `_storeCreateLock` 内
- **位置**：`CommandStream.cs` AppendDestroyConcurrent
- **猜想**：与 AppendConcurrent 一样的 race？
- **结论**：无 race。AppendDestroyConcurrent 的 resize 和 write 都在同一个 lock 内，没有"读长度→后写"的窗口。**与 AppendConcurrent 形成对比**：AppendConcurrent 只 resize 在 lock 内，write 在 lock 外（见真 bug 索引）
- **验证**：对比 AppendDestroyConcurrent 与 AppendConcurrent 的 lock 范围

#### CC4. `ComponentRegistry` copy-on-write 的并发读
- **位置**：`ComponentRegistry.cs` GetOrCreate
- **猜想**：lock 外读 Dictionary 似乎不安全
- **结论**：正确。Volatile.Read(ref _snapshot) 读快照引用，写者创建新 Dictionary copy（不修改旧 Dictionary），Volatile.Write 发布新 snapshot。读者读到旧 snapshot 的 Dictionary 仍然有效（不被写者修改）
- **验证**：读 GetOrCreate 的 `new Dictionary<...>(snapshot.TypeToId)` copy

### EntityFieldResolver

#### E1. `ScanAndCache` 中 `ScanType` 在 lock 外执行
- **位置**：`EntityFieldResolver.cs` ScanAndCache
- **猜想**：两个线程同时 ScanAndCache 同一 typeId，似乎重复计算
- **结论**：正确。ScanType 是 pure function（除 ThrowIfNestedEntity 抛异常外无副作用）。两个线程都得一致结果，进入 lock 串行，第一个写入 `arr[id]`，第二个看到 `arr[id]` 非空返回第一个的结果
- **验证**：读 ScanAndCache 的 lock 内 `if (arr[id] is null)` 检查

#### E2. `ScanType` 中 `Marshal.OffsetOf` 对 auto layout 的处理
- **位置**：`EntityFieldResolver.cs` ScanType
- **猜想**：auto layout 的 OffsetOf 行为未定义
- **结论**：正确。代码先检查 `layout is not null && layout.Value == LayoutKind.Auto` 抛异常，然后再调 OffsetOf。C# 默认 struct 是 Sequential（StructLayoutAttribute 可能返回默认 Sequential 实例或 null）。代码对 null 跳过检查（视为 Sequential），对显式 Auto 抛异常
- **验证**：读 ScanType 的 if 顺序

#### E3. `ResolveInPlace` 中 offset 边界
- **位置**：`EntityFieldResolver.cs` ResolveInPlace
- **猜想**：offset 可能超出 data.Length
- **结论**：正确。offset 来自 `Marshal.OffsetOf(type, fieldName)`，必然 < component size。`data.Length = sizes[i] = component size`
- **验证**：读 ResolveInPlace 调用方传入的 data 大小

---

## 快速排除清单（低价值判断，下次扫到直接跳过）

> 这些是"扫一眼就能确认 OK"的微判断，记录在此仅为避免审阅者重复驻足。

- `_destroyVisitedGen` 初始 `[]` 时 Destroy 仍安全：HasChildren 第一分支不依赖 visitedGen，且 EnsureDestroyScratchCapacity 在 AcquireEntityIdUnsafe 中已 grow
- `FrameDelta._buffer` 永不为 null：构造时 `Array.Empty<byte>()`，`fixed` 安全
- `ComponentType.Value` 假设 >= 0：ComponentRegistry 只产生非负 id
- `Entity.IsValid` vs `world.IsAlive` 区别：文档明确（前者只看 handle shape，后者查 world 状态）
- `RecordId / _count / _deferredSeq` 等 int 计数器溢出：实际不可能（需 2^31+ 次操作）
- `Entity(0, 0).IsValid == false`：Version 必须 > 0
- `Array.Empty<byte>()` 不为 null：fixed/AsSpan 安全
- `World.Dispose` 后调用 API 行为未定义：用户责任，DEBUG 模式 AssertNotDisposed 帮助发现
- `ComponentStore<T>.Clear` 只设 `_count=0` 不清数组：下次 Append 覆盖，ApplyToWorld 按 count 索引
- `CommandStream.Clear` 中 `_trackedBySeq=[]` 不显式 null Slot.Entity：EntitySlot 仍持有 Slot 引用，Slot.Entity 保持 placeholder，用户可通过 IsPlaceholder 检测
- `List<Entity>.EnsureCapacity` 只 grow 不 shrink：RestoreState 后保留大容量，无害
- `EnsureBatchCapacity` 中 `requiredCount + batchCount/2` 理论溢出：实际 entity 数不可能接近 int.MaxValue
- `WriteColumnOrderedTo` 中 `count * size` 理论溢出：实际 entity 数 × 组件大小不可能接近 int.MaxValue
- `HierarchyTable.Reset` 不清 `_childEntity`/`_childNext`：`_childSlotCount=0` 后所有 slot 视为未使用，下次 AllocateChildSlot 复用并覆盖
- `GetSingleton<T>` 中 chunked archetype 的 `GetEntity(0)`：segment[0] 来自 ConvertToChunked（Count=_count>0），非空
- `EntityAccessor.Set<T>` 用 `GetComponentIndexFast`（无边界检查）：文档明确"Assumes the component already exists"

---

## 决策

- **为什么单列一个文件而不是散落到各模块 kb**：审阅流程是"先读 INDEX → 找审阅相关页 → 开始审"，单一入口最契合；跨模块猜想也有安放处
- **为什么真 bug 只放索引不展开**：真 bug 的单一事实来源是 `BUG_` 测试代码 + git 历史，本文件展开会重复且易过期
- **为什么记录"快速排除清单"**：这些判断虽然低价值，但下一个审阅者仍会驻足思考几秒；一行带过可省去这些时间，且不污染主清单

### 存储 / Archetype（续）

#### A7. `ConvertToChunked` fast path 不更新 `_columnByteOffsets`
- **位置**：`Archetype.Storage.cs` ConvertToChunked line 39-53
- **猜想**：fast path（`_capacity == _segmentCapacity`）只包装数组，不更新 `_columnByteOffsets`，是否应同步？
- **结论**：无需同步。`_capacity == _segmentCapacity` 时 flat buffer 的 column layout 与 segment-capacity 的 layout 相同（capacity 相同 → `ComputeColumnLayout` 结果相同），`_columnByteOffsets` 已正确。General path（line 89）才更新。
- **验证**：读 `ComputeColumnLayout` 是 pure function over elementSizes + capacity，capacity 相同则 offsets 相同

#### A8. `ConvertToChunked` general path 中 `segOffset` vs `_columnByteOffsets` 的 segStart 索引
- **位置**：`Archetype.Storage.cs` ConvertToChunked general path line 60-87
- **猜想**：source 用 `_columnByteOffsets[col] + segStart * elemSize`，dest 用 `segOffsets[col]`。segStart 是全局行索引（基于 flat buffer），dest 只偏移列起始——是否一致？
- **结论**：正确。Source 侧：`_columnByteOffsets[col]` 是 flat buffer 中列 col 的起始字节，加上 `segStart * elemSize` 得到第 segStart 行的数据。Dest 侧：segData 是 segment 的 byte[]，`segOffsets[col]` 是按 `_segmentCapacity` 布局的列起始。`CopyBlockUnaligned` 写 `rowsInSeg * elemSize` 字节，正好是从列起始开始的 rowsInSeg 行数据。
- **验证**：手算一个 3 列、2 segment 的通用路径数据复制

#### A9. `CopyColumnFrom` 对有 Count=0 的 segment 可能越界
- **位置**：`Archetype.Storage.cs` CopyColumnFrom line 857-926
- **猜想**：chunked 模式下遍历 segment，如果某个 segment 的 Count=0（如 RestoreFlatBackup 后残留的尾部空 segment），`take = Math.Min(remaining, Math.Min(srcAvailable, dstAvailable))` 中 dstAvailable=0 或 srcAvailable=0→take=0→remaining 不变→segIdx++。若所有尾部 segment 都是空，segIdx 持续增长直到越界。
- **结论**：不可达。调用方（`CopyColumnsFrom`）保证 `count <= _count` 且 `_count` 等于所有 segment Count 之和。`remaining` 在耗尽所有非空 segment 的数据前降到 0，不会进入尾部空 segment。**但此结论依赖 `_count == sum(segment.Count)` 不变式**——若某条路径破坏该不变式，就会在此处越界。
- **验证**：读 CopyColumnsFrom 的 count 范围检查 + 确认 ReserveRows、AddEntity、RemoveAt 都维护该不变式

#### A10. `ReadColumnFrom` chunked 分支遇到空 segment 死循环
- **位置**：`Archetype.Storage.cs` ReadColumnFrom line 601-620
- **猜想**：与 A5 相同，但补充验证 RestoreFlatBackup 路径。RestoreFlatBackup 可能产生尾部 Count=0 的 segment。
- **结论**：安全。同上，`count`=调用方读取的 entity 数≤`_count`=非空 segment Count 之和。`remaining` 在到达空 segment 前降到 0。
- **验证**：读 RestoreFlatBackup 的 `seg.Count = take` 和 `_count = count` 设置

#### A11. `WriteCreatedEntitiesAndLocationsChunked` 不检查 archetype 是否实际 chunked
- **位置**：`World.EntityLifecycle.cs` WriteCreatedEntitiesAndLocationsChunked line 330-364
- **猜想**：如果 dispacher（WriteCreatedEntitiesAndLocations .cs line 282）意外调用此方法在非 chunked archetype 上，`WriteEntityAt` 也能正确工作（它检查 `_isChunked` 并走对应分支），所以是安全的。
- **结论**：安全。`WriteEntityAt` 是 dual-mode 的，两种路径都正确处理。且 dispatcher 不会错派。
- **验证**：读 WriteEntityAt 的 `if (!_isChunked)` 分支

#### A12. `FeedColumnData` 中 `_segments[s].Count` 为 0 时 take=0
- **位置**：`Archetype.Storage.cs` FeedColumnData line 714-732
- **猜想**：与 A10 相同模式，尾部空 segment 可能死循环
- **结论**：安全。`rowCount` 来自 ChunkView.Count 或 Query 枚举的 EntityCount，不会超过非空 segment 总和。
- **验证**：读所有 FeedColumnData 调用方

### World / 实体生命周期（续）

#### W7. `DestroySingle` 中 `Record = default; record.Version = nextVersion` 后 `_count--` 前 movedEntity 更新记录的时序
- **位置**：`World.EntityLifecycle.cs` DestroySingle line 197-219
- **猜想**：`arch.RemoveAt(row, out movedEntity)` 已在 chunked 模式下递增 `_flatEntitiesGeneration`。但 movedEntity 的 record 更新（line 214-218）在 `record = default; record.Version = nextVersion`（line 208-209）之后——是否可能 record 已被重用？
- **结论**：安全。`record` 是 `_records[entity.Id]` 的 ref，`movedRecord` 是 `_records[movedEntity.Id]` 的 ref。两个不同的 slot，不会互相影响。`record` 被清零（版本递增）与 `movedRecord` 更新无关。
- **验证**：确认 entity.Id != movedEntity.Id（swap-remove 只在 row != lastGlobalRow 时移动，被移动的 entity 不可能是被销毁的 entity）

#### W8. `WriteCreatedEntitiesAndLocationsFlat` 中 `GetReservedEntities` 在 chunked 模式下抛异常
- **位置**：`World.EntityLifecycle.cs` line 297
- **猜想**：`ReserveRows` 可能在调用 `WriteCreatedEntitiesAndLocations` 之前将 archetype 提升为 chunked，但 dispacher（line 282）在 ReserveRows 之后检查 `IsChunked`，不会派发到 flat 分支。
- **结论**：安全。`ReserveRows` 可能提升，但 dispatcher 在 `ReserveRows` 返回后才检查 `IsChunked`，能看到最新状态。
- **验证**：读 `CreateMany` → `ReserveRows` → `WriteCreatedEntitiesAndLocations` 的调用顺序

### WorldClone / 全量克隆

#### WC1. `Clone` 中 `CopyColumnsFrom` 可能跨 chunked/non-chunked 边界
- **位置**：`WorldClone.cs` line 30，`Archetype.Storage.cs` CopyColumnsFrom/CopyColumnFrom
- **猜想**：srcArch 可能是 chunked 的，dstArch 在 `ReserveRows` 后可能是 chunked 的 → `CopyColumnFrom` 的 general path 处理 4 种模式组合。是否所有组合正确？
- **结论**：正确。`CopyColumnFrom` general path 用 `_isChunked` 判断 source/dest 各自模式，`segments[segIdx].Count - consumed`（chunked）和 `remaining`（non-chunked）在任一种组合下正确计算可用行数。
- **验证**：手算 4 种组合（S!=C, S=C, D!=C, D=C）的循环迭代

#### WC2. `Clone` 中 `dstArch` 是新创建的但 `target.GetOrCreateArchetype` 可能返回已存在的 archetype
- **位置**：`WorldClone.cs` line 24
- **猜想**：target 是全新的 World（刚 `Reset` 清除所有 archetype），所以 `GetOrCreateArchetype` 创建新 archetype。Signature 不会重复。安全。
- **结论**：安全。`Reset` 清空 `_archetypes` 字典，`GetOrCreateArchetype` 创建全新实例。
- **验证**：读 `Reset()` 中 `_archetypes.Clear()`

### QueryCache / ChunkView 快照

#### Q5. `EnsureRefreshed` 中 segment count 变化检测用 `_archetypeExpectedViews[i]`
- **位置**：`QueryCache.cs` EnsureRefreshed line 103-126
- **猜想**：chunked 增长（新 segment 加入）使 `arch.SegmentCount` 变化，但 `EnsureRefreshed` 只比较 `expected != _archetypeExpectedViews[i]`。如果 `_archetypeExpectedViews` 数组不够大？
- **结论**：安全。EnsureRefreshed 要么先走 `currentCount != _lastArchetypeCount`（新 archetype 加入，此时调用完整 Refresh 重建所有数组），要么走 segment count 比较。后者的前提是 `_matchedArchetypeCount > 0` 且 `_archetypeExpectedViews` 在 `_matchedArchetypeCount` 范围内（由 AppendNewArchetypes 保证同步增长）。
- **验证**：读 AppendNewArchetypes 中对 `_archetypeExpectedViews` 的 Array.Resize 和 `Volatile.Write(ref _matchedArchetypeCount, ai)` 的发布顺序

#### Q6. `RefreshViewsOnly` 中 `_snapshotChunkViews` 在迭代时被覆盖
- **位置**：`QueryCache.cs` RebuildChunkViews line 152-186
- **猜想**：`_snapshotChunkViews[ci++]` 从 0 开始覆盖，并行 worker 可能正在读旧 chunk views
- **结论**：由用户契约覆盖（同 Q3）。`RefreshViewsOnly` 只在 segment count 变化时触发，这说明有结构变更正在进行，而 `ChunkView` 的文档禁止跨结构变更保留。
- **验证**：读 ChunkView struct XML doc 第一段

## 入口

- 第一次审阅代码前：本文档 → `kb-design-rationale.md`（避免提已被拒绝的优化）→ `kb-architecture-review.md`（已知问题段）
- 产生新猜想时：先查本文件对应模块段 + 快速排除清单
- 写新猜想：append 到对应模块段，4 字段格式

## 坑点

- **本文件需要持续维护**：代码改动后，某条"非 bug"可能变成真 bug（如 A6 的 AddEntity 末尾假设被打破）。任何代码改动后审阅时，应顺手检查相关条目是否仍成立
- **真 bug 索引依赖测试套件**：若 `BUG_` 测试被重命名/删除，索引失效。改测试名时同步更新本文件
- **不要把推理过程写进来**：本文件只记结论 + 指路。推理过程留在 git commit message 或 PR 描述里
