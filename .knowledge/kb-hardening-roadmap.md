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

## 里程碑总览（完整版）

| 代号 | 主题 | 优先级 | 条目数 | 影响路径 |
|------|------|--------|--------|----------|
| **M1** | **int 溢出防线** | **P0** | 9 | 存储层、CommandStream、序列化、FrameDelta |
| **M8** | **恶意输入安全** | **P0** | 5 | FrameDelta、Snapshot、Replay |
| **M3** | **栈安全** | **P1** | 2 | FrameDelta Replay、CommandStream |
| **M9** | **API 输入验证** | **P0** | 10 | 所有 public API 入口 |
| **M3** | **栈安全** | **P1** | 2 | FrameDelta Replay、CommandStream |
| **M2** | **退化性能保护** | P2 | 2 | Destroy 分组、free list 扫描 |
| **M6** | **并发安全契约** | P2 | 3 | Query 并行迭代、跨 CommandStream |
| **M4** | **契约显式化** | P2 | 3 | Debug.Assert + 源代码注释约定 |
| **M5** | **确定性与可观测性** | P2 | 5 | 文档、Diagnostic 工具、checksum |
| **M7** | **资源生命周期** | P3 | 2 | Dispose 清理 |

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

### M1.9 `FrameDelta.ReadVarint` 符号溢出

- **位置**：`FrameDelta.cs:571-592`
- **问题**：`ReadVarint` 用 `int` 累加 LEB128 值。5 字节 LEB128 可编码 > `int.MaxValue` 的无符号值，`(b & 0x7F) << 28` 溢出为负。下游用负数 `ComponentType` 索引数组 → **IndexOutOfRangeException / crash**
- **改法**：解码循环后加 `if (result < 0) throw new InvalidOperationException("Encoded value exceeds int.MaxValue")`
- **热路径**：❌ FrameDelta deserialization 冷路径

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
- **可达性分析**：✅ 可达。`Destroy(QueryDescription)` 可匹配任意 archetype 集合。典型 gameplay groupCount=1-3 确实成立，但销毁全 World（关卡卸载、压力测试）时可触发 O(n²)。代价：100K 实体 1K archetype → ~50ms。不算挂死，但明显卡顿。
- **改法（选项 C）**：设 `MaxGroupCount = 128`，超限改用 `Dictionary<Archetype, int>` 单次构造。冷路径改造，不涉及热路径退化。
- **热路径**：⚠️ Batch destroy（>8 实体）。

### ~~M2.2 `AllocateRows` 段扫描从头开始~~ ❌ 不可达，不做

- **位置**：`Archetype.Storage.cs:254-258`
- **代码位置加注释**：
  ```csharp
  // NOTE: Linear scan from 0 to find first non-full segment is O(S). S > 1000
  // requires perEntity > 2 MB (segment capacity = 1), which in turn limits
  // entity count to ~hundreds by memory — not enough for O(S) to matter.
  // Per micro-benchmark: S=1000 scan is ~0.5μs. This is intentionally NOT
  // optimized — the cache would add state on the hot path for zero real benefit.
  ```
- **原因**：`_segmentCapacity` 只有在 perEntity > 2 MB 时才小（最小 1），此时实体数受内存限制（20 MB × 1000 = 20 GB → 不可达）。S 上百时 perEntity 已经低到 scan 只要几纳秒。**实际触发不了退化。**

### M2.3 `RemoveFromFreeList` / `RepushFreeEntry` 线性扫描

- **位置**：`World.EntityLifecycle.cs:858`, `:699`
- **问题**：扫描整个 free list（可等于 entity slot count）→ O(N) 每调用；且用的是 `Array.Copy` 移位（O(N) shift），不是 swap-remove
- **可达性分析**：✅ **可达**。帧同步 / Replay 序列：帧 1 Create 100K 实体，帧 2 Destroy 全部 → free list = 100K，帧 3 Replay 帧 1 的 100K Reserve → 每个 Reserve 扫 100K → **5B 次操作 → 秒级卡顿**。这是真实 lockstep 场景。
- **改法（见下文讨论）**：
  - **必做**：`Array.Copy` 移位改为 swap-remove（消除 O(N) shift，与 archetype.RemoveAt 模式一致）
  - **可选**：加 lazy `Dictionary<int, int>` 索引消除 O(N) scan（仅在第一个 `RemoveFromFreeList` 或 `RepushFreeEntry` 调用时构建）
