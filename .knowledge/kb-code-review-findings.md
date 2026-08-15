---
title: 代码审阅发现
module: Meta
description: 审阅前必读的当前风险、已修复真 bug 回归索引与已排除非 bug 猜想；只保留结论和验证入口
updated: 2026-08-15
---
# 代码审阅发现

## 这个模块是干什么的

- 审阅前先查本页，避免重复报告已经修复或已证伪的问题。
- 真 bug 只记录 witness、修复边界和回归测试；推理细节留在代码/测试。
- 非 bug 必须保留“位置 / 猜想 / 结论 / 验证”四字段。
- 不保存会随提交漂移的全套测试总数、旧行号或一次性性能数字；当前门禁看本轮 evidence 与 `kb-safety-proof.md`。

## 当前未修风险

### Replay 没有通用事务回滚（P2，接受现状）

- **位置**：`World.EntityLifecycle.cs` 的 Replay/ReplayCore 路径。
- **边界**：`FrameDelta.Validate()` 能在 Replay 前拒绝结构损坏的 wire，但不能证明 target World 与 delta 历史兼容；reservation 首步不兼容现已在 allocator mutation 前失败，但若 Replay 在后续操作中发生 target-world 契约错误或灾难性异常，已执行操作仍不会自动回滚。
- **当前策略**：不可信 wire 先 Validate；peer 从共同 snapshot/frame 0 沿完整历史 replay；需要强事务的调用方在外层使用 World snapshot/checkpoint。
- **不做**：本轮不引入通用 dry-run shadow World 或 rollback journal。

CommandStream 的占位符/布局守卫（FieldKinds 探测 + flag 扫描器）修复了 lockstep 分叉路径（P0#1/P0#2），`Validate()` 覆盖语义违规（存在性/hierarchy）；nested Entity 现在由完整递归 offsets 合同支持，零校验 Submit 的存在性违规仍在 apply 期抛（部分应用），且不把 Submit 或 Replay 提升为灾难性异常下的通用事务。

## 已修复的真 bug 索引

### 2026-08-15 nested Entity offset reconstruction

- **结论**：nested Entity 是合法 unmanaged component contract；风险在于 source 与 Replay 使用不完整或不一致的 Entity offsets，导致 placeholder 解析分叉。5.2 通过递归 actual CLR offsets、InlineArray stride、recursion stack、sort/dedupe/bounds 统一发现路径解决。
- **回归入口**：`NestedEntityComponentTests`（普通 nested、generic wrapper、InlineArray、深层、Snapshot/Replay、Clone、Parallel、Auto 与 placeholder 边界）以及 `BUG_nested_entity_record_resolves_real_entity_and_submits`、`BUG_nested_entity_pending_batch_resolves_placeholder_at_submit`、`BUG_clone_imports_nested_entity_layout_and_deep_inline_values`。
- **历史边界**：Entity-bearing `LayoutKind.Auto` 与真正无法解析的布局仍 fail-fast；旧 P0#2/P0#3 是“漏扫导致 source/Replay 不一致”的防线，不是“nested 必须拒绝”的规则。

### 2026-08-06 零校验重构审阅（T2 系列，Validate() + 库能力守卫）

