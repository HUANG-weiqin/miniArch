# Command Buffer Replay/Rewind Benchmark Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 为 `CommandBuffer` 的 replay / reverse / rewind 路径补齐可复跑 benchmark，先用失败测试与 smoke test 锁定 helper/runner 可执行，再产出结果报告与知识库更新。

**Architecture:** 复用现有 `CommandBufferBenchmarks`、`Program.cs` 的 `command-buffer` 子命令和 `CommandBufferTests` 已锁定的 rewind 语义，把“场景生成”“benchmark state factory”“runner/脚本入口”“结果报告”拆开实现。benchmark 测量区只包含目标热路径本身，world 构建、frame 预编译、历史帧准备和结果落盘都放在测量区外；如果基础四类口径不足以暴露 hierarchy 或中间历史点回放成本，再补一条 `MiniArch-only` 扩展 benchmark。

**Tech Stack:** C# 12, .NET 8, xUnit, BenchmarkDotNet, PowerShell

---

### Task 1: 设计 replay/rewind benchmark 场景与 state factory 骨架

**Files:**
- Read: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/.knowledge/kb-command-buffer-feasibility.md`
- Read: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/.knowledge/kb-test-workflow.md`
- Read: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Read: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/Core/CommandBufferTests.cs`
- Create: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferReplayRewindBenchmarkScenarios.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`

**Step 1: 明确要测的四条主口径**

- 把 benchmark 命名和职责固定为：
  - `ReplayOnly`
  - `ReplayWithReverse`
  - `RewindOnly`
  - `ReplayWithRewind`
- 把每条口径对应的测量区固定为：
  - `ReplayOnly`：只测 `world.Replay(in frame)`
  - `ReplayWithReverse`：只测 `world.ReplayWithReverse(in frame)`
  - `RewindOnly`：先在 setup 阶段拿到 `reverse`，测量区只测 `world.Rewind(in reverse)`
  - `ReplayWithRewind`：测量区内执行一次 `ReplayWithReverse` 再紧接一次 `Rewind`

**Step 2: 设计 benchmark state factory 类型**

- 在新场景文件里规划最小状态对象：
  - `CommandBufferReplayRewindScenario`
  - `ReplayOnlyBenchmarkState`
  - `ReplayWithReverseBenchmarkState`
  - `RewindOnlyBenchmarkState`
  - `ReplayWithRewindBenchmarkState`
- 每个 state 都显式持有：
  - baseline `World`
  - 需要 replay 的 `FrameCommands`
  - 需要 rewind 的 `ReverseFrameCommands`（仅 rewind 相关 state）
  - 可选的结构摘要 helper，用于 smoke test 和结果自检

**Step 3: 固定场景工厂的输入维度**

- 先只保留一个公共 replay/rewind 场景枚举，例如：
  - `ExistingEntityMutation`
  - `CreateDestroyMixed`
  - `DestroySubtree`
- 继续沿用现有 entity 档位策略，至少覆盖：
  - `128`
  - `10_000`
- 如 benchmark 波动过大，再追加中档位 `1000`，但不要在首版把档位铺太满。

**Step 4: 写下场景构造约束，避免污染测量区**

- 场景工厂必须在 setup 阶段完成：
  - baseline world 构建
  - command buffer 录制
  - `Playback()` 生成 frame
  - rewind 相关 state 的 reverse 预采集
- benchmark 方法内部不得重新录制 command buffer，不得现算中间历史，不得在热路径里写报告文件。

### Task 2: 先写失败测试，锁定 benchmark helper 与 runner 可执行

