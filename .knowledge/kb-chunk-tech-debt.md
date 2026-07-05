---
title: Chunk 存储 structual debt 审计与偿还规划
module: MiniArch.Core
description: dual-mode 存储的结构债清单、按层分析的不变式脆弱性分布、偿还顺序与每项的"不做什么"。后期慢慢实现的参考。
updated: 2026-07-05 (initial audit after advisor review)
---
# Chunk 存储 structual debt 审计与偿还规划

## 这个模块是干什么的

- 这个文档负责：
  - 审计 `Archetype` dual-mode（flat / chunked）存储的所有结构债
  - 按"咬回来概率 × 不变式抓难度"分层，明确哪些靠纪律防、哪些裸露
  - 给出偿还顺序与每项的具体动作，作为后期逐步实现的索引
- 这个文档不负责：
  - 性能取舍的依据——见 `kb-ecs-comparison.md`（flat 模式 +70~74% vs Arch 是显式选择不是债）
  - storage layout 的完整说明——见 `kb-chunk-storage.md`（本页只覆盖它的脆弱点）
  - "是否要走 always-chunked / small-chunk PoC" 的讨论——见 `kb-design-rationale.md` §3.8 已评估的方向

## 架构（不变式分层与脆弱性分布）

当前 `Archetype` storage 跨 6 层触及 dual-mode：

### 层 1：模式切换转换点（已防，靠分布纪律）

`EnsureCapacity` 是唯一触发 `ConvertToChunked` 的入口。`AllocateRows` / `WriteEntityAt` / `AddEntity` 各自在内部单次读 `IsChunked` 后分支。

**裸露**：所有"模式切换纪律"靠调用方在内部单次读 `IsChunked`——这是模式契约。新加 layout-mutating 方法**没有结构性强制**确认它做了单次读 + 不跨分支取过期字段。靠 PR review 拦。

### 层 2：ThrowIfChunked API 家族（裸露，结构性脆弱）

以下方法**只在 archetype 还小时适用**，升 chunked 即 `InvalidOperationException`：

- `GetComponentRef<T>(colIdx)`             — `Archetype.Storage.cs:381`
- `GetComponentSpanAt<T>(colIdx)`         — `:422`
- `GetComponentSpan<T>(component)`         — `:428`
- `GetReservedEntities(start, count)`     — `:623`

调用方不止 `ChunkView`（我原以为只有 ChunkView，**顾问修正：不完整**）：

| 调用点 | 文件 |
|---|---|
| `ChunkView.GetComponentSpan<T>` / `GetComponentSpanAt<T>` non-chunked 分支 | `Core/ChunkView.cs:78,102` |
| `OrderedComponentEnumerator.Initialize` | `Query.cs:613`（**已被 `BUG_order_by_component_supports_chunked_archetypes` 咬过一次**，见 `kb-code-review-findings.md:45`） |
| `ThroughputRunner`（baseline 计时） | `tests/SharedInfrastructure/.../ThroughputRunner.cs:698,781-786` |
| `QueryProfilingRunner` | `tests/SharedInfrastructure/.../QueryProfilingRunner.cs:387` |
| `QueryBenchmarks` | `tests/MiniArch.Benchmarks/QueryBenchmarks.cs:234,299` |
| 单元测试多处 | `ArchetypeTests.cs:67`、`ChunkTests.cs:37,61,...`、`QueryTests.cs:581-582` |
| `WriteCreatedEntitiesAndLocationsFlat` 唯一 `GetReservedEntities` 调用 | `World.EntityLifecycle.cs:297` |

**已经被同族 bug 咬过一次**——是「合同不静态强制必再发生」的强论据。

### 层 3：CopyComponent 4 分支组合爆炸（裸露，N×N 增长）

`CopyComponent` / `CopyColumnFrom` / `CopyComponentRaw` 各自 `src.Chunked × dst.Chunked = 4` 分支。冷组合（src.chunked-dst.flat / src.flat-dst.chunked）只在大 archetype + 结构变更的生产场景触发，单元测试常覆盖不全。新加跨 archetype 拷贝操作自动 ×4。

### 层 4：`_flatEntitiesGeneration` 维护纪律（裸露，最危险）

`long` 计数器在 8 个 mutation 递增：
`GrowChunked`、`AllocateRows`(chunked 末)、`WriteEntityAt`(chunked 末)、`RemoveAt`(chunked 末 ×2)、`RebuildFlatEntities`、`RestoreFlatBackup`(chunked 末)。

`GetEntityStorageUnsafe()` 在 generation 不匹配时重建 `_cachedFlatEntities`，flat 模式直接 return `_entities` 不走 cache。