| 回归测试 | 位置 / witness | 修复边界 |
|---|---|---|
| P0#1：占位符静默落库分叉 | non-deferred 帧（`_deferredSeq==0`）伪造 `Entity(-1,seq)` 引用经 store/batch 静默写入 World；Snapshot 产出含坏值 delta；副本 Replay 的 Add/Set/Create op 端 `GetOffsets`+`ResolveInPlace` 抛 "Unresolved placeholder" → 源端静默、副本抛错 = **lockstep 分叉** | `FieldKinds<T>` 泛型静态探测（record 期 5 咽喉点，static readonly bool 1 读 1 分支）+ 帧级 flag + Submit/Snapshot/async 扫描器（reserve/materialize/emit 前原子拒绝，last-wins 语义）；占位符原子拒绝由已恢复的 8 个 `BUG_*` 测试锁定 |
| P0#2：nested Entity offsets 漏扫导致分叉 | 历史实现只发现 top-level Entity；nested Entity 值可在 source 落库，但 Replay 端无法按同一完整 offsets 解析 | 5.2 `EntityFieldResolver` 递归验证并收集完整 actual CLR offsets；source、Snapshot、Validate、Replay、Clone、Parallel 共用缓存，Entity-bearing Auto 与真正无法解析的布局仍在 mutation 前拒绝 |
| P0#3：Clone raw 导入绕过 marker fast path | World 组件中的 `Entity(-1,seq)` 占位符可经 `CommandStream.Clone` / `ParallelCommandStream.Clone` 的 raw `ReadComponentRaw→CommitBatchComponent` 导入 batch；泛型 record 探测（`FieldKinds<T>` + `WritePendingComponent`/store Add/Set）未覆盖 → 帧 flag 保持 0 → 旧实现可能静默导入 placeholder 或漏掉 nested 值，Snapshot 产出 `Validate()` 拒绝的 delta（P0#1/P0#2 的 Clone 变体） | 非泛型 raw 探测 `FlagFrameIfMayContainPlaceholder(ComponentType, ReadOnlySpan<byte>)` 使用 `GetOffsets` 检查任意深度 Entity 字段；Auto 或真正无法解析的布局保守置 flag，但在 consume 扫描器中才抛。探测点 = 单线程 `CloneMaterializedComponents` merger 的最终有效 values + 并行 `Clone` root / `CloneChildrenFromWorld` child raw copy；`CopyComponentsFromBatch`/`ComponentMerger.Add` 的字节源已由提交路径探测。回归测试：`BUG_clone_imported_placeholder_is_rejected_by_default_submit` / `BUG_clone_imported_placeholder_is_rejected_by_default_snapshot` / `BUG_clone_imports_nested_entity_layout_and_deep_inline_values` / `BUG_parallel_clone_imported_placeholder_is_rejected_by_default_consume` / `BUG_parallel_clone_imported_child_placeholder_is_rejected_by_default_submit` |
| P0#4：async real-id delta 为已取消 deferred batch 伪造 allocator 事件 | `DeferredEntities=true` 下 pending Create 被取消时从未触碰 source allocator，`ResolveDeferredCreates` 也有意保留其 placeholder；但 async real-id writer 仍发 `Reserve(placeholder)→Release(placeholder)`，replica 会真实分配并释放一个 slot。若后面有 live Create，Replay 立即 reservation mismatch；即使全取消，下一次 Create 也出现 source `Entity(0,v1)` / replica `Entity(0,v2)` 分叉 | real-id budget、upper bound 与 writer 统一跳过 `BatchCanceled && BatchEntities[i].IsPlaceholder`；immediate real-id cancellation 仍保留 Reserve+Release 以镜像 source 已发生的 reserve/release。`BUG_async_real_id_delta_omits_cancelled_deferred_batches` / `BUG_real_id_snapshot_omits_cancelled_deferred_batches` 锁定 async 与切换模式 Snapshot 的 wire 均不含 cancelled placeholder，且 Replay 收敛、下一 id/version 一致 |
| P0#5：显式 Entity endpoint 绕过 mandatory wire guard | 零语义校验后，AddChild/RemoveChild 的未知 placeholder 会被 Snapshot 写成 `FrameDelta.Validate()` 拒绝的 wire；Submit 先 detach child 再因 placeholder parent 抛错，留下 hierarchy 部分修改。Destroy 的 `Entity(-1,-1)`、id<-1、real version≤0 也会产出结构无效 delta | 将帧 flag 扩为 `MayNeedEntityReferencePreflight`；AddChild/RemoveChild 与 Destroy record 对 placeholder/非法 shape 置位，统一 `PreflightEntityReferences()` 在 allocator/World/target/worker 前验证 final hierarchy endpoint、destroy shape、embedded refs/layout。`BUG_hierarchy_placeholder_is_rejected_before_submit_mutates_existing_relation`、`BUG_snapshot_rejects_unknown_hierarchy_placeholder_before_returning_delta`、`BUG_snapshot_rejects_unknown_hierarchy_child_placeholder`、`BUG_snapshot_rejects_invalid_explicit_entity_shapes` 锁定 |
| `BUG_real_id_budget_rejects_before_deferred_resolution` | real-id Snapshot/async 在 `ResolveDeferredCreates()` 前用 placeholder 的短 wire width 做“精确”预算；帧可先通过 preflight，resolve 消耗 source allocator，随后 worker writer 因真实 id varint 更宽而超 `MaxFrameBytes`。实测失败后 5000 个 deferred entity 已落进 World（18423→23423） | real-id budget 对尚未解析的 pending Reserve/Create 与 hierarchy parent/child endpoint 按合法 Entity 最大 wire width（5B id + 5B version）计费；仍在 resolve/free-list/worker/target/World mutation 前拒绝。边界回归用 3-byte real ids + 精确 filler 锁定 allocator/checksum 零变化，并单测 11/12/11/21B endpoint 计费 |
| `BUG_high_component_id_placeholder_scan_preserves_last_wins` | `RejectBatchEmbeddedPlaceholders` 只对 component id `<512` 调 512-bit `IsSeen`；id≥512 时会扫描同类型全部历史值。旧的 superseded 值若引用 cancelled placeholder，Validate/Submit 会错误拒绝，而 materialize/emit 的 `DeduplicateBatchChain` 有 high-id fallback、只消费最新值 | 抽出 `HasNewerEffectiveComponentType` 作为所有 pending head-first consumer 的唯一 high-id 线性 fallback，scanner 与 `DeduplicateBatchChain` 共用。回归在禁并行 collection 中动态填充 shared registry 到 512 后验证，避免全局 registry 污染其它测试 |
| `BUG_Validate_does_not_reorder_cancelled_batch_free_list` | `Validate()` 把 `AlignCancelledBatchFreeListOrder()` 当作“纯校验阶段”执行；real-id pending 按反向顺序取消后，Validate 会重排 World free list。随后 `Clear()` 丢弃 frame 仍永久改变下一次 Create 的 id，且 `CanonicalChecksum` 在 Validate 前后变化，违背无 World 副作用契约 | 从 `Validate()` 移除 allocator realignment；Submit 与 async handoff 仍在 consume 的确定性时机自行对齐。回归比较 Validate+Clear 与直接 Clear 的 checksum/下一 Entity 完全一致 |
| LayoutKind.Auto：**非静默**（源端 Archetype 构造 `ThrowIfManagedComponent` 已兜底），仅错误类型/时机变化 | `BUG_auto_layout_record_does_not_throw_but_submit_rejects_before_mutation` | consume 扫描器 `GetOffsets` 先于 archetype 构造抛 `InvalidOperationException`（消息含 "LayoutKind.Auto"），恢复 T2 前异常类型契约 |
| P2-2：async target 覆写（契约变更，非 bug） | `SubmitAndSnapshotIntoAsync` 失败时 target 可能已 Clear/部分写入（旧 "target remains unchanged" 承诺过强） | 撤销承诺，文档化 "target 内容未定义"；迁移后的 `BUG_async_into_preflights_invalid_component_before_worker_handoff`（target NotEqual 断言）锁定 |

**契约测试索引（新增，2026-08-06）**：`Contract_*` 10 个（含 last-wins 不误杀、deferred 合法流放行、Remove-only 豁免、Validate 幂等/原子；占位符原子拒绝由已恢复的 8 个 `BUG_*` 锁定）+ `SubmitReplayParity` 1 个 + 9 个 BUG_ 测试迁移为双断言（Validate 原子拒绝 + Submit 部分应用/apply 期抛）。P0#3（Clone raw 导入）由 5 个新增 `BUG_*clone*` 测试锁定；P0#4 由 async real-id cancellation 收敛测试锁定。

> 与旧机制的关系：08-05 段的 record 探测（`MayContainPlaceholder` + 帧级 flag）已被 **`FieldKinds<T>` 泛型静态探测**取代（同 flag 扫描器架构，探测判定从 offsets 数组查表改为 per-type static readonly 1 读）；"Submit 隐式 4 项 preflight"（2026-07-25 前）已失效——语义校验抽为 `Validate()`，Submit 默认零语义校验（存在性违规 apply 期抛 + 部分应用）。性能数据见 `kb-hero-pipeline-regression.md`。

### 2026-08-05 placeholder preflight 优化审阅

