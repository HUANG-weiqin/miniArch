# Frame Read Models Implementation Plan

> **For execution agent:** 本计划必须按任务顺序执行。先做 ValueLab 证据，不允许在通过判定前修改 `src/MiniArch/` 公共 API。实现阶段使用独立 worktree：`.worktrees/frame-read-models/`。

**Goal:** 在 ECS Core 外验证 Snapshot 期间使用的帧内派生索引是否值得产品化：Build 一次，随后几万次只读查询，把 `Q × N` 降为 `N + Q × K`。

**Architecture:** 原型只放在 `tools/perf/FrameReadModels.ValueLab/`，从预热后的 `World.Query(...).GetChunks()` 与 `ChunkView.GetSpan<T>()` 读取事实；不 hook mutation，不做 freshness tracking，不修改 `World/Archetype/QueryCache/CommandStream`。比较 A entity-array baseline、B linked rows、C compact/CSR，并验证 Entity-only 与 chunk-row 组件读取两条消费路径。

**Tech Stack:** .NET 8 / C# / MiniArch public API / Release-only perf harness / embedded correctness assertions / local git checkpoints。

---

## 0. 已确认输入与完成定义

### 已确认 workload

- 常见索引实体数：几万；峰值约 100 万。
- 每帧查询：几万次。
- 单次命中：通常几个到几十个；极端几百到上千。
- 约一半查询只需要 `Entity`；另一半需要读 `Position`、`Health` 等组件。
- Snapshot 查询阶段无组件修改或结构修改。

### 产品边界

- 强 Go 方向，但必须先做 ValueLab 与物理布局验证。
- 不直接发布完整关系代数 API。
- ValueLab 未过线，不进入 `src/MiniArch/`。
- 不使用 LINQ、`IEnumerable`、捕获 lambda、逐帧集合分配。
- 操作器必须是具体泛型 struct，经 constrained generic call 保留 inline 机会。
- `FrameLookup` 不复制组件值，只保存 Snapshot 行位置。
- 旧 `BucketView` / span / enumerator 在 Clear/Rebuild/Grow 后全部失效；实验代码要在文档和断言中体现这个契约。

### Go 门槛

1. 高水位稳定后 0 B/op。
2. Frame DSL 相比 `RawSameContainer` 慢不超过约 3%。
3. 真实 `N/Q/K` 下，总成本相对最佳适用基线至少快约 15%。
4. chunk-row 组件读取相对 `Entity + World.Access/GetRef` 有稳定可重复收益。
5. break-even Q 明显低于真实几万次查询。
6. 100 万实体场景无异常内存增长、崩溃或灾难性扩容尖峰。
7. 同 key 顺序、Entity 读取、chunk-row 读取结果完全一致。

### 判死线

- 只打赢 `Dictionary<TKey, List<Entity>>`。
- DSL 税超过 3%。
- 稳态仍分配。
- chunk-row 路径没有胜过 Entity 随机定位。
- compact CSR 的额外扫描成本直到不现实的 Q 才能摊平。
- 内存随历史峰值不可接受且无显式容量管理办法。
- NoGrow 暴露部分结果。
- 为实现它需要侵入 Core。

---

## 1. 文件计划

### 新增

- `tools/perf/FrameReadModels.ValueLab/FrameReadModels.ValueLab.csproj`
- `tools/perf/FrameReadModels.ValueLab/Program.cs`
- `tools/perf/FrameReadModels.ValueLab/FrameReadModels.cs`
- `tools/perf/FrameReadModels.ValueLab/FrameReadModelLayouts.cs`
- `tools/perf/FrameReadModels.ValueLab/FrameReadModelCorrectness.cs`
- `tools/perf/FrameReadModels.ValueLab/FrameReadModelBenchmarks.cs`
- `tools/perf/FrameReadModels.ValueLab/README.md`
- `docs/plans/2026-07-11-frame-read-models-report.md`
- `.knowledge/kb-frame-read-models.md`

### 修改

- `.knowledge/INDEX.md`：把 `kb-frame-read-models.md` 挂到 MiniArch 用户 API / Snapshot 派生索引主题。

### 禁止修改

- `src/MiniArch/**`：ValueLab 阶段禁止。
- `tests/HeroPipeline.Tests/**`：禁止。
- `tools/perf/HeroComing.Perf/**`：禁止。
- 任何 baseline 文件：禁止运行或模拟 `--update-baseline`。

---

## 2. 任务与提交节点

