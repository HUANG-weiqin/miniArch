---
title: Knowledge Base Changelog
module: Meta
description: Chronological log of significant changes to the miniArch knowledge base and architecture
updated: 2026-07-06 (架构审阅知识库校准 + long cursor + ClearTransitionLog 服务器安全)
---
# Knowledge Base Changelog

> 这个页面只记录**重大架构变更和知识库校准事件**，供追溯。
> 当前状态请看 `INDEX.md` 和各 `kb-*.md` 页。


## 2026-07-06 新增 Change Tracking 知识库

- **新增 Change Tracking 子系统知识页**：`kb-change-tracking.md`，覆盖 `Track<T>` / `ChangeQuery<T>` / `ModifiedChunks` / `Transitions`。per-(archetype,column) long 版本号 + old→new signature transition log。Get=read/Set=write 契约。tracking-off 零回归（门禁绿）。详见 `kb-change-tracking.md`，设计决策见 `kb-design-rationale.md` §2.11，push-event 误判见 §3.10。
- **`kb-design-rationale.md`** §2 新增 2.11 Change Tracking 子系统决策，§3 新增 3.10 push-event 误判（原 3.10 防御性检查顺移至 3.11），front matter 日期同步更新。
- **`INDEX.md`** 模块地图新增 MiniArch.Core Change Tracking 行，快速入口新增"反应式 / 变更追踪"区。
- **`kb-changelog.md`** 本条目。

## 2026-07-06 ChangeQuery\<T\> fluent filter（With/Without/WithAny）

- **ChangeQuery\<T\> 增加 fluent filter（With/Without/WithAny）**：支持复合签名变更追踪。`QueryCache.Matches` 改 `internal`。Transitions 语义从"组件有无"升级为"签名匹配集进出"。详见 `kb-change-tracking.md`。

## 2026-07-06 long cursor + ClearTransitionLog — 服务器长运行安全

- **ChangeQuery._transitionCursor int→long**：修复 2B 回绕风险（100 ops/s 下约 8 个月溢出）。long 消除该风险，服务器可无限期连续运行。
- **World.ClearTransitionLog() 新增**：List.Clear() 复用内部数组（零 GC），cursor 通过 generation counter + clamp 自动 reset。用户需在每帧所有消费者 drain 完 transitions 后调用，以 bound 内存。
- 不调用 ClearTransitionLog 则 log 单调增长（100 ops/s ≈ 207 MB/天），调用则每帧归零。

## 2026-07-06 架构审阅知识库校准

- **Add/Set/Remove 真实语义校准**：`World.Add<T>` 当前是 ensure+overwrite（已存在时原地覆盖），不是 strict throw；`World.Set<T>` 缺失时抛异常；`World.Remove<T>` 缺失时 no-op。同步更新 `World.StructuralChange.cs` XML doc、`kb-design-rationale.md`、`kb-core-ecs.md`、`kb-architecture-review.md`。历史上 strict Add 曾被文档化，但 B1/B4 证明重复 Add 必须覆盖才能保持 Submit/Replay 收敛。
- **CommandStreamCore mutator 边界校准**：base class 不再公开 public throw mutator，也不再需要子类 `public new` 隐藏；9 个录制 mutator 只存在于 `CommandStream` / `ParallelCommandStream` sealed 子类，base 只提供 `protected *Core` helper 与消费 API。同步更新 `kb-command-stream.md`、`kb-architecture-review.md`。
- **FrameDelta.Validate 状态校准**：`FrameDelta.Validate` 已校验 component data size 与注册表 schema 一致性；`ReplayCore` 的剩余 P2 设计债是无事务回滚/目标 world allocator 兼容性，而非 size 校验缺失。同步更新 `kb-code-review-findings.md`。
- **HeroComing.Perf baseline 提醒**：不手工刷新 baseline；在 `kb-hero-pipeline-regression.md` 标注当前 2026-06-30 阈值可能落后最新 2026-07-05/06 测量，需人工确认后运行 `--update-baseline`。
- **安全猜想记录**：记录 `RestoreState` 不回退 `_archetypeSnapshot` 非 bug；capture 后新 archetype 已发布到当前 snapshot，restore 后空壳 archetype 不影响 query 正确性。