| 回归测试 | 位置 / witness | 修复边界 |
|---|---|---|
| `BUG_nested_entity_record_resolves_real_entity_and_submits` / `BUG_nested_entity_pending_batch_resolves_placeholder_at_submit` / `BUG_auto_layout_record_does_not_throw_but_submit_rejects_before_mutation` | 旧版本 record-time probing 与 consume-time layout判定时机不一致：nested Entity 曾被错误当作不可解析，Auto layout 也可能在写入后才暴露；这不是 nested Entity 的天然非法性 | 探测改为帧级单调 flag；5.2 `EntityFieldResolver` 递归发现 nested/InlineArray offsets，source 与 Replay 共用；仅 Entity-bearing Auto 或真正无法解析的布局在 mutation 前抛错 |
| `Remove_only_missing_auto_layout_component_is_applied_without_layout_scan`（契约，非 BUG）/ `BUG_Remove_only_missing_auto_layout_component_is_not_affected_by_unrelated_placeholder_frame` / `BUG_Empty_remove_only_auto_layout_store_does_not_poison_later_deferred_frame` | store `PreflightEmbeddedPlaceholders`/`ReplacePlaceholders` 在跳过 `KindRemove` 前无条件 `GetOffsets`；Remove-only/空 store 会被同帧无关 placeholder（flag=1 全量 preflight）或任意 deferred Create（`ReplacePlaceholders`）拖累而抛错，且后者发生在 allocator mutation 之后 | 两方法延迟到首个 payload entry 才取 offsets；Remove-only/空 store 永不做布局扫描。Remove 无 payload 不扫描是有意契约变更 |

### 2026-07-25 发布前审阅

| 回归测试 | 位置 / witness | 修复边界 |
|---|---|---|
| `BUG_ChangeWatch_explicit_filter_requires_watched_component` / `BUG_projected_ChangeWatch_explicit_filter_requires_watched_component` | 两个 `World.Watch<TComponent...>(explicitQuery)` 重载原样保存显式 filter；当 filter 匹配不含被观察组件的 archetype 时，Snapshot 无条件 `GetSpan<TComponent>()` 并抛异常 | 显式 filter 统一追加 `With<TComponent>()`；额外约束保留，Watch 扫描的每个 archetype 都保证含目标列 |
| `BUG_AddChildFromSnapshot_rejects_cycle_consistently_in_Debug` | `AddChildRestored` 在共享的无条件 `ValidateAddChild` 前重复执行 `Debug.Assert`；同一非法循环在 Release 抛契约异常，Debug 却先变成 testhost `DebugAssertException`，导致 Debug 全量门禁失败 | 删除重复 assert 与其私有遍历；restore 和普通 AddChild 统一由 `AddChildCore` 的 Release 常开验证拒绝循环 |
| `BUG_reused_larger_rollback_snapshot_does_not_restore_stale_hierarchy_tail` | 较大的 pooled `WorldStateSnapshot` 被复用于较小 hierarchy 时只覆写前缀，Restore 却按 backing-array 长度复制；旧 parent/child 尾数据可附着到之后复用的同 ID/version 实体 | snapshot 单独保存 capture 时的 hierarchy entity capacity；Restore 只复制该逻辑长度，不能把池化数组 capacity 当有效状态长度 |
| `BUG_recycled_snapshot_reference_is_not_reactivated_after_pool_reuse` | public snapshot class 自身被池复用；旧变量与新 capture 指向同一对象，`_isRecycled` 被重新置 false 后旧引用可消费新 snapshot，违背单次 lease 契约 | 池只保存 internal payload；公开 `WorldStateSnapshot` 改为 payload+generation 的 readonly value lease。旧 generation 即使 payload 复用也保持 recycled，且不牺牲稳态零 GC |
| `BUG_rolling_snapshot_window_discards_old_payloads_without_allocation` | 多帧 ring 覆盖未 Restore 的 snapshot 时没有 release 入口；payload 永不回池，文档声称的 rolling-window steady-state zero GC 不成立 | value lease 实现幂等 `IDisposable`；淘汰 checkpoint 时只回收 payload、不恢复 World。Restore 与 Dispose 共享单一 recycle helper |
| `BUG_hierarchy_overlay_applies_final_acyclic_reparenting_without_transient_cycle` | preflight 接受最终无环 overlay，但 Submit/Replay 按 child id 直接 Add；父子方向反转时旧边尚未解除，`World.AddChild` 观察到瞬时 cycle 并抛错 | Submit 先 detach 全部有效 intent 的 child，再安装 Add；delta 同样先 emit RemoveChild phase、再 emit AddChild phase，保持两路收敛 |
| `BUG_snapshot_load_rejects_oversized_chunk_capacity_before_archetype_allocation` | 数十字节 v3 payload 可声明 `int.MaxValue` chunk capacity 和一个空 archetype，dry validation 通过后尝试分配数 GB entity array | World 构造与 Snapshot Load 共享 `MaxChunkCapacity` 上限；不可信 header 在创建 World/archetype 前拒绝 |
| `BUG_snapshot_load_rejects_auto_layout_before_registration` | Snapshot schema 接受 unmanaged `LayoutKind.Auto` struct，dry validation 后先注册到全局 registry，随后 Archetype storage 才拒绝 | Load 在 registry 注册/World 构建前以 `InvalidDataException` 拒绝 Auto-layout（enum 除外）；ComponentSchema 仍是完整 registry handshake，可包含已注册但不可持久化的类型 |
| `BUG_Snapshot_rejects_more_than_MaxOpsPerFrame_before_writing_delta` / `BUG_snapshot_paths_reject_MaxFrameBytes_before_target_or_world_mutation` | 本地 Snapshot/async producer 可生成超过接收侧 `MaxOpsPerFrame`/`MaxFrameBytes` 的 delta；自己的 Validate/FromWire 随后拒绝；非 deferred 输出还可能先 resolve real id，async 可能先提交 World 再让 worker 失败 | 所有 Add* 写前做完整 op 原子预算检查；所有 producer 在 deferred allocator resolution、target clear、frozen swap/worker 和 World materialize 前做整帧预算 preflight。常规帧用保守 upper bound O(store 数) 快速放行，近边界才精确扫描 |
| `BUG_Submit_rejects_cancelled_embedded_placeholder_before_allocator_mutation` / `BUG_Snapshot_rejects_cancelled_embedded_placeholder_before_returning_delta` / `BUG_SnapshotInto_rejects_cancelled_embedded_placeholder_before_clearing_target` / `BUG_async_snapshot_rejects_cancelled_embedded_placeholder_before_handoff` | 有效 component Entity 字段仍引用同帧已取消 placeholder 时，Snapshot 返回自身 Validate 拒绝的 delta；Submit/async 先 reserve real id 再在 resolve 中失败，改变 allocator version，Into 还可能先动 target | pending 最终值与 existing store 在 resolve/target clear/worker/allocator 前统一 preflight placeholder lifecycle；四个生产/提交入口共用检查，失败不消费 id、不改 target、不 handoff |
| `BUG_cancelled_placeholder_in_superseded_pending_value_is_ignored` | pending 多次 Set 同类型时，旧值已被 last-wins 折叠淘汰，但 placeholder resolver 仍扫描所有非 Remove bytes；旧值引用 cancelled placeholder 会错误拒绝本应有效的最终状态 | preflight 与 resolve 都复用 `DeduplicateBatchChain`，只观察 emit/materialize 实际消费的 effective component values |
| `BUG_SnapshotInto_null_target_throws_before_resolving_deferred_entities` | `SnapshotInto(null)` 在 null dereference 前先解析 deferred placeholder 并 reserve real id，使参数错误改变 World allocator | 入口首句 `ArgumentNullException.ThrowIfNull(target)`，任何 consume/resolve 前拒绝 |
| `BUG_snapshot_load_rejects_non_positive_reserved_slot_version` | slot version 只在 live row/free entry 上检查；既非 live 又非 free 的 reserved slot 可携带 0/负版本并被 Load 接受 | 读取 slot version table 时要求每个已存在 slot 的 version 都为正；三态后续校验不再遗漏 reserved |
| `BUG_parallel_first_chunked_entity_cache_read_publishes_complete_storage` | chunked archetype 的多个并行 reader 可同时在共享 flat cache 字段上执行“赋新数组→逐 segment 填充→写 generation”；后发布的空/半填充数组可能被另一 builder 返回 | miss 以按需私有锁串行重建并 double-check，完整填充后用 volatile generation 发布；flat/hit 热路径不加锁。同族扫描将 ordered entity sort 的完整 snapshot 改为 volatile read/write 发布，其余命中均是单线程 writer cache 或已有锁/immutable snapshot |
| `BUG_Get_rejects_destination_that_aliases_component_storage` | `ComponentBucketQuery<Entity>.Get` 可把查询 key 列自身的可写 span 当 destination；首个匹配写回实体 handle 后会改坏后续 key，并使“读”查询静默修改 World | 写入前按 byte range 预检所有 key-component chunk；任何重叠都以 `ArgumentException` fail-fast，非重叠路径仍直接写 caller span |

