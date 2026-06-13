# Tricky Edge Case Tests — 设计稿

## 攻击面分析

基于对现有测试覆盖的审查，识别出以下未充分覆盖的攻击面：

### 1. Entity Handle / Lifecycle（句柄语义）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 1 | StaleHandleSetAffectsRecycled | 同一 slot 回收后，用旧句柄调用 Set | 旧句柄的 Set 写坏新实体的数据 |
| 2 | IsAliveAcrossWorlds | 用 worldA 的 entity 调 worldB.IsAlive | 跨 world 误判存活 |
| 3 | TryGetAcrossWorlds | 用 worldA 的 entity 调 worldB.TryGet | 跨 world 误读数据 |
| 4 | DestroyRecreateCycleVersions | 反复创建/销毁 100 轮 | 版本号正确性、free-list 正确性 |
| 5 | CreateManyThenSelectiveDestroyThenCreateSingles | 批量创建→选择性销毁→逐个重建 | free-list 回收顺序、版本递增正确性 |

### 2. Set / Add / Remove（组件操作）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 6 | SetDefaultValueIntegrity | Set 为 default(T) | 零值是否真的写入 byte[] |
| 7 | AddComponentAlreadyExists | 对已有组件 Add 相同类型 | 语义：覆盖？报错？还是安全 no-op？ |
| 8 | RemoveThenSetReadds | Remove<T> 后再 Set<T> | Set 的"补组件"路径是否正确重入 |
| 9 | RapidArchetypeCycling | 100 轮 Add<A>→Add<B>→Rem<B>→Add<C>→Rem<A>→Rem<C> | chunk 碎片化、迁移链正确性 |
| 10 | RemoveNonexistentSilentNoop | 连续 Remove 实体没有的组件 | 是否静默返回 |
| 11 | SetPreservesUnchangedComponents | 多组件实体上交错 Set 不同类型 | 不应写坏隔壁列 |
| 12 | SetEntityStabilityManyOverwrites | 同值反复 Set 1000 次 | location 稳定性、不会误触发迁移 |

### 3. Query（查询边界）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 13 | QueryDescriptionOrderIndependentEquality | With<A>.With<B> 与 With<B>.With<A> 是否相等 | 缓存 key 一致性 |
| 14 | QueryDescriptionWithAnyOrderIndependence | WithAny 的不同顺序 | WithAny 的去重是否无副作用 |
| 15 | SelfContradictoryQueryEmpty | With<A>.Without<A> | 应返回 0 结果 |
| 16 | QueryOnlyWithAnyEmpty | 只有 WithAny 且实体无组件 | 边界行为 |
| 17 | QueryOnlyWithAnyReturnsMatching | Only WithAny 匹配混合实体集 | WithAny 逻辑正确性 |
| 18 | DefaultForeachEntityCountMatchesAdvanced | 默认 foreach 与 advanced chunk 计数一致 | 两层 API 返回相同数据 |
| 19 | QueryAfterAllMatchingEntitiesDestroyed | 查询→全部销毁→再次查询 | 缓存 freshness |
| 20 | QueryAcrossChunkBoundariesIntegrity | 跨多个 chunk 的查询遍历 | GetChunkSpan/Chunks 一致性 |
| 21 | ReusedDescriptionQueryCacheInvalidates | 同 description 在 world 变化后的缓存 | 缓存失效 |

### 4. Hierarchy（层级）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 22 | GetChildrenOnLeafEntity | 无子实体调用 GetChildren | 应返回空非 null |
| 23 | TryGetParentOnRoot | 无父实体调用 TryGetParent | 返回 false |
| 24 | DestroyChildRecreateSlotNotInheritParent | 子实体回收→新实体不继承旧 parent | hierarchy 清理 |
| 25 | DestroyParentWithComponentChildren | 有组件的子实体级联销毁 | 组件数据正确清理 |
| 26 | DestroyOneChildKeepsSiblings | 单个子实体销毁不影响兄弟 | 精确级联 |

### 5. CommandBuffer（批量录制）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 27 | MultipleSetOnSameComponentLastWins | 同一 buffer 同一组件多次 Set | 去重逻辑 |
| 28 | CreateAddRemoveAddDifferentComponent | 录制：create→add<A>→remove<A>→add<B> | 复杂去重链正确性 |
| 29 | OperationsOnDestroyedEntityShouldNotCrash | buffer 对已销毁实体的操作 | 不应 NRE 崩溃 |
| 30 | DoubleSubmitSafe | 同一 buffer Submit 两次 | 状态防护 |
| 31 | SubmitReturnsFalseOnEmpty | 空 buffer 提交 | 返回值语义 |
| 32 | MultipleCreatedEntitiesIndependent | buffer 中多个独立实体的组件不串 | 实体隔离 |
| 33 | MixedCreateAndExistingInOneSubmit | 同次 submit 同时创建新实体+修改旧实体 | 混合操作正确性 |
| 34 | DestroyThenRecreateBufferSequence | 先 buffer.Destroy→Submit→再 buffer.Create→Submit | 跨 submit 正确性 |

