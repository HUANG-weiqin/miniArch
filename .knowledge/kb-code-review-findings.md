---
title: 代码审阅发现清单
module: Meta
description: 历次代码审阅中产生过的猜想与结论。真 bug 索引 + 已排除的非 bug 猜想。AI 审阅前必读，避免重复验证已知结论。
updated: 2026-07-05 (CS9: 第二版，base class 彻底删除 public throw 桩; 新增 API surface 反射测试替代 9 个 throw 测试)
---
# 代码审阅发现清单

> **审阅前必读**。这个文档只记录**结论 + 指路**，不重复推理过程。
> 真 bug 由 `BUG_` 前缀测试证明（本文件只放索引）；非 bug 猜想按模块归档。
>
> **当前状态**：全部 11 个 BUG 已修复并转为回归测试（通过）。

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

## 已确认的真 bug

### 已修复（`BUG_` 测试转为回归测试，现在通过）

> 全部 11 条 bug 均已修复，`BUG_` 前缀测试现在通过，充当回归守卫。

| 测试名 | 位置 | 一句话描述 | 修复 |
|---|---|---|---|
| `BUG_capture_nonchunked_then_promote_then_restore_crashes` | `WorldStateSnapshot.cs:RestoreTo` | CaptureState 备份非 chunked archetype，prediction 期间 archetype 被 promote 为 chunked 后，RestoreState 走非 chunked 分支调用 `arch.CopyDataFrom`/`GetEntityStorageUnsafe`，撞上 `_data=null`/零长度缓存数组崩溃 | `RestoreTo` 检测 backup 非_chunked 但 arch 已 chunked 时，调用新增 `Archetype.RestoreFlatBackup`，按列从 flat backup（backup-time offsets）拆分到 segments（current segment-capacity offsets）；chunked→chunked 路径改用 `ResetCount` 清零多余 segment count |
| `BUG_parallel_append_concurrent_resize_is_not_atomic` | `CommandStream.cs:AppendConcurrent` | `ParallelRecording=true` 下并发 append 时，`_data`/`_entities`/`_kinds` 三个独立 `Array.Resize` 非原子；等待循环只看 `_data.Length` 作闸门，外部线程读到 `_data` 已扩容就退出，写 `_entities[slot]`/`_kinds[slot]` 越界或写入废弃数组 | `AppendConcurrent` 改为在 `_resizeLock` 内完成 ensure+write（与 `AppendDestroyConcurrent` 同模式），消除三数组观测不一致和数据丢失（resize Array.Copy 与延迟写竞争）|
| `BUG_capacity_above_segment_capacity_corrupts_row_mapping_on_promote` | `Archetype.Storage.cs:ConvertToChunked` | 当 non-chunked `_capacity > _segmentCapacity` 时（构造大 capacity 或 `EnsureCapacity` doubling 分支 `newCapacity=max(required, _capacity*2)` 把容量顶过 segment 阈值），promote 路径把超长 buffer 直接当 segment[0]，之后 `GetSegmentAndLocal` 按 `_segmentMask` 切分会把 `globalRow ≥ _segmentCapacity` 的数据映射到不存在的 segment | 删除 `NormalizeForChunked`，`ConvertToChunked` 统一负责 canonical split：`_capacity == _segmentCapacity` 时 fast-path wrap（正常 doubling 阈值 promote 零拷贝），否则 general path 按 `_segmentCapacity` 分割为 N 个标准 segment 并 rebase column offsets（提取 `ComputeColumnLayout` 纯函数复用）|
| `BUG_parallel_destroy_on_pending_entity_does_not_cancel_like_single_threaded` | `CommandStream.cs:Destroy` | `ParallelRecording=true` 下 `Destroy(pendingEntity)` 直接调 `AppendDestroyConcurrent`，不检查 pending 状态也不调 `CancelPendingEntity`/`CancelPendingDescendants`；Submit 时该 entity 被 materialize 再 destroy，pending descendants 也被 materialize——与单线程语义（cancel 整棵子树，从不 materialize）不一致，导致 alive count / id allocator 分叉 | 并行 `Destroy` 在 `_storeCreateLock` 内复制单线程的 pending-check + cancel 逻辑；删除不再使用的 `AppendDestroyConcurrent` |
| `BUG_order_by_component_supports_chunked_archetypes` | `Query.cs:OrderedComponentEnumerator.Initialize` | `OrderByComponent<T>` 对匹配 archetype 批量读组件值时直接调用 `GetComponentSpan<T>()`；该 API 只支持非 chunked，chunked archetype 会抛 `InvalidOperationException`，导致公共排序 API 在大 archetype 上不可用 | chunked 分支按 segment 顺序调用 `GetSegmentComponentSpan<T>()` 拷贝组件值，保持与 `GetEntityStorageUnsafe()` flatten 顺序一致 |
| `BUG_chunked_restore_pooled_larger_backup_arrays_overflow_smaller_destination` | `WorldStateSnapshot.cs:ArchetypeBackupEntry.CopyFromChunked` | 修复后的回归测试在 Debug 下仍会被错误 `Debug.Assert(seg.Data.Length >= segCap)` 拦截；零组件 chunked archetype 的 `seg.Data.Length == 0` 是合法状态（没有组件列），断言把合法状态误判为损坏 | 删除该无效断言；真实安全条件由后续按列/按 `entityCount` 拷贝和已有回归测试覆盖 |
| `BUG_query_chunks_refresh_when_archetype_promotes_to_single_chunk_segment`<br>`BUG_query_chunks_refresh_existing_view_shape_when_archetype_count_also_changes`<br>`BUG_query_chunks_refresh_segment_growth_when_archetype_count_also_changes` | `QueryCache.cs:EnsureRefreshed` / `AppendNewArchetypes` / `ChunkView.cs:GetSpan` | Query 缓存只用 view 数量检测 chunk 快照是否失效；非 chunked archetype 晋升为 chunked 但仍只有 1 个 segment 时，旧 `ChunkView(segmentIndex=-1)` 未刷新，随后 `GetSpan<T>()` 走 chunked 分支访问 segment `-1` 越界。若同次还新增 archetype，`Refresh()` 的 append-only 路径也会漏重建旧 ChunkView，或在 segment 增长时首帧少返回 chunk | `_archetypeExpectedViews` 改为记录 view shape（non-chunked = -1，chunked = SegmentCount），即使 view 数量同为 1 也能检测模式变化；`AppendNewArchetypes` 追加新 archetype 前先检查已匹配 archetype 的 shape 漂移，必要时先重建 ChunkView |
| `BUG_reserverows_deadlocks_when_promotion_creates_multiple_empty_segments` | `Archetype.Storage.cs:ReserveRows` | ReserveRows 在 EnsureCapacity 晋升 chunked 后，GrowChunked 批量创建多空段；ReserveRows 只填末段（lastSegIdx = _segmentCount-1），末段满后无限循环 | `ReserveRows` 改为扫码首个非满段，而非固定填末段。同时 `AddEntityChunked` 同步改为首个非满段填充 — 同一根因统一修复 |
| `BUG_clone_deadlocks_on_archetype_with_large_component` | `WorldClone.cs:27` (ReserveRows) | 同根因，经公共 API `World.Clone()` 触发：克隆 >segCap 实体的 16KB+ 组件 archetype 死锁 | 同上根因修复 |
| `BUG_flat_entity_index_mismatches_global_row_when_segment_hole_exists` | `Archetype.Storage.cs:AddEntityChunked` | 段空洞时平坦索引 ≠ 全局行号，Save 和 CanonicalChecksum 读入空段 → 静默数据损坏 | `AddEntityChunked` 改填首个非满段，空洞不再持久存在 |
| `BUG_parallel_pending_Add_Set_Remove_bypass_batch_buffer` | `ParallelCommandStream.cs:Add/Set/Remove` | `ParallelCommandStream` 对 pending-batch 实体（刚 `Create`/`Clone`）的 `Add/Set/Remove` 走 `AppendConcurrent`（ComponentStore 路径）而非 `WritePendingComponent`（batch buffer 路径）；Submit 时实体被空身 materialize，随后 `ApplyComponentStores` 尝试对不存在组件执行 `Add`/`Set` 抛异常 | `ParallelCommandStream.Add/Set/Remove` 改为：alive 实体走 `AppendConcurrent`（无锁快路径），否则在 `_storeCreateLock` 内调 `TryGetPendingBatch` + `WritePendingComponent`/`MarkBatchComponentRemoved` |

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
- **位置**：`CommandStreamCore.cs` MaterializeFromBatchBuffer
- **猜想**：id < 512 用 bit set O(1) 去重，id >= 512 用线性扫描 O(n)，似乎不平衡
- **结论**：正确。hasLargeIds 时最后再 `SortTypesAndOffsets + DeduplicateSortedSpans` 兜底。重复 Add 由 `MarkBatchComponentRemoved` 标记旧条目 Removed、新条目独立处理
- **验证**：读 MaterializeFromBatchBuffer 的 hasLargeIds 分支