**Files:**
- Create: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/Core/CommandBufferReplayRewindBenchmarkScenarioTests.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/Program.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1`

**Step 1: 写失败的场景 helper 测试**

```csharp
[Fact]
public void Replay_rewind_benchmark_scenario_factory_creates_runnable_replay_only_state()
{
    var state = CommandBufferReplayRewindBenchmarkScenarios.CreateReplayOnlyState(
        ReplayRewindBenchmarkScenario.ExistingEntityMutation,
        128);

    Assert.NotNull(state);
    Assert.True(state.Frame.CreatedEntities.Count >= 0);
}
```

**Step 2: 写失败的 rewind smoke test**

```csharp
[Fact]
public void Replay_rewind_benchmark_scenario_factory_creates_runnable_rewind_state()
{
    var state = CommandBufferReplayRewindBenchmarkScenarios.CreateRewindOnlyState(
        ReplayRewindBenchmarkScenario.CreateDestroyMixed,
        128);

    state.World.Rewind(in state.Reverse);
    Assert.True(state.PostRewindSummary.LiveEntityCount >= 0);
}
```

**Step 3: 写失败的 runner/脚本 smoke test 目标**

- 锁定两件事：
  - `Program.cs` 能提供只跑 replay/rewind benchmark 的入口
  - `scripts/benchmark.ps1` 能把子命令放在 `--filter` 前正确透传
- 如果不想在测试里直接起子进程，就至少在计划实现时加一条最小 smoke 命令，并先预期失败。

**Step 4: 运行定向测试，确认当前失败**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferReplayRewindBenchmarkScenarioTests -v minimal
```

Expected: FAIL，因为 replay/rewind benchmark 场景工厂与定向 runner 入口尚不存在，或 `scripts/benchmark.ps1` 还不能正确透传 `command-buffer` 子命令。

### Task 3: 实现最小 benchmark helper/runner，让 smoke test 先通过

**Files:**
- Create: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferReplayRewindBenchmarkScenarios.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/Program.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/Core/CommandBufferReplayRewindBenchmarkScenarioTests.cs`

**Step 1: 先实现最小可运行的场景工厂**

- 只先打通一个最小场景：`ExistingEntityMutation`。
- 工厂至少提供：
  - 录制基础 frame
  - 克隆或重建 baseline world
  - 生成 `ReplayOnly` state
  - 生成 `RewindOnly` state

**Step 2: 在 `Program.cs` 增加 replay/rewind benchmark 专用入口**

- 推荐新增一个更窄的子命令，例如：
  - `command-buffer-replay-rewind`
- 该子命令只挂载 replay/rewind 相关 benchmark 类型，避免把现有 `record+play` benchmark 混在一起。

**Step 3: 修正 benchmark 脚本的子命令透传方式**

- 优先把 `scripts/benchmark.ps1` 改成显式参数，例如：
  - `-Command command-buffer-replay-rewind`
  - `-Filter "*Replay*"`
- 保证最终发给 `dotnet run` 的参数顺序是：
  - `-- command-buffer-replay-rewind --filter ...`
- 不要再依赖把子命令塞进 `ExtraArgs` 末尾。

**Step 4: 复跑定向测试**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferReplayRewindBenchmarkScenarioTests -v minimal
```

Expected: PASS，说明 helper 与 runner 已经最小可执行。

**Step 5: 跑一次最小 smoke benchmark**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1 -Command command-buffer-replay-rewind -Filter "*ReplayOnly*"
```

Expected: benchmark 可启动并输出至少一条 replay/rewind 相关结果，不再因为入口参数顺序错误直接跑偏到全量 benchmark。

### Task 4: 实现 `ReplayOnly` 与 `ReplayWithReverse` benchmark

**Files:**
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferReplayRewindBenchmarkScenarios.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/Core/CommandBufferReplayRewindBenchmarkScenarioTests.cs`

**Step 1: 先写失败测试，锁定 replay state 不会把 setup 混进测量区**

```csharp
[Fact]
public void Replay_only_state_exposes_prebuilt_frame_and_fresh_world()
{
    var state = CommandBufferReplayRewindBenchmarkScenarios.CreateReplayOnlyState(
        ReplayRewindBenchmarkScenario.ExistingEntityMutation,
        128);

    Assert.NotNull(state.Frame);
    Assert.False(state.World.Query<BenchmarkPosition>().Any());
}
```