- **热路径**：Submit/Replay。`RemoveFromFreeList` 在 `EnsureReplayReservation` 中调用（Replay 路径），`RepushFreeEntry` 在 cancel pending entity 时调用（Submit 路径）。

### ~~M2.4 `GetSingleton<T>` 无缓存全量扫描~~ ❌ 不可达，不做

- **位置**：`World.QueryCache.cs:31-55`
- **代码位置加注释**：
  ```csharp
  // NOTE: O(archetype_count) scan. Even with 100K archetypes this is < 1ms.
  // Marked as cold path — callers should cache the result. Not worth caching
  // internally (adds invalidation complexity on every archetype change).
  ```
- **原因**：O(A) 扫描，A = archetype 数 = 每种唯一组件组合一个。典型 10-50，极端 100K。100K 时每次 < 1ms。API 明确标注为冷路径。加缓存需要每次 archetype 变更（Create/Destroy/Add/Remove 新组件组合）做失效，热路径成本不值得。**实际触发不了可用性退化。**

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

## M4 — 契约显式化（源代码即契约）

**目标**：将依赖不变量的隐式安全（pattern 3E）加上显式断言和源代码级契约，让读代码的人一眼就知道边界条件。

### 原则
- 不修运行时热路径
- 用 `[Conditional("DEBUG")]` 断言检测契约违规
- **注释写在声明处**，不是写在文档里——`/// <remarks>` 和行内 comment 直接在代码中说明「这个不变式成立的前提是什么」

### M4.1 `_segments[segIdx]` 越界防护

- **位置**：`Archetype.Storage.cs` 多处（GetColumnRef chunked 分支、SetComponentAtTyped chunked 分支等）
- **问题**：`GetSegmentAndLocal(row)` 后直接用 `_segments[segIdx]`，无显式越界检查
- **不变式**：`row < _count` → `segIdx = row >> shift ≤ (_count - 1) >> shift < _segmentCount`
- **排查**：哪些调用点之前没有加 `Debug.Assert`？加上 `[Conditional("DEBUG")]` 断言，**必须带消息字符串**：
  ```csharp
  Debug.Assert(segIdx < _segmentCount,
      $"segIdx={segIdx} >= _segmentCount={_segmentCount}. " +
      $"GetSegmentAndLocal({row}) returned out-of-range segment index. " +
      $"This invariant broke: row {row} < _count {_count} should guarantee segIdx < _segmentCount.");
  ```

### M4.2 热路径列偏移运算加 `Debug.Assert`

- **位置**：`GetColumnRef`、`GetComponentSpanAt`、`SetComponentAtTyped` 等
- **改法**：在 `Unsafe.Add` 前加带消息的 `Debug.Assert`：
  ```csharp
  Debug.Assert(row < _count,
      $"row={row} >= _count={_count}. Entity row index out of range. " +
      $"This indicates a stale EntityRecord.RowIndex or a corrupted _count.");
  Debug.Assert((uint)columnIndex < (uint)_columnByteOffsets.Length,
      $"columnIndex={columnIndex} >= _columnByteOffsets.Length={_columnByteOffsets.Length}. " +
      $"Component column index out of range. This component type may not belong to this archetype.");
  ```
- **原则**：只在 `#if DEBUG` 中加，Release 无成本

### M4.3 `ComponentStore<T>` inline 存储契约