#### C2. `Submit` finally 中 `Clear(releaseReserved: !submitted)`
- **位置**：`CommandStreamCore.cs` Submit
- **猜想**：异常路径下已 materialize 的 entity 留在 World，似乎清理不完整
- **结论**：设计选择。World 直写路径本就无 undo。finally 只释放 reserved id；已 materialize 的 entity 靠 `TryReleaseReserved` 返回 false（IsOccupied=true）跳过，不重复释放。文档未明确"异常后状态不确定"，但符合 World 直写语义
- **验证**：读 Clear 中 `if (releaseReserved) _world.TryReleaseReserved(entity)` 与 TryReleaseReserved 的 IsOccupied 检查

#### C3. `CancelPendingDescendants` 中 foreach + 修改 Dictionary
- **位置**：`CommandStreamCore.cs` CancelPendingDescendants / EnqueueAllChildren
- **猜想**：foreach 期间修改 Dictionary 会抛 InvalidOperationException
- **结论**：正确。`EnqueueAllChildren` 内部 foreach 只读；修改（`CancelPendingEntity` 的 Remove、`_frozen.HierarchyByChild.Remove`）在 foreach 外的 while 循环里。每次 foreach 开始时 Dictionary version 已稳定
- **验证**：读 EnqueueAllChildren 和外层 while 的调用顺序

