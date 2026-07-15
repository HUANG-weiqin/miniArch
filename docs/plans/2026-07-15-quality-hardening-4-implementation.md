# MiniArch 4.0 Quality Hardening Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 在不牺牲真实高速 unsafe API 的前提下，修复已证实的存储、提交原子性、async ownership 与诊断问题，把 MiniArch 4.0 的正确性、性能、确定性、并发、API、可维护性、测试和文档证据分别提升到 8 分以上。

**Architecture:** 采用局部 commit-last 存储迁移与 CommandStream 两阶段 `Preflight → Apply`。公共 API 只做已确认的局部破坏；CommandStream 行为修复全部通过后再机械拆分 partial class，并以拆分前 Release 热路径数据和 JIT/IL 证据守住内联与吞吐。

**Tech Stack:** C#/.NET 9、xUnit、PowerShell、`CommandStream.Profile`、`HeroComing.Perf`、MiniArch/lockstep soak、JIT disassembly。

---

## 执行约束

- 工作目录固定为 `E:\godot\arch\miniArch\.worktrees\quality-hardening-4`，分支固定为 `codex/quality-hardening-4`。
- 所有行为修复严格执行 RED → 最小 GREEN → focused test → full Release test。
- 性能相关命令一律带 `-c Release`；禁止运行 `--update-baseline`。
- 任何一个提交只做一种事情。重构提交不得夹带行为改动，文档提交不得掩盖未修复 bug。
- `CommandStreamCore` 拆分前必须记录本 commit 的性能和 JIT/IL 锚点；每拆一个 partial 文件立即复测，不能等全部拆完再看总结果。
- 用户契约错误的目标是“首次 alive-world mutation 前失败”；不承诺 OOM、CLR 灾难、unsafe 误用或内部 invariant 已损坏时的 World 回滚。
- 如果 HeroComing Movement < 1642 rounds/s、Attack < 997 rounds/s，或内存持续增长，当前 runtime 改动不得闭环。

## Task 1：冻结起点与性能证据

**Files:**

- Create: `docs/plans/2026-07-15-quality-hardening-4-evidence.md`
- Verify: `tools/perf/CommandStream.Profile/Program.cs`
- Verify: `tools/perf/HeroComing.Perf/Program.cs`

### Step 1：确认干净起点

Run:

```powershell
git status --short --branch
git log -3 --oneline
dotnet build -c Release --no-restore miniArch.sln
dotnet test -c Release --no-build miniArch.sln
```

Expected: 分支为 `codex/quality-hardening-4`；除计划提交外无未提交修改；build/test exit 0。

### Step 2：记录所有 CommandStream 场景

Run:

```powershell
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --list
$scenarios = 'existing-set','existing-add-remove','create-small4','create-duplicates','create-destroy','snapshot-only'
foreach ($scenario in $scenarios) {
    1..3 | ForEach-Object {
        dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario $scenario --warmup 3 --measure 10
    }
}
```

Expected: 每个场景三次完成、checksum 稳定、没有持续 heap 增长。把中位数与 `record%/submit%/snapshot%/clear%` 写入 evidence 文档，不写整段原始日志。

### Step 3：记录架构端到端基线

Run:

```powershell
1..3 | ForEach-Object {
    dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
}
```

Expected: 如 Attack 仍低于 997，证据文档明确记为“起点红灯”，不将其归因于本轮尚未实施的改动。

### Step 4：创建证据文档骨架

文档只保留：commit、机器/运行配置、每场景三次结果与中位数、失败门禁、后续 before/after 表。不要复制控制台全文。

### Step 5：提交

```powershell
git add docs/plans/2026-07-15-quality-hardening-4-evidence.md
git commit -m "docs: capture quality hardening baseline"
```

## Task 2：修复单字节 archetype 扩容溢出

**Files:**

- Modify: `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`
- Modify: `tests/MiniArch.Tests/Core/ArchetypeTests.cs`
- Modify: `src/MiniArch/Core/Archetype.cs`