- **位置**：`CommandStreamCore.cs:2735-2760`（`ComponentStore<T>` / `StoreEntry<T>` / `LocalBuffer`）
- **问题**：`StoreEntry<T>` 将 `T Value` inline 存储在数组中。`new StoreEntry<T>[256]` 时 `sizeof(T) × 256` 若超出可用内存则 OOM。这是一个架构约束——不是 bug，而是故意为之的性能设计
- **改法（不修运行时，只加契约）**：

  1. **在 `StoreEntry<T>` 声明处加注释**：
  ```csharp
  // T is stored inline (not boxed/pointer) for cache locality in ApplyToWorld.
  // CONSTRAINT: sizeof(T) × InitialCapacity(256) must fit in available memory.
  // For components larger than ~64 KB, prefer the pending-entity/BatchBuf path
  // (stream.Create() + stream.Set(pending, ...)) which serializes into byte[]
  // and avoids per-type inline arrays.
  internal struct StoreEntry<T> where T : unmanaged { ... }
  ```

  2. **在 `LocalBuffer` 的 `new StoreEntry<T>[256]` 处加 DEBUG 断言**：
  ```csharp
  // sizeof(T) is a compile-time constant; the assert has zero runtime cost
  // even in DEBUG — the JIT eliminates the dead branch.
  Debug.Assert(sizeof(T) <= 65536,
      $"ComponentStore<{typeof(T).Name}> initial capacity 256 × sizeof(T)={sizeof(T)} " +
      $"requires {256L * sizeof(T) / (1024*1024)} MB. For larger components, " +
      $"use the pending-entity path (BatchBuf).");
  ```

  3. **在 `_entries` 的 `EnsureStoreCapacity()` 加倍处加同样断言**：
  ```csharp
  Debug.Assert(sizeof(T) <= 65536,
      $"ComponentStore<{typeof(T).Name}> ensure capacity: sizeof(T)={sizeof(T)} > 64 KB. " +
      $"For large components, use the pending-entity path (BatchBuf) instead of " +
      $"ComponentStore which stores T inline in StoreEntry<T>[] arrays.");
  ```

- **效果**：DEBUG 下用户用超大 T 时立即得到明确错误并指明替代路径；Release 下零成本（`Debug.Assert` 消失，`sizeof(T)` 是常量表达式
- **不修运行时**：这是架构约束，不是 bug。`StoreEntry<T>` inline 设计是刻意的性能取舍，不改。

---

## M5 — 确定性与可观测性

### M5.1 溢出错误信息清晰化

- 所有 `checked` 溢出改为抛 `InvalidOperationException` 或专用 `EntityTooLargeException`，带上上下文（archetype 签名、perEntity 大小、capacity）
- 外部调用者（如 `World.Create<T>`）能在测试中捕获到明确错误，而非 `OverflowException`

### M5.2 组件大小上限文档化

- 在 `ComponentSchema.Fingerprint()` 或 `ComponentRegistry` 入口处 doc-comment 写明单组件的实际上限（当前 ≈134 MB per entity 在 segment cap=1 时）
- 在 `README.md` 或设计文档标注硬限制

### M5.3 `WorldDigest` Dictionary 迭代顺序非确定

- **位置**：`WorldDigest.cs:122`, `:184-208`
- **问题**：`foreach` 遍历 `Dictionary<Type, ...>` 和 `Dictionary<int, ...>` 无排序。Dictionary 迭代顺序跨 .NET 版本/运行不可保证。`WorldDigestResult` 的 doc 声称「所有 hash 确定」——**错误**
- **改法**：迭代前按 key 排序（`OrderBy(k => k.Key)`），或修正 doc 声明为「仅用于调试, 不保证跨 host 一致」
- **热路径**：❌ Diagnostic 冷路径
- **原则**：不入侵 core，只修 diagnostic 工具本身

### M5.4 struct padding bytes 文档警告

