---
title: 代码审阅发现
module: Meta
description: 健壮性审阅发现汇总——已确认的设计债、已验证的安全猜想、已排除的非 bug 猜想
updated: 2026-07-09
---

> **M2 re-apply (2026-07-09)**: Epoch guard added — `World.ReservedReleaseEpoch` + `CommandStreamCore._submitEpoch` fast-path avoids O(N) pending-slot scan when no release has occurred since last sync. HeroComing.Perf: Movement 1902.1, Attack 1125.6, memory stable. Baseline gate (≥1642/≥997) passes.

> **Contract calibration (2026-07-09)**: Current code/test contract is strict Add: `World.Add<T>` throws when the entity already has `T` (`TrickyEdgeCaseTests.Add_component_that_already_exists_throws`). Older B1/B4 notes are historical context and must not be read as the current Add-as-overwrite contract.

> **Verification count policy (2026-07-09)**: Historical bug entries no longer preserve stale exact totals such as `673/695/845/872` because those counts rot on every test addition. Phrases like "当时全量测试通过" mean the original fix was full-suite green at that time; the current audit re-verified the suite as MiniArch.Tests 873/873 + HeroPipeline.Tests 5/5 PASS.

# 代码审阅发现

> 审阅前必读。包含已确认的设计债、已验证安全猜想、以及被排除的非 bug 猜想。

## 审阅历史

| 日期 | 类型 | 审阅者 | 范围 |
|------|------|--------|------|
| 2026-07-06 | 鲁棒性审阅 | Boss Agent (GPT-5.5) | 全量核心模块 (11 modules, ~8000 LOC) |

---

## 设计债 (P2)

### #1: CommandStream.Submit() 无事务回滚 (R11) ✅ 已修复

- **模式**: R11 部分修改
- **位置**: `CommandStreamCore.cs:443-474` Submit(), `CommandStreamCore.cs:548-578` PreValidatePendingSlots, `World.cs:1144-1153` IsSlotReserved
- **缺失的安全条件**: `MaterializeAllPending` 成功但后续 `ApplyHierarchy`/`ApplyComponentStores`/`ApplyDestroys` 失败时，已 materialize 的实体无法回滚
- **真实风险**: 低。该异常仅在 CommandStream 内部数据不一致时触发，正常用户路径不可达
- **建议修复**: 在 materialize 前预验证所有 slot 为 reserved 状态（defense-in-depth）
- **修复方案**: 在 `Submit()` 和 `SubmitFromFrozen()` 的 materialize 前增加 `PreValidatePendingSlots()`，扫描所有非 cancelled pending batch 的 slot 是否仍为 reserved 状态；若 slot 已不再是 reserved（`IsOccupied` 或 `Version` 不匹配），则立即抛 `InvalidOperationException`。新增 `World.IsSlotReserved(Entity)` 作为检查 helper，internal 不增加 public API。
- **回归测试**: `BUG_submit_prevalidates_reserved_pending_slots_before_materialize`（`CommandStreamTests.cs`）
- **验证**: 全量 Release 测试通过，HeroComing.Perf: Movement 1902.1 r/s, Attack 1125.6 r/s，内存稳定

### #2: EntityFieldResolver 静默跳过未决占位符 (R8) ✅ 已修复

- **模式**: R8 静默截断
- **位置**: `EntityFieldResolver.cs:174-189` ResolveInPlace（修复时 line numbers 已变）
- **原始缺陷**: 当 `seq >= resolveMap.Length` 或 `resolved.Id < 0` 时，不返回错误也不记录日志，死占位符 Entity(-1, seq) 残留于组件数据
- **修复方案**: 直接在 `ResolveInPlace` 中 fail-fast 抛 `InvalidOperationException`，消息含 seq 值。签名不变（仍 `void`）。不改调用方。
- **后续保护**:
  - 两个红测试（seq OOB、resolved.Id < 0）直接测试 `EntityFieldResolver.ResolveInPlace`
  - 现有集成测试 `Submit_resolves_embedded_Entity_ref_after_Destroy_pending` 已从"验证静默跳过"改为"验证抛出 `InvalidOperationException`"
- **真实风险（修复前）**: 低-中。需要恶意 FrameDelta（占位符引用无对应 Reserve）。正常 lockstep 场景不可达

### #3: ReplayCore 无事务语义 (R11)

- **模式**: R11 部分修改
- **位置**: `World.EntityLifecycle.cs` ReplayCore
- **缺失的安全条件**: Delta 回放中途失败时无法回滚已执行操作
- **真实风险**: 低。用户应通过 `FrameDelta.Validate()` 预校验——这已在文档中。`FrameDelta.Validate` 现在已校验 component data 大小与 schema 一致性；剩余风险是 replay 中途失败仍无法回滚，且 allocator/free-list 兼容性只能由从 frame 0 replay 或 snapshot bootstrap 保证
- **建议修复**: 强化文档约束；如需进一步 harden，可增加 replay 前 dry-run/target-world compatibility check，但当前 ROI 低
- **当前状态**: 2026-07-09 评估：接受现状，不修。其他 3 个 P2 已全部修复，此为唯一残留 P2。风险已被 `FrameDelta.Validate()` + 文档约束覆盖。

### #4: TrySetBit 未对 id >= 512 做守卫导致 mask 别名折叠 ✅ 已修复