### Step 1：写公开行为回归测试

在 `WorldLifecycleTests` 新增：

```csharp
[Fact]
public void BUG_single_byte_archetype_promotes_past_chunk_capacity()
{
    using var world = new World(chunkCapacity: 128);
    var entities = new Entity[129];

    for (var i = 0; i < entities.Length; i++)
        entities[i] = world.Create((byte)i);

    Assert.Equal(129, world.EntityCount);
    for (var i = 0; i < entities.Length; i++)
        Assert.Equal((byte)i, world.Get<byte>(entities[i]));
    world.ValidateInvariants();
}
```

同组覆盖 `bool` 和单字节 unmanaged tag，测试目标是 `chunkCapacity + 1`。

### Step 2：确认 RED

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~BUG_single_byte_archetype_promotes_past_chunk_capacity"
```

Expected: `OverflowException`；若不是该原因，停止并重新核对 witness。

### Step 3：给纯 helper 写边界测试

把容量计算 helper 调整为 `internal static` 以便 InternalsVisibleTo 测试，新增 1、2、`MaxSegCap` 附近和超大 `perEntityBytes` 用例。断言结果：

- `> 0`；
- 是 2 的幂；
- `<= MaxSegCap`；
- `result * perEntityBytes <= ArrayMaxLength`。

先运行 focused test，确认旧实现至少一个边界失败。

### Step 4：最小修复容量计算

在 `Archetype.ComputeSegmentEntityCapacity` 中先使用 `uint`/`ulong` 计算和 clamp，最后再转 `int`。禁止把 `0x80000000u` 直接 cast 为 `int`。

建议结构：

```csharp
var maxByArray = (uint)(ArrayMaxLength / perEntityBytes);
var rounded = BitOperations.RoundUpToPowerOf2(maxByArray);
var safeByArray = rounded == 0 || rounded > MaxSegCap
    ? (uint)MaxSegCap
    : rounded;
var requested = BitOperations.RoundUpToPowerOf2((uint)chunkCapacity);
return (int)Math.Min(requested, safeByArray);
```

实际实现还必须处理 `perEntityBytes > ArrayMaxLength` 和 `requested == 0`，并由测试固定行为。

### Step 5：验证 GREEN

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldLifecycleTests|FullyQualifiedName~ArchetypeTests"
dotnet test -c Release --no-build miniArch.sln
```

### Step 6：提交

```powershell
git add src/MiniArch/Core/Archetype.cs tests/MiniArch.Tests/Core/WorldLifecycleTests.cs tests/MiniArch.Tests/Core/ArchetypeTests.cs
git commit -m "fix: harden archetype segment capacity"
```

## Task 3：让 flat → chunked 与 direct Create 局部 commit-last

**Files:**

- Modify: `src/MiniArch/Core/Archetype.cs`
- Modify: `src/MiniArch/Core/World.EntityLifecycle.cs`
- Modify: `tests/MiniArch.Tests/Core/ArchetypeTests.cs`
- Modify: `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

### Step 1：固定 promotion 后数据完整性

新增测试创建多个不同尺寸组件，在 promotion 边界前后读取全部实体，并调用 `world.ValidateInvariants()`。测试名使用 `BUG_` 前缀，覆盖 entity 列、各 component 列和 row index。

### Step 2：先确认现有行为基线

当前测试可能直接 GREEN，因为这是异常安全结构性修复而不是另一条必现错误。记录这一点，不伪称 RED。已由 Task 2 的真实 RED 证明 promotion 路径可抛。

### Step 3：局部构造 segment 状态

重写 `ConvertToChunked`：

1. 计算 local offsets/count；
2. 分配 local `Segment[]`；
3. 将 flat entity/component 数据复制到 locals；
4. 全部成功后才赋 `_segments`、`_segmentCount`、`_columnByteOffsets`；
5. 最后清空 flat backing。

禁止在成功点前发布任一新 segment 字段。

### Step 4：direct Create 先保容量

在 `World.CreateInArchetype` 取得 entity id 和修改 records/free-list 前调用：

```csharp
archetype.EnsureCapacity(archetype.EntityCount + 1);
```

随后原 `AddEntity` 的二次检查应成为无操作；先不做额外 API 设计。

### Step 5：验证与提交

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~ArchetypeTests|FullyQualifiedName~WorldLifecycleTests"
dotnet test -c Release --no-build miniArch.sln
git add src/MiniArch/Core/Archetype.cs src/MiniArch/Core/World.EntityLifecycle.cs tests/MiniArch.Tests/Core/ArchetypeTests.cs tests/MiniArch.Tests/Core/WorldLifecycleTests.cs
git commit -m "refactor: commit archetype growth after preparation"
```

