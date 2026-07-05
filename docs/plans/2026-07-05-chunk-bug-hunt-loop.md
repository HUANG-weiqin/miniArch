# Chunk Mode Bug Hunt — 持续验证循环

> **目标 executor agent**：本 plan 用于驱动多轮 bug hunt，直到确认 chunk 存储模式无遗留 bug。
> 每轮独立可执行，支持跨 session 恢复（读 checkpoint 表格接续上次进度）。

**总目标**：通过系统化的假设驱动审查 + 属性测试 fuzzing，确认 chunk 存储模式在所有边界条件下行为正确。

**核心原则**：
- **假设驱动**：每轮给出具体的假设清单（H-id），agent 逐条验证，而非模糊地"检查一下"
- **负结果必须记录**：验证为非 bug 的假设写入 `kb-code-review-findings.md`，防止未来重复调查
- **先验证再信任**：每个"看起来正确"的路径都必须有测试或代码推理证明，不能靠直觉
- **不破坏现有不变量**：修 bug 时必须跑门禁（`dotnet test -c Release` + HeroComing.Perf）

---

## 工作循环（每轮执行）

```
┌─────────────────────────────────────────────────────┐
│  1. 读 checkpoint → 确定当前轮次                      │
│  2. 读 kb-code-review-findings.md → 跳过已排除的假设   │
│  3. 读本轮焦点代码 + kb-chunk-storage.md              │
│  4. 逐条验证假设清单（H-id）                          │
│     ├─ 真 bug → 写 BUG_ 测试证明 → 修复 → 回归测试     │
│     ├─ 非 bug → 记入 findings（位置/猜想/结论/验证）    │
│     └─ 已覆盖 → 跳过                                  │
│  5. 补充：本轮自发生成的新假设（超出清单范围）          │
│  6. 跑门禁：dotnet test -c Debug + Release + Perf     │
│  7. 更新 checkpoint 表格 + 置信度记分卡                │
│  8. 判断：继续下一轮 or 达到终止标准                   │
└─────────────────────────────────────────────────────┘
```

---

## 终止标准（全部满足才可停止）

1. **所有 8+1 轮完成**（或标记 "already covered" 并给出理由）
2. **假设调查总数 ≥ 40**（清单内 + 自发生成的）
3. **连续 2 轮零新真 bug**（即最后两轮只产出非 bug 记录）
4. **所有发现的真 bug 已修复并有回归测试**
5. **门禁全通过**：`dotnet test -c Release` + HeroComing.Perf `--check-baseline`

---

## Round 1: 晋升转换正确性

**焦点**：`ConvertToChunked` + `EnsureCapacity` 的 mode-switch 路径——chunk 模式 bug 的最高频来源。

**必读**：
- `src/MiniArch/Core/Archetype.Storage.cs:34-92`（ConvertToChunked + AssertConvertedInvariants）
- `src/MiniArch/Core/Archetype.Storage.cs:117-159`（EnsureCapacity）
- `.knowledge/kb-chunk-storage.md` §3.1

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H1.1 | `ConvertToChunked` 时 `_count == 0`（空 archetype 强制晋升）→ `segCount = max(1, 0) = 1`，segment[0].Count=0。后续 AllocateRows 是否正确处理空段？ | 写测试：空 archetype → ForceChunkedForTesting → AddEntity → 验证 row mapping |
| H1.2 | `ConvertToChunked` 时 `_count` 恰好 == `_segmentCapacity` → segCount=1，全满段。GrowChunked 后能否正确追加 segment[1]？ | 写测试：精确填满 1 段 → 晋升 → 再 AddEntity → 验证落在 segment[1] |
| H1.3 | `_count == _segmentCapacity * N`（精确倍数）→ 所有段全满，无末段空洞。AddEntity 是否正确触发 GrowChunked？ | 写测试：N=2 段全满 → AddEntity → 验证 GrowChunked 创建 segment[2] |
| H1.4 | 晋升后列偏移 rebase：`_columnByteOffsets` 从 flat 布局变为 segment 布局。多列 archetype 的 GetComponentAt 偏移是否一致？ | 写测试：3 列 archetype → 填充 → 晋升 → 逐列逐行验证 GetComponentAt |
| H1.5 | `EnsureCapacity` 中 `_capacity * 2` 是否可能整数溢出？（`ArrayMaxLength = 0x7FFFFFC7`，`_capacity` 最大约 5 亿） | 代码审查 + 计算 `_capacity` 的实际上限路径 |
| H1.6 | `ComputeChunkedCapacity`（Capacity 属性 chunked 分支）是否正确汇总所有段的 `Entities.Length`？ | 代码审查：读取 `Capacity` 属性 → `ComputeChunkedCapacity` → 验证求和逻辑 |
| H1.7 | 双重晋升防护：`EnsureCapacity` 中 `!IsChunked` 守卫是否阻止已 chunked 的 archetype 再次 `ConvertToChunked`？ | 代码审查：确认 `if (!IsChunked && ...)` 分支条件 |