**裸露**：
- 漏 bump 静默提供给 `QueryEnumerator.MoveNext()` 热路径陈旧 entity id
- 任何 Debug assert 抓不到"应该 bump 没 bump"——只能抓"gen 匹配时 cache 内容正确"
- 8 个递增点全靠人工维护

### 层 5：跨模式 snapshot 翻译（已防，缺对称断言）

`RestoreFlatBackup`（`Archetype.Storage.cs:779`）处理 4 组合：

| backup | arch 当前 | 路径 |
|---|---|---|
| flat | flat | 同 layout col-by-col |
| flat | flat (layout 变了) | 双 offset lookup |
| flat | chunked | 分到 segments |
| chunked | chunked | `RestoreTo` 走 segment 复制 |

唯一不可能：`chunked backup → flat arch`（单向晋升）。

**裸露**：chunked→chunked 有 `Debug.Assert(SegmentCount <= arch.SegmentCount)`，**flat→flat / flat→chunked 缺等价断言**。

### 层 6：segment shape invariants（全隐式，顾问新加）

下列不变式目前没有集中 DEBUG 验证：

- `_count == Σ seg.Count`
- 除末段外所有 seg 满（`Entities.Length == _segmentCapacity`，`seg.Count == _segmentCapacity`）
- `_columnByteOffsets` 关联当前 `_segmentCapacity`
- `ChunkView._segmentIndex = -1` sentinel 表示 non-chunked（命名常量缺失 + DEBUG 有效性断言缺失）

不变式全靠隐式维持，deep refactor 前应先固化。

### 不算债的两点

- `WriteCreatedEntitiesAndLocationsFlat` vs `...Chunked` 二分——**显式 flat 批量 fast path**，保留；只有 `GetReservedEntities` 的名字/合同是债
- `ConvertToChunked` 在 `_count==0` 时仍造空 segment 0——sloppy but cold。短路口反而引入 `IsChunked && SegmentCount==0` 新 sub-state，所有 chunked 不变式都要再防一个边界。**跳过**

## 决策（偿还顺序与"不做什么"）

**核心原则**（advisor 给）：flat 热路径必须保持 contiguous；chunked 路径必须 segmented；**API 不应假装两种模式有相同物理形状**。ChunkView 是正确的"一个连续可迭代存储单元"公共抽象（flat 模式 1 archetype = 1 chunk；chunked 模式 1 segment = 1 chunk）。

### 推荐顺序

| # | 项 | 做什么 | 不做什么 |
|---|---|---|---|
| **1** | DEBUG invariant 固化（前置） | `AssertSegmentInvariants()`（验证 `_count==Σseg.Count`、除末段满、offset 关联 `_segmentCapacity`）+ flat cache 内容校验 + `_segmentIndex = -1` 命名常量 + DEBUG 有效性断言 | 不动运行时逻辑。仅作为后续 4 项的安全网（任何 refactor 后跑 invariant assert 立刻知道有没有破） |
| **2** | 删 ThrowIfChunked API 家族 | 重命名 `GetComponentRef`/`GetComponentSpanAt`/`GetComponentSpan`/`GetReservedEntities` → `GetFlat*` + `Debug.Assert(!IsChunked)`；调用方（`Query.cs:613`、bench harness、tests）改走 `ChunkView` 或 segment 遍历；`GetComponentRefAt(col,row)` 带行参数版本保留双模式 | **不让它们在 chunked 下"工作"**——chunked 物理上没有单一连续 span，返回 copy span 破坏写语义并分配内存，返回 row-0 ref 让调用方当连续基地址用静默 corrupt |
| **3** | 抽 layout-agnostic 拷贝原语 | **仅在 4 模式组合测试覆盖后**。封装 `CopyComponent`/`CopyColumnFrom`/`CopyComponentRaw` 的 4 分支为 layout-abstraction（LayoutAccess struct with `GetRef(col, row) → ref byte`） | 不加 virtual call / bounds check；不丢 `CopySmall` inlining |
| **4** | 集中 cache invalidation | `InvalidateFlatEntityCache()` 集中 + DEBUG 内容校验（每次 `GetEntityStorageUnsafe` 命中 cache 时抽查 segment.Count 求和等于 cache 长度） | **不做 token 化**——blast radius 大且不真正消除纪律（仍要每个 mutation 通过 token-producing primitive）。集中只是减少"写错 bump 表达式"，递增点数量不变——真正减少纪律的方式是减少 mutation site |
| **5** | 跨模式 snapshot 断言 | flat→chunked / flat→flat restore 加 `Debug.Assert`，对齐 chunked→chunked 的 `SegmentCount <= arch.SegmentCount` 模式 | — |
| ~~6~~ | ~~`ConvertToChunked` 短路 `count==0`~~ | — | 跳过；顾问建议 |

