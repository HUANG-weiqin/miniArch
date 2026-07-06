---
title: 代码审阅发现
module: Meta
description: 健壮性审阅发现汇总——已确认的设计债、已验证的安全猜想、已排除的非 bug 猜想
updated: 2026-07-06 (B5: EnsureReplayReservation swap-remove 导致 FreeList 顺序分歧; B6: CancelPendingEntity push order 与 Release 顺序不同)
---
# 代码审阅发现

> 审阅前必读。包含已确认的设计债、已验证安全猜想、以及被排除的非 bug 猜想。

## 审阅历史

| 日期 | 类型 | 审阅者 | 范围 |
|------|------|--------|------|
| 2026-07-06 | 鲁棒性审阅 | Boss Agent (GPT-5.5) | 全量核心模块 (11 modules, ~8000 LOC) |

---

## 设计债 (P2)

### #1: CommandStream.Submit() 无事务回滚 (R11)

- **模式**: R11 部分修改
- **位置**: `CommandStreamCore.cs:334-359` Submit()
- **缺失的安全条件**: `MaterializeAllPending` 成功但后续 `ApplyHierarchy`/`ApplyComponentStores`/`ApplyDestroys` 失败时，已 materialize 的实体无法回滚
- **真实风险**: 低。该异常仅在 CommandStream 内部数据不一致时触发，正常用户路径不可达
- **建议修复**: 在 materialize 前预验证所有 slot 为 reserved 状态（defense-in-depth）

### #2: EntityFieldResolver 静默跳过未决占位符 (R8)

- **模式**: R8 静默截断
- **位置**: `EntityFieldResolver.cs:174-189` ResolveInPlace
- **缺失的安全条件**: 当 `seq >= resolveMap.Length` 或 `resolved.Id < 0` 时，不返回错误也不记录日志，死占位符 Entity(-1, seq) 残留于组件数据
- **真实风险**: 低-中。需要恶意 FrameDelta（占位符引用无对应 Reserve）。正常 lockstep 场景不可达
- **建议修复**: `ResolveInPlace` 返回 `int unresolvedCount`，调用方非零时抛异常；或在 `FrameDelta.Validate` 中校验占位符引用的完整性

### #3: ReplayCore 无事务语义 (R11)

- **模式**: R11 部分修改
- **位置**: `World.EntityLifecycle.cs` ReplayCore
- **缺失的安全条件**: Delta 回放中途失败时无法回滚已执行操作
- **真实风险**: 低。用户应通过 `FrameDelta.Validate()` 预校验——这已在文档中。但 Validate 当前不校验 component data 大小与 schema 一致性（潜在绕过路径）
- **建议修复**: 强化文档约束；`FrameDelta.Validate` 增加 component data 大小交叉校验

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
- **验证**: 浸泡测试 20000 帧 PASS + 测试 `Add_component_that_already_exists_overwrites_value`

### B2: `Clear` 不释放已取消 batch 的预留实体

- **位置**: `CommandStreamCore.cs:1515` `Clear`
- **症状**: FreeList 分裂，总数正确但 free slots + occupied slots ≠ 总 slot 数
- **根因**: `Clear(releaseReserved: false)` 跳过已取消 batch 的实体（未调用 `ReleaseReservedEntity`），但 ReplayCore 始终释放 → 两路径 FreeList 分歧
- **触发场景**: `Create` → `Destroy`（取消 batch）后实体 slot 永久残留为"reserved"状态
- **修复**: 取消的 batch 无论 `releaseReserved` 标志都释放
- **验证**: 浸泡测试 PASS + 全部 673 现存测试通过

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
- **验证**: 浸泡测试 20000 帧 PASS + 测试 `Add_component_that_already_exists_overwrites_value`

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
- **回归测试**：`Submit_and_Replay_free_list_diverges_with_multi_cancel`（目前 FAIL，修复后应通过）
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
- **验证**: 浸泡测试 200000 帧 PASS + `HeroComing.Perf` 性能门禁通过 + 全部 695 个单元测试通过。

### 修复原则

这些修复遵循**不变性原则**：Submit 路径和 Replay 路径必须在操作顺序和语义上一致。分歧（如排序差异、去重差异、释放差异）是 Submit/Replay 不一致的根因。库层不做过度防御（不静默吞非法操作），但语义上等价的操作（Add 已存在 = 写值）应被允许。