- **模式**: 边界缺失守卫
- **位置**: `CommandStreamCore.cs:1625-1636` TrySetBit
- **原始缺陷**: `TrySetBit` 最后一个分支（`id < 448` 后的 `return TrySetBitInLane(ref b7, id - 448)`）未检查 `id < 512`。C# 对 `ulong` 的移位操作将移位计数掩码到低 6 位，导致 `id = 512..575` 的 `1UL << (id - 448)` 等价于 `1UL << ((id - 448) & 63)`，错误地别名到 b7 的 0..63 位（即 id 448..511 的范围）。这使 `MaterializeFromBatchBuffer` 中构建的 `ComponentMask` 被损坏：当实体使用 id >= 512 的组件时，mask 可能错误地匹配到 448-511 区间的原型，导致实体被分配到完全错误的 archetype。
- **真实风险**: 中。仅影响 `ComponentType.Value >= 512` 的组件（需要 513+ 个组件类型注册）。低 id 组件的热路径完全不受影响（`id < 448` 的分支不变）。调用方 `DeduplicateBatchChain` 已有正确守卫；`MaterializeFromBatchBuffer` 依赖 `TrySetBit` 的守卫。
- **修复方案**: 在最后分支增加 `if (id < 512) return TrySetBitInLane(ref b7, id - 448); return false;`。将 `TrySetBit` 改为 `internal static` 以便直接测试。
- **回归测试**: `TrySetBit_rejects_ids_512_and_above`、`TrySetBit_accepts_ids_448_to_511`、`TrySetBit_detects_already_set_at_boundaries`、`MaskBuilder_setting_high_id_does_not_alias_into_b7`
- **验证**: 全量 Release 测试通过，HeroComing.Perf: Movement 1870.2 r/s, Attack 1172.9 r/s，内存稳定

---

## 已验证安全的模式 (P3)

### #4: QueryCache 形状检测捕获 Archetype 平坦→分段晋升 (R5+R6+R13)

- **位置**: `QueryCache.cs:103-126` EnsureRefreshed + `Archetype.Storage.cs:58-100` ConvertToChunked
- **猜想**: 非分块 ChunkView(-1) 在 archetype 晋升为分块后访问 `_data`（已设 null）→ NRE
- **结论**: ✅ 已验证安全（单线程）。`EnsureRefreshed` 在快路径 #2 通过 `ExpectedViewShape(arch) != _archetypeExpectedViews[i]` 检测形状变化（-1 → SegmentCount），触发 `RefreshViewsOnly()` under lock 全量重建。并发场景下的竞争窗口是已文档化的"查询期间禁止结构变更"约束，不是实现缺陷
- **验证**: 代码走读 `QueryCache.cs:113-125` + `ChunkView.cs:85-98`

### #5: Archetype Edge Cache 无需失效机制 (R14)

- **位置**: `Archetype.cs:107-122` TryGetAddDestination / TryGetRemoveDestination
- **猜想**: Edge cache 不检查 generation 计数器，新 archetype 创建后旧缓存项可能指向过时目标
- **结论**: ✅ 已验证安全。`(SourceArchetype, AddComponentX) → DestinationArchetype` 映射由签名算术决定，签名不可变且 archetype 永不删除 → 映射永久正确。`Reset()` 时旧 archetype 全部重建，旧缓存随对象 GC。代码中不存在失效逻辑是正确设计，不是遗漏
- **验证**: 代码走读 `Archetype.cs:107-122` + 全量搜索 `_addDestinationCache` 引用（仅 6 处，无失效/清除代码） + `World.cs:1227-1266` generation 计数器仅用于泛型静态 `CachedCreateArchetype`

### #6: ChunkView 内部引用有效性 (R13)

- **位置**: `ChunkView.cs:85-98` GetSpan<T>()
- **猜想**: ChunkView 返回 Span<T> 指向 Archetype 内部 byte[]，结构变更后 Span 变悬空
- **结论**: ⚠️ 契约约束，非实现缺陷。XML doc 明确禁止跨结构变更持有 ChunkView。违反时最可能的结果是 NRE 或 IndexOutOfRangeException（fail-fast），而非静默错读。QueryCache 在每次 EnsureRefreshed 时重建视图
- **验证**: XML doc `ChunkView.cs:14-19` + QueryCache 重建逻辑

---

## 安全猜想（已调查，非 bug）

| # | 猜想 | 调查结果 | 验证方式 |
|---|------|----------|----------|
| S1 | `HierarchyTable.RemoveDestroyed` 中 `_firstChild` 未重置导致 ID 回收后继承旧链表 | ❌ 非 bug。第 199 行显式 `_firstChild[entity.Id] = NoSlot`，已缓存的局部变量 `slot` 继续遍历 | 代码走读 `HierarchyTable.cs:183-214` |
| S2 | `Archetype.RemoveAt` 分块模式跨段 swap-remove 产生空洞破坏段连续不变式 | ❌ 非 bug。始终与最后一个非空段交换，`AssertSegmentInvariants` (DEBUG) 验证"非空段连续在前" | 代码走读 `Archetype.Storage.cs:333-369` |
| S3 | `World.Destroy` 子树中间节点 hierarchy 清理不全 | ❌ 非 bug。后序遍历 + `DestroySingle` 中每节点调自己的 `RemoveDestroyed`，子节点先清理 → 父节点轮到时链表已空 | 代码走读 `World.EntityLifecycle.cs:83-229` |
| S4 | `Archetype.RemoveAt` 残留组件数据在 ID 复用时泄露 | ❌ 非 bug。Archetype 内同组件集合保证新实体迁入时全量覆写。`Count` 边界阻止读取 | 设计分析 |
| S5 | `Archetype._cachedFlatEntities` 世代计数器 `long` 溢出 | ❌ 非 bug。需 2^63 次布局变更，物理不可能 | 数值分析 |
| S6 | EntityFieldResolver struct layout 变化导致 offset 缓存失效 | ❌ 非 bug。`Marshal.OffsetOf` 实时查 CLR，缓存按 `ComponentType.Value` 索引，类型→ID 映射不可变 | 代码走读 `EntityFieldResolver.cs:70-103` |
| S7 | `RestoreState` 不回退 `_archetypeSnapshot` 导致 query 看不到 capture 后创建的新 archetype | ❌ 非 bug。capture 后创建新 archetype 时当前 `_archetypeSnapshot` 已经由 `PublishArchetypeSnapshot` 追加；restore 后这些 archetype 作为空壳保留，QueryCache 看到空 chunk/entity count 不影响正确性。archetype 永不删除是 QueryCache append-only 失效机制的基础 | 代码走读 `World.cs:1205-1228` + `World.QueryCache.cs:127-140` + `QueryCache.cs:103-126` |
| S8 | `CommandStream` 对 pending entity 的 `Create+Add/Set/Remove` 应产生中间 `Changes()` / `Transitions()` | ❌ 非 bug。pending batch 的契约就是只保留最终 materialized state；同一 pending entity 上的 Add/Set/Remove 在 Submit/Snapshot/Replay 前折叠为最终组件签名。中间操作不会作为独立 write/transition 事件暴露；只有 final state 创建时的最终 filter 匹配结果可观察 | 代码走读 `WritePendingComponent` / `MarkBatchComponentRemoved` → `DeduplicateBatchChain` / `MaterializeFromBatchBuffer` / `EmitCreateFromBatch` + 契约测试 `A6/A7/B16/B16b` |
| S9 | **M4 变质对等扫描**：Submit/Replay/Restore 三路收敛验证——9 个模式共 9 个测试全 PASS，零分歧 | ✅ 无歧 — Submit/Replay/Restore 收敛。测试覆盖：P1 (Create+Add)、P2 (Set)、P3 (Remove+Add 同组件)、P4 (Add+Remove 同组件)、P5 (Hierarchy+cascade destroy)、P6 (create/cancel churn B5/B6 territory)、P7 (Clone+mutate)、P8 (Add+Set+Remove 同组件)、P9 (高密度混合 burst)。Restore 路径验证：所有模式 CaptureState 后 RestoreState 正确回滚到 pre-mutation 状态。3 路字节级 checksum 一致 | 运行 `SubmitReplayRestoreParityTests` 9 个测试，全量 Release 测试通过 |