## 2026-07-04 持续审计：4 项代码优化 + 测试覆盖补充

- **World.Clone() 重复 XML doc 修复**：删除第一个冗余 `<summary>` 块（11 行），保留第二个正确的 ID-preserving 描述。
- **GetSegment value-return → ref 替代**：`WorldStateSnapshot.CopyFromChunked` 改用 `GetSegmentRef` 避免 struct 拷贝；删除零调用的 `GetSegment()` 值返回方法。
- **WorldSnapshot.Save 去双缓冲**：`bodyStream.ToArray()` → `GetBuffer()` + span 切片，Save 时减少一次完整 body 拷贝。
- **CommandStream.EmitHierarchyToDelta 零分配**：排序数组改用 `ArrayPool`；比较 lambda 替换为 `HierarchyComparer` 缓存单例。
- **测试补充**：新增 `ChildrenEnumerableTests.cs`（12 个测试覆盖死子实体过滤、重设父实体、零分配验证等边界）。
## 2026-07-04 拆掉 World.Replay + TryResolvePlaceholder 公共 API

- **`World.Replay(FrameDelta)` 删除**：已 `[Obsolete]` 一个版本，函数体只有一行 `ReplayCore(delta)`。CommandStream.Replay() 直接调 `_world.ReplayCore(delta)`。
- **`World.TryResolvePlaceholder()` 改为 `internal`**：`EntitySlot` + `CommandStream.Track()` 完全覆盖用例。CommandStream 内部仍通过 InternalsVisibleTo 使用。
- **`World.ReplayCore()` 改为 `internal`**（原 `private`）。
- **所有调用方迁移**：sample（LockstepSimulator / AuthorityMirrorSimulator / NetcodeVerification）改用 `host.Stream.Replay()` 或 `new CommandStream(world).Replay()`；测试全部迁移到 `new CommandStream(world).Replay()`。
- **删除 4 个 `TryResolvePlaceholder_*` 测试**（测试旧公共 API，EntitySlot 测试已覆盖等价功能）。
- **移除 `MiniArch.Tests.csproj` 的 CS0618 抑制**（不再有 obsoleted API）。
- **`FrameDelta.xml` 引用更新**：全部 `World.Replay` → `CommandStream.Replay`。
- **README Features 行更新**：`World.TryResolvePlaceholder()` → `EntitySlot` + `CommandStream.Track()`。
- **`kb-deferred-create-design.md` TryResolvePlaceholder 节标记为已移除**，指向 EntitySlot。
- 全 Build + 541 Tests + HeroComing.Perf 门禁通过（Movement 2021.5 / Attack 1248.0）。

## 2026-07-03 全量审阅落地（死代码清理 + deadcode.ps1 修复 + 过度防御改善）

### 洁癖全量清扫（YAGNI + static 化 + 命名诚实）

全量系统性扫描 44 个源文件（~8970 行），按"激进 YAGNI、纯函数优先、名字诚实"原则清扫。Build + 500 MiniArch.Tests + 5 HeroPipeline.Tests + HeroComing.Perf 门禁全部通过（Movement 2025.3、Attack 1244.9）。

- **YAGNI：移除 8 处内部/私有方法的无意义 null guard**：
  - `World.ReplayCore(delta)` — private，调用方 `Replay()` 已做 checked
  - `ComponentRegistry.GetOrCreate(type)` — internal class，caller 同程序集
  - `Archetype.CopyColumnsFrom(source, ...)` — internal method
  - `QueryCache.Create(world, ...)` — internal static
  - `WorldClone.Clone(source)` — internal class
  - `Signature(ComponentType[])` / `Signature.CreateNormalized(...)` — internal class
  - `Archetype(signature, ...)` — internal 构造
- **纯函数原则：5 个未用 this 的实例方法改为 `static`**：
  - `World.EnsurePlaceholderMap` — 仅操作参数 ref map
  - `World.GetComponentType<T>` — 仅访问 `Component<T>.ComponentType`（静态）
  - `World.ResolveComponentTypes` — 仅用 `ComponentRegistry.Shared`（静态）
  - `World.MoveEntityCore` — 仅调用参数上的方法
  - `World.CreateQueryComponentSet` — 仅用 `ComponentRegistry.Shared`（静态）