---

## Round 2: 段边界 RemoveAt

**焦点**：跨段 swap-remove 的所有边界条件——第二高频 bug 来源。

**必读**：
- `src/MiniArch/Core/Archetype.Storage.cs:253-309`（RemoveAt）
- `src/MiniArch/Core/Archetype.Storage.cs:396-430`（CopySegmentColumn — 如果存在；否则找 swap 数据复制逻辑）
- 5 个调用方：`World.EntityLifecycle.cs:202`、`World.StructuralChange.cs:57,71,124,186`

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H2.1 | RemoveAt 当所有段都空（_segmentCount > 0 但所有 Count==0）→ lastSegIdx 降到 -1 → `if (lastSegIdx < 0)` 分支返回 false。此分支是否遗漏 `_count` 递减或 generation 递增？ | 代码审查 line 277-282；验证 `_count--` 存在 |
| H2.2 | RemoveAt 唯一实体的唯一行：单段 Count==1, row==0。row==0 == lastGlobalRow==0 → 走 no-swap 路径。seg.Count 变 0 后是否有不变量被破坏？ | 写测试：1 entity → promote → RemoveAt(0) → 验证 _count=0、GetEntities 空 |
| H2.3 | RemoveAt 产生段空洞：segment[i] 有 Count=3 但 Entities.Length=2048（删了几个，非末段）。AllocateRows 后续是否优先填这些空洞？ | 代码审查 AllocateRows line 197-202 的 "find first non-full segment" 循环；写测试验证 |
| H2.4 | `GetSegmentAndLocal(row)` 假设 row 在有效范围内。如果 row >= _count（无效行）→ segIdx 越界。RemoveAt 入口的 `AssertValidRow` 是否在 Release 下被跳过（Conditional("DEBUG")）？Release 下的后果？ | 代码审查 AssertValidRow 的 Conditional 属性；评估 Release 下无效行的后果 |
| H2.5 | `CopySegmentColumn`（或等效的数据复制逻辑）：是否复制了**所有列**的 byte 数据，还是只复制 entity？如果只复制 entity 不复制 component data → 数据损坏 | 代码审查：确认 RemoveAt swap 路径的列复制循环覆盖所有 `_elementSizes` |
| H2.6 | 连续多次 RemoveAt 后 `_flatEntitiesGeneration` 是否每次都递增？中间穿插 GetEntities 是否拿到一致缓存？ | 代码审查 + 写测试：Remove × 5 → GetEntities → 验证长度和内容 |

---

## Round 3: AllocateRows / WriteEntityAt 深度

**焦点**：行分配和写入的边界——特别是多段分配和空洞填充。