### Task 0: Worktree 与基线确认

**Status:** 已在本会话完成，执行 agent 不应重复创建 worktree。

**Worktree:**

- Path: `E:\godot\arch\miniArch\.worktrees\frame-read-models`
- Branch: `exp/frame-read-models`
- Base commit: `4b4e977 rename: DestroyMany → Destroy(ReadOnlySpan<Entity>) overload`

**已运行命令:**

```bash
dotnet build -c Release miniArch.sln
dotnet test -c Release miniArch.sln
```

**已确认基线:**

- Build: 0 warnings, 0 errors。
- `MiniArch.Tests`: 913 passed。
- `HeroPipeline.Tests`: 5 passed。

**Commit:** 无代码变更，不提交。

---

### Task 1: 写入本计划并提交

**Files:**

- Create: `docs/plans/2026-07-11-frame-read-models.md`

**Steps:**

1. 保存本计划。
2. 检查 diff 只包含计划文件。
3. 提交。

**Verification:**

```bash
git status --short --branch
git diff -- docs/plans/2026-07-11-frame-read-models.md
git diff --check
```

**Commit:**

```bash
git add docs/plans/2026-07-11-frame-read-models.md
git commit -m "docs: plan frame read models value lab"
```

---

### Task 2: 搭建 ValueLab 项目骨架

**Goal:** 建立可运行、可断言、可测分配的控制台项目；默认命令完成 correctness + representative perf，不需要额外参数。

**Files:**

- Create: `tools/perf/FrameReadModels.ValueLab/FrameReadModels.ValueLab.csproj`
- Create: `tools/perf/FrameReadModels.ValueLab/Program.cs`
- Create: `tools/perf/FrameReadModels.ValueLab/README.md`

**Implementation notes:**

- `csproj` 引用 `../../../src/MiniArch/MiniArch.csproj`。
- `Program` 支持参数：
  - default：correctness + quick representative matrix。
  - `--correctness-only`：只跑正确性。
  - `--quick`：10K/50K/100K 小矩阵。
  - `--full`：包含 1M、Q=50K、极端 K 与容量场景。
  - `--n`, `--q`, `--distinct`, `--distribution`, `--where-hit`, `--arity`, `--layout`：专项定位。
- 所有性能命令必须拒绝 Debug：运行时检查 `#if DEBUG` 打印错误并返回非 0。
- 输出字段固定：scenario/layout/N/Q/K/distinct/distribution/whereHit/arity/buildMs/probeMs/entityConsumeMs/componentConsumeMs/totalMs/allocatedBytes/gen0/gen1/gen2/storedRows/distinctKeys/maxBucketSize/resized/retainedBytes/verdict。
- 不引入 BenchmarkDotNet；用固定 warmup + `Stopwatch.GetTimestamp()` + `GC.GetAllocatedBytesForCurrentThread()`，减少 harness 复杂度。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
```

Expected: 程序存在并输出 `Correctness: PASS`；此阶段可只有 smoke assertion。

**Commit:**

```bash
git add tools/perf/FrameReadModels.ValueLab
git commit -m "perf: add frame read models value lab skeleton"
```

---

### Task 3: 定义实验数据、operator 与 Rows DSL 雏形

**Goal:** 用最小 DSL 表达目标链路，且能和手写 `RawSameContainer` 公平比较。

**Files:**

- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModels.cs`
- Modify: `tools/perf/FrameReadModels.ValueLab/Program.cs`

**Types to add:**

```csharp
internal readonly record struct Position(int X, int Y);
internal readonly record struct Health(int Value);
internal readonly record struct Team(int Value);
internal readonly record struct Cell(int Value);

internal interface IFramePredicate<T1>
    where T1 : unmanaged
{
    bool Match(Entity entity, in T1 c1);
}

internal interface IFramePredicate<T1, T2>
    where T1 : unmanaged
    where T2 : unmanaged
{
    bool Match(Entity entity, in T1 c1, in T2 c2);
}

internal interface IFramePredicate<T1, T2, T3>
    where T1 : unmanaged
    where T2 : unmanaged
    where T3 : unmanaged
{
    bool Match(Entity entity, in T1 c1, in T2 c2, in T3 c3);
}

internal interface IFrameKeySelector<TKey, T1>
    where TKey : unmanaged, IEquatable<TKey>
    where T1 : unmanaged
{
    TKey Select(Entity entity, in T1 c1);
}

// 同样提供 2/3 component selector。
```

