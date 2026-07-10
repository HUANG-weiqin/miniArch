# DestroyMany / Destroy(QueryDescription) 设计

> 对应需求：新增 `DestroyMany(ReadOnlySpan<Entity>)` 做批量销毁优化，和 `Destroy(QueryDescription)` 实现极速查询销毁。

## 目标

1. **尽量减少 archetype 结构变更次数**：把 N 次独立的 swap-remove 合并为单次一致性 pass。
2. **不丢失级联语义**：`Destroy(Entity)` 的 subtree 销毁必须保持，不能产生半孤子。
3. **零心智负担**：与现有 `Destroy(Entity)` 语义一致——已死实体静默跳过，有孩子的自动级联。

## API

```csharp
public sealed partial class World
{
    /// <summary>
    /// 批量销毁。静默跳过已死实体，有孩子的实体触发级联销毁（subtree），
    /// 无孩子的实体按 archetype 分组批量移除。
    /// </summary>
    public void DestroyMany(ReadOnlySpan<Entity> entities);

    /// <summary>
    /// 销毁所有匹配 query 描述的实体。语义同 DestroyMany。
    /// </summary>
    public void Destroy(in QueryDescription description);
}
```

## 实现算法

两个 API 走同一套内部方法 `DestroyEntitiesCore`。

### Phase 1 — 有孩子：级联

```
deadSet = 用于去重的容器

for each entity in input:
  if !world.IsAlive(entity): continue        // 静默跳过
  if deadSet.Contains(entity): continue      // 已从 cascade 杀死

  if _hierarchy.HasChildren(this, entity):
    Destroy(entity)                           // 现有 Destroy，级联杀死 subtree
    // Destroy 内部会把 subtree 全部推进 _destroyOrderScratch
    // 但对我们来说，这些被杀死的实体也要记到 deadSet
    for each killed in _destroyOrderScratch:
      deadSet.Add(killed)
  else:
    childless.Add(entity)
```

### Phase 2 — 无孩子：按 archetype 批量移除

```
group childless by archetype

for each (arch, entitiesInArch) in groups:
  // 收集要删除的行
  rowsToRemove = sorted(list of entity.RowIndex for each entity)
  
  // 情况 A：该 archetype 所有实体都被删 → ResetCount() 最快
  if rowsToRemove.Length == arch.EntityCount:
    entities = arch.GetEntities()                     // 拿全部实体做 record 清理
    ProcessDeadEntities(arch, entities, deadSet)     // version bump + free-list + hierarchy
    arch.ResetCount()                                 // O(1)，不清数据只归零
    continue

  // 情况 B：部分删除 → 降序 swap-remove
  rowsToRemove.SortDescending()
  for each row in rowsToRemove:
    entity = arch.GetEntity(row)
    arch.RemoveAt(row, out movedEntity)
    if movedEntity.IsValid:
      ref movedRecord = ref _records[movedEntity.Id]
      movedRecord.Archetype = arch
      movedRecord.RowIndex = row
    // 销毁 entity
    KillEntityRecord(entity)
    deadSet.Add(entity)
```

`KillEntityRecord` 封装的原子操作：

```csharp
void KillEntityRecord(Entity entity)
{
    var nextVersion = NextEntityVersion(entity);
    if (_hierarchy.HasAnyRelations(entity))
        _hierarchy.RemoveDestroyed(entity);
    ref var record = ref _records[entity.Id];
    record = default;
    record.Version = nextVersion;
    PushFreeIdUnsafe(entity.Id, nextVersion);
}
```

### `Destroy(query)` 的特殊路径

query 路径只需要把 matched archetypes 扫一遍产出 entity 列表，然后走同样的两阶段算法。扫法就是利用 `_queryCache.GetArchetypeSpan()` 遍历所有匹配 archetype 的 entity 找到有孩子的，其余同 `DestroyMany`。

不引入 `IChunkForEach` 或其它公共 API。

## 去重容器选择

Phase 1 的 cascade 会产生"父在输入集中、子也被 cascade 杀死"的情况。子可能在 Phase 2 又出现（输入集包含子），需要去重。