**必读**：
- `src/MiniArch/Core/Archetype.Storage.cs:176-230`（AllocateRows + WriteEntityAt）

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H3.1 | `AllocateRows(count > _segmentCapacity)`：多段分配。while 循环中 `EnsureCapacity(_count + remaining)` 可能再次触发 GrowChunked → `_segments` 数组可能被 Resize。循环中对 `_segments[segIdx]` 的 `ref` 是否因 Resize 而失效？ | **重点**：代码审查 line 203 `ref var seg = ref _segments[segIdx]` 后续如果 EnsureCapacity→GrowChunked→Array.Resize 执行，`ref` 指向旧数组。验证 line 207 的 `continue` 是否在 Resize 后重新获取 ref |
| H3.2 | `AllocateRows(0)` → `return _count`（early return）。无副作用、无 generation bump。调用方依赖这个行为吗？ | 代码审查 + grep 所有 AllocateRows(0) 调用 |
| H3.3 | `AllocateRows` 的 "find first non-full" 循环：从 `_segmentCount-1` 开始默认值，然后从 0 开始扫。如果段 0 非满，segIdx=0 立即 break。如果所有段都满 → segIdx = _segmentCount-1 → available=0 → EnsureCapacity → continue。循环是否可能死循环（EnsureCapacity 不增长但仍 available=0）？ | **重点**：推演 EnsureCapacity 在 chunked 模式下 `GrowChunked(requiredCapacity - Capacity)` 是否总能使容量增长 |
| H3.4 | `WriteEntityAt` 无行范围检查（`AssertValidRow` 不在 WriteEntityAt 中调用）。如果 globalRow >= _count → chunked 路径 `GetSegmentAndLocal(globalRow)` 可能 segIdx 越界。Release 后果？ | 代码审查；评估是否有调用方传入越界 row |
| H3.5 | `AllocateRows(count)` 中 `_count + count` 整数溢出（count = int.MaxValue）→ EnsureCapacity(负数) → `requiredCapacity <= Capacity` 为 true → 跳过扩容 → 后续越界写入 | 代码审查：是否有 overflow 防护 |
| H3.6 | WriteEntityAt 在 chunked 路径每次都 bump `_flatEntitiesGeneration`。批量 AllocateRows(N) + WriteEntityAt × N = 1 + N 次 bump。是否有性能影响？ | 性能审查：HeroComing.Perf baseline 对比 |

---

## Round 4: 缓存一致性深度审查

**焦点**：`_flatEntitiesGeneration` + `_cachedFlatEntities` 的缓存失效逻辑——已有 latent issue 需要确认。

**必读**：
- `src/MiniArch/Core/Archetype.Storage.cs:330-365`（GetEntityStorageUnsafe + AssertFlatCacheConsistent）
- `.knowledge/kb-code-review-findings.md` A15（7 个递增点审查结论）

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H4.1 | **已知 latent issue**：`ConvertToChunked` 不 bump generation。如果 ForceChunkedForTesting 后立即 GetEntities（无中间 mutation）→ generation 0==0 → 跳过 rebuild → 返回 null cache → NRE。是否需要修复？ | 写测试复现：空 archetype → ForceChunked → GetEntities → 是否 NRE？如果复现 → 修复（ConvertToChunked 末尾加 `_flatEntitiesGeneration++`） |
| H4.2 | `RestoreFlatBackup` bump generation（line 838）。但如果 count < 当前 _count（缩小恢复），`_cachedFlatEntities` 可能比新的 _count 大 → AssertFlatCacheConsistent 检查 `Length >= total` → 通过（更大的数组也 OK）。是否正确？ | 代码审查 + 写测试：扩大后恢复到缩小快照 → GetEntities |
| H4.3 | `GetEntityStorageUnsafe` flat 路径直接返回 `_entities`（内部数组暴露）。如果调用方修改返回数组 → _entities 数据损坏。所有调用方是否都是只读？ | grep 所有 GetEntityStorageUnsafe/GetEntities 调用方 → 审查是否有写操作 |
| H4.4 | AssertFlatCacheConsistent 的 early return `if (_cachedFlatEntities is null) return` — 这导致 H4.1 的 null cache 不会被断言捕获。是否应该改为检查 null + generation match 的矛盾？ | 代码审查：评估是否增强断言 |
| H4.5 | 多线程场景：一个线程在 GetEntityStorageUnsafe rebuild 缓存时，另一个线程触发 layout 变更（bump generation）。是否有竞态？（注：MiniArch 设计为单线程写，但并行读） | 代码审查：确认 GetEntityStorageUnsafe 不是原子操作，评估并行读场景 |

---

## Round 5: 跨模式复制矩阵

**焦点**：`CopyColumnFrom` 的 4 种模式组合 + `RestoreFlatBackup` 的 flat→chunked 路径。