- **位置**：`ThrowIfManagedComponent`（已拦截 `LayoutKind.Auto`），checksum 路径（`FeedColumnData` / `FeedRowData`）
- **问题**：`LayoutKind.Sequential` 的 struct 在字段间可能有 padding byte。若用户通过指针 cast / `Unsafe.As` 构造组件，padding 内容非确定 → checksum 分歧
- **改法**：
  - 在 `ThrowIfManagedComponent` 的 doc-comment 写明：「component 必须用 `new T()` / `default(T)` 构造，不能通过指针 reinterpret 初始化。否则 padding byte 非零导致 checksum 不一致」
  - 可选：在 `DEBUG` 下对第一个实例做 `Marshal.OffsetOf` 字段间 gap 检测并 warn
- **热路径**：❌ 注册时冷路径

### M5.5 `AppendInt` 字节序显式化

- **位置**：`WorldSnapshot.cs:271-274`（`AppendInt`），`WorldDigestResult.cs:63`
- **问题**：`MemoryMarshal.AsBytes(Span<int>)` 和 `BitConverter.GetBytes` 输出平台字节序。x64 永远是 LE，但理论上不跨 BE 架构。锁步网络通常要求全 host 一致
- **改法**：改为显式 `BinaryPrimitives.WriteInt32LittleEndian(span, v)`（在 `System.IO.BinaryPrimitives` 中已定义，零分配）
- **热路径**：❌ Snapshot Save/Load 冷路径

---

---

## M6 — 并发安全（契约 + Debug 断言，不入侵热路径）

**目标**：在单线程写 + 多线程读的契约下，用最小代价检测违规。**不在热路径加锁**，不入侵内核读写路径。

### 原则
- 所有 `volatile`/`lock`/`ThreadLocal` 保持现状——它们已经正确
- 不修改 `GetColumnRef`、`GetSpan<T>`、`MoveNext` 等热路径
- 只加两样东西：**`[Conditional("DEBUG")]` 断言** + **文档明确契约**

### M6.1 并发结构变更检测（Debug Assert）

- **问题**：`Parallel.ForEachChunkParallel` + 主线程结构变更同时发生 → 工作线程读 archetype stale/null 状态 → UB
- **改法**：在 `World` 上加 **`_structureChangeInProgress` `int` 计数器**（volatile 或 `int` 原子）：
  - `BeginStructChange()` → `Interlocked.Increment`（或单线程 `++`，因为所有结构变更已经是单线程）
  - `EndStructChange()` → `Interlocked.Decrement`
  - `AssertNoStructChange()` → `[Conditional("DEBUG")] Debug.Assert(_structureChangeInProgress == 0,
        "Structural change detected during query iteration! " +
        "Query iteration and structural changes (Create/Destroy/Add/Set/Remove) " +
        "must not overlap in the same thread. Snapshot the query before parallel work.")`
- **调用点**：
  - `Query.ForEachChunkParallel` 入口处加 `AssertNoStructChange()`
  - 所有 `*Enumerator.MoveNext()` `GetChunks()` 入口加 `AssertNoStructChange()`
  - World 结构变更入口（`Create`/`Destroy`/`Add`/`Set`/`Remove`）加 `Begin/EndStructChange`
- **热路径**：⚠️ `MoveNext()` 加的是 `[Conditional("DEBUG")]`——Release 零成本
- **不入侵内核**：计数器在 `World` 层，`Archetype`/`ChunkView` 不感知

### M6.2 跨 `ParallelCommandStream` 实体分配冲突

- **位置**：`ParallelCommandStream.cs:50-179`
- **问题**：两个 `ParallelCommandStream` 指向同一个 `World` 时，`_storeCreateLock`（流级别）无法互斥 `ReserveDeferredEntityUnsafe`（无锁）→ 实体 ID 分配静默冲突
- **改法（方案 A，推荐）**：
  - 将 `_entityIdLock` 从 `ReserveDeferredEntity` 提升为 World 级保护——所有实体分配路径（`ReserveDeferredEntity` + `ParallelCommandStream`）都经过同一把锁
  - 代价：`ParallelCommandStream.Create` 多一次 `Monitor.Enter`（每帧实体创建次数有限，可接受）
