---
title: Hardening Roadmap
module: Meta
description: 系统性的健壮性加固路线图——从 int 溢出、退化性能、内存安全到确定性保障，按里程碑组织
updated: 2026-07-12
---

# Hardening Roadmap

## 这个文档是干什么的

- 系统化记录 miniArch 库的**攻击面分析结果和加固计划**
- 每个里程碑（M1, M2, …）对应一类薄弱点，按「实际风险 × 触发概率」排序
- **不涉及新功能**——只做防御性加固
- 加固原则：**零热路径退化**，所有兜底走冷路径
- **审阅**：2026-07-12 由 reviewer agent 完成 M1 设计审阅。结论：「思路正确，有 2 个严重缺陷需修复（M1.2 空签名除零 + 非 2 的幂段容量），修复后可实施」。详见各条目中的「审阅发现」。

## 里程碑总览

| 代号 | 主题 | 优先级 | 影响面 |
|------|------|--------|--------|
| **M1** | **int 溢出防线** | P0 | 存储层、CommandStream、序列化 |
| **M2** | **退化性能保护** | P1 | Destroy/Allocate/O(n²) 场景 |
| **M3** | **栈安全** | P1 | stackalloc、递归深度 |
| **M4** | **内存安全显式化** | P2 | Unsafe.Add 边界断言、segIdx 检查 |
| **M5** | **确定性与可观测性** | P2 | 溢出抛异常 vs 静默损坏、错误信息质量 |

---

## M1 — Int 溢出防线

**目标**：在 .NET 单数组上限（`ArrayMaxLength ≈ 2.14 GB`）前，所有 `int` 运算不静默溢出。

### M1.1 `ComputeColumnLayout` long 化

- **位置**：`Archetype.Storage.cs:1224`
- **问题**：`totalBytes += elementSize * capacity` 是 `int * int`，超过 ~2.14 GB 时静默溢出 → 传给 `GC.AllocateArray` 抛 `ArgumentOutOfRangeException`
- **改法**：
  - `totalBytes` 改为 `long`，乘法提升为 `(long)elementSize * capacity`
  - 结果 > `ArrayMaxLength` 时抛明确异常（"Component storage exceeds .NET array limit"）
  - 新增 `AlignUp64(long, int)`
- **热路径**：❌ 只冷路径（构造/扩容）
- **参见**：`kb-hardening-m1-design.md`（方案细节）

### M1.2 `ComputeSegmentEntityCapacity` 下限降为 1 + 上限钳位（含 M1.7 合并）

- **位置**：`Archetype.cs:136`
- **问题**：
  - `Math.Max(16, ...)` 硬下限 16；当 `perEntity > 134 MB` 时 `16 * perEntity > ArrayMaxLength`
  - `ArrayMaxLength / perEntity` 可能产生非 2 的幂值（如 14），破坏 `GetSegmentAndLocal` 的 `SHR+AND` 映射
  - 当 `perEntity = 0`（空签名 archetype）时 `ArrayMaxLength / perEntity` 除零
- **改法（审阅修正版）**：

```csharp
private static int ComputeSegmentEntityCapacity(Type[] componentTypes)
{
    var perEntity = ComputeAlignedPerEntitySize(componentTypes);
    if (perEntity > ArrayMaxLength)
        ThrowEntityTooLarge(...);

    if (perEntity > 0)
    {
        var raw = Math.Max(1, TargetSegmentBytes / perEntity);
        var pow2Cap = BitOperations.RoundUpToPowerOf2((uint)raw);

        // 必须保持 2 的幂，否则 GetSegmentAndLocal 的 SHR+AND 映射错误
        // 取不超过 ArrayMaxLength 的最大 2 的幂作为上限
        const int MaxSegCap = 1 << 30; // 1,073,741,824

        var maxSegCapFromArray = ArrayMaxLength / perEntity;      // 数组上限
        var safeSegCapFromArray = (int)BitOperations.RoundUpToPowerOf2((uint)maxSegCapFromArray);
        // RoundUpToPowerOf2 可能超过 ArrayMaxLength → 钳位到 MaxSegCap
        if (safeSegCapFromArray > MaxSegCap) safeSegCapFromArray = MaxSegCap;

        var clampedByArray = Math.Min((int)pow2Cap, safeSegCapFromArray);
        return Math.Min(clampedByArray, MaxSegCap);
    }

    return 65536; // 空签名 archetype：无组件，大段容量
}
```