#### C4. `ResolveTrackedSlots` hasReal false 时 slot.Entity 不更新但 Next 清空
- **位置**：`CommandStreamCore.cs` ResolveTrackedSlots
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

#### F3. FrameDelta 无 header 设计
- **位置**：`FrameDelta.cs` 整体
- **猜想**：没有 magic/version/endianness 头，直接裸 op 数据，似乎不安全
- **结论**：YAGNI 后的确定设计。FrameDelta 的 wire 格式直接是 op 数据流（无 header、无 magic、无版本号），不保留任何预留/占位字节。调用者只需确保序列化/反序列化端 ComponentRegistry 一致，编码极致简化后不再需要任何自描述元数据
- **验证**：`EnsureHeader`/`ValidateHeader`/`DataStart`/`HeaderSize` 全部删除，`_buffer[0]` 就是第一个 op 的 tag

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
- **位置**：`CommandStreamCore.cs` EmitHierarchyToDelta / ApplyHierarchyToWorld
- **猜想**：Submit 路径（不排序）与 Replay 路径（排序）hierarchy apply 顺序不一致
- **结论**：不影响最终状态。Dictionary 中同一 child 只有一个 entry（后者覆盖），Apply 顺序不影响每个 child 独立的关系建立。EmitHierarchyToDelta 排序是为了 lockstep delta 的跨 host 确定性
- **验证**：读 HierarchyByChild 的索引语义（child 为 key，唯一）

### 并发 / 内存模型

#### CC1. `GetOrCreateStoreParallel` double-check locking
- **位置**：`CommandStreamCore.cs` GetOrCreateStoreParallel
- **猜想**：第一层 `_frozen.Stores[id]` 在 lock 外读，似乎 race
- **结论**：正确。引用类型赋值在 .NET 是 atomic（ECMA-335 Partition I），读到的要么 null 要么完整引用。null 时进入 lock 重新读 + 创建
- **验证**：查 ECMA-335 引用类型赋值原子性保证

#### CC2. `GetOrCreateStoreParallel` 中 Stores 数组 resize 与读的 race
- **位置**：`CommandStreamCore.cs` GetOrCreateStoreParallel
- **猜想**：resize 替换 Stores 数组，外部读旧引用越界
- **结论**：正确。Array.Resize 在 lock 内，退出 lock 有 memory barrier。读字段在 lock 外，但读到旧引用时 id < 旧长度（否则进入 lock resize），读到新引用时 id < 新长度。两种情况都安全
- **验证**：读 double-check 的两个分支条件

#### CC3. `AppendDestroyConcurrent` resize + write 都在 `_storeCreateLock` 内
- **位置**：`CommandStreamCore.cs` AppendDestroyConcurrent
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

#### A16. `ConvertToChunked` 不 bump `_flatEntitiesGeneration`（H4.1 latent issue）
- **位置**：`Archetype.Storage.cs` ConvertToChunked line 34-75
- **猜想**：H4.1：ConvertToChunked 不 bump generation。ForceChunkedForTesting 后立即 GetEntities 可能返回 null cache → NRE。
- **结论**：非 bug。`_cachedFlatEntitiesGeneration` 在构造时初始化为 -1，而 `_flatEntitiesGeneration` 默认 0。首次 chunked GetEntityStorageUnsafe 调用时 -1 != 0 触发 rebuild，缓存被正确创建。后续调用（无中间 mutation）时 generation 匹配，但已缓存的数组内容与 segment 数据一致（ConvertToChunked 复制了 entity 数据到 segment，缓存是从旧 flat 数组构建的相同数据）。如果有中间 mutation，AllocateRows/WriteEntityAt/RemoveAt 会 bump generation 使下一次 GetEntityStorageUnsafe 进入 rebuild。
- **验证**：代码审查 + `GetEntities_after_ConvertToChunked_returns_correct_data` 测试覆盖（确认无 NRE 且数据正确）。