## Task 4：局部校准 unsafe API 与 4.0 surface

**Files:**

- Modify: `src/MiniArch/Core/ChunkView.cs`
- Modify: `src/MiniArch/Core/Query.cs`
- Modify: `src/MiniArch/Core/Entity.cs`
- Modify: `src/MiniArch/MiniArch.csproj`
- Modify: `tests/MiniArch.Tests/PublicApiBaseline.txt`
- Modify: `tests/MiniArch.Tests/PublicApiSentinelTests.cs`
- Modify: `tests/MiniArch.Tests/Core/ChunkColumnIndexTests.cs`
- Modify: `samples/HeroPipeline/Systems/ModifierApplySystem.cs`

### Step 1：先改 expected API baseline 并确认 RED

只改 expected surface：

- `GetComponentSpanAt<T>` → `UnsafeGetComponentSpanAt<T>`；
- 删除 public `Entity.IsUnmappedSentinel`；
- 版本改为 4.0.0 不改变 API sentinel 文本格式。

同步 `PublicApiBaseline.txt` 与 sentinel test 内嵌文本，然后运行：

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~PublicApiSentinelTests"
```

Expected: actual surface 与新 expected 不一致。

### Step 2：实施最小 surface 改动

- `ChunkView.GetComponentSpanAt<T>` 直接改名为 `UnsafeGetComponentSpanAt<T>`，不保留兼容别名。
- 更新 `Query.cs` 和 sample caller。
- `Entity.IsUnmappedSentinel` 改为 `internal`。
- `VersionPrefix` 改为 `4.0.0`。

### Step 3：添加 Debug-only 配对诊断

`UnsafeGetComponentSpanAt<T>` 在 `#if DEBUG` 下断言：view 初始化、column index 范围、实际 component type == `Component<T>.ComponentType`。Release 方法体不得增加类型检查和异常分支。

XML 明确：index 必须来自同一 ChunkView/archetype 对同一 `T` 的 `TryGetComponentIndex<T>`；错配、跨 structural change 或跨 archetype 重用均未定义。

### Step 4：验证 Debug 与 Release surface

```powershell
dotnet test -c Debug tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~ChunkColumnIndexTests"
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~PublicApiSentinelTests|FullyQualifiedName~ChunkColumnIndexTests"
dotnet build -c Release --no-restore miniArch.sln
```

Expected: Debug 错配由断言尽早暴露；Release 正确配对路径无新增 guard；public API 只有上述两项局部破坏。

### Step 5：提交

```powershell
git add src/MiniArch tests/MiniArch.Tests samples/HeroPipeline/Systems/ModifierApplySystem.cs
git commit -m "feat!: calibrate unsafe column API for 4.0"
```

## Task 5：ComponentStore 提交前预检

**Files:**

- Modify: `tests/MiniArch.Tests/Core/CommandStreamTests.cs`
- Modify: `src/MiniArch/Core/CommandStreamCore.cs`

### Step 1：写三个原子性 witness

分别覆盖 invalid Add、Set、Remove。每个测试先录制 pending create，再对 existing entity 录制必失败的 strict operation；保存提交前：

- `world.EntityCount`；
- existing entity 的 component 值/存在性；
- pending entity handle。