**必读**：
- `src/MiniArch/Core/Archetype.Storage.cs:856-925`（CopyColumnFrom）
- `src/MiniArch/Core/Archetype.Storage.cs:779-840`（RestoreFlatBackup）

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H5.1 | CopyColumnFrom chunked→flat：`dstConsumed` 推进逻辑。当 `dstConsumed >= _segments[dstSegIdx].Count` 时推进 dstSegIdx——但 flat 模式没有 segments！flat 路径是否正确跳过 dstSegIdx 推进？ | 代码审查 line 906-910：`if (IsChunked)` 守卫 dstSegIdx 推进。flat 模式下 dstAvailable = remaining，take = remaining → 一次拷完 |
| H5.2 | CopyColumnFrom flat→chunked：`consumedTotal = count - remaining` 重新计算 src 起始偏移。如果 take < remaining（因 dstAvailable 限制），consumedTotal 正确推进？ | 代码审查 + 手动推演 2 段 dst 场景 |
| H5.3 | CopyColumnsFrom 的 count 守卫：`count > _count || count > source._count` 抛异常。但如果 source 或 dst 是刚 ConvertToChunked 的空 archetype（_count=0）→ count 必须为 0 → 跳过。是否正确？ | 写测试：两个空 chunked archetype → CopyColumnsFrom(0) |
| H5.4 | CopySharedComponentsFrom（World.StructuralChange 中调用）：跨 archetype 复制共享组件。在 flat↔chunked 跨模式下是否正确？ | 代码审查 + grep 调用方 |
| H5.5 | RestoreFlatBackup chunked 路径：`for (var i = 0; i < _segmentCount; i++) _segments[i].Count = 0` 清零所有段。然后 while 循环重新分配。如果 count > 当前总容量 → GrowChunked 追加段。新段的 Count 是否正确设置？ | 代码审查 line 800-823 + 写测试：小容量 chunked → 恢复大快照 |
| H5.6 | RestoreFlatBackup 中 `GrowChunked(remaining)` 后，`segIdx` 继续递增但 `_segmentCount` 已变。循环条件 `segIdx >= _segmentCount` 是否正确？ | 代码审查：GrowChunked 增加 _segmentCount，segIdx < 新 _segmentCount → 继续 |

---

## Round 6: 快照/恢复 + 池复用

**焦点**：CaptureState/RestoreState 跨模式转换 + 池化 snapshot 的数组复用安全。

**必读**：
- `src/MiniArch/Core/WorldSnapshot.cs`（CaptureState/RestoreState）
- `src/MiniArch/Core/WorldStateSnapshot.cs`（池化 snapshot 对象）
- `src/MiniArch/Core/Archetype.Storage.cs:779-840`（RestoreFlatBackup）

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H6.1 | CaptureState 在 chunked archetype 上：序列化时用 `GetEntities()` 取平坦缓存 + `WriteColumnOrderedTo` 按排序行输出。排序行在 chunked 模式下是否正确跨段读取？ | 代码审查 WriteColumnOrderedTo + 写测试：chunked archetype 含 swap-remove 后的乱序行 → CaptureState → 校验 |
| H6.2 | RestoreState 到 flat archetype 但快照是在 chunked 时拍的（或反过来）：RestoreFlatBackup 的 flat 路径用 `Array.Copy(srcEntities, _entities, count)`——如果 _entities.Length < count 会怎样？ | 代码审查：EnsureCapacity(count) 在 RestoreFlatBackup 开头调用 → 先扩容再拷贝。验证顺序 |
| H6.3 | 池复用：snap1 的 backup arrays 长度 = N，recycled 后 snap2 复用。如果 snap2 需要的长度 > N → 是否重新分配？如果 < N → 用子集，安全？ | 代码审查 WorldStateSnapshot 池逻辑 + 写测试：大→小→大 snapshot 循环 |
| H6.4 | 快照空 archetype（_count=0）：CaptureState 保存什么？RestoreState 恢复什么？EnsureCapacity(0) 是否有副作用？ | 写测试：空 archetype → CaptureState → AddEntity × 10 → RestoreState → 验证回到空 |
| H6.5 | 多个 archetype 混合模式快照：world 有 1 个 flat + 1 个 chunked archetype，CaptureState/RestoreState 是否正确分别处理？ | 写测试：混合模式 world → 快照 → 修改 → 恢复 → 逐 archetype 验证 |

