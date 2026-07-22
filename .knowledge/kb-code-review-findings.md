---
title: 代码审阅发现
module: Meta
description: 审阅前必读的当前风险、已修复真 bug 回归索引与已排除非 bug 猜想；只保留结论和验证入口
updated: 2026-07-22
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
- **边界**：`FrameDelta.Validate()` 能在 Replay 前拒绝结构损坏的 wire，但不能证明 target World 与 delta 历史兼容；若 Replay 中途发生 target-world 契约错误或灾难性异常，已执行操作不会自动回滚。
- **当前策略**：不可信 wire 先 Validate；peer 从共同 snapshot/frame 0 沿完整历史 replay；需要强事务的调用方在外层使用 World snapshot/checkpoint。
- **不做**：本轮不引入通用 dry-run shadow World 或 rollback journal。

CommandStream 的 pending/component/hierarchy/async preflight 已修复已知“用户契约错误导致部分提交”路径，但它不把 Submit 或 Replay 提升为灾难性异常下的通用事务。

## 已修复的真 bug 索引

### 2026-07-22 全面审阅

| 回归测试 | 位置 / witness | 修复边界 |
|---|---|---|
| `BUG_full_lookup_missing_key_returns_empty_span` | `FrameLookup.FindSlot` 在开放寻址表恰好占满时查找不存在 key，没有空 stamp 可终止，进入无限循环 | 探测回到起点时返回未命中 |
| `BUG_generation_wrap_does_not_make_empty_slots_look_occupied` | `FrameLookup.Clear` 的 generation 回绕为 0 后与零初始化 stamp 混淆，非默认 key 的首次插入无限探测 | generation 命中 0 时清空 stamp 并从 1 重新开始 |
| `BUG_Describe_stale_handle_does_not_report_recycled_entity_as_alive` | `EntityDump.Describe` 只检查 slot occupied；ID 复用后会把 stale handle 报告成新实体且读取新组件 | alive 判定同时校验输入 handle version |
| `BUG_ValidationResult_keeps_its_issues_after_later_validation` | `ValidationResult.Issues` 包装 `WorldValidator` 的 ThreadStatic 复用 List；下一次 Validate 会清空并改写旧结果 | 构造结果时复制 issue 数组，结果与 scratch 生命周期解耦 |
| `BUG_full_lookup_accepts_additional_rows_for_existing_key` | `FindOrCreateSlot` 在 `distinctKeys >= _capacity` 时立即返回 -1，即使 key 已存在（开放寻址表满但 key 已在） | bounded probe 先匹配 existing key，绕一圈找不到才 -1 |
| `BUG_ensure_capacity_preserves_built_lookup` | `EnsureCapacity` 仅 `Array.Resize` 扩 table，没有 rehash live entries，扩后 indexer 返回错误位置 | 新局部 arrays 完整 rehash 后发布；异常保持旧结果 |
| `BUG_stateful_struct_selector_uses_same_initial_state_for_both_passes` | `TryBuild` 两遍共用同一 `selector` 参数；可变 struct 的 `Select` 在第一遍中 mutate 自身，第二遍从不同状态开始导致找不到 key | 每遍独立拷贝原始 selector（`countSelector`/`scatterSelector`）；第二遍 slot<0 时抛明确 `InvalidOperationException`，外层 catch 后 `Clear` |
| `BUG_failed_build_does_not_expose_partial_lookup` | `TryBuild` 中 `selector.Select`/`GetHashCode`/`Equals` 抛异常后，可能留下部分或旧的 lookup 数据 | `TryBuild` 用 `try/catch` 包裹 core，异常时 `Clear()` 后 `throw` |
| `BUG_constructor_rejects_invalid_capacity` / `BUG_EnsureCapacity_rejects_invalid_capacity` | 构造和 `EnsureCapacity` 未验证负数和超过 `MaxCapacity`（=1&lt;&lt;30）；`CeilPow2` 对大输入溢出 | 两个容量参数都限制在 0..`MaxCapacity`；`Build` 用饱和倍增并删除任意重试次数上限 |

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
| `HierarchyTable.RemoveDestroyed` | `_firstChild` 未重置，ID 复用继承旧链 | 非 bug。先保存 slot 再把 `_firstChild[id]` 置 NoSlot，局部 slot 继续释放链 | `HierarchyTable.RemoveDestroyed` 代码走读 |
| `Archetype.RemoveAt` chunked | 跨 segment swap-remove 留空洞 | 非 bug。只与最后非空 segment 的尾实体交换，Debug invariant 守卫连续性 | `AssertSegmentInvariants` + chunked destroy tests |
| `World.Destroy` hierarchy | 子树中间节点清理不全 | 非 bug。后序遍历，child 先 `RemoveDestroyed`，parent 后处理 | hierarchy cascade tests |
| `Archetype.RemoveAt` dead bytes | ID 复用会读到旧组件值 | 非 bug。`Count` 隔离 dead zone，新实体迁入时全列覆写 | storage tests + validator |
| `EntityFieldResolver` offset cache | struct layout 后续变化使缓存失效 | 非 bug。运行进程内 Type→ComponentType 与 CLR layout 固定，offset 首次按实际 type 计算 | `EntityFieldResolver` 代码走读 |
| `RestoreState` 保留 capture 后创建的空 archetype | query 会把空壳当活数据 | 非 bug。archetype append-only，QueryCache 可见但 entity count 为 0 | restore/query tests |
| pending `Create+Add/Set/Remove` | 应暴露每个中间 Watch event | 非 bug。pending batch 契约只 materialize 最终状态 | pending Watch/transition parity tests |
| `CompactRemoveRowsFlat` hole-fill | live prefix 留下 stale source entity | 非 bug。hole 只由 tail suffix survivor 填；最终 dead suffix 清零 | batch destroy checksum/diff/validator tests |
| `ComponentBucketQuery.Get/TryGet` + short destination | 返回值大于 destination 长度看似“写入计数”越界 | 非 bug。实现有意返回总匹配数并只写入前缀，用一次扫描同时报告截断；XML、参数名和知识页已与该契约对齐 | `Short_destination_reports_total_match_count` / `Empty_destination_still_reports_matches` |

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