- **名字诚实：3 处重命名**：
  - `GetRequiredLocation` → `RequireLocation` — 该方法 throw（不存在即抛），"Get" 误导
  - `ArrayPoolUtil` → `ArrayPoolStack` — "Util" 模糊，实际实现栈式 PushPooled/GrowPooled
  - `SpanHelper` → `SpanSorting` — "Helper" 模糊，实际提供 SortAndDeduplicate + CombineHashCodes
- **未改动的（经评估后判定合理）**：
  - `ISpanFeeder` 单实现接口 — `Archetype` 和 `HashFeeder` 之间的清洁接缝，不移除
  - `[Conditional("DEBUG")]` 守卫方法 — Release 零开销模式
  - 3 个 bare `catch` in StructuralChange — 回滚补偿逻辑，意图明确
  - 公共 API 的 null guard（`Query.OrderByComponent`、`WorldSnapshot.Save/Load` 等）— 公共边界保留

### 洁癖全量清扫（第二轮 — 顾问检视补漏）

Apply advisor 轮次审阅发现，补充 3 项：
- **YAGNI：删除 `FrameDelta.OpDecoder.BackingBuffer`** — 零引用 dead public property
- **文件与类名同步**：`ArrayPoolUtil.cs` → `ArrayPoolStack.cs`、`SpanHelper.cs` → `SpanSorting.cs`
- **冗余 using/directive 清理**：`World.Create.Generated.cs` 移除无用 `using System.Buffers` + `using System.Runtime.CompilerServices`

全量代码审阅完结，逐项落地。所有 Debug + Release test 绿，HeroComing.Perf 门禁通过（Movement 2002.3、Attack 1217.0）。

- **`deadcode.ps1` 三处 bug 修复**：
  - Bug 1：`$def -split ':', 3` 在 Windows 盘符（`E:`)处断开 → 改为 `'^(.+):(\d+):(.*)$'` regex 解析
  - Bug 2：`rg -c` 输出正斜杠 vs `rg -n` 输出反斜杠 → 比较前统一为 `/`
  - Bug 3：脚本优先使用 `sg -n` 但 ast-grep 不支持该语法 → 移除 sg 分支，固定使用 rg
  - 修完后脚本能正确检测死代码（验证：插入故意死方法后被捕获）
- **旧 `CommandBuffer` 残留死代码删除**：
  - `World.cs: MaterializeReservedEntity(IReadOnlyList<RawComponentValue>, bool)`、`MaterializeReservedEntityCore`、`MaterializeReservedEntityDirect`、`BuildReplaySignature` — 整条 IReadOnlyList 链只有 3 个空组件调用方，替换为 `MaterializeEmptyReservedEntity`（2 行 inline helper）
  - `FrameDelta.cs: AddAdd/AddSet(byte[] 重载)`、`AddComponentData`、`WriteDataWithSize` — 热路径用 unsafe 变体，byte[] 重载零调用
  - `FrameDelta.cs: ReadData()` — 零调用（ReplayCore 用 ReadVarint+ReadBytes）
- **组件相关死代码删除**：
  - `ComponentType.cs` 的 `implicit operator int` 和 `explicit operator ComponentType(int)` — 零调用（全库用 `.Value`）
  - `ComponentRegistry.cs: GetRegistryHash()` + `_cachedRegistryHash` 字段 + `ComputeHash` — 零调用，无人读取该 hash
- **公共 API 简化**：
  - `World.TryGetEntityVersion()` — 零调用，`IsAlive` 已覆盖，按 YAGNI 删除
- **过度防御改善**：
  - `CommandStream.Clear(releaseReserved)` 的 if/else 双分支合并为单循环+条件
  - `Archetype.RebuildFlatEntities()` 移除冗余 `_cachedFlatEntitiesGeneration = -1`（`_flatEntitiesGeneration++` 已使缓存失效）
- **kb 同步**：`kb-changelog.md` 本条目

外部 review 确认的问题逐项落地，全部 Debug+Release test 绿，HeroComing.Perf 门禁通过（Movement 1941.8、Attack 1208.9）。