---

## 交叉引用

- 历史上已修复的真 bug 和回归测试 → 见各 kb 页的"坑点"段 + 对应测试文件
- 已知约束和设计权衡 → `kb-design-rationale.md`
- 架构审视 → `kb-architecture-review.md`
- 鲁棒性审阅技能 → `.agents/skills/robustness-review/SKILL.md`

---

## 浸泡测试发现的 bug（已验证已修复）

浸泡测试 `tools/soak/MiniArch.Soak/` 通过长周期 Submit/Replay 随机操作序列验证正确性。以下是发现的库级 bug：

### B1: `ApplyRawAdd` 重复 Add 抛异常（Replay 路径）

- **位置**: `World.StructuralChange.cs:152` `ApplyRawAdd`
- **症状**: ReplayCore 处理 delta 中的 Add 操作时，如果实体已有该组件则抛异常
- **根因**: Submit 路径 `ApplyTypedAdd` 通过 `PruneStaleComponentCommands` 去重，Replay 路径无等价去重
- **触发场景**: Clone 继承组件后同一帧又 Add 同一组件 → delta 含重复 Add
- **修复**: 实体已有组件时原地写值而不是抛异常（Add 语义是"确保实体带该组件"）
- **验证**: 浸泡测试 20000 帧 PASS（历史，ApplyRawAdd/ApplyTypedAdd 内部覆盖行为）；当前 `World.Add<T>` 公 API 为 strict Add，测试 `TrickyEdgeCaseTests.Add_component_that_already_exists_throws` 验证已存在时抛异常

### B2: `Clear` 不释放已取消 batch 的预留实体

- **位置**: `CommandStreamCore.cs:1515` `Clear`
- **症状**: FreeList 分裂，总数正确但 free slots + occupied slots ≠ 总 slot 数
- **根因**: `Clear(releaseReserved: false)` 跳过已取消 batch 的实体（未调用 `ReleaseReservedEntity`），但 ReplayCore 始终释放 → 两路径 FreeList 分歧
- **触发场景**: `Create` → `Destroy`（取消 batch）后实体 slot 永久残留为"reserved"状态
- **修复**: 取消的 batch 无论 `releaseReserved` 标志都释放
- **验证**: 浸泡测试 PASS + 当时全量测试通过

### B3: `ApplyHierarchyToWorld` 与 `EmitHierarchyToDelta` 层级操作顺序不同

- **位置**: `CommandStreamCore.cs:951` `ApplyHierarchyToWorld`
- **症状**: 同一帧内多个 AddChild/RemoveChild 产生不同循环检测/跳过结果
- **根因**: `ApplyHierarchyToWorld` 按字典迭代（插入顺序），`EmitHierarchyToDelta` 按 `HierarchyComparer` (Entity.Id) 排序 → 某些操作序列下 Submit 看到循环而 Replay 不报，或反之
- **修复**: `ApplyHierarchyToWorld` 也使用 `HierarchyComparer` 排序
- **验证**: 浸泡测试 PASS

### B4: `ApplyTypedAdd` 重复 Add 抛异常（Submit 路径）

- **位置**: `World.StructuralChange.cs:100-107` `ApplyTypedAdd<T>`
- **症状**: Submit 时 Clone 创建的实体已有组件，ComponentStore 的 Add 操作试图再次添加 → 抛异常
- **根因**: Submit 流程中 MaterializeCreates（含 Clone 的组件）先运行，然后 ComponentStore 的 Add 操作运行 → 组件已存在
- **触发场景**: 浸泡测试中 OpClone 后紧接 OpAdd 同一组件
- **修复**: 与 B1 相同：实体已有组件时原地写值而不是抛异常
- **验证**: 浸泡测试 20000 帧 PASS（历史，ApplyRawAdd/ApplyTypedAdd 内部覆盖行为）；当前 `World.Add<T>` 公 API 为 strict Add，测试 `TrickyEdgeCaseTests.Add_component_that_already_exists_throws` 验证已存在时抛异常