### 2026-07-22 全面审阅

| 回归测试 | 位置 / witness | 修复边界 |
|---|---|---|
| `BUG_full_lookup_missing_key_returns_empty_span` | `FrameLookup.FindSlot` 在开放寻址表恰好占满时查找不存在 key，没有空 stamp 可终止，进入无限循环 | 探测回到起点时返回未命中 |
| `BUG_generation_wrap_does_not_make_empty_slots_look_occupied` | `FrameLookup.Clear` 的 generation 回绕为 0 后与零初始化 stamp 混淆，非默认 key 的首次插入无限探测 | generation 命中 0 时清空 stamp 并从 1 重新开始 |
| `BUG_Describe_stale_handle_does_not_report_recycled_entity_as_alive` | `EntityDump.Describe` 只检查 slot occupied；ID 复用后会把 stale handle 报告成新实体且读取新组件 | alive 判定同时校验输入 handle version |
| `BUG_ValidationResult_keeps_its_issues_after_later_validation` | `ValidationResult.Issues` 包装 `WorldValidator` 的 ThreadStatic 复用 List；下一次 Validate 会清空并改写旧结果 | 构造结果时复制 issue 数组，结果与 scratch 生命周期解耦 |
| `BUG_WorldValidator_detects_missing_forward_hierarchy_link` / `BUG_WorldValidator_detects_forward_hierarchy_link_to_wrong_parent` / `BUG_WorldValidator_detects_cyclic_child_slot_chain` | validator 从 child→parent 反向表枚举后又调用同一反向表的 `TryGetParent`，所谓双向检查是同源自证；缺失/错误 forward link 及 child-slot 环仍报告 valid | 先扫描 forward list 建 live relation set，再用独立 reverse 表交叉验证；重复、越界、环明确报错，bulk Clear 留下的 stale-version entry 仍忽略 |
| `BUG_WorldValidator_detects_record_to_archetype_row_mismatch` | occupied record 只验证 row 范围和 record 间不碰撞；两个 record 交换 row 后仍可通过，archetype row 也未回查 record | 同时验证 record→row 中的完整 Entity handle，以及每个 archetype row→record 的 version/archetype/row 映射 |
| `BUG_WorldValidator_detects_out_of_range_free_list_entry` / `BUG_WorldValidator_detects_free_list_version_mismatch` | free-list 校验只看“若 ID 在范围内则不能 occupied”和 duplicate；越界 ID、非正/不匹配 version 可在计数碰巧相等时报告 valid | 每个 free entry 必须对应范围内的 unoccupied record 且 version 正数并完全相等 |
| `BUG_WorldValidator_classifies_duplicate_free_id_as_error` | 同一 ID 出现两次会被 allocator 重复发放，属于确定性结构损坏，却被标成 Warning | `FreeListDuplicate` 提升为 Error；pending reservation 的容量差仍保留 Warning |
| `BUG_WorldValidator_detects_reserved_count_mismatch` | slot − occupied − free 只被当作“可能有 pending”的 Warning，未与真实 `_reservedCount` 对照；reservation 计数损坏无法定位 | 暴露 internal 只读计数给 Diagnostics；派生值不等于真实值时报 Error，二者一致且大于 0 才是合法 pending Warning |
| `BUG_full_lookup_accepts_additional_rows_for_existing_key` | `FindOrCreateSlot` 在 `distinctKeys >= _capacity` 时立即返回 -1，即使 key 已存在（开放寻址表满但 key 已在） | bounded probe 先匹配 existing key，绕一圈找不到才 -1 |
| `BUG_ensure_capacity_preserves_built_lookup` | `EnsureCapacity` 仅 `Array.Resize` 扩 table，没有 rehash live entries，扩后 indexer 返回错误位置 | 新局部 arrays 完整 rehash 后发布；异常保持旧结果 |
| `BUG_stateful_struct_selector_uses_same_initial_state_for_both_passes` | `TryBuild` 两遍共用同一 `selector` 参数；可变 struct 的 `Select` 在第一遍中 mutate 自身，第二遍从不同状态开始导致找不到 key | 每遍独立拷贝原始 selector（`countSelector`/`scatterSelector`）；第二遍 slot<0 时抛明确 `InvalidOperationException`，外层 catch 后 `Clear` |
| `BUG_failed_build_does_not_expose_partial_lookup` | `TryBuild` 中 `selector.Select`/`GetHashCode`/`Equals` 抛异常后，可能留下部分或旧的 lookup 数据 | `TryBuild` 用 `try/catch` 包裹 core，异常时 `Clear()` 后 `throw` |
| `BUG_constructor_rejects_invalid_capacity` / `BUG_EnsureCapacity_rejects_invalid_capacity` | 构造和 `EnsureCapacity` 未验证负数和超过 `MaxCapacity`（2^30）；`CeilPow2` 对大输入溢出 | 两个容量参数都限制在 0..`MaxCapacity`；`Build` 用饱和倍增并删除任意重试次数上限 |
| `BUG_Destroy_small_aliasing_entity_span_consumes_original_handles` | 小批量 `Destroy(ReadOnlySpan<Entity>)` 逐个 swap-remove；输入若直接别名 `ChunkView.GetEntities()`，尚未读取的 handle 会被覆写或清零 | ≤8 个实体先 stackalloc 快照输入，再开始结构变更；大批量路径本就先完整收集后删除 |
| `BUG_Create_duplicate_component_types_throws_before_world_mutation` | 直接 `World.Create<T1,T2,...>` 传入重复组件类型时 `Signature` 静默去重，随后同一列被写两次并返回少于声明数量的组件集合 | arity 2..16 的 cache-miss 路径统一比较 normalized signature count；重复类型在分配 entity 前 fast-fail |
| `BUG_ChangeWatch_reentrant_Diff_throws_and_recovers` / `BUG_TransitionWatch_reentrant_Diff_throws_and_recovers` | handler 对同一个 watch 嵌套 Diff 会覆写外层复用 buffer/epoch 状态，导致重复或错误 callback | 三种 watch 的 Snapshot/Diff 统一增加实例级重入 guard，并用 `finally` 保证异常后恢复；不同 watch 仍可组合 |
| `BUG_failed_projected_Snapshot_invalidates_partial_baseline_and_recovers` | projected `Snapshot` 的 `Project` 中途抛异常后，`_hasSnapshot` 仍保留 true，后续 Diff 会读取已清理/半写的 baseline | Snapshot 开始收集前 invalidate baseline；只有完整成功才发布，异常后必须重新 Snapshot，operation guard 可恢复 |
| `BUG_Snapshot_load_rejects_duplicate_archetype_signature` | 不可信 snapshot 可声明两个归一化后相同的 archetype signature；Load 会静默合并，无法精确重建 payload 声明的 world 结构，重存也不再规范等价 | dry-validate 以 schema index 构造归一化 signature 并全局去重，在注册 schema 或构建 World 前拒绝重复 |
| `BUG_Snapshot_load_rejects_truncated_large_slot_table_before_allocation` | header 可声明数百万 slot、body 却不含 version table；Load 先分配 `int[slotCount]` 再读，几十字节输入可诱发数十 MB 至约 1GB 分配 | 在分配前用 long 计算 version table 字节数并与剩余 body 比较；截断输入直接 `InvalidDataException` |
| `BUG_snapshot_round_trip_preserves_reserved_entity_count` / `BUG_clone_preserves_reserved_entity_count` | World 含 CommandStream 预留但未 materialize 的 slot 时，Load/Clone 复制 records/free list 却把 `_reservedCount` 留为 0，导致 `EntityCount` 把 reservation 误报为活实体 | 所有重建路径从 slot count − free count − occupied count 统一推导 reservation；Reset 先清旧计数 |
| `BUG_Replay_existing_reservation_does_not_double_count_reserved_entity` | source 对自己的 real-id `Snapshot` 直接 Replay 时，目标 slot 已由 producer 预留；Replay 仍重复增加 `_reservedCount`，materialize 后 alive entity 的 `EntityCount` 变为 0 | 只有从 free list 实际取回 slot 才增加 reservation；已预留的同一 handle 直接复用现有计数 |
| `BUG_Replay_reservation_mismatch_does_not_consume_entity_id` / `BUG_Replay_stale_reservation_mismatch_does_not_consume_free_slot` | target allocator 与 real-id delta 不兼容时，Replay 先调用普通 reserve 消耗无关 fresh ID 或 stale-version free slot，再发现 handle 不匹配并抛错 | `EnsureReplayReservation` 只接受 matching free/reserved slot 或紧邻 fresh slot；其他状态在 allocator mutation 前 fail-fast |
| `BUG_Validate_rejects_unterminated_reservation` | `FrameDelta.Validate` 接受只有 Reserve、没有 Create/Release 的 delta；Replay 后留下无 owner 的永久 reservation | Validate 结束时要求 reserved set 为空 |
| `BUG_Validate_rejects_placeholder_use_before_create` | placeholder 已 Reserve 但尚未 Create 时被 Add/Set/层级/Destroy 使用，Validate 仍通过；Replay 在 reservation 落地后失败 | Validate 对所有操作 endpoint 检查 lifecycle state；existing real entity 仍可直接操作 |
| `BUG_Validate_rejects_unknown_remove_component_type` | Remove 是唯一未验证 component registry 的 component op，未知 type id 可绕过 Validate | Remove 与 Create/Add/Set 统一拒绝未注册 type id |
| `BUG_Validate_rejects_zero_version_real_entity` | primary/parent real entity 的 version=0 不属于有效运行时 handle，却被 Validate 当作合法 shape | placeholder 允许 seq=0；real entity version 必须为正 |
| `BUG_Validate_rejects_unreserved_embedded_placeholder` | Create/Add/Set payload 的 Entity 字段可引用从未 Reserve 的 placeholder；Validate 通过后 Replay 会在已 materialize owner 后失败 | dry validation 按注册 type 的 Entity field offset 扫 payload，placeholder 必须已有前置 Reserve |
| `BUG_nested_ForEachChunkParallel_does_not_overwrite_outer_partitions` | 外层并行 query 捕获调用线程的 ThreadStatic partition buffer；同线程 callback 嵌套另一个并行 query 会覆写该 buffer，使外层后续 worker 处理内层 World 的 chunk | 可复用 buffer 增加调用期 lease；同线程重入改用独立 fallback buffer，`finally` 释放 lease；非重入稳态路径不新增分配 |
| `BUG_hash_properties_do_not_expose_mutable_result_storage` / `BUG_hash_dictionaries_do_not_expose_mutable_result_storage` | `WorldDigestResult` 虽是 readonly struct，但公开 getter 直接返回内部 `byte[]`；`ReadOnlyDictionary` 也不保护 value array，调用方可改坏已返回结果 | 保持公共 `byte[]` API，所有 hash getter 返回 defensive copy，字典 getter 返回 value 深复制的只读快照 |
| `BUG_ComponentInfo_raw_bytes_do_not_expose_mutable_report_storage` | `ComponentInfo.RawBytes` 直接暴露报告内部数组，违背 Diagnostics“输出不可变”契约 | 私有持有原始 bytes，getter 返回 defensive copy；`EntityReport.ToString` 每组件只取一次副本 |