**Step 2: 运行定向测试，确认失败**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferReplayRewindBenchmarkScenarioTests -v minimal
```

Expected: FAIL，说明 replay state 还没有把 frame/world 准备好，或断言无法成立。

**Step 3: 实现 `ReplayOnly` benchmark**

- 在 `CommandBufferBenchmarks.cs` 新增 replay-only benchmark 类型或新分组：
  - `MiniArch_CommandBuffer_ReplayOnly`
- 只在 benchmark 方法里执行：
  - `state.World.Replay(in state.Frame);`

**Step 4: 实现 `ReplayWithReverse` benchmark**

- 新增：
  - `MiniArch_CommandBuffer_ReplayWithReverse`
- 只在 benchmark 方法里执行：
  - `state.World.ReplayWithReverse(in state.Frame);`

**Step 5: 复跑测试并做 build 验证**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferReplayRewindBenchmarkScenarioTests -v minimal
dotnet build E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release
```

Expected: PASS。

### Task 5: 实现 `RewindOnly` 与 `ReplayWithRewind` benchmark

**Files:**
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferReplayRewindBenchmarkScenarios.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/Core/CommandBufferReplayRewindBenchmarkScenarioTests.cs`

**Step 1: 先写失败测试，锁定 rewind state 已经处于“可回退”状态**

```csharp
[Fact]
public void Rewind_only_state_starts_after_forward_replay_has_already_happened()
{
    var state = CommandBufferReplayRewindBenchmarkScenarios.CreateRewindOnlyState(
        ReplayRewindBenchmarkScenario.CreateDestroyMixed,
        128);

    Assert.True(state.ForwardApplied);
    Assert.NotEqual(state.BaselineSummary, state.ForwardSummary);
}
```

**Step 2: 运行定向测试，确认失败**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferReplayRewindBenchmarkScenarioTests -v minimal
```

Expected: FAIL，说明 rewind-only state 还没有把 reverse 准备好，或者 forward/baseline 摘要未分离。

**Step 3: 实现 `RewindOnly` benchmark**

- 新增：
  - `MiniArch_CommandBuffer_RewindOnly`
- setup 阶段先完成：
  - baseline world
  - `ReplayWithReverse`
  - 保存 reverse
- benchmark 方法里只执行：
  - `state.World.Rewind(in state.Reverse);`

**Step 4: 实现 `ReplayWithRewind` benchmark**

- 新增：
  - `MiniArch_CommandBuffer_ReplayWithRewind`
- benchmark 方法里执行：
  - `var reverse = state.World.ReplayWithReverse(in state.Frame);`
  - `state.World.Rewind(in reverse);`
- 每个 iteration 必须从同一个 baseline world 重建，不能复用已被 rewind 过的残留状态。

**Step 5: 复跑测试并执行窄过滤 smoke**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferReplayRewindBenchmarkScenarioTests -v minimal
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1 -Command command-buffer-replay-rewind -Filter "*Rewind*"
```

Expected: PASS，且 `RewindOnly` / `ReplayWithRewind` 两类 benchmark 都可被过滤执行。

### Task 6: 如有必要，补 hierarchy 或 middle-history benchmark

**Files:**
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferReplayRewindBenchmarkScenarios.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/Core/CommandBufferReplayRewindBenchmarkScenarioTests.cs`

**Step 1: 先判断基础三类场景是否已覆盖关键成本**

- 如果当前结果已经能清楚区分：
  - `reverse capture` 成本
  - `rewind` 成本
  - `replay + rewind` 端到端成本
- 则不要额外补 benchmark，避免过早扩张范围。

**Step 2: 仅在有证据表明缺口存在时补一条扩展 benchmark**

- 候选 A：`HierarchyDestroySubtree`
  - 关注 `Destroy` + subtree restore + parent/child 恢复成本
- 候选 B：`MiddleHistoryReplay`
  - 关注“先回到中间历史点，再按原 frame 继续 replay”的准备成本与终局回放成本

**Step 3: 先写失败测试锁定扩展场景工厂可执行**

