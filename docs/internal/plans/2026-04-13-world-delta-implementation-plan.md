# MiniArch World Delta Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 为 `CommandBuffer` 增加双向 `WorldDelta` 产出能力，并让 `World` 能正向/反向应用该 delta，在同实例回退与不同实例顺序同步场景下保持公开状态同态。

**Architecture:** 保留现有 `FrameCommands` / `Replay` 作为底层执行 IR；新增 entity 级 `WorldDelta` 作为更高层公开状态差异模型。delta 生成不通过克隆 world 或临时 replay+rewind 完成，而是复用当前 world 的公开读取入口构建 `Before`，再按现有 replay 顺序在 shadow public state 上推演 `After`。

**Tech Stack:** C# 12, .NET 8, xUnit, PowerShell

---

### Task 1: 落设计文档并锁定公开 API

**Files:**
- Create: `docs/plans/2026-04-13-world-delta-design.md`
- Create: `docs/plans/2026-04-13-world-delta-implementation-plan.md`
- Read: `.knowledge/kb-command-buffer-feasibility.md`
- Read: `.knowledge/kb-core-ecs.md`

**Step 1:** 写入设计文档，固定 `WorldDelta` 的目标、边界、双向语义、hierarchy/cascade destroy 规则与跨实例前提。

**Step 2:** 写入实现计划，明确最小改动文件和 TDD 顺序。

**Step 3:** 不提交 git commit；保留文档为当前工作树变更，后续实现完成后再由用户决定是否提交。

### Task 2: 写失败测试锁定最小公开契约

**Files:**
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Write the failing test**

- 新增一组失败测试，至少覆盖：
  - `PlaybackDelta()` 不改 source world，且能在另一个同步 world 上 `ApplyDeltaForward()` 得到相同公开状态。
  - `ApplyDeltaBackward()` 能把同一个 world 恢复到生成 delta 前的公开状态。
  - `AddChild/RemoveChild` 与 cascade destroy 的 delta 正反向语义正确。
  - same-frame transient entity 不出现在最终 delta 中。

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal`

Expected: 编译失败或测试失败，错误集中在 `WorldDelta` 类型、`PlaybackDelta()`、`ApplyDeltaForward()`、`ApplyDeltaBackward()` 尚不存在。

### Task 3: 新增最小 WorldDelta 类型并让测试从接口缺失进入行为失败

**Files:**
- Create: `src/MiniArch/Core/WorldDelta.cs`
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Modify: `src/MiniArch/Core/World.cs`

**Step 1: Write minimal implementation**

- 新增最小公共类型：`WorldDelta`、`WorldDeltaEntry`、`WorldEntityPublicState`。
- 在 `CommandBuffer` 增加 `PlaybackDelta()` 空骨架。
- 在 `World` 增加 `ApplyDeltaForward()` / `ApplyDeltaBackward()` 空骨架与内部 `CaptureDelta(...)` 入口。

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal`

Expected: 项目可编译，但新增测试因行为未实现而失败。

### Task 4: 先打通最小 delta 回路

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Write the failing test**

- 若前一轮测试过宽，先拆成最小单实体场景：existing entity 的 `Set/Add/Remove` 在 delta forward/backward 后前后状态正确。

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Delta" -v minimal`

Expected: 失败，说明 shadow public state 推演或 apply 还未恢复组件快照。

**Step 3: Write minimal implementation**

- 在 `World` 中实现：
  - 读取 entity 当前公开组件与 parent 的 helper
  - 基于 `FrameCommands` 构建候选实体集
  - 在 shadow state 上按 `create -> AddChild/RemoveChild -> add -> set -> remove -> destroy` 推演 `After`
  - 生成双向 `WorldDelta`
  - 让 `ApplyDeltaForward()` / `ApplyDeltaBackward()` 对最小组件场景生效

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Delta" -v minimal`

Expected: 最小 delta 测试通过。

### Task 5: 补齐 hierarchy、cascade destroy 与跨实例同步

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1: Write the failing test**

- 扩展失败测试，覆盖：
  - `AddChild/RemoveChild/relink`
  - destroy root 触发 existing subtree 级联销毁
  - `RemoveChild` 后逃逸 subtree destroy
  - `create -> AddChild 到 doomed subtree -> 同帧消失`
  - 多帧 tick-by-tick 跨实例顺序 apply

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal`

Expected: 失败集中在 hierarchy、cascade destroy 或 transient 折叠缺口。

**Step 3: Write minimal implementation**

- 补齐 shadow hierarchy destroy 闭包推演。
- 让 apply 流程先 materialize 目标活体、再同步全量组件、再修 parent、最后 destroy 目标为空的 entities。

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal`

Expected: 命令缓冲相关 delta 测试通过。

### Task 6: 完整验证并更新知识库

**Files:**
- Modify: `.knowledge/kb-command-buffer-feasibility.md`
- Modify: `.knowledge/INDEX.md`（仅在新增知识页时）

**Step 1:** 更新知识页，记录 `WorldDelta` 的定位、生成方式、双向 apply 边界和主要测试入口。

**Step 2: Run test to verify it passes**

Run: `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal`

Expected: `MiniArch.Tests` 全量通过；若存在仓库既有无关失败，需明确区分。

**Step 3:** 复读知识库与文档，确认内容与实现一致。
