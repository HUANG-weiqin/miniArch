# 分段存储补充测试计划

当前已有 6 个分段测试（`ArchetypeTests.cs`），覆盖基础增删查和 World 级操作。需要补充以下测试确保分段路径无漏洞。

---

## 1. 跨段删除——被删行不在末段

### `Chunked_mode_cross_segment_remove_preserves_other_entities`

强制分段，填满一个段（多段），删除中间段的实体，验证：
- 删除行被末段最后实体填充
- 末段 Count 减 1
- 其他实体位置和数据不受影响

---

## 2. 多种组件类型 + 跨段删除

### `Chunked_mode_cross_segment_remove_with_multi_component`

用 Position + Velocity + Health 三组件，强制分段后跨段删除，验证三种组件数据都被正确拷贝。

---

## 3. AddEntity 在 EnsureCapacity 触发分段后正确的行号

### `Chunked_mode_add_entity_after_ensure_capacity_converts_to_chunked`

`AddEntity` 里先调 `EnsureCapacity` 再检查 `_isChunked` 是否变为 true。测试：用 capacity=2 的 archetype，插入第三个实体触发 EnsureCapacity → 切分段，验证 AddEntity 返回的 row 正确且数据可读。

---

## 4. 扩容期间 Query 正确刷新

### `Chunked_mode_query_refreshes_after_segment_growth`

创建一个 World 并查询，保持 query 对象。再加实体触发分段扩段，验证同一个 query 对象刷新后能看到新实体。

---

## 5. 分段模式下 ReserveRows 返回正确行号

### `Chunked_mode_reserve_rows_returns_valid_global_row`

分段后调用 ReserveRows(10)，验证返回的行号是全局连续行号，且后续 AddEntity 不冲突。

---

## 6. 分段模式下 GetChunks 返回正确数量的 ChunkView

### `Chunked_mode_get_chunks_returns_one_view_per_segment`

多段情况下，query.GetChunks() 返回的 chunk 数等于段数，每个 chunk 的 Count 对应段内实体数。

---

## 7. 分段模式下读完所有实体后 EntityCount 一致

### `Chunked_mode_query_enumerates_all_entities`

通过 Query 的 chunk 列表累加所有段内实体数，和 world.EntityCount（需要间接验证）一致。

---

## 8. 分段模式下 segment 实体大小的合理性

### `Chunked_mode_segment_capacity_is_byte_based`

用不同大小的组件（如 Position 8B vs LargeComp 128B）构造 Archetype，验证 `SegmentEntityCapacity` 大致符合 `2MB / bytesPerEntity`，而不是固定 65536。

---

## 实施说明

- 所有新测试加在 `tests/MiniArch.Tests/Core/ArchetypeTests.cs` 末尾
- 使用 `archetype.ForceChunkedForTesting()` 和 `archetype.AddSegmentForTesting()` 辅助方法
- 测试组件定义参考现有 `Position` / `Velocity` / `Health`
- 跑完后执行 `dotnet test -c Release` 验证全量通过