- **关键不变式**：`_segmentCapacity` 始终是 2 的幂，保证 `GetSegmentAndLocal` 用 `SHR+AND` 正确映射
- **验证**：`_segmentCapacity = 1` 时，`_segmentBitShift = TrailingZeroCount(1) = 0`, `_segmentMask = 0` → `GetSegmentAndLocal(row) = (row >> 0, row & 0) = (row, 0)`。每个 segment 恰好 1 entity，全局行号 = 段号。已验证通过 `WriteEntityAt`、`RemoveAt`、`AllocateRows`、`CopyColumnFrom`、`CompactRemoveRowsChunked` 的所有路径。
- **审阅发现**：原设计 `Math.Min(pow2Cap, ArrayMaxLength / perEntity)` 有**2 个严重缺陷**：
  1. `perEntity = 0` 时除零崩溃（空签名 archetype）
  2. `ArrayMaxLength / perEntity` 可能不是 2 的幂（如 14），`GetSegmentAndLocal(13) = (6, 13)` 映射到不存在的段 6 → **行映射损坏**
- **审阅者建议**：合并 M1.7（`(int)RoundUpToPowerOf2` 截断防护）到此处，在 `(int)` 转换前就完成所有钳位

### M1.3 Archetype 构造函数初始容量钳位

- **位置**：`Archetype.cs:56`
- **问题**：`_capacity = capacity (= _chunkCapacity = 128)` → 平坦 `byte[] = 128 * perEntity` 可能溢出 `int`
- **改法**：
  - 构造时计算 `maxFlatCapacity = Math.Max(1, ArrayMaxLength / perEntity)`
  - `capacity = Math.Min(capacity, maxFlatCapacity)`
  - 被钳位后第一次 `EnsureCapacity` 触发 `_capacity * 2 > _segmentCapacity` → 立即晋升 chunked
- **热路径**：❌ 构造时

### M1.4 `N * elemSize` 用 `checked` 保护

- **问题**：多处 `N * elemSize` 用作 `CopyBlockUnaligned` 的列拷贝长度或 `AsSpan` 的长度。溢出后可能绕过现有的 `<= 0` 检测（漫到正小值），造成越界读写或数据损坏。
- **目标位置**：
  | 位置 | 表达式 | 上下文 |
  |------|--------|--------|
  | `Archetype.Storage.cs:77` | `rowsInSeg * elemSize` | `ConvertToChunked` 列拷贝 |
  | `Archetype.Storage.cs:199` | `_count * elemSize` | `EnsureCapacity` flat 扩容拷贝 |
  | `Archetype.Storage.cs:1064` | `count * elemSize` | `RestoreFlatBackup` |
  | `Archetype.Storage.cs:1099` | `take * elemSize` | `RestoreFlatBackup` chunked 分支 |
  | `Archetype.Storage.cs:325` | `entityCount * elemSize` | WorldStateSnapshot restore |
- **改法**：统一 `checked((uint)(N * elemSize))`——与 `:1144` 和 `:1181` 已用法一致
- **热路径**：⚠️ 扩容路上热路径。`checked` 在溢出时抛异常（不在正常路径产生分支），x64 上 `imul + jo` 非溢出时零开销
- **审阅发现**：原 M1.4 遗漏了 `RestoreFlatBackup`（`:1064`, `:1099`）和 WorldStateSnapshot restore（`:325`）。已补充。

### M1.4.5 `new byte[count * size]` 和 `AsSpan(offset, count * elemSize)` 溢出保护

- **位置**：
  - `Archetype.Storage.cs:868`：`var buf = new byte[count * size];`（`WriteColumnOrderedTo`——Snapshot Save）
  - `Archetype.Storage.cs:977`：`_data.AsSpan(offset, count * elemSize)`（`GetColumnBytes`——Snapshot Save）
