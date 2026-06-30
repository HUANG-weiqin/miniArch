# MiniArch Command Buffer Rewind Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 为 `CommandBuffer` 增加可保留的 frame 级回退能力：`Playback()` 继续只产出正向 `FrameCommands`，`ReplayWithReverse()` / `PlayWithReverse()` 返回可用于 `Rewind()` 的 reverse IR，并通过公开可观察的 `World` 状态验证回退前后行为正确。

**Architecture:** 保持现有“recording/compile 与 world mutation 分离”的边界：`CommandBuffer` 只负责正向 compile，reverse 信息由 `World` 在真正执行正向帧时采集并构造成独立 `ReverseFrameCommands`。测试默认只校验 entity 存活性、组件值、query 结果、parent/children 与 destroy subtree 等公开语义，不要求 allocator 或其他 internal state 做精确镜像回滚。

**Tech Stack:** C# 12, .NET 8, xUnit, PowerShell

---

### Task 1: 锁定 reverse IR 与公开 API 骨架

**Files:**
- Read: `.knowledge/kb-command-buffer-feasibility.md`
- Read: `.knowledge/kb-hierarchy-runtime.md`
- Read: `.knowledge/kb-test-workflow.md`
- Modify: `src/MiniArch/Core/FrameCommands.cs`
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1:** 在 `CommandBufferTests.cs` 先写失败测试，锁定以下公开契约：
- `Playback()` 仍然不改 world。
- `ReplayWithReverse(in FrameCommands)` 会应用正向帧并返回 `ReverseFrameCommands`。
- `PlayWithReverse()` 与 `Playback()+ReplayWithReverse()` 在 world 最终状态上等价。
- `Rewind(in ReverseFrameCommands)` 能把 world 恢复到应用前的公开可观察状态。

**Step 2:** 运行定向测试，确认当前缺少 reverse 类型与 API。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal
```

预期：编译失败或测试失败，报错集中在 `ReverseFrameCommands`、`ReplayWithReverse`、`PlayWithReverse`、`Rewind` 尚不存在。

**Step 3:** 在 `FrameCommands.cs` 增加独立 reverse IR 公共类型；在 `World.cs` / `CommandBuffer.cs` 增加最小可编译 API 骨架，不实现完整行为。

**Step 4:** 再次运行同一组测试，确认已经进入行为失败而不是接口缺失。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal
```

预期：项目可编译，新增测试因行为未实现而失败。

### Task 2: 先完成最小回路，打通 Playback -> ReplayWithReverse -> Rewind

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/FrameCommands.cs`
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`

**Step 1:** 先写最小失败测试，只覆盖单实体基础回路：
- existing entity 的 `Set<T>` 正向生效后，`Rewind()` 恢复旧值。
- existing entity 的 `Add<T>` 正向生效后，`Rewind()` 让组件消失。
- existing entity 的 `Remove<T>` 正向生效后，`Rewind()` 恢复组件和值。

**Step 2:** 运行只包含最小回路的测试，确认失败。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Rewind" -v minimal
```

预期：失败，说明 reverse 采集与回退执行尚未恢复组件旧状态。

**Step 3:** 在 `World.cs` 先以最小实现打通 reverse 采集与回退执行路径，只覆盖 existing entity 的 `Add/Set/Remove` 公开语义。

**Step 4:** 复跑定向测试，确认最小回路通过，且 `Playback()` 仍不改 world。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests&FullyQualifiedName~Rewind" -v minimal
```

预期：通过；测试只验证组件存在性、组件值与 query 可见性，不断言内部存储细节。

### Task 3: 覆盖所有命令的正向与逆向语义

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/FrameCommands.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `tests/MiniArch.Tests/Core/WorldStructuralChangeTests.cs`

**Step 1:** 按 TDD 扩充失败测试，覆盖所有命令：
- `Create` 后 world 可见；`Rewind()` 后实体不再 alive。
- `Destroy` existing entity 后 world 不可见；`Rewind()` 后实体、组件和 query 可见性恢复。
- `Add` / `Set` / `Remove` 的正反向效果正确。
- `AddChild` / `RemoveChild` 的正反向效果正确。
- mixed script 中同一实体跨多个命令桶的最终状态与回退状态正确。

**Step 2:** 运行 command buffer 与 structural change 相关测试，确认失败集中在命令覆盖缺口。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~WorldStructuralChangeTests" -v minimal
```

预期：失败，至少暴露 `Create/Destroy/AddChild/RemoveChild` 的 reverse 语义未齐全。

**Step 3:** 补齐 `World` 的 reverse 采集与 `Rewind()` 执行逻辑，使全部命令都具备可逆公开语义。

**Step 4:** 复跑同一组测试，确认所有命令在 apply -> rewind 后恢复到预期 world 状态。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~WorldStructuralChangeTests" -v minimal
```