### 2026-07-15 quality hardening

| 回归测试 | 位置 / witness | 修复边界 |
|---|---|---|
| `BUG_single_byte_archetype_promotes_past_chunk_capacity` | `Archetype.Storage` 单字节列翻倍时 `int` 乘法溢出，可能越过 segment capacity | checked/long 容量预算；超过 flat 上限时 promotion，提交字段前先完成可能抛错的准备 |
| `BUG_bool_archetype_promotes_past_chunk_capacity` | `bool` 列同族溢出 | 同上 |
| `BUG_single_byte_tag_archetype_promotes_past_chunk_capacity` | 多个单字节列同族溢出 | 同上 |
| `BUG_submit_preflights_invalid_add_before_materializing_pending` | strict Add 到 Apply 才失败，pending 已 materialize | allocator/materialize 前模拟 component presence |
| `BUG_submit_preflights_invalid_set_before_materializing_pending` | strict Set 到 Apply 才失败 | 同上 |
| `BUG_submit_preflights_repeated_add_before_any_world_mutation` | 同 store 重复 Add 在中途失败 | preflight 按录制顺序更新虚拟 presence |
| `BUG_set_preflight_row_cache_is_disabled_when_any_store_is_structural` | 一个 store 迁移实体后，另一 store 使用旧 row cache | 任一 component store 结构化时全局禁用 Set row cache |
| `BUG_submit_preflights_hierarchy_overlay_cycle_before_world_mutation` | final hierarchy overlay 形成 cycle，较早操作已落地 | materialize/apply 前验证最终 parent overlay |
| `BUG_submit_preflights_hierarchy_cycle_through_existing_parent_chain` | overlay 与当前 World parent chain 合成 cycle | 同上 |
| `BUG_submit_preflights_deferred_hierarchy_cycle_before_reserving_ids` | deferred placeholder cycle 在 real-id reserve 后才失败 | placeholder 阶段直接验证 overlay |
| `BUG_async_submit_preflights_invalid_component_before_worker_handoff` | async API 在契约失败前 swap/start worker | active state 上先 preflight，后 handoff；立即登记 frozen/task ownership |
| `BUG_async_into_preflights_invalid_component_before_worker_handoff` | preflight 失败仍可能改写复用 target | preflight 通过前不启动 target writer |
| `BUG_debug_structural_scope_recovers_after_exception` | Debug `BeginStructChange/EndStructChange` 异常后计数残留 | Debug 配对使用 `try/finally`；Release 业务路径不增加异常区 |
| `BUG_stale_existing_entity_set_is_skipped_so_submit_matches_replay` | liveness 后移后 stale-only store 已被 prune，但旧 dirty flag 仍让 `Submit()` 错报已执行工作 | `PruneStaleCommands` 同步返回剩余命令状态；single/parallel、record 时 stale/consume 前 stale 均断言 `Submit()==false` |