### B5: `EnsureReplayReservation` swap-remove 导致 FreeList 顺序分歧（Submit vs Replay）

- **位置**: `World.EntityLifecycle.cs:549-560` `RemoveFromFreeList` / `World.EntityLifecycle.cs:423-426` `PopFreeIdUnsafe`
- **症状**: Submit 与 Replay 产生不同的 free-list 顺序；alive entities 集合完全一致，但 free-list 上已回收 slot 的顺序不同。最终 CanonicalChecksum 因 free-list 顺序不匹配而报错。浸泡测试 `--seed 111 --entity-cap 100 --entity-floor 10 --ops-per-frame 50` 在单帧高频操作下触发。
- **根因**: 同一帧内多个 pending entity 被 Create 后部分被 Cancel（Destroy）时，Submit 路径与 Replay 路径对 free-list 的移除方式不同：
  - **Submit 录制期**：`CreateImpl()` → `AcquireEntityIdUnsafe()` → `PopFreeIdUnsafe()` 始终从 free-list **末尾**（栈顶）LIFO 弹出，不改变剩余条目的相对顺序。
  - **Replay 期**：`EnsureReplayReservation()` (Case 1) → `RemoveFromFreeList(entity)` 从 free-list **任意位置**扫描匹配的 `(id, version)`，找到后执行 **swap-remove**（用末尾元素覆盖匹配位置，再递减 count）。当匹配的实体不在末尾时，末尾元素被 swap 到中间，改变了 free-list 的内存顺序。
  - 当同一帧内有 2+ 个 pending entity 被取消并重新被后续 Create 消费时，Replay 路径的 swap-remove 级联改变那些**在帧结束时幸存**的 free-list 条目的顺序，而 Submit 路径的 LIFO pop 不会产生这种重排。
- **详细机制**：见 `tests/MiniArch.Tests/Core/FrameDeltaDeterminismTests.cs` 中 `Submit_and_Replay_free_list_diverges_with_multi_cancel` 测试的 XML doc。核心序列：Create 3 entities（消耗 free-list 顶部 3 个 slot），Cancel 其中 2 个（Push back 到栈顶），然后 Submit vs Replay 在帧结束后 survivor 条目的数组顺序不同。
- **触发条件**：需要单帧内 2+ 个 pending entity 被取消，且 free-list 上至少 2 个幸存条目（即 frame 结束后 free-list 非空）。浸泡测试高 ops/frame + 小 entity cap 时容易命中：Create 耗尽 free-list、Cancel 回填、survivor 因 swap-remove 顺序错乱。
- **修复建议**：`RemoveFromFreeList` 不应使用 swap-remove。改为从末尾线性扫描找到匹配项后，直接递减 `_freeIdCount`（用末尾元素覆盖匹配项，而不是反过来）。但调用方 `EnsureReplayReservation` 需要保证匹配的实体确实在 free-list 上且其位置不影响正确性。或者让 `EnsureReplayReservation` 走统一的 `PopFreeIdUnsafe()` + 版本校验，但需处理 Reserve 要求指定实体而非任意实体的问题。
- **回归测试**：`Submit_and_Replay_free_list_diverges_with_multi_cancel`（已修复，当前 PASS）
- **确认**: 推翻"cascade destroy ordering"假说；根因在 free-list 的 swap-remove vs LIFO pop，而非层级级联顺序。

### B6: `CancelPendingEntity` 录制期 push 顺序与 Replay 期 Release 顺序不同（修复后仍存在二次分歧）

- **位置**: `CommandStreamCore.cs:1096` `CancelPendingEntity` / `World.EntityLifecycle.cs:449` `ReleaseReservedEntity`
- **症状**: 浸泡测试 `--seed 111 --entity-cap 100 --entity-floor 10 --ops-per-frame 50 --frames 5000` 在 frame ~1839 报 CanonicalChecksum mismatch——occupancy 一致但 free-list 上 2 个相邻条目互换位置（如 9(v54) 与 121(v9)）。
- **根因**: B5 的 shift-remove 修复后，仍有第二条分歧路径：
  - **录制期**：`CancelPendingEntity` 在用户 Destroy 调用时立即调用 `ReleaseReservedEntity` → `PushFreeIdUnsafe`，将废 slot 追加到 free-list 末尾。push 顺序是**用户销毁顺序**。
  - **Replay 期**：`EmitPendingEntitiesToDelta` 按 batch 索引（创建顺序）发射 `Reserve+Release` 对。Release 处理 `ReleaseReservedEntity` → `PushFreeIdUnsafe`，push 顺序是**batch 创建顺序**。
  - 当同一帧内 pending entity 的创建顺序与销毁顺序不同时（如创建 A 再创建 B，但销毁 B 再销毁 A），两个路径的 free-list 末尾条目顺序相反——相邻两条目互换。
- **触发场景**: 浸泡测试中 `Clone(76)→9` 创建 entity 9、`Create(121)` 创建 entity 121，然后 `Destroy(9)`、`Destroy(121)` 依次调用。batch 创建顺序为 [15, ..., 121, 9, ...]，销毁顺序为 9 先于 121。录制期 push 顺序为 9(v54)、121(v9)；Replay 期的 Release 顺序为 121(v9)、9(v54)。最终 free-list 上 9 与 121 互换。
- **修复**: 在 `Submit()` 开始时调用 `AlignCancelledBatchFreeListOrder()`，遍历已取消的 batch（按 batch 创建顺序），对每个仍留在 free-list 中的 cancelled entity 执行 `RepushFreeEntry`（从当前位置移除并重新 append 到末尾）。这使源 world 的 free-list 在 `ApplyDestroys`（push 常规销毁）之前与 Replay 路径的 free-list 一致。
  - **副作用**: batch 顺序对齐改变了后续帧的 entity ID 分配（free-list pop 顺序不同），间接触发了已存在的级联销毁顺序问题和层级循环检测问题。为此额外修复：
    - `ApplyDestroys` 和 `SubmitFromFrozen` 在销毁前检查 `IsAlive`（与 Replay 路径一致），防止已级联销毁的 entity 再次销毁报错。
    - 浸泡测试 `OpAddChild` 的循环检测增强为模拟 `ApplyHierarchy` 的排序顺序检测循环，防止违反库层级不变式的操作被记录。