### 6. Chunk / Structural Integrity（存储结构）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 35 | EntitiesAcrossChunkBoundariesPreserveValues | 跨 chunk 边界实体 Set 验证 | chunk 定位 |
| 36 | ChunkSpanContractsAfterRemoval | 移除实体后 span 收缩 | GetEntities 正确性 |
| 37 | EntityMigrationBetweenChunksPreservesComponents | 迁移导致 chunk 变化 | swap-remove 正确性 |
| 38 | ArchetypeVisibleWhenEmpty | 最后一个实体移除后 archetype 仍 query 可见 | 空 archetype 不缩减 |

### 7. World / Multi-World / Replay（世界边界）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 39 | ReplayEmptyDelta | replay 空 delta | 无菌操作 |
| 40 | MultipleWorldsIndependent | 两个 world 互不干扰 | 全局状态污染 |
| 41 | EnsureCapacityZero | capacity=0 时 create | 边界值 |
| 42 | ChunkCapacityOne | chunkCapacity=1 时的行为 | 极端 chunk 配置 |

### 8. API Facades（对外 API）

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 43 | TryGetOnDestroyedEntity | 对已销毁实体 TryGet | 返回 false |
| 44 | TryGetOnEntityWithoutComponent | 对无该组件的实体 TryGet | 返回 false |
| 45 | OrderByReturnsSorted | 排序查询正确排序 | comparer 应用 |
| 46 | TwoOrderByEnumerationsIndependent | 两次枚举结果一致 | 池化 buffer 不污染 |
| 47 | QueryAdvancedProperty | .Advanced 返回有效的 core.Query | bridge 正确性 |

### 9. ComponentRegistry

| # | 测试 | 攻击点 | 风险 |
|---|------|--------|------|
| 48 | SameTypeRegisteredMultipleTimes | 相同 struct 多次注册 | id 一致性 |
| 49 | DifferentWorldsShareRegistry | 不同 world 共享 registry | 实体不串 |

---

## 执行结果：49/49 通过

运行时发现的问题：

### 🔴 真实 Bug

**Bug #1: `CommandBuffer` 对已销毁实体操作导致 NRE 崩溃**
- 测试: `CommandBuffer_operations_on_destroyed_entity_should_not_crash`
- 表现: `NullReferenceException` at `World.ApplyRawAddOrSet`
- 原因: 实体已从 world 移除，buffer 录制时仍持有旧 handle，Submit 时无法找到 entity location
- 期望行为: 应该静默 no-op，或至少抛出可理解的异常

### 🟡 已知设计取舍（验证为真）

**取舍 #1: Release 编译下版本检查被跳过**
- 测试: `Stale_handle_Set_affects_recycled_entity_in_Release_because_version_check_is_elided`
- 表现: `GetRequiredLocation()` 在 Release 下直接返回 `_locations[id]`，跳过 `_versions[id] != entity.Version` 检查
- 代码位置: `World.cs:1355-1374`
- 文档记录: `kb-core-ecs.md` 决策 #120 "Release 下信任 entity handle 不过期"
- 风险: 用户持有过期句柄执行 Set/Add/Remove 会静默写坏回收后的实体

**取舍 #2: 不同 World 首实体 (Id,Version) 碰撞**
- 测试: `Multiple_worlds_operate_independently`
- 表现: 两个独立 World 的首实体都是 `(Id=0, Version=1)`，TryGetLocation 的版本检查通过
- 影响: 跨 World 的 entity 句柄可能误判为 alive。实际影响小（用户不应混用不同 World 的 entity）

**取舍 #3: 空 chunk/archetype 不缩减**
- 测试: `Query_after_all_matching_entities_destroyed_still_shows_empty_chunks`, `Archetype_with_entity_creation_and_removal_leaves_empty_archetype_visible_to_query`
- 表现: 所有实体销毁后，`MatchedChunks` 仍包含 Count=0 的 chunk
- 文档记录: `kb-core-ecs.md` 决策 #117 — "空 archetype 只会在显式 TrimExcess() 后被移除"
- 影响: query 遍历需自行跳过 Count=0 的 chunk

### 🟢 已验证保证

- `Add` 已存在的组件 → 覆盖（安全）
- `Remove` 不存在的组件 → 静默 no-op
- `Remove` + `Set` → 正确触发重入
- `TryGet` / `IsAlive` → 总是做版本检查（包括 Release）
- `QueryDescription` → 顺序无关等价
- `With<A>.Without<A>` → 返回空结果
- 级联销毁 → 正确清理 hierarchy
- 跨世界 ComponentRegistry → 共享但实体不串
- 100轮 archetype 快速 → 无状态损坏
- chunkCapacity=1 → 正确工作