- **死代码删除**：`Core/InlineMap.cs` + `Core/OverflowPool.cs`（约 200 行）。二者构成互相引用的孤立簇，零实例化、零外部 caller、零测试。`deadcode.ps1` 因按符号名统计文本出现次数（自引用计数 >0）而漏报——已知检测盲区。纯删除，IL 仅减少。
- **API 重命名 `GetFirst<T>()` → `GetSingleton<T>()`**：旧 `GetFirst<T>` 用 `CreateArchetypeCache<T>` 缓存只命中**单组件 `{T}` 原型**，多组件原型里的实体被静默漏掉（语义与 XML doc "stores component T" 不符，且无测试覆盖该边界）。新 `GetSingleton<T>` 全量扫描 archetype，返回**唯一**含 T 的实体（singleton 语义：0 或 >1 抛异常），O(archetypes) 冷路径。同时删除随之变死的 `TryGetCreateArchetype<T>`。
- **测试**：`WorldLifecycleTests.cs` 删除 3 个 GetFirst 用例，新增 5 个 GetSingleton 用例，其中 `GetSingleton_finds_entity_in_multi_component_archetype` 作为回归用例固化旧 API 的缺陷已修复。
- **P4 防御性契约**：`CommandStream.CloneImpl` deferred 路径原来忽略 `TryGetPendingBatch` 返回值（依赖 `CreateDeferredImpl` 必设 `_lastCreated` 的隐式契约），改为显式检查并 fail-fast throw。运行时行为零变化，纯防回归。
- **文档校准（3 处陈旧/矛盾）**：`kb-architecture-review.md` StructuralChange "upsert/Add-Set alias" 改为 strict（指向 §2.9）；`kb-core-ecs.md` 坑点 "Set 静默添加" 改为 strict；`kb-core-ecs.md` 重复的 WorldStats 行去重。
- **设计原则一致性**：删除死代码兑现"激进 YAGNI"；GetFirst→GetSingleton 兑现"名字诚实"（singleton 实为 singleton）；显式契约兑现 fail-fast 风格。

## 2026-07-01 ComponentSchema 握手 API

新增 `ComponentSchema.Fingerprint()`——跨进程交换 FrameDelta 前的注册表兼容性校验，填补 FrameDelta wire 格式用裸整数 id 编码组件类型但无检测机制的缺口。

- **新增 `src/MiniArch/ComponentSchema.cs`**（public static 门面）：`Fingerprint()` 返回 32 字节 SHA-256 指纹（哈希内容 = `count + 每个类型 FullName（按 id 顺序）`，顺序敏感）。同一份二进制 lockstep 下注册顺序天然确定，指纹校验连接基线；运行时分叉由 per-frame `world.Checksum()` 检测。
- **`ComponentRegistry.cs` 新增 `GetFingerprint()`**（internal）：用 `IncrementalHash` 流式追加。
- **`FrameDelta.cs` 注释更新**：原"add a type-mapping header at the transport layer"改为指向 `ComponentSchema.Fingerprint`。
- **设计原则**：YAGNI（不做映射表协商、不做注册锁定）；纯函数（静态、无副作用）；概念唯一（一个指纹、一个表示）。
- **新增测试** `ComponentSchemaTests.cs`（7 个）：顺序一致/不一致、类型集不同、数量不同、返回长度、空注册表、逐步注册时指纹实时变化。
- **kb 同步**：`kb-lockstep-playbook.md`（新增步骤 0）、`kb-core-ecs.md`（架构段 + 用户 API 表）。

## 2026-07-01 代码硬化（7 个低垂果实）

完整的代码硬化执行计划见 `docs/internal/plans/2026-07-01-miniarch-codebase-hardening-plan.md`。
所有 7 个 Task 已完成，Debug + Release dotnet test 全绿。