`Existing_entity_component_liveness_is_decided_when_the_stream_is_consumed` 是 consume-time 契约测试，不是旧实现 bug 的 witness：record 时不读 World，consume 时按完整 `(Id, Version)` 统一 prune；stale/ID-reuse 安全与 stale-only 返回值由 `BUG_stale_*`、delayed stale 和 parallel stale 测试共同守卫。

### 仍在当前代码中生效的历史修复

| 索引 / 回归 | 原缺陷 | 当前修复边界 |
|---|---|---|
| EntityFieldResolver unresolved placeholder tests | OOB/unmapped placeholder 被静默留在组件字段 | `ResolveInPlace` fail-fast，错误含 sequence |
| `TrySetBit_rejects_ids_512_and_above` 等 | id ≥512 被 C# shift mask 别名到 lane 7 | 最后一 lane 显式 `<512` guard |
| `Snapshot_load_rejects_*`、schema import tests | schema/payload 无界读取、重复/非法 type、验证后期污染 registry | `ComponentSchemaCodec` 有界解析；Load 全量 dry-validate 后再注册/构建 |
| `BUG_pending_clone_copies_from_resized_batch_buffer` | `Array.Resize` 后继续写旧 BatchBuf 引用 | reserve 后重新读取当前 buffer |
| `CreateMany_duplicate_component_types_throws` | bulk path 重复类型破坏 last-wins | CreateMany 初始化前置条件不满足时 fast-fail |

### soak 发现的 Submit/Replay 分歧（B1-B6）

| # | 结论 | 当前防线 |
|---|---|---|
| B1/B4 | 历史内部 raw/typed Add 处理不同，Clone+Add 可分叉 | 当前 public/CommandStream Add 均为 strict Add；preflight 与 Replay 契约测试守卫，不再把“Add 已存在=覆盖”当公共语义 |
| B2 | cancelled pending batch 清理遗漏 reservation | Clear/consume 释放 reservation 的回归测试 |
| B3 | hierarchy Apply 与 emit 迭代顺序不同 | 两路共享确定性 comparer/overlay 语义 |
| B5 | replay free-list 任意位置 swap-remove 改变 survivor 顺序 | 指定 reservation 的 free-list removal 保序 |
| B6 | record-time cancel push 顺序与 wire Release 顺序不同 | `AlignCancelledBatchFreeListOrder()` 按 batch 顺序重排 |