`Submit()` 抛出后断言：EntityCount 不变、pending 不 alive、existing 状态未变、`ValidateInvariants()` 通过。测试名：

```text
BUG_submit_preflights_invalid_add_before_materializing_pending
BUG_submit_preflights_invalid_set_before_materializing_pending
BUG_submit_preflights_invalid_remove_before_materializing_pending
```

### Step 2：确认 RED

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~BUG_submit_preflights_invalid_"
```

Expected: 当前 Submit 已 materialize pending，至少 EntityCount 断言失败。

### Step 3：写同 entity/type 顺序模拟测试

覆盖：

- absent: Add → Set → Remove，合法且最终 absent；
- present: Set → Remove → Add → Set，合法且最终 present/new value；
- absent: Add → Add，提交前失败且 World 不变；
- present: Remove → Remove，提交前失败且 World 不变。

### Step 4：实现可复用 scratch

在 `CommandStreamCore` 持有：

```csharp
private int[] _preflightGeneration = [];
private byte[] _preflightPresence = [];
private int _preflightEpoch;
```

按 max entity id 扩容；每个 typed store 使用新 epoch。首次遇到 entity 时用 World 实际状态初始化 present 位，后续同 store/同 entity 按 entry 顺序更新虚拟状态。稳态不得按提交创建 Dictionary 或 HashSet。

### Step 5：给 ComponentStore 增加只读 preflight virtual

在 abstract store 增加验证方法。typed store：

- set-only 保留独立循环；
- mixed path 按 Add/Set/Remove 更新 scratch；
- 抛错文本与 Apply 当前 strict 语义一致；
- placeholder/pending/stale 继续遵守 Prepare/Prune/folding 现有边界。

### Step 6：将 preflight 放到首次 mutation 前

`Submit()` 的目标顺序：

```text
PrepareStores
ResolveDeferredCreates / validate reservation ownership
Preflight component + hierarchy
AlignCancelledBatchFreeListOrder
MaterializeAllPending
ApplyHierarchy
ApplyComponentStores
ApplyDestroys
Clear
```

注意：reservation resolve/release 不是 alive-world mutation；失败由 `Clear(releaseReserved: true)` 归还。`AlignCancelledBatchFreeListOrder` 会触碰 allocator free-list，因此必须在所有用户契约 preflight 之后。

### Step 7：GREEN 与热路径即时检查

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandStreamTests"
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario existing-set --warmup 3 --measure 10
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario existing-add-remove --warmup 3 --measure 10
```

如果 `existing-set` 中位数相对 Task 1 下降 > 5%，先定位 `submit%`，不得进入下一任务。优先减少重复查找/错误的 per-submit allocation，不放松 preflight 语义。

### Step 8：提交

```powershell
git add src/MiniArch/Core/CommandStreamCore.cs tests/MiniArch.Tests/Core/CommandStreamTests.cs
git commit -m "fix: preflight component commands before submit"
```

## Task 6：Hierarchy 最终 overlay 预检

**Files:**

- Modify: `tests/MiniArch.Tests/Core/CommandStreamTests.cs`
- Modify: `src/MiniArch/Core/CommandStreamCore.cs`

### Step 1：写联合 intent cycle witness

构造两个或三个现有实体与一个 pending create，让本帧最终 `HierarchyByChild` overlay 形成环。录制阶段允许完成，`Submit()` 才抛；断言 pending 未 materialize、原 hierarchy 不变、EntityCount 不变。

如果某个候选场景已在录制时拒绝，换成“现有 World parent 链 + 本帧两个覆盖 intent”的组合，直到得到真实 Submit-time RED；不要为了测试删除录制期早失败保护。