#### A17. AllocateRows 中 `ref var seg = ref _segments[segIdx]` 在 EnsureCapacity + Array.Resize 后安全（H3.1）
- **位置**：`Archetype.Storage.cs` AllocateRows line 196-209
- **猜想**：`ref var seg = ref _segments[segIdx]` 后续 if `EnsureCapacity` → `GrowChunked` → `Array.Resize(ref _segments)`，`ref seg` 指向旧数组 → 数据损坏。
- **结论**：安全。当 `available == 0` 时调用 `EnsureCapacity` 然后 `continue`。`continue` 跳回 while 循环顶部，`segIdx` 和 `ref seg` 从当前 `_segments`（可能已 resize）重新获取。当 `available > 0` 时直接使用 seg 且不调用 EnsureCapacity，无 resize → ref 稳定。代码审查确认无路径在 Array.Resize 后使用旧 ref。
- **验证**：代码审查 + `AllocateRows_bulk_spans_multiple_segments` 测试覆盖。

#### A18. AllocateRows 全段满时 EnsureCapacity 总是增长容量，不会死循环（H3.3）
- **位置**：`Archetype.Storage.cs` AllocateRows line 205-209
- **猜想**：`available == 0` 时 EnsureCapacity 可能不增长（`requiredCapacity <= Capacity`）→ continue → 再次 available == 0 → 死循环。
- **结论**：不可达。所有段满时 `_count == Capacity`（因 `_count = sum(Count)` 且 `Count == Entities.Length` 即 Capacity）。`remaining > 0` → `requiredCapacity = _count + remaining > Capacity` → `EnsureCapacity` 调用 `GrowChunked(requiredCapacity - Capacity) > 0` → 总增长。chunked 模式下无 flat 模式的整数溢出风险。
- **验证**：代码审查 + `AllocateRows_when_all_segments_full_creates_new_segment` 测试覆盖。

#### A19. `_capacity * 2` 整数溢出在 flat EnsureCapacity 中不可达（H1.5）
- **位置**：`Archetype.Storage.cs` EnsureCapacity line 131
- **猜想**：unchecked 算术下 `_capacity * 2` 溢出 → `new Entity[负数]` 崩溃。
- **结论**：安全。`_capacity * 2 > _segmentCapacity` 转换守卫确保 archetype 在 capacity 远未达到溢出阈值前就晋升为 chunked。对于最小 segCap 的组件（Position 8 bytes, segCap=262144），转换发生在 `_capacity > 131072`（溢出阈值 ~10亿）。对于最大 segCap 的组件（空 tag, segCap=65536），转换发生在 `_capacity > 32768`。均远低于溢出阈值。
- **验证**：代码审查设计级评估。

#### A20. 3 个 chunked 命名测试实际运行在 flat 模式（2026-07-05 chunk 诚实性审计）
- **位置**：`ArchetypeTests.cs` Chunked_mode_works_with_world_operations (L225), Chunked_mode_queries_return_correct_data (L267), CaptureState_restoreState_mixed_modes (L1959)
- **猜想**：这些测试名含"chunked"但实际从未进入 chunked 模式：Position segCap=262144，150-200 实体永远无法自然触发 promotion。
- **结论**：确认。三个测试均通过 `MiniQueryCache` 获取 archetype 后调用 `ForceChunkedForTesting()` + `Assert.True(IsChunked)` 修复。同时为所有 ~49 个 `ForceChunkedForTesting()` 调用点补全了缺失的 `Assert.True(IsChunked)` 断言，确保任何未来重构导致 `ForceChunkedForTesting` 不生效时能立即暴露。修复过程中发现 fuzz 测试中 `if` 无大括号导致断言跑在循环每次迭代上，已在 step-200/500 promotion 块补上大括号。
- **验证**：`dotnet test -c Release` 607 通过，`HeroComing.Perf --check-baseline` 通过。

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

#### A13. `RemoveAt` 所有 5 个调用方正确更新 `movedEntity.RowIndex`
- **位置**：`World.EntityLifecycle.cs:202`，`World.StructuralChange.cs:57,71,124,186` — 共 5 个 `RemoveAt` 调用方
- **猜想**：RemoveAt 调用方可能未正确处理 `movedEntity.RowIndex` 更新，导致 entity location 表与实际存储脱同步
- **结论**：全部正确。3 个 `out _` 回退路径（lines 57, 124, 186）安全——它们移除刚由 AddEntity 返回的末行（row == lastGlobalRow），RemoveAt 判定 row==last→不 swap→movedEntity=default。2 个显式更新路径（lines 202, 71）正确检查 `movedEntity.IsValid` 后更新 `RowIndex`
- **验证**：代码审阅 + `RemoveAt_after_promotion_preserves_row_mapping` 测试覆盖 swap-remove 后 RowIndex 正确性