- **验证**: 浸泡测试 200000 帧 PASS + `HeroComing.Perf` 性能门禁通过 + 当时全量单元测试通过。

### B7: `ChangeQuery.Previous()` 快照捕获点对缺失 captured type 崩溃

- **位置**: `ChangeQuery.cs` 的 `OnBeforeWrite`、`OnAfterWrite`、`OnBeforeTransition`、`WriteNewTransitionSnapshot`
- **症状**: 当 `.Previous()` 启用且 `.Capture<T>()` 注册的类型在当前 entity 的 archetype 中不存在时（例如 entity 有 `Position` 但无 `Mana`，而 `Mana` 被 capture），原代码使用 `GetComponentIndexFast` + `GetComponentBytes` 读取不存在的列 → `IndexOutOfRangeException` 或错读其他组件数据。
- **根因**: 4 个快照捕获点使用 `GetComponentIndexFast`（无边界检查），假设所有 captured type 一定存在于匹配 archetype 中。实际存在 entity 通过 filter 匹配（`.With<HP>()`）但部分 captured type 不存在的场景（如 capture `Mana` + filter `With<HP>`，entity 只有 `Position,HP`）。
- **触发场景**: `Track().Capture<Mana>().With<HP>().Previous()` 后，对一个有 `HP` 但无 `Mana` 的 entity 做 `Add<Velocity>`（结构变更触发 `OnBeforeTransition`），或其他写操作触发 `OnBeforeWrite`/`OnAfterWrite`/`WriteNewTransitionSnapshot`。
- **修复**: 将 4 个点的 `GetComponentIndexFast` 替换为 `TryGetComponentIndex`，类型不存在时跳过字节拷贝，快照对应范围保持零值。
- **回归测试**: `Previous_on_empty_world_then_Add_does_not_crash`、`Previous_on_empty_world_then_Set_does_not_crash`、`Previous_then_Remove_captured_component_keeps_zero_new_snapshot`
- **验证**: 上述回归测试通过。HeroComing.Perf 门禁 MOV 1925/ATK 1179，内存稳定。

### B8: `RestoreState()` 后旧 `ChangeQuery` 的 typed tracker 未自愈

- **位置**: `ChangeQuery.cs:64-83` `EnsureUsable` / `World.cs:1273-1276` `RestoreState`
- **症状**: 旧 query 在 `RestoreState()` 后继续使用时，`ValueChanges<T>()` 先 drain 出 restore 前的脏数据；后续新的 `Set<T>` 又完全不再进入该 tracker
- **根因**: `RestoreState()` 清空了 `world._typedTrackers`，但 `ChangeQuery.EnsureUsable()` 的 self-heal 只重注册 transition query，没有清空并重建 `_typedTracker`
- **修复**: self-heal 时显式 `DeactivateTypedTracker()`，随后按当前 query 配置重新激活 typed tracker
- **回归测试**: `X_Watch_RestoreState_ValueTrackingSurvivesRestore` (CrossFeatureParityTests.cs:567)、`X_Watch_RestoreState_TransitionTrackingSurvivesRestore` (608)、`RestoreState_preserves_watch_for_post_restore_mutations` (ChangeTrackingSnapshotTests.cs:60)
- **验证**: 回归测试通过（Watch API 等价覆盖）

### B9: typed fast path 激活后继续加 filter / 第二个 capture，`ValueChanges<T>()` 仍返回旧 tracker 数据

- **位置**: `ChangeQuery.cs:90-104` `Capture<T>` / `ChangeQuery.cs:186-231` `With/Without/WithAny`
- **症状**: `Track().Capture<Position>().Previous()` 先激活 typed tracker 后，再 `.With<Position>()` 或 `.Capture<Velocity>()`，query 已不满足单 capture + 无 filter 契约，但 `ValueChanges<Position>()` 仍继续返回数据
- **根因**: typed tracker 只有“激活”逻辑，没有“失效撤销”逻辑；query 配置变化后旧 tracker 仍挂在 world 上继续接收 `Set<T>`
- **修复**: 引入 `DeactivateTypedTracker()` / `RefreshTypedTrackerActivation()`；filter 和第二 capture 会撤销 typed fast path
- **回归测试**: 无直接等价测试——旧 typed fast path 方案（`ValueChanges<T>` / `Track().Capture<T>().Previous()`）已由 Watch API 取代。Watch API 中 `ChangeWatch` / `TransitionWatch` 的激活状态由 `Watch()` 时刻的快照决定，不存在"激活后改配置"的场景。此风险已被架构设计消除。
- **验证**: 不再适用（旧 API 已删除）

### B10: query 创建后 world 继续扩容，typed tracker 的 entity 索引数组不跟随增长

- **位置**: `World.StructuralChange.cs:159-197` `ApplyTypedSet` / `ChangeTracker.cs:61-79`
- **症状**: query 在 `EntityCapacity=64` 时创建，world 后续创建到第 65 个实体再 `Set<T>`，`SlotByEntityPlusOne[id]` 越界，最终在 `ValueChanges<T>()` drain 时触发 `IndexOutOfRangeException`
- **根因**: tracker 只在激活时 `PreSize()` 一次；后续 `Set<T>` 直接用 `Unsafe.Add` 按 `entity.Id` 索引，没有按 world 增长补扩容
- **修复**: `ApplyTypedSet` 在访问 slot 前调用 `typedTracker.EnsureEntityCapacity(id)`；该方法只增长当前写 buffer，不动可能仍被用户 span 持有的 `SpareLog`
- **回归测试**: `Watch_handles_world_growth_after_arming` (ChangeQueryTests.cs:274)
- **验证**: 回归测试通过