### Step 2：确认 RED

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~BUG_submit_preflights_hierarchy_overlay_cycle"
```

Expected: 当前 ApplyHierarchy 在 pending materialize 后抛，原子性断言失败。

### Step 3：实现只读最终 overlay 解析

按 Apply 的 child-id 确定顺序遍历 `HierarchyByChild`：

- parent/child 在提交后必须存在；
- parent != child；
- Destroy 集合中的关系按现有语义跳过；
- 沿 parent 链优先读取本帧 overlay，缺失时读取 World 当前 parent；
- 遇到当前 child 即 cycle；
- 步数上界由活实体数 + pending 数限定，越界视为 invariant 错误。

不写 World，不提前 materialize placeholder。

### Step 4：验证

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandStreamTests|FullyQualifiedName~Hierarchy"
dotnet test -c Release --no-build miniArch.sln
```

### Step 5：提交

```powershell
git add src/MiniArch/Core/CommandStreamCore.cs tests/MiniArch.Tests/Core/CommandStreamTests.cs
git commit -m "fix: preflight hierarchy overlay before submit"
```

## Task 7：Async preflight 与 FrozenState ownership

**Files:**

- Modify: `tests/MiniArch.Tests/Core/CommandStreamTests.cs`
- Modify: `src/MiniArch/Core/CommandStreamCore.cs`

### Step 1：写两个 async 契约错误 witness

对 `SubmitAndSnapshotAsync` 和 `SubmitAndSnapshotIntoAsync` 各录制 pending create + invalid strict component op。Into 测试先用有效 delta 填充 target 并保存 bytes。

断言：

- API 在启动 worker 前同步抛；
- source World 不变；
- target bytes 不变；
- pending reservation 已释放；
- stream 下一次合法提交可复用。

测试名使用 `BUG_async_...`。

### Step 2：确认 RED

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~BUG_async_"
```

Expected: 当前 worker 已启动且 Apply 后失败；至少 World 或 target/ownership 断言失败。

### Step 3：重排 async active-state 流程

在 active `_frozen` 上完成：Prepare → resolve/validate reservations → component/hierarchy preflight。全部成功后才 `SwapOutState`。

`SwapOutState` 不再隐式 `ResolveDeferredCreates()`；它只做回收与状态交换。所有 caller 必须在调用前显式完成 resolve。

### Step 4：先登记 ownership，再 Apply

worker 创建后立即设置 `_pendingFrozen` / `_pendingTask`。同步 Apply 用 `try/catch`：

1. 捕获原异常；
2. 观察/等待 worker 完成；
3. Into 路径按已定义失败契约清理 target；
4. 保持 frozen 引用直到 worker 停止读取；
5. 重新抛原异常。

正常用户错误已在 preflight 拦截；该 catch 是内部异常兜底，不宣称 World 回滚。

### Step 5：验证 overlapping/reuse/target 测试

```powershell
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~SubmitAndSnapshot|FullyQualifiedName~BUG_async_|FullyQualifiedName~SwapOutState"
dotnet test -c Release --no-build miniArch.sln
```

### Step 6：提交

```powershell
git add src/MiniArch/Core/CommandStreamCore.cs tests/MiniArch.Tests/Core/CommandStreamTests.cs
git commit -m "fix: validate async submit before worker handoff"
```

## Task 8：Debug structural-change 计数异常安全

**Files:**

- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/World.StructuralChange.cs`
- Modify: `src/MiniArch/Core/World.EntityLifecycle.cs`
- Modify: `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`

### Step 1：写 Debug-only witness

暴露一个 `internal` Debug-only 只读诊断属性，测试先触发 duplicate Add 或其他 direct structural exception，再断言计数恢复为 0，并执行下一次合法 structural change。

测试体用 `#if DEBUG` 包围，名字：

```text
BUG_debug_structural_scope_recovers_after_exception
```

### Step 2：确认 Debug RED

```powershell
dotnet test -c Debug tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~BUG_debug_structural_scope_recovers_after_exception"
```

### Step 3：最小 try/finally 修复

所有 `BeginStructChange()`/`EndStructChange()` 成对入口用异常安全 scope。保证 Release 不引入保护分支：优先用 `#if DEBUG` 包围 debug-only try/finally 或可证明被 Conditional 消除的结构。

### Step 4：验证 Debug/Release