- **改法（方案 B，最小）**：
  - 在 `ParallelCommandStream` 中检测 `_world._reservedCount` 在跨流场景下的不一致，抛明确异常
- **热路径**：⚠️ 方案 A 加锁只在 `Create`/`Reserve` 路径，不涉及 `Set`/`Get` 热路径

### M6.3 `WorldSnapshot` ThreadStatic reentrancy 防护

- **位置**：`WorldSnapshot.cs:29-30`（`_csEntries`, `_csRelations`）
- **问题**：checksum callback 内再调 checksum → 内层 `Clear()` 清空外层正在排序的列表 → 数据损坏
- **改法**：`[Conditional("DEBUG")]` 在 `_csEntries.Clear()` 前检查 `_inComputeChecksum` flag，抛明确异常：
  ```csharp
  Debug.Assert(!_inComputeChecksum,
      "Reentrant call to ComputeChecksum or ComputeCanonicalChecksum detected. " +
      "These methods are not reentrant: the checksum callback must not trigger " +
      "another checksum computation.");
  ```
- **热路径**：❌ Diagnostic 冷路径

---

## M7 — 资源生命周期清理

**目标**：Dispose 路径完整清理，消除悬垂引用和释放后使用。

### M7.1 `Archetype._owner` 移除或 Dispose 时清空

- **位置**：`Archetype.cs:55`, `World.cs:562`, `World.cs:148-179`
- **问题**：`_owner` 是死代码（从未读取），`World.Dispose()` 不清空。若未来代码读取 `_owner` 会得悬垂引用
- **改法（选项 A，推荐）**：
  - 在 `World.Dispose()` 中遍历 `_archetypes.Values`，设 `arch._owner = null`
  - **或者**直接删掉 `_owner` 字段（零调用，YAGNI）
- **热路径**：❌ Dispose 冷路径

### M7.2 释放后 Query 枚举器防护

- **位置**：全局
- **问题**：`World.Dispose()` 后，用户持有的 `QueryEnumerator` 仍有旧 archetype 引用。迭代可能返回数据
- **改法**：在 `QueryEnumerator.MoveNext()` 加 `[Conditional("DEBUG")]` 的 `_world.AssertNotDisposed()` 检查。`AssertNotDisposed` 本身应带消息：
  ```csharp
  [Conditional("DEBUG")]
  internal void AssertNotDisposed()
  {
      Debug.Assert(!_disposed,
          "This World has been disposed. Query enumerator from a disposed World " +
          "returns stale data. Ensure all queries complete before World.Dispose().");
  }
  ```
- **热路径**：⚠️ `[Conditional("DEBUG")]` 只在 Debug 生效，Release 零成本

### M7.3 `ArrayPool.Return` 遍历确认

- **审计结论**：全部 29 个 `Rent` 均有对应 `Return`，且处于 `try/finally` 中 ✅ 无需修改

---

## M8 — 恶意输入 / 安全（DEFCON）

**目标**：FrameDelta 反序列化和 Snapshot 加载路径不受畸形输入攻击。

### 原则
- 所有防御在**冷路径**（`Validate()` / `Replay()` 入口 / `Load()` 入口）
- 不入侵热路径（不修改 `Archetype.Storage`、`Query` 等内核）
- 不信任 wire data——每个解码值都要经过范围检查

### M8.1 `FrameDelta.ReadVarint` 防符号溢出

- **已归入 M1.9**——`if (result < 0) throw` 在解码循环后

### M8.2 `Replay` 路径组件数据大小校验

- **位置**：`World.cs:1105-1169`（`ReplayCreateOpCore`）, `:834-901`（`ApplyRawAdd` / `ApplyRawSet`）
- **问题**：`WriteComponentRaw` 写入 `_elementSizes[colIdx]` 字节，但 delta 中声明的 `dataSize` 可能不同。若 `dataSize < elemSize` → 读取 delta buffer 越界（读入相邻 op 的原始字节）→ **静默 corruption**
- **现有防护**：`FrameDelta.Validate()` 检查 `dataSize == expected`，但 `Validate()` 是**可选调用**，`Replay(delta)` 不自动调它
- **改法（选项 A）**：
  - 在 `ReplayCore` 入口处（`World.cs:737`）增加 `delta.ValidateAsync(world)` 调用，失败则抛异常。这样所有 replay 入口自动校验
  - Validate 是 O(n) 扫描，但在网络帧同步场景下 delta 本就不大（典型 < 1MB），可接受