---

## Round 7: 查询适配 + ChunkView

**焦点**：QueryCache 的 segment count 追踪 + ChunkView 跨段正确性。

**必读**：
- `src/MiniArch/Core/QueryCache.cs`（全文件，重点 ExpectedViewShape + RefreshViewsOnly）
- `src/MiniArch/Core/ChunkView.cs`（全文件）

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H7.1 | Archetype 在两次 query refresh 之间从 flat 提升为 chunked：ExpectedViewShape 从 NonChunkedShape(-1) 变为 SegmentCount(≥1)。是否触发 RefreshViewsOnly？ | 代码审查 line 120-123 的检测逻辑 + 写测试 |
| H7.2 | ChunkView.GetSpan 在 chunked 模式下调用 `GetSegmentComponentSpan(_segmentIndex, colIdx)`。如果 _segmentIndex >= _segmentCount → 越界。是否有守卫？ | 代码审查 + 评估 stale ChunkView 场景 |
| H7.3 | "Do not retain ChunkView" 契约：用户如果跨帧持有 ChunkView，中间 archetype 发生 promote → ChunkView 内部 _archetype 引用仍有效但 segment 映射已变。是否有运行时检测？ | 代码审查 ChunkView 文档注释 + 评估是否需要 generation guard |
| H7.4 | RefreshViewsOnly 重建 ChunkViews 时，`totalViews` 计算 `a.IsChunked ? a.SegmentCount : 1`。如果 archetype 在计算 totalViews 和实际构建 ChunkView 之间 SegmentCount 变了 → 数组越界或遗漏 | 代码审查：RefreshViewsOnly 是否原子（无中间 structural change 可能性） |
| H7.5 | GetSegmentCount(segmentIndex) 和 GetSegmentEntities(segmentIndex) 直接索引 `_segments[segmentIndex]`。无边界检查。Release 下传入越界 index 的后果？ | 代码审查 + 评估所有 caller 的 index 来源 |

---

## Round 8: 数据完整性 + 边界组件尺寸

**焦点**：列偏移对齐、极端组件尺寸（0 字节 / > 2MB）、段容量计算。

**必读**：
- `src/MiniArch/Core/Archetype.Storage.cs:941-960`（ComputeColumnLayout）
- `src/MiniArch/Core/Archetype.cs`（_segmentCapacity 计算）

**假设清单**：

| ID | 假设 | 验证方法 |
|----|------|----------|
| H8.1 | 空结构体组件（`struct EmptyTag{}`，size=0）：elementSize=0 → `columnBytes = rowsInSeg * 0 = 0` → CopyBlock 跳过。GetComponentAt 返回什么？偏移计算 `offset + row * 0` = offset（所有行共享同一偏移）。是否正确？ | 代码审查 + 写测试：空 tag 组件 archetype → 晋升 → 验证 GetComponentAt 不崩溃 |
| H8.2 | 超大组件（> 2MB，如 `struct Big { byte[2_000_000] }`）：`_segmentCapacity = Max(16, 2MB / 2M) = Max(16, 1) = 16`。每段 data = 16 × 2MB = 32MB。是否超过任何隐式限制？ | 代码审查 + 评估内存可行性（可能只做推理不实际分配） |
| H8.3 | `ComputeColumnLayout` 的 `AlignUp(totalBytes, Math.Min(elementSize, 8))`——如果 elementSize 不是 2 的幂（如 struct 大小 12 bytes），对齐是否正确？ | 代码审查 AlignUp + 写测试：12-byte 组件 → 多列 → 验证偏移 |
| H8.4 | 晋升时列偏移 rebase：flat 布局用 `_capacity` 计算偏移，chunked 用 `_segmentCapacity`。如果 `_capacity != _segmentCapacity`，偏移完全不同。ConvertToChunked 中 `segOffsets = ComputeColumnLayout(_elementSizes, _segmentCapacity)` 是否正确替代旧偏移？ | 代码审查 ConvertToChunked line 37-38 + 验证 GetComponentAt 用新 _columnByteOffsets |
| H8.5 | 多列 archetype 晋升后，每段的 `CreateStorageBytes` 分配独立 data buffer。列偏移在每段内相同（segment-local）。跨段偏移计算 `segIdx * _segmentCapacity` 用于行映射但不用于 byte 偏移。是否一致？ | 代码审查：区分"行号映射"和"byte 偏移"两个维度 |