- **Task 1 — 整数 isqrt**：`HomingBulletSteerSystem` 的 `Math.Sqrt` 替换为纯整数 `IntMath.Isqrt`，demo 的 1000 帧 checksum 现在真正跨硬件确定。新增 `IntMathTests.cs`（13 边界用例）。
- **Task 2 — FrameDelta wire 预算**：`MaxFrameBytes`（16 MiB）和 `MaxOpsPerFrame`（1M）在 Deserialize 入口和 OpDecoder 循环中 fail-fast，关闭 wire OOM 攻击面。新增 2 个 `[Fact]`。
- **Task 3 — `#if DEBUG` → `[Conditional("DEBUG")]`**：8 个 `#if DEBUG` 全部转换为 `[Conditional]` 方法（`ValidateAlive`、`AssertValidRow`、`AssertPositiveElementSize` 等）。Zero `#if DEBUG` 残留。
- **Task 4 — `Entity.IsPlaceholder`**：新增 `IsPlaceholder`（Id==-1, Version>=0）和 `IsUnmappedSentinel`（Id==-1, Version<0）属性。9 处 placeholder 检测站替换为 `entity.IsPlaceholder`。
- **Task 5 — WorldSnapshot CRC32**：格式版本 3→4，Save 附加 `Crc32.HashToUInt32` 尾部校验；Load 对 v4 验证 CRC、v3 向后兼容。新增 `System.IO.Hashing` 依赖 + 3 个 `[Fact]`。
- **Task 6 — FsCheck PBT**：集成 FsCheck 3.0，`Snapshot_roundtrip_preserves_canonical_checksum` 属性（MaxTest=200）通过。generator 覆盖 Position/Velocity/Health 组合。
- **Task 7 — SpanFeeder struct 接口**：`delegate void SpanFeeder` 替换为 `ISpanFeeder` struct 接口 + `HashFeeder` 特化结构，checksum 路径闭包分配消除。`FeedColumnData`/`FeedRowData` 改为泛型 `ref TFeeder`。


外部 review 出的三项高优改动全部落地（A1 + E1/E2 + B3）：

- **A1：`MiniArch.Core.Query` → `MiniArch.Core.QueryCache`**（internal 重命名，免门禁）。消除 public struct `MiniArch.Query` 与 internal class `Core.Query` 的命名空间碰撞。涉及 `Core/Query.cs` 类定义、`Core/QueryIterators.cs`、`Query.cs` facade、`World.QueryCache.cs`、`World.cs` 字段类型、19 个 test/benchmark 文件的 `using MiniQuery = ...` 别名。
- **E1+E2：`WorldStateSnapshot` 生命周期 + rollback 池**。原 `_stateSnapshotSpare` 单 slot 改为 `Stack<WorldStateSnapshot>` 池；新增 `IsRecycled` 公共属性；`RestoreState` 对已 recycled 的 snapshot 抛 `InvalidOperationException`（修复原静默状态污染 bug）；支持 GGPO 多帧深度回滚窗口稳态零 GC。新增 4 个测试覆盖：recycled 标志、double-restore throw、多帧乱序 restore、稳态池复用。
- **B3：`IChunkForEach` 接口**（`src/MiniArch/Query.cs:196`）。新增 `Query.ForEachChunk<TForEach>(ref TForEach)` 和 `ForEachChunkParallel<TForEach>(TForEach)`，零分配 + JIT 特化去虚化。保留原 delegate API。同步重构：抽出 `BuildEntityRangePartitions` helper 让 delegate/IChunkForEach 两套并行入口共用 partitioning。
- **`EntityInfo` 从公共 API 删除**：`RowIndex` 对用户无用（`Archetype` 是 internal 字段且无意义），改 `internal`。`World.TryGetEntityVersion(Entity, out int)` 作为公共替代。`TryGetLocation` 改 `internal`。涉及 `EntityInfo.cs`、`World.EntityLifecycle.cs`、`docs/README.md`、`kb-core-ecs.md`。
- **CHANGELOG**：新增 2.2.0 条目。
- **kb 同步**：`kb-architecture-review.md`（§4b 新增"回滚快照池"、§6 Query 系统更新、P3b 新增"已修复"段）、`kb-core-ecs.md`（用户 API 分层表加 IChunkForEach 行、命名说明改为 QueryCache）、`kb-snapshot-persistence.md`（WorldStateSnapshot 生命周期段全新）、`kb-parallel-query.md`（API 段加 IChunkForEach、决策段重写、不做的事表更新）。

## 2026-06-30 第二轮 agent 反馈修复

基于 6 个 agent 再次审计的反馈，做了以下增量修复：