#### A14. `CopyColumnFrom` 4 种模式组合安全
- **位置**：`Archetype.Storage.cs:843-912` CopyColumnFrom 4 模式组合
- **猜想**：flat↔chunked 的 4 种组合中可能有边界条件未处理（特别是跨段 copy 时 srcConsumed/dstConsumed 推进逻辑）
- **结论**：安全。CopyColumnFrom 的 while 循环用 `srcAvailable/dstAvailable` + `take = min(remaining, min(srcAvail, dstAvail))` 正确处理所有 4 种组合。新增 `CopyColumnsFrom_chunked_to_chunked` 和 `CopyColumnsFrom_flat_to_chunked` 测试覆盖
- **验证**：2 个新测试（3000 entities 和 100 entities round-trip）全通过

#### A15. 7 个 `_flatEntitiesGeneration++` 递增点全部必要且充分
- **位置**：`Archetype.Storage.cs` 7 个 `_flatEntitiesGeneration++` 递增点（lines 110, 215, 229, 293, 307, 739, 825）
- **猜想**：可能有 layout 变更操作遗漏递增 `_flatEntitiesGeneration`，导致 `GetEntityStorageUnsafe` 返回 stale 缓存
- **结论**：7 个递增点全部必要且充分。`ConvertToChunked` 自身不递增，但安全：`_cachedFlatEntitiesGeneration` 在构造时初始化为 -1，而 `_flatEntitiesGeneration` 默认 0，首次 GetEntityStorageUnsafe 调用时 -1 != 0 强制重建缓存。后续无中间 mutation 时缓存内容与 segment 数据一致。已确认非 bug（2026-07-05 bug hunt H4.1）。
- **验证**：`rg "_flatEntitiesGeneration\+\+"` 确认 7 个递增点；`AssertFlatCacheConsistent` DEBUG 断言守护缓存一致性；`GetEntities_after_ConvertToChunked_returns_correct_data` 测试覆盖。

#### A21. "非末段必须满（`Count == segCap`）"是错误断言（2026-07-05 修正 commit `40165f7`）
- **位置**：`Archetype.Storage.cs` `AssertSegmentInvariants`
- **猜想**：commit `40165f7` 认为"除末段外所有段必须 `Count == _segmentCapacity`"是核心不变量，加入 DEBUG 断言。结果打破 20 个测试（`AllocateRows_skips_empty_tail_segments_and_fills_first_available`、`Chunked_mode_get_chunks_returns_one_view_per_segment`、`Fuzz_large_scale_random_operations_multi_column`、`Capture_*_prediction_*`、`BUG_clone_deadlocks_on_archetype_with_large_component` 等）。
- **结论**：错误断言。`GetSegmentAndLocal(globalRow) = (row>>shift, row&mask)` 依赖的是**每段容量等长**（`Entities.Length == segCap`），不是段都满。`RemoveAt` 跨段 swap-remove 后 `lastSeg` 留空洞（`Count < segCap`），若后跟预分配空段就出现"非末段未满"——这是合法中间状态，`AllocateRows` 按"填首个非满段"补上。真正必要的不变量是"**非空段连续在前**"（空段不得夹在非空段之间，否则 `globalRow = _count` 与除法映射错位）。已修正断言为检查 `seenEmpty` 后不再有 `Count>0` 段。详见 `kb-chunk-storage.md` §3.6。
- **验证**：`dotnet test`（Debug）687/687 通过；`dotnet test -c Release` 674/674 通过；`HeroComing.Perf --check-baseline` Movement 1663/Attack 1216 通过。

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

### CommandStream / ComponentStore

#### CS1. `ApplyToWorld` 中 `lastArch` 缓存在 Add/Remove 后正确失效
- **位置**：`CommandStreamCore.cs` ComponentStore.ApplyToWorld line 1979-2022
- **猜想**：Add/Remove 操作移动 entity 到新 archetype，但 `lastArch` 缓存是否在跨 archetype 的情况下误用旧索引？
- **结论**：正确。`KindAdd`/`KindRemove` 分支都设 `lastArch = null`，下一个 Set 重新解析索引。Set 自身不会改变 entity 的 archetype，`arch == lastArch` 检查有效。
- **验证**：读 ApplyToWorld 中 `lastArch = null` 的两个赋值点