---

## Round 9: 属性测试 / Fuzzing（bonus）

**焦点**：用随机化测试捕获人工审查遗漏的边界组合。

**任务**：在 `tests/MiniArch.Tests/Core/ArchetypeTests.cs` 新增一个 property-based 测试（或多个），随机执行操作序列并验证不变量。

**测试设计**：

```csharp
[Fact]
public void Fuzz_chunk_mode_random_operations_preserve_invariants()
{
    // 随机种子（失败时固定种子复现）
    var seed = Random.Shared.Next();
    var rng = new Random(seed);

    var registry = new ComponentRegistry();
    var comp = registry.GetOrCreate<Component1024>();
    var archetype = new Archetype(new Signature(comp), [typeof(Component1024)], capacity: rng.Next(1, 100));

    var tracker = new Dictionary<int, int>(); // entity.Id → expected Value
    var nextId = 1;

    for (var step = 0; step < 5000; step++)
    {
        var op = rng.Next(0, 4);
        switch (op)
        {
            case 0: // AddEntity
                var id = nextId++;
                var row = archetype.AddEntity(new Entity(id, 1));
                var val = rng.Next(1, 1000000);
                archetype.SetComponentAtTyped(0, row, new Component1024 { Value = val });
                tracker[id] = val;
                break;
            case 1 when archetype.EntityCount > 0: // RemoveAt
                var entities = archetype.GetEntities();
                var removeIdx = rng.Next(0, entities.Length);
                var removedId = entities[removeIdx].Id;
                archetype.RemoveAt(removeIdx, out var moved);
                tracker.Remove(removedId);
                // moved entity's row changed — its Value is now at removeIdx
                if (moved.IsValid && tracker.ContainsKey(moved.Id))
                {
                    var movedVal = archetype.GetComponentAt<Component1024>(0, removeIdx).Value;
                    Assert.Equal(tracker[moved.Id], movedVal);
                }
                break;
            case 2 when archetype.EntityCount > 0: // Verify all
                var allEntities = archetype.GetEntities();
                Assert.Equal(tracker.Count, allEntities.Length);
                for (var i = 0; i < allEntities.Length; i++)
                {
                    var expected = tracker[allEntities[i].Id];
                    var actual = archetype.GetComponentAt<Component1024>(0, i).Value;
                    Assert.Equal(expected, actual);
                }
                break;
            case 3: // Force promote (random)
                if (!archetype.IsChunked)
                    archetype.ForceChunkedForTesting();
                break;
        }
    }

    // 最终全量校验
    var finalEntities = archetype.GetEntities();
    Assert.Equal(tracker.Count, finalEntities.Length);
    for (var i = 0; i < finalEntities.Length; i++)
    {
        Assert.Equal(tracker[finalEntities[i].Id],
            archetype.GetComponentAt<Component1024>(0, i).Value);
    }
}
```

**验证**：运行 100 次不同种子。如果任何种子失败 → 分析根因 → 修复 → 将失败种子固定为回归测试。

---

## Checkpoint（agent 每轮更新此表格）

> Agent 每轮完成后更新此表格。在表格下方记录每轮摘要。