```powershell
dotnet test -c Debug tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~WorldStructuralChangeTests"
dotnet test -c Release --no-build miniArch.sln
```

### Step 5：提交

```powershell
git add src/MiniArch/Core/World.cs src/MiniArch/Core/World.StructuralChange.cs src/MiniArch/Core/World.EntityLifecycle.cs tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs
git commit -m "fix: restore debug structural scope after exceptions"
```

## Task 9：在拆分前锁定 CommandStream 热路径与内联锚点

**Files:**

- Modify: `docs/plans/2026-07-15-quality-hardening-4-evidence.md`
- Verify: `src/MiniArch/Core/CommandStreamCore.cs`

### Step 1：行为修复后全量 GREEN

```powershell
dotnet build -c Release --no-restore miniArch.sln
dotnet test -c Release --no-build miniArch.sln
```

未全绿不得开始拆分。

### Step 2：重新记录拆分前 CommandStream 基线

对六个场景各运行三次 `--warmup 3 --measure 10`，记录中位数和阶段占比。该组结果命名为 `pre-split`，不能复用 Task 1 的旧实现基线。

### Step 3：保存关键方法 IL hash

构建 Release 后，用反汇编工具提取并 hash 以下方法的 IL：

- `CommandStreamCore.Submit`
- `CommandStreamCore.PrepareStores`
- `CommandStreamCore.ApplyComponentStores`
- `ComponentStore<T>.Append`
- `ComponentStore<T>.ApplyToWorld`
- `CommandStream.Set<T>` / `ParallelCommandStream.Set<T>`

优先使用本机可用的 `ildasm`/`ilspycmd`；如果不可用，用一个只读临时 inspector 通过 `MethodBody.GetILAsByteArray()` 输出 method identity + SHA256。临时工具放 workspace 外，不提交。

### Step 4：记录 JIT 内联诊断

对 `existing-set` 和 `existing-add-remove` 启用 .NET JIT inline diagnostics/disassembly，确认最常用 record path 的调用关系。环境变量采用当前 .NET runtime 支持的 `DOTNET_JitDisasm`/`DOTNET_JitPrintInlinedMethods`；只保存关键摘要，不提交巨量 dump。

Expected: evidence 文档记录“哪些方法当时被内联/未内联”、IL hash 和 pre-split 中位数。

### Step 5：提交锚点证据

```powershell
git add docs/plans/2026-07-15-quality-hardening-4-evidence.md
git commit -m "docs: lock command stream pre-split evidence"
```

## Task 10：机械拆分 CommandStreamCore，逐步性能复测

**Files:**

- Modify: `src/MiniArch/Core/CommandStreamCore.cs`
- Create: `src/MiniArch/Core/CommandStreamCore.Submit.cs`
- Create: `src/MiniArch/Core/CommandStreamCore.ComponentStore.cs`
- Create: `src/MiniArch/Core/CommandStreamCore.Pending.cs`
- Create: `src/MiniArch/Core/CommandStreamCore.Hierarchy.cs`

### Step 1：每次只迁移一个闭合区域

顺序：

1. hierarchy；
2. pending；
3. component store；
4. submit/async。

只移动原文本到同一 `partial class`。不抽 helper、不改签名、不改属性、不改 `MethodImpl`、不改泛型约束、不改语句顺序。

### Step 2：每次迁移后的硬门禁

每个文件迁移后立即运行：

```powershell
dotnet build -c Release --no-restore miniArch.sln
dotnet test -c Release --no-build miniArch.sln
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario existing-set --warmup 3 --measure 10
dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario existing-add-remove --warmup 3 --measure 10
```

并重新计算 Task 9 的关键 IL hash。

Expected:

- IL hash 全部相同；
- inline diagnostics 的关键结果相同；
- 两个场景三次中位数相对 `pre-split` 不下降超过 3%；
- submit 阶段没有稳定性恶化。

只要 IL hash 改变，先视为拆分夹带了语义/编译差异并回退该步；不能用“噪声”解释。IL 相同而时间单次波动时再跑两次，以中位数判断。