### 已删除子系统的历史 bug（B7-B16）

B7-B16 属于旧 `ChangeQuery` / `Track().Capture().Previous()` / shared tracker 路径。该 API 和相关 registry/dispatch 文件已删除，当前 Watch 是独立 `Snapshot(World)` → `Diff(World)` pull 模型。这些条目不再作为当前代码的审阅依据；当前覆盖看 `WatchApiTests`、`WatchProjectedTests`、`ChangeTrackingSnapshotTests` 与 `CrossFeatureParityTests`。

### 2026-07-19 Query 顺序升级为签名排序

| 回归测试 | 问题 | 修复 |
|---------|------|------|
| `Query_iterates_archetypes_in_signature_order` / `Save_load_preserves_archetype_signature_order` | archetype 创建历史会让逻辑等价 World 的 query 顺序分叉 | `_archetypeSnapshot` 按 `Signature` 字典序插入；Save→Load 后仍由签名唯一决定顺序 |
| `Save_load_preserves_empty_archetypes` | Snapshot 丢弃空 archetype 会改变可观察的 World 结构 | Save/Load 保留空 archetype；checksum 仍可独立过滤空 archetype |
| `Clone_preserves_empty_archetypes_and_their_signature_order` | Clone 跳过空 archetype会改变可观察的 World 结构 | Clone 为每个源 archetype 建立目标 archetype，仅对空 archetype 跳过数据拷贝 |

## 已验证安全的模式（非 bug）