- **改法（选项 B，最小）**：
  - 在 `WriteComponentRaw` 中加带消息的 `Debug.Assert`：
  ```csharp
  Debug.Assert(dataSize == _elementSizes[columnIndex],
      $"Remote component data size ({dataSize}) != local element size " +
      $"({_elementSizes[columnIndex]}) for component column {columnIndex}. " +
      "FrameDelta.Validate() must be called before Replay() to prevent " +
      "silent data corruption from size mismatch.");
  ```
- **热路径**：❌ Replay 冷路径

### M8.3 `PreScanForCapacity` 恶意 OOM 防护

- **位置**：`World.cs:971-1049`
- **问题**：real-id 模式下，delta 中 `Reserve(Entity(hugeId, ...))` 导致 `_records` 预分配数组巨大（可达数 GB）→ OOM
- **现有防护**：`Destroy` 路径（`:1035-1036`）已有明确注释阻止跟踪 maxEntityId，但 **`Reserve` 和 `Create` 路径未保护**
- **改法**：在 `maxEntityId` 增长时加上限：
  ```csharp
  if (id >= 0 && id > maxEntityId)
      maxEntityId = Math.Min(id, _records.Length * 2 + 1024);
  ```
  或 `Math.Min(id, _records.Length + 65536)`，确保不会一次性膨胀过大
- **热路径**：❌ PreScan 冷路径

### M8.4 Snapshot Load 恶意 `entitySlotCount` 防护

- **位置**：`WorldSnapshot.cs:155-176`
- **问题**：v3 快照（无 CRC）可直接指定 `entitySlotCount = 2_000_000_000` 触发 OOM
- **改法**：在 `Load` 中加 `if (entitySlotCount > maxReasonableSlots) throw`。上限可设为 `256 * 1024 * 1024`（约 2.6 亿实体，4 GB `EntityRecord[]`）或由用户配置
- **热路径**：❌ Load 冷路径

### M8.5 Snapshot hierarchy 循环防护

- **位置**：`HierarchyTable.cs:28-29`（`AddChildRestored`）
- **问题**：Snapshot restore 用 `AddChildRestored` 绕过 `ValidateAddChild`。恶意快照可构建循环层级
- **后果**：`CollectDestroySubtree` 有 `_destroyVisitedGen` 防无限循环，所以不会栈溢出，但 `RemoveChild` 等操作行为可能错误
- **改法**：在 `AddChildRestored` 中加带消息的 `Debug.Assert` 反证——快照中不应有循环，若有则在调试期发现：
  ```csharp
  Debug.Assert(!ValidateAddChild(world, parent, child),
      $"Snapshot restore created a hierarchy cycle: parent={parent}, child={child}. " +
      "Cycle-free hierarchy is a snapshot invariant; malformed snapshots " +
      "should be rejected during Load().");
  ```
- **热路径**：❌ Restore 冷路径

---

## M9 — API 输入口系统性验证

**目标**：对所有用户可见的 public API 入口做系统性输入验证。对于造成热路径退化的，用 `Debug.Assert` + 源代码契约保护。

### 原则
- 所有 public 方法**必须**对 null 参数抛 `ArgumentNullException`（参考 `ChangeWatch`/`WorldSnapshot` 的模式）
- 所有 `int` 参数在用作数组大小时必须有**合理上限**（不一定是硬上限，而是防止 OOM 的挡板）
- `default(ChunkView)` / `default(EntityAccessor)` / `default(QueryDescriptor)` 等零值 struct 必须有 `Debug.Assert` 或文档说明
- **BUG 直接修**（对合法输入崩溃的），**契约用注释写在声明处**