**DSL surface in lab only:**

```csharp
Rows<T1>.From(world, query).Where<TPredicate>().KeyBy<TKey, TSelector>().Into(lookup)
Rows<T1, T2>.From(world, query).Where<TPredicate>().KeyBy<TKey, TSelector>().Into(lookup)
Rows<T1, T2, T3>.From(world, query).Where<TPredicate>().KeyBy<TKey, TSelector>().Into(lookup)
```

**Rules:**

- `Where` 可选；无 where 走 `PassAll` struct。
- operator 不存入 interface 字段。
- 不捕获 lambda。
- `QueryDescription` 在测量前构造并 warm up：`world.Query(query).GetChunks()` 至少调用一次。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
```

Expected: DSL smoke 可 build 一个 entity-only lookup 并返回正确实体数。

**Commit:**

```bash
git add tools/perf/FrameReadModels.ValueLab
git commit -m "perf: add frame rows operator prototype"
```

---

### Task 4: 实现 Snapshot 行位置与统一读取接口

**Goal:** 所有候选布局先共享一个行位置表示，避免一开始同时存 `Entity` 和 row 造成概念重复。

**Files:**

- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModels.cs`
- Create/Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModelLayouts.cs`

**Types:**

```csharp
internal readonly struct RowRef
{
    public readonly int ChunkIndex;
    public readonly int RowIndex;
}

internal readonly struct ChunkRun
{
    public readonly int ChunkIndex;
    public readonly int Start;       // inclusive in layout entries, not chunk row
    public readonly int Length;
}

internal readonly struct BuildResult
{
    public readonly int MatchedRows;
    public readonly int StoredRows;
    public readonly int DistinctKeys;
    public readonly int MaxBucketSize;
    public readonly bool Resized;
}
```

**Lookup contract:**

- `Clear()` 保留容量。
- `TryBuildNoGrow` 容量不足返回 false，目标必须为空，不发布部分结果。
- `BuildAutoGrow` 容量不足先 grow scratch，再完整发布。
- Rebuild/Grow/Clear 后旧 view 失效；ValueLab 不暴露长期 view，只在 README/report 写契约。
- Entity 枚举：按 key 遍历匹配 Entity，Entity 从 `chunks[rowRef.ChunkIndex].GetEntities()[rowRef.RowIndex]` 得到。
- Chunk-row 枚举：按 key 返回 row refs/runs，让 caller 用 chunk spans 读组件。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
```

Expected: Entity 与 row-ref 能互相映射；empty world 不崩。

**Commit:**

```bash
git add tools/perf/FrameReadModels.ValueLab
git commit -m "perf: add frame lookup row reference contract"
```

---

### Task 5: 实现 A/B/C 三种物理布局

**Goal:** 用同一 workload 比较三种布局，不预判最终方案。

**Files:**

- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModelLayouts.cs`
- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModels.cs`

**Layout A: `EntityArrayLookup<TKey>`**

- key → contiguous `Entity` slice 或 entry chain。
- 作为简单用户基线，只保证 entity-only 快速读取。
- 不作为 chunk-row component 读取的最终候选。

**Layout B: `LinkedRowLookup<TKey>`**

- 单遍 build。
- open-address table：key → head/tail/count。
- row entries：`Key`, `RowRef`, `Next`。
- Build 快；同 key 读取可能跳跃。
- 同 key 顺序必须保持 Snapshot 扫描顺序：维护 tail append，不允许 head prepend 反转。

**Layout C: `CompactRowLookup<TKey>`**

- 两遍 CSR：count → prefix sum → stable scatter。
- key table 保存 key/count/start。
- row refs 连续存储，同 key 顺序稳定。
- Build 较贵，读取局部性最好。

**Specialized layout:**

- `DenseIntCompactLookup`：用于 Cell/Room/int 有界 key。用 dense counts + prefix sum + stable scatter。
- Entity-key direct-address 仅做 smoke/notes；除非时间充足，不进入默认 perf 矩阵，避免范围膨胀。

**Hash rules:**