### B11: destroyed entity 的旧 slot 未清，id 复用后被误判为同一实体的二次 Set

- **位置**: `World.EntityLifecycle.cs:205-230` `DestroySingle` / `World.StructuralChange.cs:282-301` `RemoveBoxed`
- **症状**: 同一 drain 周期内，实体 A `Set<T>` 后被销毁，再复用相同 id 创建实体 B 并 `Set<T>`，`ValueChanges<T>()` 只返回 1 条，且把 A 的 `Old` 与 B 的 `New` 错拼到一起
- **根因**: `SlotByEntityPlusOne` 只按 `entity.Id` 建索引；destroy / remove 成功后没有及时把该 slot 清零，后续同 id 写入走了“重复 Set”分支
- **修复**: destroy 时清所有 typed tracker 的该 id slot；remove 某组件时只清对应 component tracker 的 slot，避免 remove+add+set 跨组件生命周期串脏
- **回归测试**: `Destroy_and_id_reuse_does_not_leak_stale_change` (ChangeQueryTests.cs:346)、`ChangeWatch_destroy_recreate_reports_stale_old` (WatchApiTests.cs:151)、`ChangeWatch_destroy_recreate_stale_slot_reported` (WatchApiTests.cs:199)
- **验证**: 回归测试通过

### B12: `CommandStream.Set` 的 set-only / mixed Set 路径绕过 typed tracking

- **位置**: `CommandStreamCore.cs:2701-2783` `ComponentStore<T>.ApplyToWorld`
- **症状**: `world.Track().Capture<T>().Previous().ValueChanges<T>()` 能看到直接 `world.Set<T>()` 的写入，但看不到 `CommandStream.Set()` 经 `Submit()` 落地的写入；Hero pipeline 这类大量经 `CommandStream` 改值的场景中，observer 的 `changes=0`
- **根因**: `ApplyToWorld` 的 set-only fast path 和 mixed Set 分支都直接调用 `SetComponentAtFlatNoTrack` / `SetComponentAtTypedNoTrack`，完全绕过 `World.ApplyTypedSet<T>`，因此 typed tracker 从未收到 CommandStream 的 Set 写入
- **修复**: 仅当 `world._typedTrackers` 非空时，`CommandStream` 的 Set 路径改走 `World.ApplyTypedSet<T>`；无人 tracking 时保留原 no-track 快路径，默认 baseline 不变
- **回归测试**: `Watch_captures_CommandStream_Set` (ChangeQueryTests.cs:66)
- **验证**: 回归测试通过

### B13: shared `ChangeTracker<T>` 切换后的 API / 预分配回归

- **位置**: `ChangeQuery.cs` / `SharedTrackerRegistry.cs` / `World.cs`
- **症状**: shared tracker 初版实现中存在 4 个回归风险：
  1. `.Previous().Capture<T>()` 顺序不创建 tracker，`ValueChanges<T>()` 永远为空。
  2. `ValueChanges<T>()` 未检查 T 是否等于 query 唯一 captured type，可能读到另一个 query 创建的同类型 world tracker。
  3. `ChangeQuery.ClearChanges<T>()` 未按 query captured type 限定，可能越权清掉其它组件类型的 shared tracker。
  4. `SharedTrackerRegistry.GetOrCreateTracker<T>()` 创建或复用 tracker 时未 `PreSize(world.EntityCapacity - 1)`，首个 `Set<T>` 重新分配，破坏稳态零分配预期。
- **根因**: 从 per-query tracker 切到 world-shared tracker 后，原先由 `_typedTracker is ChangeTracker<T>` 隐式提供的类型隔离和由 `TryActivateTypedTracker<T>` 提供的预分配语义被移除，但初版未显式补回。
- **修复**:
  - `Capture<T>()` 在 `_hasPrevious` 已为 true 且仍满足单 capture/no filter 时创建 shared tracker。
  - `ValueChanges<T>()` / `ChangeQuery.ClearChanges<T>()` 均要求 T 等于唯一 captured type。
  - `World.ClearChanges<T>()` 增加 `AssertNotDisposed()`。
  - `SharedTrackerRegistry.GetOrCreateTracker<T>(entityCapacity)` 创建/复用时调用 `PreSize(entityCapacity - 1)`。
- **回归测试**: 无直接等价测试——旧 `ValueChanges<T>` / `ClearChanges<T>` / `SharedTrackerRegistry` API 已全部被 Watch API 取代。Watch API 中每个 `ChangeWatch<T>` 独立持有 tracker，类型隔离和预分配由 watch 创建逻辑天然保证。此风险已被架构设计消除。
- **验证**: 不再适用（旧 API 已删除）

### B14: capture-only query 常驻 transition 注册 / shared registry 常驻导致 Hero 基线回退

- **位置**: `World.Track()` / `ChangeQuery.RefreshTransitionRegistration()` / `World.SharedTrackers` / `CommandStreamCore.ComponentStore<T>.ApplyToWorld()`
- **症状**:
  1. `Track().Capture<T>()` 不开 `Previous()`、不加 filter 时仍被 `World.Track()` 立即注册为 transition observer，高 churn 场景处理上亿次无意义 transition。
  2. shared tracker 初版让每个 `World` 常驻分配 `SharedTrackerRegistry`，no-observer `CommandStream` 也必须经 registry 判断，HeroComing no-observer Movement 从接近 1950 掉到约 1836。