- **阈值对齐**：Movement 阈值 1209→1210（1512.5 × 80% = 1210.0，此前 1209 是舍入误差）。涉及 AGENTS.md、CONTRIBUTING.md、kb-hero-pipeline-regression.md、kb-glossary.md。
- **`kb-hero-pipeline-regression.md` 加"如果失败"段**：门禁失败时直接给出 profiling 命令和热点路径索引。
- **`kb-profiling-workflow.md` 脆路径修复**：硬编码 DLL 路径改为 `dotnet run --project`；加交叉链接到 `kb-cache-optimization.md` 热路径分析表和 `kb-query-invalidation.md`。
- **`kb-architecture-review.md` O1-O5 加链接**：每条可优化点现在指向对应的 kb 页。
- **`kb-repo-overview.md` 加 Quickstart**：构建/测试/门禁命令的速查。
- **`INDEX.md` 加 `_template.md` 引用**：新增知识页时提示先看模板。
- **`kb-core-ecs.md` Destroy+Create 坑点改善**：加结论（当前线程安全/理论风险）、加 repro 方向、加测试文件引用（缺用例`Destroy_then_Create_same_frame_recycles_id_with_incremented_version`）。
- **`kb-glossary.md` 加 Tag 条目**：明确 MiniArch 中标签就是零大小组件，`With<T>()` 即可查询。
- **`kb-chunk-storage.md` Storage Invariants 加测试映射列**：每个不变量关联可能失败的测试文件。

## 2026-06-30 知识库结构优化

基于 6 个不同角色 agent（新人/Bug fix/Perf/Feature/Lockstep/Refactor）的视角审计，做了以下结构调整：

- **`kb-cache-optimization` 全文重写**：删除已废弃的 CommandBuffer/TryGetArchetype 优化段（P7/P9/P10/P12/P13/P14/P15），保留当前有效内容，重组为主题分组而非 P 编号。
- **changelog 从 INDEX.md 拆出到本页**：INDEX 只保留模块地图和快速入口，减少导航噪音。
- **新建 `kb-perf-harnesses.md`**：4 套 perf harness 的消歧矩阵（PipelineBenchmarkTests / HeroComing.Perf / SubmitAndSnapshotAsync / GameTickSim）。
- **新建 `kb-lockstep-playbook.md`**：端到端帧同步 spine 页，整合 5 个碎片化页面的导航。
- **新建 `kb-glossary.md`**：术语表（GGPO/SoA/LEB128/Tier 等）。
- **去冗余**：World partial 文件列表权威源只在 `kb-architecture-review.md`，其他页改为链接；Merge 历史只在 `kb-frame-delta-merge.md`。
- **跨链接修复**：`kb-command-stream.md` → `kb-frame-delta-merge.md` / `kb-deferred-create-design.md` 互链；perf 页面互联。
- **合并墓碑页**：`kb-debug-metrics.md` 合并到 `kb-architecture-review.md` 已删除子系统段。
- **`kb-core-ecs.md` 补坑点**：同帧 `World.Destroy + World.Create` 的 id 回收/version 一致性。
- **`kb-chunk-storage.md` 加 Storage Invariants 集中段**。

## 2026-06-30 全库知识库校准

本次全库审阅修正了 kb 文档落后于代码演进的漂移，全部为 .knowledge/ 文档改动，零 IL 差异：