- **风险**：`count * size` 或 `count * elemSize` 超过 `int.MaxValue` 时溢出，传给 `new byte[溢出值]` 或 `AsSpan(..., 溢出值)` 导致 `OverflowException` 或 `ArgumentOutOfRangeException`
- **改法**：`checked((int)((uint)count * (uint)size))`，溢出时抛明确异常而非静默 OOM
- **热路径**：❌ Snapshot Save 冷路径

### M1.5 `CommandStreamCore.ReserveBatchBufSpace` 溢出防护

- **位置**：`CommandStreamCore.cs:1711`
- **问题**：`_batchBufLen + size` 未检查溢出；`_batchBufLen += size` 累积可超过 `int.MaxValue`
- **改法**：加 `if (_batchBufLen > int.MaxValue - size) ThrowBufferOverflow()`
- **热路径**：⚠️ 每帧大量 SetComponent 走过这里，但检查只是单次 `if` + 抛出分支不会被预测

### M1.6 无限循环兜底

- **位置**：`CommandStreamCore.cs:1542`（`GrowPendingBatchFor` 的 `while (newLen <= entityId) newLen *= 2;`）
- **风险**：`newLen` 溢变负后，循环永不终止
- **改法**：`if (newLen > maxLen) { newLen = maxLen; break; }` 或在乘法后加 `if (newLen <= 0)` 检测

### M1.7 `(int)RoundUpToPowerOf2` 截断防护

- **位置**：`Archetype.cs:146`
- **风险**：当 `raw` 接近 `2^31` 时，`RoundUpToPowerOf2` 返回设了高位的 `uint`，`(int)` 截断为负数
- **改法**：**已合并到 M1.2**——在 M1.2 的修正版中用 `MaxSegCap = 1 << 30` 作为硬上限，`RoundUpToPowerOf2` 的结果在 `(int)` 转换前就被钳位，不会出现设高位的情况。

### M1.8 `_batchBufLen + size` / `_records.Length * 2` 等倍增溢出

- **位置**：多处（`World.EntityLifecycle.cs:49`, `:663`, `:679`；`Archetype.Storage.cs:188`）
- **共同模式**：`N * 2` 或 `N += delta` 中的 `N` 接近 `int.MaxValue` 时溢出
- **改法**：统一用 `Math.Min(N * 2, MaxSafeValue)` 或直接依靠已存在的 `ArrayMaxLength` 上限

### 验证方式

- `perEntity` 为 150 MB 的 archetype 能正常创建，每段 1 entity，正常读写
- `perEntity` 为 2 GB 时抛明确异常（非静默崩溃）
- 所有 hot path 反汇编确认：无额外分支、无 `long` 运算、无 `checked` 区域（除 M1.4 外）
- `dotnet test -c Release` 全通过 + `HeroComing.Perf --check-baseline`

---

## M2 — 退化性能保护

**目标**：确保在最坏输入下不会出现 O(n²) 或不可接受的退化。

### M2.1 `DestroyCollectedEntities` 分组扫描 O(n²)

- **位置**：`World.EntityLifecycle.cs:466-495`
- **问题**：外层 `n` 个待删实体，内层线性扫描 groups（可增长到 `n`）→ O(n²)
- **当前假设**："groupCount is typically 1-3"——无强制保障
- **改法（选项）**：
  - A: 固定大小数组 + 超过阈值改用 `Dictionary<Archetype, int>` 单次构造
  - B: 对 destroy 输入按 archetype 预排序后一次扫描——O(n log n) 最坏
  - C: 设上限 `MaxGroupCount = 128`，超限改用 Dictionary
- **热路径**：⚠️ Batch destroy（>8 实体）

### M2.2 `AllocateRows` 段扫描从头开始

- **位置**：`Archetype.Storage.cs:254-258`
- **问题**：每次 `AllocateRows` 从 segment 0 开始扫描找非满段 → O(S) 每 allocate，S 可达数十万
- **改法**：缓存最后一个已知非满段索引 `_firstAvailableSegment`，或扫描从上一次位置开始
- **热路径**：⚠️ 实体创建

### M2.3 `RemoveFromFreeList` / `RepushFreeEntry` 线性扫描

- **位置**：`World.EntityLifecycle.cs:858`, `:699`
- **问题**：扫描整个 free list（可等于 entity slot count）→ O(N) 每调用
- **改法**：free list 改为按 id 排序或用延迟批量清除策略（不在正常路径做反查）
- **热路径**：Submit/Replay