```csharp
[Fact]
public void Optional_hierarchy_or_middle_history_benchmark_state_is_deterministic()
{
    var state = CommandBufferReplayRewindBenchmarkScenarios.CreateOptionalState(
        ReplayRewindBenchmarkScenario.DestroySubtree,
        128);

    Assert.NotNull(state);
}
```

**Step 4: 实现最小扩展 benchmark，并保持 `MiniArch-only`**

- 这类 benchmark 不需要做 `MiniArch vs Arch` 对比。
- benchmark 名称里显式带上：
  - `Hierarchy`
  - 或 `MiddleHistory`
- 只在确有必要时落地一个，不要两个都做。

**Step 5: 跑窄过滤确认它可单独执行**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1 -Command command-buffer-replay-rewind -Filter "*Hierarchy*"
```

Expected: 只有扩展 benchmark 被执行；如果最终判断“不需要补”，就在结果报告中明确说明未增加额外口径的原因。

### Task 7: 跑 benchmark 过滤项并记录结果

**Files:**
- Create: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/docs/benchmarks/2026-04-12-command-buffer-replay-rewind-benchmark-report.md`
- Read: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/BenchmarkDotNet.Artifacts/results/`

**Step 1: 跑 replay-only / replay-with-reverse 过滤项**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1 -Command command-buffer-replay-rewind -Filter "*ReplayOnly*|*ReplayWithReverse*"
```

Expected: 输出两类 replay benchmark 的时间与分配结果。

**Step 2: 跑 rewind-only / replay-with-rewind 过滤项**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1 -Command command-buffer-replay-rewind -Filter "*RewindOnly*|*ReplayWithRewind*"
```

Expected: 输出两类 rewind benchmark 的时间与分配结果。

**Step 3: 如果 Task 6 落了扩展口径，再单独跑一次**

Run:
```powershell
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1 -Command command-buffer-replay-rewind -Filter "*Hierarchy*|*MiddleHistory*"
```

Expected: 只输出扩展 benchmark。

**Step 4: 把结果写入 benchmark 报告**

- 在 `docs/benchmarks/2026-04-12-command-buffer-replay-rewind-benchmark-report.md` 记录：
  - benchmark 入口命令
  - 场景说明
  - entity 档位
  - 每条 benchmark 的 Mean / Allocated
  - 简短解读：哪条成本主要来自 reverse capture，哪条主要来自 rewind 执行
  - 原始 BenchmarkDotNet 报告文件路径

### Task 8: 更新知识库，沉淀 replay/rewind benchmark 入口与结论

**Files:**
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/.knowledge/kb-command-buffer-feasibility.md`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/.knowledge/kb-test-workflow.md`
- Modify: `E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/.knowledge/INDEX.md`（仅当新增知识页时）

**Step 1: 更新 command buffer 知识页**

- 记录新增 replay/rewind benchmark 口径：
  - `ReplayOnly`
  - `ReplayWithReverse`
  - `RewindOnly`
  - `ReplayWithRewind`
- 明确说明这些 benchmark 只比较 `MiniArch` 自身不同入口的成本，不承担跨引擎功能正确性证明。

**Step 2: 更新测试/benchmark workflow 知识页**

- 记录 replay/rewind benchmark 的运行命令。
- 记录 `scripts/benchmark.ps1` 新的子命令透传方式。
- 记录何时需要补 `Hierarchy` 或 `MiddleHistory` 扩展 benchmark。

**Step 3: 运行最终验证**

Run:
```powershell
dotnet test E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferReplayRewindBenchmarkScenarioTests -v minimal
dotnet build E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release
powershell -ExecutionPolicy Bypass -File E:/godot/arch/miniArch/.worktrees/command-buffer-rewind/scripts/benchmark.ps1 -Command command-buffer-replay-rewind -Filter "*Replay*|*Rewind*"
```

Expected: tests PASS，benchmark 项目可编译，目标过滤项可执行，知识页中的命令与实际入口一致。