### Step 3：全部拆完后六场景复测

对六个场景各跑三次，与 `pre-split` 比较。任一热场景稳定下降 > 3%，回退拆分并定位，不进入文档阶段。

### Step 4：提交纯迁移

```powershell
git add src/MiniArch/Core/CommandStreamCore*.cs
git commit -m "refactor: split command stream core partials"
```

该提交 diff 必须只表现为同类 partial 间移动。

## Task 11：以 Profile 证据优化热路径

**Files:**

- Modify only when measured: `src/MiniArch/Core/CommandStreamCore*.cs`
- Modify only when measured: `src/MiniArch/Core/CommandStream.cs`
- Modify only when measured: `src/MiniArch/Core/ParallelCommandStream.cs`
- Modify: `docs/plans/2026-07-15-quality-hardening-4-evidence.md`

### Step 1：先路由瓶颈

比较 Task 1、Task 9、Task 10 的 `record%/submit%/snapshot%/clear%`。只优化稳定主导且相对起点退化/阻塞 Hero 门禁的阶段。

### Step 2：候选必须单独验证

每个候选单独 commit 前运行对应场景至少三次。保留条件：

- 目标场景端到端中位数稳定改善；
- 非目标六场景无 > 3% 稳定退化；
- HeroComing 至少无退化；
- checksum、tests、内存稳定。

不满足即回退候选，不积累“理论更快”的代码。

### Step 3：不得放松的边界

不得为了吞吐删除 preflight、改变 strict Add/Set/Remove、给 unsafe API 增加 Release guard、缓存跨 structural change 的裸地址，或运行 `--update-baseline`。

### Step 4：提交每个被证实候选

提交信息说明机制，不写空泛 `optimize performance`。每个提交在 evidence 文档有 before/after。

## Task 12：精简知识库、XML 与公共文档

**Files:**

- Modify: `.knowledge/kb-code-review-findings.md`
- Modify: `.knowledge/kb-safety-proof.md`
- Modify: `.knowledge/kb-command-stream.md`
- Modify: `.knowledge/kb-snapshot-persistence.md`
- Modify: `.knowledge/kb-design-rationale.md`
- Modify: `.knowledge/kb-core-ecs.md`
- Modify if routing changes: `.knowledge/INDEX.md`
- Modify: `docs/api.md`
- Modify: relevant XML docs under `src/MiniArch/Core/`

### Step 1：先使用 knowledge-base-maintenance skill 重新审计

按该 skill 的 inventory/事实源/删除优先规则工作。先搜索已知过期词：

```powershell
rg -n "发布级正确性|无校验和|_parallelMode|ParallelRecording|ModifiedChunks|Track\(|GetComponentSpanAt|IsUnmappedSentinel|Clear\(query" .knowledge docs src
```

### Step 2：修正 findings 单一事实源

- 新真 bug 加入 `BUG_` 测试索引，含位置、witness、修复边界；
- 推翻“Submit 事务风险已完全修复”等错误结论；
- 新证实非 bug 只记录位置/猜想/结论/验证；
- 不复制实施推理。

### Step 3：删除或改写陈旧绝对结论

- safety proof 改为带版本、范围、命令、当前结果和未覆盖项的验证报告；
- command stream 只描述当前 Preflight/Apply/async ownership；
- snapshot 明确当前 CRC/checksum 事实；
- design/core 删除旧 Track/ModifiedChunks 与错误 `ref world.Get` 示例；
- `Clear(query)` 明确高速非级联契约和安全替代 `Destroy(query)`；
- unsafe span 以 XML 为主事实源，docs 只链接/摘要。

### Step 4：遵守知识页模板

每个被改知识页保留完整 front matter，并把 `updated` 改为 `2026-07-15`。只有主题/关键词路由变化才改 INDEX。

### Step 5：验证文档没有陈旧符号

重复 Step 1 的 rg；每个剩余命中必须是历史迁移说明或明确契约，不得是当前错误示例。

### Step 6：提交