### M2.4 `GetSingleton<T>` 无缓存全量扫描

- **位置**：`World.QueryCache.cs:31-55`
- **问题**：O(A) 全 archetype 扫描，A 可上千
- **改法**：加 `_cachedSingleton: (Archetype, int)` 缓存，在对应 archetype 的实体数归零或结构变更时失效
- **热路径**：❌ 标记为冷路径但易误用

---

## M3 — 栈安全

**目标**：消除栈溢出风险——`StackOverflowException` 在 .NET 中不可捕获，进程直接挂。

### M3.1 `stackalloc byte[scratchSize]` 改为 ArrayPool

- **位置**：`World.cs:1147`
- **问题**：`scratchSize` 来自 delta 中最大含 Entity 字段的组件大小。若组件接近 1 MB，`stackalloc` 分配 1 MB 栈空间 → 栈溢出（默认 1 MB 栈）
- **改法**：`scratchSize > 256 ? ArrayPool.Rent(scratchSize) : stackalloc byte[scratchSize]`
- **热路径**：❌ FrameDelta replay 冷路径

### M3.2 `stackalloc ComponentType[componentCount]` 防护

- **位置**：`CommandStreamCore.cs:1223, 1256`
- **问题**：`componentCount` 来自 `CreateManyGroup`，当前 API 路径限制 ≤8，但字段本身是 `int`
- **改法**：加 `if (componentCount > 64)` 改用 `ArrayPool.Rent`
- **热路径**：❌ Submit 冷路径

---

## M4 — 内存安全显式化

**目标**：将依赖不变量的隐式安全（pattern 3E）加上显式断言或防御。

### M4.1 `_segments[segIdx]` 越界防护

- **位置**：`Archetype.Storage.cs` 多处（GetColumnRef chunked 分支、SetComponentAtTyped chunked 分支等）
- **问题**：`GetSegmentAndLocal(row)` 后直接用 `_segments[segIdx]`，无显式越界检查
- **不变式**：`row < _count` → `segIdx = row >> shift ≤ (_count - 1) >> shift < _segmentCount`
- **排查**：哪些调用点之前没有加 `Debug.Assert(segIdx < _segmentCount)`？加上 `[Conditional("DEBUG")]` 断言

### M4.2 热路径列偏移运算加 `Debug.Assert`

- **位置**：`GetColumnRef`、`GetComponentSpanAt`、`SetComponentAtTyped` 等
- **改法**：在 `Unsafe.Add` 前加 `Debug.Assert(row < _count)` 和 `Debug.Assert(columnIndex >= 0 && columnIndex < _columnByteOffsets.Length)`
- **原则**：只在 `#if DEBUG` 中加，Release 无成本

---

## M5 — 确定性与可观测性

### M5.1 溢出错误信息清晰化

- 所有 `checked` 溢出改为抛 `InvalidOperationException` 或专用 `EntityTooLargeException`，带上上下文（archetype 签名、perEntity 大小、capacity）
- 外部调用者（如 `World.Create<T>`）能在测试中捕获到明确错误，而非 `OverflowException`

### M5.2 组件大小上限文档化

- 在 `ComponentSchema.Fingerprint()` 或 `ComponentRegistry` 入口处 doc-comment 写明单组件的实际上限（当前 ≈134 MB per entity 在 segment cap=1 时）
- 在 `README.md` 或设计文档标注硬限制

---

## 执行顺序建议

```
M1 (Integer overflow) — 最危险，优先修复
  ↓
M3 (Stack safety) — 不可恢复的 crash，次之
  ↓
M2 (Degenerate perf) — 影响可用性，按场景评估
  ↓
M4 (Memory safety assertions) — 防御性，可与 M1 合批
  ↓
M5 (Observability) — 收尾文档化
```

## 相关文件

- `.knowledge/kb-chunk-storage.md` — 当前 chunk 存储设计
- `.knowledge/kb-core-ecs.md` — ECS 运行时架构
- `.knowledge/kb-architecture-review.md` — 架构审阅
- `.knowledge/kb-code-review-findings.md` — 已修复 bug + 已排除非 bug