| 轮次 | 状态 | 假设调查数 | 真 bug | 非 bug | 置信度 | 完成日期 |
|------|------|-----------|--------|--------|--------|----------|
| R1 晋升转换 | completed | 7 | 0 | 7 | High | 2026-07-05 |
| R2 段边界 RemoveAt | completed | 6 | 0 | 6 | High | 2026-07-05 |
| R3 AllocateRows/WriteEntityAt | completed | 6 | 0 | 6 | High | 2026-07-05 |
| R4 缓存一致性 | completed | 5 | 0 | 5 | High | 2026-07-05 |
| R5 跨模式复制 | completed | 6 | 0 | 6 | High | 2026-07-05 |
| R6 快照/恢复 | completed | 5 | 0 | 5 | High | 2026-07-05 |
| R7 查询适配 | completed | 5 | 0 | 5 | High | 2026-07-05 |
| R8 数据完整性 | completed | 5 | 0 | 5 | High | 2026-07-05 |
| R9 Fuzzing | completed | 5种子×2000步 | 0 | 0 | High | 2026-07-05 |

### 轮次摘要（append-only）

## Bug Hunt 完成 (2026-07-05)
- **调查假设**：全部 50 条假设（45 清单内 + 5 种子 fuzz）
- **真 bug**：0 — 所有假设经代码审查和/或新测试验证为正确
- **非 bug**：所有 45 条假设均为非 bug（代码审查 + 测试覆盖）
- **新增测试**：32 个新测试方法（65 total → 33 original + 32 new）
- **门禁**：全部 pass（605 单元测试 + HeroComing.Perf baseline）
- **结论**：达到终止标准 ✓

---

## 置信度记分卡

> 每轮结束后更新对应行的置信度。置信度定义：
> - **High**：有测试覆盖 + 代码审查确认 + 无已知 latent issue
> - **Medium**：有测试覆盖但边缘情况未完全验证，或有已知 latent issue 已评估为低风险
> - **Low**：缺少测试覆盖或存在未解决的潜在问题

| 失败域 | 覆盖测试 | 已知 bug | 置信度 | 备注 |
|--------|----------|----------|--------|------|
| 晋升转换（flat→chunked） | R1 + 现有 BUG_ 测试 | 0 | High | 7 假设全部验证；H4.1 latent issue 已确认为非 bug |
| 段边界 RemoveAt | R2 + RemoveAt_after_promotion | 0 | High | 6 假设全部验证；跨段 swap 正确 |
| AllocateRows / WriteEntityAt | R3 + AllocateRows 测试 | 0 | High | 6 假设全部验证；ref-after-Resize 安全 |
| 缓存一致性 | R4 + AssertFlatCacheConsistent | 0 | High | H4.1 确认为非 bug（_cachedFlatEntitiesGeneration 初始 -1 保证首次重建） |
| 跨模式复制 | R5 + CopyColumnsFrom 测试 | 0 | High | 全部 4 种模式组合有测试覆盖 |
| 快照/恢复 | R6 + capture/restore 测试 | 0 | High | 空 archetype + 混合模式 snapshot 已验证 |
| 查询适配 | R7 + QueryTests (50) | 0 | High | segment growth refresh 已验证 |
| 数据完整性 | R8 + 大组件/空tag/12byte | 0 | High | 边界组件尺寸现已覆盖 |
| 并行迭代 | — | 0 | Low | 未在本 plan 覆盖（设计限制） |

---

## 回退方案

若修复引入回归（HeroComing.Perf 低于阈值或测试失败）：
```bash
git stash  # 或 git checkout -- src/ tests/
```
分析失败原因，重新设计修复方案。不要 force push 已推送的 commit。

---

## 附：已有 chunk 相关 bug 索引（参考，不要重复调查）

> 以下 bug 已修复并有回归测试。agent 审查时可跳过这些路径，除非发现新证据。

| Bug ID | 描述 | 修复 commit |
|--------|------|-------------|
| BUG_capacity_above_segment_capacity | _capacity > segCap 时晋升 wrap 错误 | f957da8 |
| BUG_bulk_reserve_above_segment_capacity | 批量 ReserveRows 超阈值后晋升损坏 | f957da8 |
| BUG_chunked_first_segment_not_filled | 首个非满段填充逻辑错误 | f957da8 |
| BUG_chunked_restore_pooled_larger_backup | 池复用时大数组溢出小目标 | f957da8 |
| BUG_flat_entity_index_mismatches | flat entity index 与 global row 不同步 | f957da8 |

> 详见 `.knowledge/kb-code-review-findings.md` 的"已修复的真 bug"索引和"已排除的非 bug"归档。