```powershell
git add .knowledge docs/api.md src/MiniArch/Core
git commit -m "docs: align 4.0 contracts and verification evidence"
```

## Task 13：最终简化、审阅与完整门禁

**Files:**

- Review: all changed files
- Finalize: `docs/plans/2026-07-15-quality-hardening-4-evidence.md`

### Step 1：使用 simplify skill 做无行为变化清理

只简化本轮改动；不得重新抽象热路径。清理后重新跑 focused/full tests。

### Step 2：检查 diff 与 API 破坏范围

```powershell
git diff 7cde430...HEAD --stat
git diff 7cde430...HEAD -- src/MiniArch tests/MiniArch.Tests
git status --short
```

Expected: 无临时工具、dump、bin/obj；breaking surface 仅确认的 rename/internalization 与 4.0 版本。

### Step 3：完整 Release build/test

```powershell
dotnet build -c Release --no-restore miniArch.sln
dotnet test -c Release --no-build miniArch.sln
```

### Step 4：完整 soak/determinism/lockstep

```powershell
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 100000
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --determinism --frames 200000
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --sweep 8 --frames 10000 --hosts 4
```

Expected: 全部 exit 0、checksum/determinism 一致、内存不持续增长。

### Step 5：最终 CommandStream 与架构性能门禁

六个 CommandStream 场景各跑三次，与 Task 1 和 `pre-split` 中位数比较。随后：

```powershell
1..3 | ForEach-Object {
    dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
}
```

Expected:

- Movement ≥ 1642 rounds/s；
- Attack ≥ 997 rounds/s；
- 内存稳定；
- CommandStream 热路径相对 `pre-split` 无 > 3% 稳定退化；
- 不用单次幸运结果闭环。

### Step 6：完成证据文档

写入最终 commit、测试计数、soak 参数/结果、CommandStream before/pre-split/after 中位数、Hero 三次结果、API diff、已知非目标。结论必须区分：已修复 bug、仅文档修正、保留的 unsafe 契约、仍未承诺的灾难性回滚。

### Step 7：最终提交与干净树确认

```powershell
git add docs/plans/2026-07-15-quality-hardening-4-evidence.md
git commit -m "docs: finalize MiniArch 4.0 hardening evidence"
git status --short --branch
```

Expected: 工作树干净；evidence 中记录的 commit 与最终 HEAD 对应。如果 evidence 提交导致 HEAD 变化，文档同时记录 runtime verification commit 与 final documentation commit，避免自指矛盾。

## STOP / RETURN 条件

- 新测试不能稳定复现设计中的 bug：STOP，重新核对 witness，不先改生产代码。
- 为实现 preflight 必须引入 World shadow/通用 rollback journal：RETURN 设计评审，本计划不授权扩范围。
- Component preflight 使 `existing-set` 稳定下降 > 5% 且无法用零分配/查找复用消除：STOP，保留 correctness 分支证据并重新评估实现机制，不能静默接受。
- partial 拆分改变关键 IL hash 或内联结果：立即回退该次迁移，定位文本/属性/可见性差异。
- HeroComing 任一场景低于阈值或内存增长：不得宣称 8+；先用 CommandStream.Profile 定位并仅做有证据优化。
- 知识库存在与当前代码相反的绝对结论：不得完成，即使测试全绿。

## 完成判定

只有以下全部满足才完成：

1. 所有新增 `BUG_` witness 经历了可观察 RED → GREEN；
2. public API 破坏范围与已批准设计一致；
3. strict component/hierarchy 契约错误在 alive-world mutation 前失败；
4. async contract error 不启动 worker、不改 target、不遗失 ownership；
5. Debug structural scope 异常后恢复；
6. CommandStream 拆分前后关键 IL/内联与吞吐有直接证据；
7. full Release test、soak、determinism、lockstep 全绿；
8. HeroComing 两阈值与内存门禁通过；
9. 知识库/XML/docs 与 4.0 当前事实一致；
10. 工作树干净，最终证据可追溯到验证过的 commit。