三种选择：

| 容器 | 代价 | 选否 |
|---|---|---|
| `HashSet<Entity>` | 每 entity 一次 hash + L2 cache miss | ❌ 游戏帧可能万级实体 |
| `BitArray` | 按 `entity.Id` 位索引，O(1) 无 hash | ✅ |
| `SortedSet<int>` + 二分 | O(log N) | ❌ |

选 **`BitArray`**，复用 `_destroyVisitedGen` 模式——用 gen 计数器避免每次清空整个 array：

```csharp
// World 已有 _destroyVisitedGen (int[]) + _destroyCurrentGen (int)

private void MarkDead(int id) { _destroyVisitedGen[id] = _destroyCurrentGen; }
private bool IsDead(int id) => (uint)id < (uint)_destroyVisitedGen.Length 
    && _destroyVisitedGen[id] == _destroyCurrentGen;
```

这样去重是 O(1) 且零分配（已有数组）。gen 溢出时退化到 `Array.Clear` 重置——与现有 `Destroy` 完全一样。

## 缓存友好性

- Phase 1 遍历输入集：如果是 `DestroyMany(Span<Entity>)`，传入的是连续的 `Entity[]` 切片，按 `entity.Id` 顺序访问 `_records[]`（dense array）和 `_hierarchy._firstChild[]`（同长度），cache miss 少。
- Phase 2 分组：先按 archetype 分类，同一 archetype 的 entity 在同一 chunk 中连续——`RemoveAt` 遍历组件列时一直是按行顺序的 L1/L2 命中。
- **情况 A（整 archetype 清零）**：完全不遍历组件数据，只遍历 entity 列表做 record 清理，缓存极优。

## 测试

1. `DestroyMany_empty_entity_list` — 空输入不崩溃
2. `DestroyMany_single_entity` — 等价于 `Destroy`
3. `DestroyMany_skips_dead_entities` — 已死实体静默跳过
4. `DestroyMany_cascades_subtree` — 父在输入，子被级联
5. `DestroyMany_dedup_cascaded_children` — 父子都在输入，不重复杀子
6. `DestroyMany_multiple_archetypes` — 输入实体跨 multiple archetype
7. `DestroyMany_full_archetype_clear` — 同一 archetype 全部杀光走 ResetCount 路径
8. `DestroyMany_partial_archetype_clear` — 同一 archetype 杀部分走 swap-remove 路径
9. `Destroy_query_all` — `Destroy(new QueryDescription().With<Position>())` 清空所有 Position 实体
10. `Destroy_query_cascade` — query 命中含孩子的实体，级联生效
11. `Destroy_query_no_hierarchy` — 纯组件实体，走 ResetCount 最快速路径
12. `Destroy_query_some_already_dead` — 部分实体已死，静默跳过
13. `DestroyMany_entity_count_correct` — `world.EntityCount` 验证
14. `DestroyMany_recycles_ids` — 被销毁实体的 id 可被后续 Create 复用
15. `DestroyMany_destroy_then_create_same_frame` — 同帧 destroy + create，version 正确

## 文件变更

| 文件 | 变更 |
|---|---|
| `src/MiniArch/Core/World.EntityLifecycle.cs` | 新增 `DestroyMany`、`Destroy(in QueryDescription)`、`DeadSet` 内部 helper |
| `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs` | 新增所有测试 |

无需改动 `Archetype.Storage.cs` 现有 `RemoveAt`，Phase 2 直接复用 `RemoveAt`。无需新增 `IChunkForEach` 或 QueryPath API。

## 基准预期

`dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`

预期：Movement/Attack 不受影响（Hero pipeline 不使用 destroy bulk API）。后续可用 `CommandStream.Profile` 加 destroy-heavy workload 验证加速比。

## 不做的

- **不做 `Destroy(Query).Exact()` 特化**：Exact 模式只是匹配更少的 archetype，算法不区别对待。
- **不做 per-entity callback**：`Destroy(Entity)` 没有 callback，批量版本也不加。
- **不做异步变体**：批量 destroy 是同步且快的。