### 三项关键判断的依据

1. **(2) 删而不补**：`GetComponentSpan<T>()` 在 chunked 下"工作"是错的。`OrderedComponentEnumerator` 修过的 bug（`kb-code-review-findings.md:45`）正是同一陷阱——同族 bug 已经发生过，合同不静态强制还会再发生。

2. **(4) token 化先不做**：把 cache 失效摊到 immutable token 需要 mutation 全部通过 token-producing primitive。token 化自身 blast radius 大，且不真正消除纪律（即使强制走 primitive 也是纪律的一种）。集中 + DEBUG 校验覆盖度足够且改动面小。

3. **(6) 跳过**：`_count==0` 仍造空 segment 0 是 sloppy but cold。强行短路引入新 sub-state，让所有 chunked 不变式都要再防一个边界——除非显式需要 `IsChunked && SegmentCount==0`，不动。

## 认知模型

- 理解本页时，把 dual-mode 看成：**6 层不变式 × 静态可强制性** 的二元分布
- 最重要的 insight（advisor 给）：
  - **chunked archetype 没有单一连续 span 是物理事实**，API 不该假装两种模式同形状
  - **`BumpLayoutGen()` 集中不减少纪律**——8 个 mutation site 仍要每个调一次。减少纪律的真正方式是减少 mutation site，不是集中表达
  - **sloppy 但 cold 的边界不构成债**——动它的成本（引入新 sub-state）比现状高
- 常见误解：
  - 误判 1：以为"让 `GetComponentSpan<T>` 在 chunked 下也工作"是安全化——反向，是放大了撒谎的范围
  - 误判 2：以为"集中 bump 函数把纪律从 8 减到 1"——数学错误，递增点数量没变
  - 误判 3：把"flat 模式保留"算成债——不是，是显式性能选择，见 `kb-ecs-comparison.md:55`

## 入口

- 审视现状先看：
  - `src/MiniArch/Core/Archetype.Storage.cs`：dual-mode 所有路径
  - `src/MiniArch/Core/Archetype.cs`：字段定义 + `IsChunked`
  - `src/MiniArch/Core/ChunkView.cs`：正确的公共抽象
  - `src/MiniArch/Core/WorldStateSnapshot.cs`：snapshot 跨模式翻译
- 修 bug / 防回归先看：
  - `.knowledge/kb-code-review-findings.md:45`：同族 bug 已咬过的历史
  - `.knowledge/kb-ecs-comparison.md:55`：性能 baseline 不能回吐
- 偿还债务按本页 §决策 表逐项做

## 坑点

- 历史上容易出问题的地方：
  - `OrderedComponentEnumerator` 因调用 chunked-throwing API 被咬过（已修，但合同不静态强制）
  - 2026-06-13 `EnsureCapacity` 模式切换 bug：靠 `AddEntity = AllocateRows + WriteEntityAt` 单次读取防住，靠纪律不靠结构
  - `_flatEntitiesGeneration` 漏 bump 静默污染 query 热路径，无 assert 抓得住
- 容易误判的地方：
  - 以为集中 bump 函数能消除纪律（不能，advisor 修正）
  - 以为 token 化能消除纪律（不能真正消除，blast radius 还大）
  - 以为让 throwing API "在 chunked 下也工作" 是安全化（是放大谎言）
- 改这里时要特别小心：
  - **任何让 flat `ChunkView.GetSpan<T>()` 走 generic segmented abstraction 的改动**——会杀掉 `kb-ecs-comparison` 记录的 +70~74% 优势
  - **拷贝原语加 bounds check / virtual call / 丢 `CopySmall` inlining**
  - **过度 invalidate `_cachedFlatEntities`** → query / order / snapshot 路径触发额外 flatten + 分配
  - **benchmark harness 改走 ChunkView 改变 benchmark 自身** → 失去对比基准，必须 before/after 同一访问模式

## 验证门禁（每项偿还后必跑）

按 AGENTS.md §5：

```
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

阈值：Movement ≥1210 rounds/s、Attack ≥767 rounds/s、内存不涨、不崩。

**额外** 须跑 `dotnet run -c Release --project tools/perf/GameTickSim.Perf` 守护 flat 模式 +70~74% vs Arch 的优势（`kb-ecs-comparison.md` 数据）。

第 2 项（删 ThrowIfChunked API）还要加针对性测试：
- non-chunked → single segment 晋升后 query refresh
- 多 segment 增长
- 4 种 src×dst copy 模式组合
- snapshot restore：flat→chunked / chunked→chunked
- 每个 entity mutation 路径后的 cache rebuild