### 审计发现的 BUG（必须修，非契约）

#### M9.1 `ChunkView.GetSpan<T>()` / `GetComponentSpanAt<T>()` 未检查组件存在性

- **位置**：`ChunkView.cs:88-101, 116-124`
- **问题**：`GetComponentIndexFast(Component<T>.ComponentType)` 当 T 不在 archetype 中时返回 `-1`（`_componentIdToColumnIndex[id]` 的默认值），然后 `_elementSizes[-1]` → `IndexOutOfRangeException`
- **不修运行时（热路径）**——`Debug.Assert` 必须带消息说明：
  ```csharp
  Debug.Assert(_archetype.TryGetComponentIndex(Component<T>.ComponentType, out _),
      $"ChunkView.GetSpan<{typeof(T).Name}>() called on archetype without this component. " +
      $"The chunk's archetype component types: [{string.Join(", ", _archetype.ComponentTypes)}]. " +
      $"Use ChunkView.TryGetComponentIndex<T>() to check before calling GetSpan<T>() " +
      $"for optional components.");
  ```
- **修复文档**：在 `<remarks>` 中说明「只应在通过 Query 筛选过的 chunk 上调用，或先调 `TryGetComponentIndex<T>()`」
- **热路径**：⚠️ `GetSpan<T>` 是每帧迭代热路径——只加 `Debug.Assert`

#### M9.2 `EntityAccessor.Get<T>()` / `Set<T>()` 同模式

- **位置**：`EntityAccessor.cs:35, 51`
- **问题**：同 `GetComponentIndexFast` 未检查
- **改法**：同上 `Debug.Assert`（消息写明实体 + 组件上下文）：
  ```csharp
  Debug.Assert(_archetype.TryGetComponentIndex(Component<T>.ComponentType, out _),
      $"EntityAccessor.Get<{typeof(T).Name}>(): the entity's archetype " +
      $"does not contain component {typeof(T).Name}. Verify with Has<T>() " +
      $"before calling Get<T>(), or use TryGet<T>().");
  ```
- **热路径**：⚠️ `Debug.Assert` 只在 DEBUG

#### M9.3 `default(ChunkView)` / `default(EntityAccessor)` NRE

- **位置**：`ChunkView.cs`, `EntityAccessor.cs`
- **问题**：`ref struct` 可以被 `default` 初始化，`_archetype` 为 null → `NullReferenceException`
- **改法**：每个 public 方法入口加：
  ```csharp
  [Conditional("DEBUG")]
  private void AssertInitialized() =>
      Debug.Assert(_archetype is not null, "ChunkView was default-initialized; use Query.GetChunks() to obtain a valid view.");
  ```
- **热路径**：⚠️ `[Conditional("DEBUG")]`——Release 零成本

#### M9.4 `CommandStreamCore.Replay(FrameDelta)` null 参数

- **位置**：`CommandStreamCore.cs:746-752`
- **问题**：`delta` 直接解引用，无 null 检查
- **改法**：加 `ArgumentNullException.ThrowIfNull(delta)`
- **热路径**：❌ Replay 冷路径

#### M9.5 `CommandStreamCore.SubmitAndSnapshotIntoAsync(FrameDelta)` null 参数

- **位置**：`CommandStreamCore.cs:849`
- **问题**：`target.Clear()` 前无 null 检查
- **改法**：同上，`ArgumentNullException.ThrowIfNull(target)`
- **热路径**：❌ 异步 snapshot 冷路径

#### M9.6 `WorldSnapshot.Load(Stream)` 截断流保护

- **位置**：`WorldSnapshot.cs:115-118`
- **问题**：`BitConverter.ToInt32(snapshotBytes, 0)` 当 `snapshotBytes.Length < 4` 时抛 `ArgumentOutOfRangeException`，没有包装成有意义的错误消息
- **改法**：
  ```csharp
  if (snapshotBytes.Length < 8)
      throw new InvalidDataException($"Snapshot data is too short ({snapshotBytes.Length} bytes). Expected at least 8 bytes for header.");
  ```