- 不装箱 comparer/hasher。
- 仅使用 `EqualityComparer<TKey>.Default` 的泛型静态路径或 key 自身 `GetHashCode()`；如发现装箱/分配，改成 `IFrameKeyHasher<TKey>` struct。
- Clear hash/set 用 occupied-slot 清理或 generation stamps，不能只把 count 设 0 后读取旧槽。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
```

Expected: A/B/C/DenseInt 均通过相同 correctness matrix。

**Commit:**

```bash
git add tools/perf/FrameReadModels.ValueLab
git commit -m "perf: prototype frame lookup physical layouts"
```

---

### Task 6: 正确性矩阵

**Goal:** 把用户列出的 correctness 条目全部变成可执行 assertion。

**Files:**

- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModelCorrectness.cs`
- Modify: `tools/perf/FrameReadModels.ValueLab/Program.cs`

**Required cases:**

1. Empty world。
2. 单 key、多 key、缺失 key、default key。
3. 所有实体落入同一热点桶。
4. hash collision（自定义 `CollisionKey`，常量 hash code）。
5. 多 archetype。
6. flat 与 chunked/multi-segment storage（用足够多实体触发分段；也可调用已有 test hook 不行则只用 public create 触发）。
7. Where 0%、部分、100% 命中。
8. 同 key 扫描顺序。
9. Entity 与 chunk-row 两种读取结果完全一致。
10. AutoGrow 多次扩容。
11. Clear 后重建。
12. NoGrow 早期/晚期失败无部分结果。
13. 100 万实体容量和整数边界 smoke（仅 `--full` 默认可跳过；最终验证必须跑一次）。
14. Snapshot 结束后失效契约写入 README/report。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000
```

Expected: `Correctness: PASS`，失败时打印 case name、layout、seed。

**Commit:**

```bash
git add tools/perf/FrameReadModels.ValueLab
git commit -m "test: cover frame read model correctness matrix"
```

---

### Task 7: 性能基线与分段测量

**Goal:** 比较 ValueLab 候选与合理基线，测出 build/probe/consume/GC/retained capacity，而不是只给总时间。

**Files:**

- Modify: `tools/perf/FrameReadModels.ValueLab/FrameReadModelBenchmarks.cs`
- Modify: `tools/perf/FrameReadModels.ValueLab/Program.cs`

**Required baselines:**

1. `RawRepeatedScan`：预构造 QueryDescription 的最佳手写重复扫描。
2. `ComponentBucketQuery`：少量 key 的按需扫描策略。
3. `RawSameContainer`：手写 chunk loop 直接写入同一个 FrameLookup 原语，用于隔离 DSL 税。
4. `Dictionary<TKey, List<Entity>>`：普通用户基线，不作为唯一胜利证据。
5. Entity-only layout。
6. Entity + `World.Get<T>` / `World.Access/GetRef` 随机定位组件读取。
7. chunk-row selection 组件读取。
8. linked 与 compact CSR。

**Default quick matrix:**

- N: 10K, 50K, 100K。
- Q: 1, 8, 1K, 10K。
- K: 4, 16, 64，外加热点桶 512。
- distinct key: 64, 4096。
- distribution: uniform, clustered, hot。
- where hit: 10%, 50%, 100%。
- arity: 1, 2, 3。
- consume: entity-only, component, 50/50 mixed。

**Full matrix (`--full`):**

- N: 10K, 50K, 100K, 1M。
- Q: 1, 8, 1K, 10K, 50K。
- K: 4, 16, 64, 512, 1K, all。
- distinct key: 4, 64, 4096, high-cardinality。
- distribution: uniform, clustered, Zipf/hot bucket。
- where hit: 1%, 10%, 50%, 100%。
- capacity: warmed, first grow, severe underestimate, peak reuse。

**Measurement segments:**

- Query/GetChunks fixed cost。
- Build time。
- probe time。
- Entity-only consume。
- component consume。
- total frame cost。
- B/op and GC counts。
- retained capacity/bytes。
- AutoGrow spikes。
- max bucket and average bucket length。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
```

Expected: quick 在可接受时间内完成；full 至少完成一次并输出 Go/No-Go 初判。

**Commit:**

```bash
git add tools/perf/FrameReadModels.ValueLab
git commit -m "perf: measure frame read model layouts"
```

---

### Task 8: 结果报告与产品裁决

**Goal:** 把实验收束成董事长能读懂的 Go/No-Go 报告，同时保留工程证据。

**Files:**

- Create: `docs/plans/2026-07-11-frame-read-models-report.md`
- Modify: `tools/perf/FrameReadModels.ValueLab/README.md`

**Report sections:**