#### CS2. `ApplyToWorld` 中 TryGetRecord 返回 struct copy，但 ApplyTypedAdd/RemoveBoxed 需要最新 record
- **位置**：`CommandStreamCore.cs` ComponentStore.ApplyToWorld line 1995
- **猜想**：`record` 是 `TryGetRecord` 返回的 struct 拷贝，ApplyTypedAdd 内部修改了 entity 在 `_records[]` 中的实际 record，但本地拷贝仍是旧值。如果同一个 entity 在同一个 store 中有后续操作，第二次循环的 `TryGetRecord` 会读到已更新的 record——但本循环剩下的代码（SetComponentAtTyped 等）用的是 `record.RowIndex` 这一拷贝的旧 row，是否指向 chunked archetype 中错误的位置？
- **结论**：安全。`ApplyTypedAdd(entity, record, ...)` 中虽然 `record` 是拷贝，但 `MoveEntityCore` 返回新的 destinationRowIndex，`FinishMoveEntity` 用该值直接写 `_records[entity.Id]`。Set 操作的 `record.RowIndex` 取自本次循环顶部 `TryGetRecord` 的结果，该记录在 Set 之前不会被修改（Set 是纯写操作，不移动 entity）。后续 entity 的操作重新调用 TryGetRecord 读到最新值。
- **验证**：读 ApplyTypedAdd 中 MoveEntityCore → FinishMoveEntity 的 RowIndex 更新

#### CS3. `CloneChildrenRecursive` 从 chunked source archetype 读组件
- **位置**：`CommandStreamCore.cs` CloneChildrenRecursive line 1351-1417
- **猜想**：`archetype.ReadComponentRaw(i, sourceRow, ptr)` 从 chunked archetype 读取组件数据。`sourceRow` 是 entity 在 chunked archetype 中的全局 row，`ReadComponentRaw` 用 `GetSegmentAndLocal` 正确映射到 segment 内偏移。
- **结论**：安全。ReadComponentRaw 是 dual-mode 的（chunked 分支用 GetSegmentAndLocal），sourceRow 来自 `TryGetLocation` 返回的 `RowIndex`，指向正确的全局行。
- **验证**：读 ReadComponentRaw 的 `if (!_isChunked)` 分支

#### CS4. `ComponentStore.AppendConcurrent` 中 resize+write 在同一 lock 内（已修复）
- **位置**：`CommandStreamCore.cs` ComponentStore.AppendConcurrent line 1944-1967
- **猜想**：此路径是否仍存在 `BUG_parallel_append_concurrent_resize_is_not_atomic` 说的 race？
- **结论**：已修复。Array.Resize + element write 全部在 `_resizeLock` 内，消除了三数组观测不一致的问题。
- **验证**：对比 `AppendConcurrent`（lock 内 resize+write）与 `EnsureStoreCapacity`（单线程，resize 分开无 lock）。查看 git 历史确认此路径是修复目标

#### CS5. `GetOrCreateStoreParallel` double-check locking 中 Stores 数组 resize
- **位置**：`CommandStreamCore.cs` GetOrCreateStoreParallel line 1549-1578
- **猜想**：两个线程同时为不同 type id 进入 resize 分支，`Array.Resize` 能否 shrink 数组？
- **结论**：正确。`_storeCreateLock` 串行化 resize。`newLen = Math.Max(id + 1, Stores.Length == 0 ? 16 : Stores.Length * 2)` 只增不减（Array.Resize 在本项目中从不用来 shrink 到小于当前 length）。第二个线程进入时重新检查 `id >= Stores.Length`，此时 Stores 已被第一个线程扩到足够大，不会再 resize。
- **验证**：手算 A(id=20)+B(id=25) 并发进入的时序

#### CS6. `SwapOutState` 回收 `_spareFrozen` 时 `PendingBatch` 数组不清零
- **位置**：`CommandStreamCore.cs` SwapOutState line 672-716
- **猜想**：`_spareFrozen` 回收后，`_frozen.PendingBatch`（id→batchIdx 映射）保留两帧前的 stale 条目；新的 `Create()` 只覆盖当前 entity.Id 对应的 slot，其他 stale 条目是否会被 `TryGetPendingBatch` 误读？
- **结论**：安全。`_pendingBatchMin`/`_pendingBatchMax` 在 SwapOutState 中被重置为 `int.MaxValue`/`0`，`TryGetPendingBatch` 的 range guard `(uint)(id - min) < (uint)(max - min)` 在 min=max 的极端值下对所有 id 都返回 false。且 `PendingBatchCount` 被重置为 0，Add/Set 的前置检查 `_frozen.PendingBatchCount > 0` 短路。stale 条目不可达。
- **验证**：手算 min=int.MaxValue, max=0 时的 range check（`id - int.MaxValue` unchecked→负→uint 大值，`0 - int.MaxValue` unchecked→int.MinValue+1→uint 大值，前者 > 后者→false）