- **热路径**：❌ Load 冷路径

### 契约缺口（文档/注释修复，不修代码）

#### M9.7 `Remove<T>()` 缺失组件时静默 no-op

- **位置**：`World.StructuralChange.cs:202`
- **问题**：当 entity 不含组件 T 时，`Remove<T>` 静默返回。用户可能误以为移除了组件
- **改法**：在 XML doc 的 `<remarks>` 中注明：「组件不存在时不抛异常，静默 no-op。调用前可用 `Has<T>()` 确认」

#### M9.8 CommandStream `Add/Set/Remove<T>` 对不存在实体静默丢弃

- **位置**：`CommandStream.cs:57, 78, 100`
- **问题**：`!_world.IsAlive(entity)` → 静默丢弃，不通知用户
- **改法**：在 XML doc 的 `<remarks>` 中注明：「如果 entity 不存在或非 alive，命令被静默丢弃。可用 `Entity.IsPlaceholder` 确认」

#### M9.9 `World.EntityCount` 可能返回负数

- **位置**：`World.cs:213`
- **问题**：`_entitySlotCount - _freeIdCount - _reservedCount` 在内部不变量被破坏时可为负
- **改法**：`Math.Max(0, ...)` 或在 `<remarks>` 中注明「仅用于诊断，可能小于实际存活数量」

### 架构设计限制（不修，文档化）

#### M9.10 跨 World 实体污染

- **位置**：全局——`Entity` 结构体不关联所属 World
- **问题**：`Entity(Id, Version)` 只通过 `(slot, version)` 配对鉴别。World A 的实体可能恰好在 World B 中有相同 `(Id, Version)` → 静默操作到错误实体
- **改法**：**不修**。将 World 标识嵌入 Entity 会破坏值类型语义（16→24 字节，失去 `IComparable<Entity>` 的简单性）。在 `Entity` 的 XML doc 中加 `<remarks>`：
  ```xml
  /// <remarks>
  /// Entity has no World association. Using an Entity from one World in
  /// another World's APIs is undefined behavior —if the slot+version
  /// coincidentally match, operations silently affect the wrong entity.
  /// </remarks>
  ```

---

## 执行顺序建议

```
M1 (Integer overflow) + M8 (Security) + M9 (API validation) — 最危险，可并行
  ↓
M3 (Stack safety) — 不可恢复的 crash
  ↓
M6 (Concurrency contracts) — DEBUG 断言，易落地
  ↓
M2 (Degenerate perf) — 影响可用性，按场景评估
  ↓
M4 (Memory safety assertions) — 防御性，可与 M1/M6 合批
  ↓
M5 (Observability) + M7 (Lifetime) — 收尾文档化
```

```
M1 (Integer overflow) + M8 (Security) — 最危险，可并行
  ↓
M3 (Stack safety) — 不可恢复的 crash
  ↓
M6 (Concurrency contracts) — DEBUG 断言，易落地
  ↓
M2 (Degenerate perf) — 影响可用性，按场景评估
  ↓
M4 (Memory safety assertions) — 防御性，可与 M1/M6 合批
  ↓
M5 (Observability) + M7 (Lifetime) — 收尾文档化
```

**核心原则**：
- M6/M4 不改热路径，全用 `[Conditional("DEBUG")]` 断言
- M2 在冷路径兜底
- M1/M8 在冷路径前置校验
- 所有条目修复后 `dotnet test -c Release` + `HeroComing.Perf --check-baseline` 必须通过

## 相关文件

- `.knowledge/kb-chunk-storage.md` — 当前 chunk 存储设计
- `.knowledge/kb-core-ecs.md` — ECS 运行时架构
- `.knowledge/kb-architecture-review.md` — 架构审阅
- `.knowledge/kb-code-review-findings.md` — 已修复 bug + 已排除非 bug