```markdown
# Frame Read Models ValueLab Report

## Verdict
- Go / No-Go / Conditional Go

## Plain-English Summary

## What Was Measured

## Correctness Evidence

## Performance Evidence

## Layout Decision
- A Entity-array baseline
- B Linked row entries
- C Compact CSR
- Dense int specialization

## Go/No-Go Gates

## Applicable Range

## Do-Not-Use Range

## Productization Recommendation

## Commands Run
```

**Decision rules:**

- 如果 C 过 Go 门槛：报告推荐下一阶段只产品化 compact/CSR + dense-int；B 作为可选 fallback 或删除。
- 如果 B/C 都不过：No-Go，不进 `src/MiniArch/`，保留 ValueLab 和知识页避免重复探索。
- 如果仅某个窄范围过线：Conditional Go，明确适用区间与禁用区间。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab
```

Expected: 默认命令输出与报告一致。

**Commit:**

```bash
git add docs/plans/2026-07-11-frame-read-models-report.md tools/perf/FrameReadModels.ValueLab/README.md
git commit -m "docs: report frame read models value lab verdict"
```

---

### Task 9: 知识库更新

**Goal:** 只记录实测结论，不把设计猜想写成事实。

**Files:**

- Create: `.knowledge/kb-frame-read-models.md`
- Modify: `.knowledge/INDEX.md`

**Knowledge page requirements:**

- 使用 `.knowledge/_template.md` 结构。
- Front matter 完整：`title`、`module`、`description`、`updated: 2026-07-11`。
- 先写结论，再写细节。
- 必须包含：
  - 适用区间。
  - 禁用区间。
  - 物理布局裁决。
  - Go/No-Go 门槛结果。
  - Snapshot 生命周期和失效契约。
  - 不能侵入 Core 的边界。
  - 后续产品化 TDD 建议（若 Go）。

**Verification:**

```bash
git diff -- .knowledge/INDEX.md .knowledge/kb-frame-read-models.md
git diff --check
```

**Commit:**

```bash
git add .knowledge/INDEX.md .knowledge/kb-frame-read-models.md
git commit -m "docs: record frame read models value lab findings"
```

---

### Task 10: 最终验证门禁

**Goal:** 用用户指定命令证明当前分支可交付。

**Commands:**

```bash
dotnet build -c Release miniArch.sln
dotnet test -c Release miniArch.sln
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
git diff --check
```

**Notes:**

- 本实验不修改 `src/MiniArch/`，理论上 HeroComing.Perf 不是架构变更强制项；但用户已要求最终命令，必须跑。
- 不允许运行 `--update-baseline`。
- 如果 HeroComing.Perf 低于阈值但本分支未改 core：先重跑一次确认噪音；仍失败则报告环境/噪音，不改 baseline。

**Final commit:**

若 Task 8/9 后还有报告补充或小修：

```bash
git add docs/plans/2026-07-11-frame-read-models-report.md tools/perf/FrameReadModels.ValueLab .knowledge/INDEX.md .knowledge/kb-frame-read-models.md
git commit -m "docs: finalize frame read models experiment"
```

---

## 3. 执行策略

1. 每个 Task 只做本 Task 文件，不顺手产品化。
2. 每个 Task 验证通过后本地提交。
3. 若出现编译/测试失败：先定位最小失败，不扩大改动。
4. 若性能数据触发判死线：停止优化冲动，写清 No-Go 或 Conditional Go。
5. 若为了过线想改 Core：停止，这本身就是 No-Go。
6. 最终汇报必须大白话：先说“值不值得做”，再说“证据是什么”，最后说“下一步最小动作”。

---

## 4. 董事长汇报模板

```markdown
董事长，结论是：[Go / No-Go / Conditional Go]。

一句话：我们把“每次问都扫全表”的活，变成“先整理一次索引，后面按桶拿”，在 [适用区间] 里能省 [数字]；但在 [禁用区间] 里不划算。

关键证据：
1. 正确性：覆盖 empty/multi-key/hot bucket/collision/multi-archetype/chunked/NoGrow/1M 等场景，全部通过。
2. 性能：最佳布局是 [B/C/DenseInt]；相比最佳基线 [快/慢] [百分比]；DSL 税 [百分比]；稳态分配 [数字]。
3. 组件读取：chunk-row 相比 Entity 随机 GetRef [有/无] 稳定收益。

建议：
- 如果 Go：下一步只产品化 [最小 API]，继续 TDD，不做 GroupBy/Join/TopK。
- 如果 No-Go：不要进库，保留 ValueLab 作为证据，避免以后重复投入。
```