- **根因**: Capture 不是 filter，但旧注册逻辑把 cursor 创建等同于 transition 订阅；同时把“没有 tracking”表示为“空 registry 对象”而不是旧版 direct-null fast path，破坏 no-track 空状态。
- **修复**:
  - `World.Track()` 不再立即注册 query；只有 `.With/.Without/.WithAny` 设置 filter 后才注册 transitions。
  - capture-only/no `Previous()`/no filter query 变为 inert cursor。
  - `SharedTrackerRegistry` 改为 lazy/null，只有 `Previous()` 创建 value tracker 时才分配；Dispose 清空并置 null。RestoreState 只清 pending changes，保留仍存活观察者的 tracker。
  - `CommandStreamCore.ComponentStore<T>.ApplyToWorld()` 在 registry 为 null 时直接走内联 no-track loop；tracked loop 留在独立 helper。
- **回归测试**: 无直接等价测试——旧 capture-only/inert query 概念及 `Track()` / `ValueChanges_nontracking_query_does_not_interfere` 均随旧 API 删除。Watch API 中 `Watch<T>()` 创建即为 active watch，无惰性 cursor 机制。此风险已被架构设计消除。
- **验证**: 不再适用（旧 API 已删除）

### B15: `RestoreState()` 后旧 observer 需要手动读取 re-arm，否则漏掉第一批 post-restore mutation

- **位置**: `World.RestoreState()` / `ChangeQuery.EnsureUsable()` / `SharedTrackerRegistry.Clear()`
- **症状**:
  1. value query：`RestoreState()` 后如果用户没有先调用 `ValueChanges<T>()` 触发 self-heal，而是直接 `Set<T>`，这次 post-restore Set 不会进入 tracker。
  2. transition query：`RestoreState()` 清空 `_changeQueries` 后，旧 filter query 不再注册；restore 后第一次 Create/Add/Remove/Destroy 发生在下一次 `Transitions()` 前时会被漏掉。
- **根因**: restore 把 observer runtime state 当作可完全丢弃的缓存处理；但旧 `ChangeQuery` 是用户持有的 live cursor，语义上不应要求用户在 rollback 后手动 re-arm。lazy self-heal 只能修复“先读后写”，不能修复“先写后读”。
- **修复**:
  - `RestoreState()` 不再清空 transition observer 列表，而是对 live query 调用 `OnWorldRestored()`：清 stale transition/cache，推进 generation，并保留注册。
  - `SharedTrackerRegistry.ClearChanges()` 只清 pending value-change logs，保留已创建 tracker；Dispose 仍用 `Clear()` 移除 tracker 并置 null。
- **回归测试**: `X_Watch_RestoreState_ValueTrackingSurvivesRestore` (CrossFeatureParityTests.cs:567)、`X_Watch_RestoreState_TransitionTrackingSurvivesRestore` (608)、`X_Watch_RestoreState_NoStaleTransitionsAfterRestore` (642)、`RestoreState_preserves_watch_for_post_restore_mutations` (ChangeTrackingSnapshotTests.cs:60)
- **验证**: Release 全量测试通过；HeroComing.Perf baseline gate 通过（Movement 1940.1 / Attack 1189.0，内存 OK）；MiniArch.Soak sweep 8/8 PASS。

### B16: Replay raw Add 后同批 raw Set 漏掉 value diff baseline（历史——旧 API 已删除）

- **位置**: `World.StructuralChange.cs` `ApplyRawAdd` / `ChangeTracker.cs` boundary diff baseline 捕获（旧文件，2026-07-09 随 Watch 重构删除，具体行号已不可用）
- **症状**: 已存在实体在 delta replay 中先 `Add<T>(valueA)` 后 `Set<T>(valueB)`；旧 `TrackValueChanges<T>()` 已启用时，`.Changes` 返回空，而 Submit typed path 会返回 `{ Old=valueA, New=valueB }`。
- **根因**: old boundary diff 改造后 typed Add 会 capture 初始 baseline，但 raw Replay Add 只迁移并写入 bytes；后续 raw Set 直接写当前值。读端扫描发现该 entity 无 baseline，于是把最终值 `valueB` 当作新 baseline，漏掉 Add 初值到 Set 终值的 diff。**此 API 已被 Watch pull-event 模型取代，不影响当前行为**。
- **修复**: `IChangeTrackerControl` 增加 raw baseline capture；`ApplyRawAdd` 成功后按 component type 对已存在 tracker 写入 Add 初值 baseline。raw Set 与 `Set<T>` 热路径不查询 tracker。
- **回归测试**: `BUG_Replay_existing_entity_add_then_set_tracks_value_from_add_baseline`（测试文件已随旧 API 删除，不可用）
- **验证**: 新增回归测试先 RED（Actual 0），修复后 GREEN。

### 修复原则

这些修复遵循**不变性原则**：Submit 路径和 Replay 路径必须在操作顺序和语义上一致。分歧（如排序差异、去重差异、释放差异）是 Submit/Replay 不一致的根因。库层不做过度防御（不静默吞非法操作），但语义上等价的操作（Add 已存在 = 写值）应被允许。ChangeQuery 自身的修复遵循**防御性读取原则**：只要组件可能缺失，就必须使用 `TryGetComponentIndex` 而不是 `GetComponentIndexFast`。

---

## M5 热路径防御/成本删除审计（2026-07-09）

### 审计范围

`src/MiniArch/Core/` 全量 core 文件（~9500 LOC），重点扫描：
- Dictionary/HashSet 在每帧热路径中的使用
- `if (condition) throw` 检查在 10000+/帧循环中
- 范围/索引检查的完全冗余

### 基线数据

```
Scenario           |   Rounds/s |   ms/round |   Rounds |  Heap Δ KB |   Memory
Movement           |     1877.6 |      0.533 |    56328 |    -3012.8 |       OK
Attack             |     1133.6 |      0.882 |    34009 |    -2910.2 |       OK
```

### 已检查的候选及裁决