#### CS7. `SubmitAndSnapshotAsync` 中 main thread 和 background thread 并发读同一 FrozenState
- **位置**：`CommandStreamCore.cs` SubmitAndSnapshotAsync line 618-634
- **猜想**：`SubmitFromFrozen(frozen)`（main thread）和 `BuildFromFrozen(frozen)`（background thread）同时遍历 frozen 的数组，是否 race？
- **结论**：安全。两条路径对 frozen 都是**只读**：SubmitFromFrozen 写 `_world`，BuildFromFrozen 写 `delta`。frozen 的 arrays（BatchHeads/BatchComps/BatchBuf/DestroyEntities/Stores 等）在 SwapOutState 后不再被 recording 路径写入。并发只读无 race。
- **验证**：确认 SubmitFromFrozen 和 BuildFromFrozen 的所有写入目标（_world / delta）都不在 frozen 内

#### CS8. `ResolveArchetypeForMask` mask cache 满后 hash 驱逐可能碰撞
- **位置**：`CommandStreamCore.cs` ResolveArchetypeForMask line 1252-1293
- **猜想**：cache 满（8 slot）后 `slotIdx = mask.GetHashCode() & (MaskCacheSize-1)`，不同 mask 可能 hash 到同一 slot 互相驱逐
- **结论**：性能问题不是正确性问题。读取时验证 `slot.Mask.Equals(mask)`，miss 则走 `GetOrCreateArchetype`。功能正确，只影响命中率。
- **验证**：读 cache lookup 的 Equals 验证 + miss fallback 路径

#### CS9. `CommandStream` 拆分为 base + sealed 子类时用 abstract+override 会 regression ~10%（已修，历经两版）
- **位置**：`src/MiniArch/Core/CommandStreamCore.cs`（base）+ `CommandStream.cs` / `ParallelCommandStream.cs`（子类）的 9 个 mutator
- **猜想**：把 `_parallelMode` flag 拆成两个 sealed 子类时，最初用 `public abstract` + `public override` 是教科书式 OOP，应该零开销
- **结论**：**真 performance regression**。.NET 8 JIT **不**对 generic virtual 方法（`Add<T>` / `Set<T>` / `Remove<T>`）做可靠的 devirtualize，即使 receiver 静态类型是 sealed 子类。每次调用付虚表查证 + 阻止 inline。HeroComing.Perf 实测 Movement -9.4%、Attack -11.8%。
- **修复（第一版）**：base class 的 9 个 mutator 改为 `public` 非虚拟 + 默认 throw `NotSupportedException`；子类用 `public new` 隐藏（完全非虚拟，JIT direct call + inline）。修复后比原版还快 ~8%（因为同时去掉了 `_parallelMode` 分支判断）。
- **修复（第二版，2026-07-05）**：base class 彻底删除 9 个 public throw 桩，只保留 `protected *Core()` helper。子类用 `public`（不再需要 `new`）。Base class 上本来会 throw 的操作现在编译时就不可达。等价于消除那最后 9 行"lying API"。
- **副作用**：base class 内部递归路径（`CloneChildrenRecursive`）必须直接调 `*Core` helper（`CreateCore`/`AddChildCore`），不能调公共 mutator。
- **验证**：`dotnet test -c Release` 666 通过；`HeroComing.Perf --check-baseline` 通过（Movement 2142/Attack 1309）。
- **教训**：拆分 ECS 热路径类型时，**不要**用 abstract+override。用 sealed 子类 + base 只暴露 `*Core` helper。Base class 上不提供会 throw 的公共 mutator。