| 位置 | 猜想 | 结论 | 验证 |
|---|---|---|---|
| `QueryCache.EnsureRefreshed` + `Archetype.Storage.ConvertToChunked` | flat ChunkView 在 archetype promotion 后读到空 `_data` | 非 bug（单线程契约）。expected view shape 从 flat 变 segment count 时重建 views；并发结构写仍被禁止 | `EnsureRefreshed` shape 分支 + QueryCache/ChunkView tests |
| `Archetype` add/remove destination cache | 新 archetype 创建后 edge cache 需要失效 | 非 bug。source signature 与 component op 唯一决定 destination；signature 不变、archetype 不删除 | 搜索 `_addDestinationCache/_removeDestinationCache` + reset 路径 |
| `ChunkView.GetSpan` / `UnsafeGetComponentSpanAt` | 返回 span 跨结构变更后仍应有效 | 非 bug，是借用期契约。结构变更后 view/span/column index 全部失效；Unsafe 违约可静默错写 | `ChunkView` XML + Debug mismatch tests |
| `EntityAccessor` 跨 entity-storage 结构变更复用 | cached `(Archetype,row)` 可能指向被 swap-remove 后占位的其他实体，应由 World structure version 在 Release fail-fast | 非 bug，是 Accessor 的 unchecked borrow 契约；为违约调用增加 World 字段与每次 Get/Set/Has 分支会侵入内核并破坏其跳过 record/version check 的目的 | `EntityAccessor` / `World.Access` XML、`kb-core-ecs.md` 热路径契约、`kb-hardening-roadmap.md` M6“只用文档与 Debug 断言”原则 |
| entity query enumeration 期间结构变更 | cached backing array/count 可能跳过或重复实体，应在每次 MoveNext/ForEach callback 后用全局 version 检查 | 非 bug，是 entity enumerator 与 chunk span 的 unchecked borrow 契约；禁止在迭代期间结构变更，Release `MoveNext` 不增加全局状态读取与分支 | `Query` XML、`kb-core-ecs.md` 借用期契约、`kb-hardening-roadmap.md` M6 明令不修改 MoveNext 热路径 |
| `HierarchyTable.RemoveDestroyed` | `_firstChild` 未重置，ID 复用继承旧链 | 非 bug。先保存 slot 再把 `_firstChild[id]` 置 NoSlot，局部 slot 继续释放链 | `HierarchyTable.RemoveDestroyed` 代码走读 |
| `Archetype.RemoveAt` chunked | 跨 segment swap-remove 留空洞 | 非 bug。只与最后非空 segment 的尾实体交换，Debug invariant 守卫连续性 | `AssertSegmentInvariants` + chunked destroy tests |
| `World.Destroy` hierarchy | 子树中间节点清理不全 | 非 bug。后序遍历，child 先 `RemoveDestroyed`，parent 后处理 | hierarchy cascade tests |
| `Archetype.RemoveAt` dead bytes | ID 复用会读到旧组件值 | 非 bug。`Count` 隔离 dead zone，新实体迁入时全列覆写 | storage tests + validator |
| `EntityFieldResolver` offset cache | struct layout 后续变化使缓存失效 | 非 bug。运行进程内 Type→ComponentType 与 CLR layout 固定，offset 首次按实际 type 计算 | `EntityFieldResolver` 代码走读 |
| `RestoreState` 保留 capture 后创建的空 archetype | query 会把空壳当活数据 | 非 bug。archetype append-only，QueryCache 可见但 entity count 为 0 | restore/query tests |
| pending `Create+Add/Set/Remove` | 应暴露每个中间 Watch event | 非 bug。pending batch 契约只 materialize 最终状态 | pending Watch/transition parity tests |
| `CompactRemoveRowsFlat` hole-fill | live prefix 留下 stale source entity | 非 bug。hole 只由 tail suffix survivor 填；最终 dead suffix 清零 | batch destroy checksum/diff/validator tests |
| `ComponentBucketQuery.Get/TryGet` + short destination | 返回值大于 destination 长度看似“写入计数”越界 | 非 bug。实现有意返回总匹配数并只写入前缀，用一次扫描同时报告截断；XML、参数名和知识页已与该契约对齐 | `Short_destination_reports_total_match_count` / `Empty_destination_still_reports_matches` |
| `Destroy(ReadOnlySpan<Entity>)` 大批量分支 / `Destroy(query)` | 输入 span 也可能别名 archetype 存储，删除会覆写尚未读取的 handle | 非 bug。两条路径都在任何 storage mutation 前完整收集 destroy roots；只有已修复的小批量逐个删除分支需要输入快照 | `BeginDestroyCollection` → collect loop → `DestroyCollectedEntities` 代码走读 |
| `HierarchyTable.ClearHierarchyState` + surviving parent | parent 的 stale child slot 之后会被复用并指向无关活实体 | 非 bug。该 parent slot 没有被 `FreeChildSlot`，因此不会复用；方法只释放被清实体自己的 child-list slots，并先把自己的 `_firstChild` 断开 | `ClearHierarchyState` 与 `RemoveDestroyed` 对照走读；`World.Clear` XML 明示 stale-slot 边界 |
| 内部 dynamic archetype 构建 / Snapshot Load | `Signature` 去重也会掩盖重复类型，应像 public `World.Create` 一律抛错 | 非 bug。dynamic set 运算有意 canonicalize；Snapshot payload 在构建前已逐 archetype 拒绝重复 schema index。只有 direct generic Create 声称每个参数都是独立组件 | `GetOrCreateArchetype(ReadOnlySpan<ComponentType>)` / `ValidateArchetypePayload` 代码走读；CreateMany duplicate 测试 |
| `Query.ForEachChunk` / entity query callback 嵌套同一只读 query | 内层 `EnsureRefreshed` 重入 refresh lock 会死锁或改坏外层枚举状态 | 非 bug。Monitor lock 同线程可重入，每次枚举器/Span 都持有独立迭代状态；前提仍是回调内不做结构变更 | 嵌套控制流走读 + QueryCache double-check；parallel scratch 的独立缺陷由对应 `BUG_` 测试守卫 |
| `OrderedComponentEnumerator` comparison 内嵌套排序 | 内外排序会共享 comparer 或 pooled buffers 并互相改写 | 非 bug。`ComparisonCache.Acquire` 每次创建不可变 comparer，每个 enumerator 独立租用和持有 buffer | `OrderedComponentEnumerator.Initialize` / `ComparisonCache<T>` 生命周期走读 |
| `WorldSnapshot` checksum / `WorldValidator` 的 ThreadStatic collections | 与 parallel partition 同族，会在同线程重入时被覆写 | 非 bug。collection 活跃期间没有调用用户 delegate、虚 comparer、外部 Stream 或其他可重入边界；公共方法同步返回后才可能再次进入 | checksum feeder/排序/层级枚举与 validator 全控制流走读；唯一跨用户 callback 的 ThreadStatic partition 已单独修复 |
| `WorldDigest.CombineTypeDictHash` / `CombineIntDictHash` | Dictionary 枚举顺序未显式排序，逻辑相同的 digest total 可能分叉 | 非 bug（当前构造链）。component key 首次出现顺序由 signature-sorted archetype snapshot + signature component order 唯一决定；per-archetype key 按递增 index 插入；两个结果字典保留该确定性插入顺序 | `ComputePerComponentHashes` / `ComputePerArchetypeHashes` 的唯一插入点走读；相同 world 状态不存在不同插入历史 witness |
| `WorldSnapshot.Load` / `World.RestoreState` | 恶意输入或错误 snapshot 在恢复中途抛错会留下对调用方可见的半恢复 World | 非 bug（非灾难性异常边界）。持久化 Load 在注册 schema/构建 World 前 dry-validate 完整 payload，构建的是未发布局部 World；in-memory snapshot 是绑定源 World 的 opaque trusted handle，public ownership/recycle 校验均在 mutation 前。仅 OOM/灾难性内部失败不承诺 rollback | Load 两阶段控制流、`Snapshot_load_does_not_register_schema_type_when_later_payload_is_invalid`、RestoreState source/recycle tests 与完整 persistence 回归 |
| `CommandStream.Replay` 失败后的 `_replayTrackedBySeq` | 失败 Replay 残留 tracking 会在下一帧错误解析旧 `EntitySlot` | 非 bug。合法的下一帧 `Snapshot() → Clear()` 会以新 tracking 替换旧数组；异常后显式 `Clear()` 也会丢弃残留。只有把其他 stream 的 delta 配合 `resolveSlots:true` 才能复现错解析，但该调用违反“只解析本 stream 自己 delta”的契约 | 失败发生在 materialize 后的最小 delta + 下一帧同 stream Snapshot/Replay witness；`Snapshot`/`Clear` tracking 发布控制流走读 |
| `FrameDelta.Validate` embedded placeholder + `Reserve → Release` | component Entity 字段引用最终被 Release 的 placeholder 看似应因目标未 Create 而拒绝 | 非 bug。placeholder 在 Reserve 时已确定性映射成 real handle；component 允许保存之后失活的 Entity，正如它可保存后来 Destroy 的实体。Validate 只要求引用前存在可解析映射，不要求帧末存活 | `ReplayCore` 在 Reserve 写 placeholder map，Release 不抹映射；`ResolveInPlace` 解析成原 version，随后 slot version 前进使其成为普通 stale handle |

## 决策

- public `World.Add<T>` 与 CommandStream Add 是 strict Add；已存在时抛异常。`Set<T>` 要求已存在，`Remove<T>` 缺失为 no-op。
- Submit 与 Snapshot→Replay 的操作顺序、stale filtering、placeholder resolve 和 allocator 演化属于确定性契约。
- 用户可触发的公共边界要 fail-fast；只有已经由同一 consume 阶段证明过的内部热路径才可使用 unchecked helper。
- 修复具体 bug 后要做同族 pattern scan；新增真 bug 必须有 `BUG_` witness，再把索引写回本页。

## 认知模型

本页是“结论路由表”，不是 changelog。需要推理细节时按测试名和 symbol 跳到代码；不要在这里复制提交过程或保存已删除实现的逐行历史。

## 入口

- CommandStream 当前契约：`kb-command-stream.md`
- 存储：`kb-chunk-storage.md`
- Watch：`kb-change-tracking.md`
- 当前验证范围：`kb-safety-proof.md`
- 运行审阅前：先搜索本页中的测试名、symbol 和猜想关键词

## 坑点

- “某个 preflight 已修复”不等于整个 Submit/Replay 具有事务回滚。
- 旧测试数、旧行号和旧性能样本会漂移；只把它们当历史，不作为当前完成证据。
- 已删除 API 的 bug 不应继续驱动当前设计；先确认 symbol 仍存在。
- 确定性问题经常表现为 logical entities 相同但 free-list/order/checksum 不同，不能只比 EntityCount。