预期：通过；断言重点是 entity alive、组件值、query 命中与 parent/children，而不是 allocator/internal state 的逐字段一致。

### Task 4: 覆盖 hierarchy、destroy subtree 与多实体场景

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/HierarchyTable.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `tests/MiniArch.Tests/Core/WorldLifecycleTests.cs`

**Step 1:** 先写失败测试，覆盖 hierarchy 与 subtree 回退：
- 新建 parent/child 并在同帧 `AddChild`，正向后 parent/children 可见，回退后关系消失。
- existing hierarchy 执行 `RemoveChild`，回退后恢复原 parent。
- destroy parent 触发 subtree destroy，回退后整棵子树恢复，且 child-parent 关系正确。
- 混合 existing/newly-created 实体的 hierarchy 脚本在回退后恢复到帧前公开状态。

**Step 2:** 运行 lifecycle 与 command buffer 测试，确认 subtree/hierarchy 相关断言失败。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~WorldLifecycleTests" -v minimal
```

预期：失败，说明 destroy subtree 前的关系快照与回退恢复还不完整。

**Step 3:** 在 `World.cs` 中补齐 destroy 前子树采集与回退恢复顺序；必要时只为 `World` 增加读取当前 hierarchy 状态的轻量辅助，不把历史栈塞进 `HierarchyTable`。

**Step 4:** 复跑同一组测试，确认 hierarchy 与 subtree 的回退语义稳定。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~WorldLifecycleTests" -v minimal
```

预期：通过；父子关系、子树销毁与回退都由公开 API 观测成功。

### Task 5: 覆盖多帧、多实体与 Replay/Play 两条入口的一致性

**Files:**
- Modify: `src/MiniArch/Core/World.cs`
- Modify: `src/MiniArch/Core/CommandBuffer.cs`
- Modify: `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Modify: `tests/MiniArch.Tests/Core/QueryTests.cs`

**Step 1:** 先写失败测试，覆盖时序与规模：
- 多实体随机脚本：至少包含 `Create/Add/Set/Remove/Destroy/AddChild/RemoveChild` 混合。
- 多帧栈式流程：frame1 应用并保存 reverse1，frame2 应用并保存 reverse2，然后按 `reverse2 -> reverse1` 回退，最终 world 回到初始公开状态。
- `PlayWithReverse()` 与 `Playback()+ReplayWithReverse()` 在多实体多帧场景中结果一致。
- rewind 后 query 结果、hierarchy 结果、entity alive 集合与回退前快照一致。

**Step 2:** 运行 command buffer 与 query 测试，确认失败集中在多帧回退和入口一致性。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~QueryTests" -v minimal
```

预期：失败，暴露 reverse frame 栈式使用或入口一致性问题。

**Step 3:** 补齐 reverse frame 的多帧使用约束与两条入口共享逻辑，避免 `PlayWithReverse()` 与 `ReplayWithReverse()` 分叉出不同语义。

**Step 4:** 复跑同一组测试，确认多帧、多实体、双入口都通过。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~QueryTests" -v minimal
```

预期：通过；至少有一组测试显式证明多帧按后进先出回退可恢复初始公开 world 状态。

### Task 6: 完整验证并更新知识库

**Files:**
- Modify: `.knowledge/kb-command-buffer-feasibility.md`
- Modify: `.knowledge/kb-hierarchy-runtime.md`
- Modify: `.knowledge/kb-test-workflow.md`
- Modify: `docs/plans/2026-04-12-miniarch-command-buffer-rewind-implementation-plan.md`（仅在执行过程中需要补充实际验证说明时）

**Step 1:** 补充或更新知识页，记录以下结论：
- `ReverseFrameCommands` 的定位与边界。
- `ReplayWithReverse()` / `PlayWithReverse()` / `Rewind()` 的契约。
- rewind 默认只保证相邻帧、栈式使用场景。
- destroy subtree / hierarchy / 多帧回退的验证入口。

**Step 2:** 运行完整验证，覆盖构建与全部关键测试。

测试命令：
```powershell
./scripts/verify.ps1
```

预期：完整 build + test 通过。

**Step 3:** 如果 `verify.ps1` 过重或受既有无关阻塞影响，再补充核心回归命令并记录结果。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj -v minimal
```

预期：`MiniArch.Tests` 全量通过；若存在仓库既有无关阻塞，需要在执行记录中单独说明，不得把本功能回归与外部问题混在一起。

**Step 4:** 回读知识页与计划文件，确认 `.knowledge/INDEX.md` 仍准确，且新增认知已写回知识库。

测试命令：
```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferTests -v minimal
```

预期：command buffer rewind 相关测试最终保持通过，可作为后续 agent 的定向回归入口。