- **路径漂移（10 个 kb 页）**：`shared/MiniArch.SharedInfrastructure/` → `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/`；`scripts/` → `tools/scripts/`；`perf/` → `tools/perf/`；`docs/plans/` → `docs/internal/plans/`；`benchmarks\` → `tests\`。涉及 kb-repo-overview / kb-profiling-workflow / kb-throughput-workflow / kb-gameticksim-scenarios / kb-commandstream-game-perf / kb-test-workflow / kb-cache-optimization / kb-ecs-comparison。
- **`kb-query-invalidation` 重大重写**：原正文声称 `AppendNewArchetypes` 是"全量重建"，与代码不符（`Query.cs:190` 是 append-only 增量扫描，从 `_lastArchetypeCount` 起）。front matter 描述原本正确但正文整段错了。改为正确的两段式增量失效描述。
- **`kb-user-api-layering` 重大重写**：原描述 `MiniArch.Core.Query.Create(...)` 公开 advanced 入口、`MiniArch.Query.Advanced` 暴露 Core.Query、`EachSpan` API 等均与现状不符（Core.Query 是 internal；Advanced 是 internal；EachSpan 已删除）。改为基于现状的 facade 描述。
- **`kb-test-workflow` 测试文件名**：`CommandBufferTests.cs` → `CommandStreamTests.cs`；补充缺失的 `WorldStatsTests.cs`、`ParallelQueryTests.cs`、`FrameDeltaDeterminismTests.cs`、`CommandBufferGamePerfTests.cs`。
- **`kb-cache-optimization`**：删除已不存在的内部 `Chunk` struct 引用（实际只有 public `ChunkView`）。
- **`kb-ecs-comparison`**：失效机制行从"全量重建"改为"两段式增量"，并加交叉引用指向 `kb-query-invalidation`。
- **`kb-command-stream`**：`EnsureReplayReservation` 行号 418 → 451。
- **残留漂移修复**：`kb-architecture-review` partial 计数 6→7 + 行号修正；`kb-command-stream` ReplayCore 行号 450→481 + 测试名修正；`kb-core-ecs` 补 World.SnapshotBridge/Checksum + Archetype.TestHooks 到 partial 列表。

> 教训：`kb-architecture-review.md` 末尾警告的"曾经存在的 kb 文档落后于代码演进"在本次审阅中被实证——多页错误描述持续了 1–3 个月。后续架构变更必须同步更新对应 kb 页（AGENTS.md §4 已强制）。
> 同日新建 `kb-checksum.md`（从 kb-snapshot-persistence 拆出——后合并回）、`kb-frame-delta-merge.md`（后合并回 kb-command-stream），补全知识覆盖空白。

## 2026-06-30 文档单一事实来源收敛

- **性能阈值对齐**：AGENTS.md 与 CONTRIBUTING.md 的回归阈值统一指向 `kb-hero-pipeline-regression.md` 的 80% baseline（Movement ≥1210 / Attack ≥767 rounds/s，后修正为精确值 1210）。此前 AGENTS.md 写 1407/854、CONTRIBUTING.md 写 866/200 均已过期。
- **回滚路径文档纠偏**：README/AGENTS 历史把 `World.Clone()` 推荐为回滚方案，但 2026-06-29 起真正的高频回滚路径是 `CaptureState/RestoreState`。现 README Frame-Sync 示例、Features、When-to-Use 表、`docs/comparison.md`、`docs/README.md`、`World.Clone()` XML doc 均已区分二者职责。`World.Clone()` 现定位为"分支/独立副本"。
- **`kb-snapshot-persistence` 补 WorldStateSnapshot 段**：澄清三套状态复制机制（WorldSnapshot / WorldClone / WorldStateSnapshot）职责正交。

## 2026-06-29 DeferredEntities flag

- **`CommandStream.DeferredEntities` flag**：`false`（默认）时 `Create()`/`Clone()` 分配 real id（单机）；`true` 时返回 placeholder（多 host lockstep）。`Snapshot()` 按 flag 输出 placeholder delta 或 real-id delta。`SubmitAndSnapshotAsync()` 始终输出 real-id delta。
- **删除 `CreateImmediate()` / `CloneImmediate()`**：公共 API 和 `ICommandRecorder` 接口同步移除。`DeferredEntities=false` 时 `Create()`/`Clone()` 即原 immediate 行为。
- **`Snapshot()` 不再泄漏 host world id**：`DeferredEntities=true` 时跳过 `ResolveDeferredCreates()`，delta 保留 placeholder。
- **ReplayCore placeholder→local 映射**：`_replayPlaceholderMap` 按 seq 索引，每帧 `mapLen=0` 重置防 stale。`ResolveReplayEntity` 加 bounds check。
- **`_replayPlaceholderMapLen` 字段删除**：mapLen 现在是 ReplayCore 局部变量，不复用跨帧。
- **WorldSnapshot free list 持久化**：格式 v3 直接序列化 free list 数组到流末尾，不再调用 RebuildFreeIdStack。`WorldClone` 改为 `CopyFreeIdsFrom`。新增 `Save_load_preserves_free_id_allocation_order` 验证测试。
- **`WorldStateSnapshot.cs` 接入 World**：`World.CaptureState()` / `World.RestoreState()` 在稳态零分配地备份/恢复 Records、FreeIds、per-archetype 数据（非 chunked + chunked）、Hierarchy。`_createArchetypeCacheGeneration++` 使 query cache 失效。4 个端到端测试验证：state preservation、deterministic ids、idempotency、cache invalidation。
- **XML docs 明确区分 `WorldSnapshot` vs `WorldStateSnapshot`**：`WorldSnapshot` 的 doc 写明"NOT for in-memory rollback"，`WorldStateSnapshot` 的 doc 写明"NOT for persistence/network"。`WorldStateSnapshot` 改用 `int[] FreeIds + int[] FreeIdVersions` 避免依赖 `World.RecycledEntity`（private 嵌套类型）。
- **Tier 1 完整实现**：`World.CaptureState()` / `World.RestoreState()` 通过 Array.Copy 在稳态零分配地备份/恢复 Records、FreeIds、per-archetype 数据（非 chunked + chunked）、Hierarchy。`_createArchetypeCacheGeneration++` 使 query cache 失效。预测帧创建的空 archetype count 被置零。4 个端到端测试验证：state preservation、deterministic ids、idempotency、cache invalidation。
- **死代码清理 + Delta 确定性排序**：删除 `RebuildFreeIdStack()`（v3 格式后零调用点），kb-snapshot-persistence 移除其引用。`EmitHierarchyToDelta` 按 `child.Id` 排序输出，消除 Dictionary 迭代顺序导致的 delta 字节级非确定性（不影响 entity ID）。

## 2026-06-29 Checksum 加固

- **Archetype 存储零填充**：`CreateStorage` 从 `GC.AllocateUninitializedArray` 改为 `GC.AllocateArray`（零初始化），消除组件 struct padding 字节中的未定义值导致跨 peer checksum 不一致的风险。
- **CanonicalChecksum 加入 free list**：`ComputeCanonicalChecksum` 在实体/组件/层级之后追加 free list 中每个 (Id, Version) 对。此前 canonical checksum 仅覆盖活实体，若两 host 因 bug 出现不同 free list 但活实体一致时无法检测。
- **暴露 FreeList 内部 API**：`World.RecycledEntity` 从 `private` 改为 `internal`，新增 `World.FreeList` 属性供 checksum 访问。

## 2026-06-28 CommandStream API 统一

- **CommandStream 并行 API 统一**：删除 `SetConcurrent`/`AddConcurrent`/`RemoveConcurrent` 专用方法，`ParallelRecording=true` 时所有 Record API 透明切换为并发实现。单线程零退化。

## 2026-06-22 全库审阅

- **修复 CommandStream.BuildFromFrozen bug**：`EmitHierarchyToDelta` 被重复调用两次，导致 FrameDelta 中 AddChild/RemoveChild 操作重复写入
- **知识库全面更新**：修正过时文件路径（Ecs/Query.cs → Query.cs）、删除不存在的文件引用（SpanQueryIterators.cs、ChunkViewTyped.cs）、修复旧字段名引用（_archetypeVersion → _createArchetypeCacheGeneration）

## 2026-06-08 大重构

- **World 拆分为 partial 文件**：World.cs + World.EntityLifecycle.cs + World.Create.Generated.cs + World.QueryCache.cs + World.StructuralChange.cs
- **Archetype 拆分为 partial 文件**：Archetype.cs（字段/metadata）+ Archetype.Storage.cs（存储操作）
- **Edge cache 使用直索引 `Archetype?[]`**（按 componentId 直索引，O(1) 查找）
- **DebugMetrics 整个子系统已删除**（kb-debug-metrics.md 保留作为历史记录）
- **FrameDelta 热路径 struct 大幅缩小**（Movement +50% / Attack +29%）
- **ComponentMask 扩展为 512-bit**（8 × `ulong`），覆盖 component id 0..511 的快速匹配
- **新增分段存储模式**：Archetype 超过阈值后自动切换为多 Segment 模式（详见 `kb-chunk-storage.md`）