| # | 位置 | 猜想 | 结论 | 验证 |
|---|------|------|------|------|
| M5-1 | `FrameDelta.cs:227-228` `Validate()` 内 `new HashSet<Entity>()` (x2) + line 322 `new HashSet<int>()` | 这些 HashSet 在 Validate 调用时分配，如果在每帧回放前调用则为每帧分配 | ❌ 非热路径。`Validate()` 的文档明确标注"对不受信任的 delta，在 Replay 前调用"。在 lockstep 常态下，本地生成的 delta 跳过 Validate。两个 HashSet 的存在是有意的防御设计，每帧路径不调用 | 代码走读：`Validate()` 注释 + 无从 `ReplayCore` 调用 Validate 的调用图 |
| M5-2 | `World.cs:38-57` `_archetypes` / `_archetypeByMask` / `_archetypeByHash` / `_replayCreateCounts` Dictionary | 每帧 archetype 查找中 Dictionary 查找开销 | ❌ 非热点。`_archetypeByMask`（热路径）是 canonical mask 的 O(1) Dictionary 查找，`_replayCreateCounts` 在 `PreScanForCapacity` 中重用（热稳定后零分配）。`_archetypes` 仅在冷启动或罕见的高 ID 组件路径上访问 | 代码走读 + §3.7（Signature 分配已缓存） |
| M5-3 | `World.cs:84-85` `_queryFiltersByDescription` / `_queries` Dictionary | 每次 query 迭代前从 Dictionary 查 QueryCache 的开销 | ❌ 非热点。这些 Dictionary 仅用于 query 创建/恢复，每帧的 `GetChunkViewSpan`/`GetArchetypeSpan` 直接访问缓存的 `_snapshotChunkViews`/`_snapshotArchetypes` 数组，不涉及 Dictionary 查找 | 代码走读 `QueryCache.cs:103-126` |
| M5-4 | `CommandStreamCore.cs:2942` `FrozenState.HierarchyByChild` Dictionary`<Entity, HierarchyIntent>` | Submit/Snapshot 路径上的 Dictionary 迭代 | ❌ 条件性使用。仅在帧内有 AddChild/RemoveChild 操作时填充。`.Clear()` 在 `SwapOutState` 中每帧重置，无累积。字典大小 = 每帧的分层操作数，典型值为 0-10 | 代码走读：`SwapOutState` line 981 `_frozen.HierarchyByChild.Clear()` |
| M5-5 | `Archetype.Storage.cs:233-275` `AllocateRows()` 分块路径扫描所有 segment 查找可用容量 | 线性扫描 segment 在 archetype 有很多 segment 时浪费 | ❌ 非热点。`AllocateRows` 在实体创建/迁移时调用，而非每帧查询迭代。`_segmentCapacity` 目标为 2MB/segment，因此大多数 archetype 保持 1 段。扫描仅发生在实体分配时（远不频繁于查询） | 代码走读 + `TargetSegmentBytes` 定义（2MB） |
| M5-6 | `World.StructuralChange.cs:34-49` `ApplyTypedSetFast` 中的 `AssertAlive` / `TryGetComponentIndex` | defense 检查在每实体热路径中的开销 | ❌ 已证明零成本（§3.11）。`Get<T>`/`GetRef<T>` 的 defense 检查在 2026-07-06 benchmark 中显示 <2% 噪声。`TryGetComponentIndex` 是功能性的（决定写入列位置），非防御性的 | `kb-design-rationale.md` §3.11 基准 |
| M5-7 | `ComponentStore.cs` `ApplyToWorld` set-only fast path 中每实体 `GetRecordFast` 调用 | `GetRecordFast` 的范围/版本检查开销 | ❌ 已证明零成本（§3.11）。`GetRecordFast` 使用与 `Get<T>` 相同的轻量检查 | `kb-design-rationale.md` §3.11 基准 |
| M5-8 | `EntityFieldResolver.cs:167-192` `ResolveInPlace` fail-fast 检查（seq OOB、resolved.Id < 0） | 在 Replay Add/Set 路径上触发异常的 defense 检查 | ❌ 未触及。硬约束明确禁止移除："DO NOT remove: EntityFieldResolver fail-fast"。这些检查会捕获静默数据损坏 | 任务指令 |
| M5-9 | `World.cs:368-388` `Get<T>` / `GetRef<T>` 中的 `AssertNotDisposed` + `AssertAlive` | defense 检查的开销 | ❌ 未触及。硬约束明确禁止移除 §3.11："DO NOT remove: Get<T>/GetRef<T>/GetRecordFast checks (already proven zero-cost)" | 任务指令 + `kb-design-rationale.md` §3.11 |

### 裁决：跳过——无候选在证据筛选下存活

在检查的 9 个候选中，有 4 个是冷路径/结构变更路径，2 个已被 §3.11 确认为零成本，2 个因硬约束而被排除，1 个是有条件使用但未过度分配。库的热点（`QueryCache` 中的查询迭代、`Archetype.Storage` 中的 chunk span 访问、`ComponentStore.ApplyToWorld` 中的组件写入）已无不必要的 Dictionary/HashSet 操作或冗余 defense 检查。

---

## M6 — YAGNI / Dead Code Deletion Round (2026-07-09)

Removed 15 lines of dead code from `src/MiniArch/` (zero callers confirmed via `deadcode.ps1` + manual rg verification):

| Symbol | File | Type | Lines |
|--------|------|------|-------|
| `Archetype.ContainsComponent` | `Core/Archetype.cs:183` | `internal` method | 3 |
| `FrozenState.AssertWritable` + `_isReadOnly` | `Core/CommandStreamCore.cs:2953-2961` | `internal` method + field under `#if DEBUG` | 12 |

**Retained with explanation**: None — both candidates confirmed dead and deleted.
**Perf gate**: Exempt per AGENTS.md §5a (pure dead code removal, zero IL change impact on runtime paths).