#### CS10. `ParallelCommandStream.Add/Set/Remove` 对 pending-batch 实体走 AppendConcurrent 而非 WritePendingComponent（真 bug）
- **位置**：`ParallelCommandStream.cs:60-78`（修复后 `src/MiniArch/Core/ParallelCommandStream.cs` 的 Add/Set/Remove）
- **猜想**：parallel 路径的 `CanRecordParallelComponentCommand` 对 alive 和 pending 实体都返回 true，用 `AppendConcurrent` 统一处理，Submit 时序 `SealParallelStores → MaterializeAllPending → ApplyComponentStores` 使组件在实体 materialize 后 apply——最终状态应该一致。
- **结论**：**真 bug**。`ApplyComponentStores` 走 `ApplyToWorld`，对 `KindSet` 调用 `SetComponentAtTyped`（要求组件已存在，否则抛 `Archetype does not contain component X`），对 `KindAdd` 要求组件不存在（重复 Add 抛 `Entity already has component Y`）。而 batch buffer 路径（`WritePendingComponent`）把组件写入批处理缓冲区，在 `MaterializePending` 阶段一次性将 batch 实体按完整组件集创建。两条路径语义不等价。
- **修复**：`ParallelCommandStream.Add/Set/Remove` 改为：alive 实体走 `AppendConcurrent`（无锁快路径），否则在 `_storeCreateLock` 内调 `TryGetPendingBatch` + `WritePendingComponent`/`MarkBatchComponentRemoved`。与单线程 `CommandStream` 完全一致的模式。
- **验证**：4 个回归测试（`ParallelCommandStream_Set_on_pending_entity_applies_component`、`ParallelCommandStream_Add_on_pending_entity_does_not_throw_on_second_Add`、`ParallelCommandStream_Remove_on_pending_entity_skips_component`、`ParallelCommandStream_Clone_then_Add_component_matches_single_threaded`）+ `dotnet test -c Release` 627 通过 + `HeroComing.Perf --check-baseline` 通过。
- **教训**：split 重构中，parallel 路径不能简单等价取"最终状态一致"——batch buffer 和 component store 的 apply 语义不同。pending-batch 实体必须在 `_storeCreateLock` 内走 batch buffer 路径。`CanRecordParallelComponentCommand` helper 方法本身正确（alive + pending 都可以录），但录到哪里的决策错了。

### FrameDelta / ReplayCore

#### F5. `PreScanForCapacity` 预测 Create 计数，但 main pass 中 archetype 可能被提前 chunked
- **位置**：`World.cs` PreScanForCapacity line 736-813
- **猜想**：PreScan 调 `arch.EnsureCapacity(arch.EntityCount + count)` 可能把 archetype 提升为 chunked。Main pass 中 Create 操作向同一 archetype 加 entity 时走 chunked 路径。
- **结论**：正确。`PlaceEntityInArchetype` → `AddEntity` → `AddEntityChunked` 正确处理 chunked 存储。且 main pass 中该 archetype 已有预分配的 segments，AddEntityChunked 直接添加。
- **验证**：读 AddEntityChunked + 确认 main pass 和 pre-scan 间无其他修改

#### F6. `PreScanForCapacity` 中非 canonical mask 跳过容量预测
- **位置**：`World.cs` PreScanForCapacity line 771-779
- **猜想**：`builder.BitsSet != compCount`（存在 id>=512 的组件）时跳过预分配，main pass 的 Create 会触发热路径的 EnsureCapacity 倍增。但后续 main pass 的 Create 操作间没有结构变更，所以每次 Create 最多触发一次翻倍，不会 cascade？
- **结论**：正确。即使无预分配，main pass 中 Create 操作是顺序执行，每次 Create 的 EnsureCapacity 从当前 capacity 翻倍（或直接 ConvertToChunked）。单个 Create 不会引起链式翻倍。非 canonical mask 的 archetype 通常实体数很少，成本可接受。
- **验证**：读 EnsureCapacity 的翻倍逻辑 + ReplayCreateOpResolved 的调用路径

#### F7. `ReplayCreateOpCore` 中 `PlaceEntityInArchetype` 可能将 archetype chunked
- **位置**：`World.cs` ReplayCreateOpCore line 913
- **猜想**：`archetype.AddEntity(entity)` 在 archetype 容量不足时 `EnsureCapacity` → 可能 ConvertToChunked。然后 `WriteComponentRaw` 用 `rowIndex`（全局 row）写入——chunked 模式下用 GetSegmentAndLocal 正确映射。
- **结论**：安全。WriteComponentRaw 是 dual-mode 的。rowIndex 由 AddEntity 返回，在 chunked 模式下是全局 row，GetSegmentAndLocal 正确解析到 (segment, local)。
- **验证**：读 WriteComponentRaw 的 chunked 分支

#### F8. `ReplayCore` 中 `AddChild` 操作在 `Create` 操作之后执行
- **位置**：`World.cs` ReplayCore line 583-588
- **猜想**：`AddChild(parent, child)` 中 parent 或 child 可能在前面的 Create 操作中刚刚被创建。如果该 Create 触发了 archetype 的 chunked 晋升，parent/child 的 record 中 RowIndex 指向 chunked 行——但 hierarchy 操作不依赖 RowIndex，只处理 entity ID。安全。
- **结论**：安全。HierarchyTable 用 entity ID 索引，不读 archetype RowIndex。
- **验证**：读 HierarchyTable.AddChild 实现

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
