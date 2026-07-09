# 知识页审计报告 — Group E

**审计日期**: 2026-07-09
**工作树**: `.worktrees\knowledge-consistency-audit-20260709`
**审计者**: Agent (read-only audit)
**规则**: 不修改 .knowledge 文件；每个 false/stale claim 包含 file:line、原始声明、实际事实、证据路径/行/命令、建议操作。

---

## 1. kb-test-workflow.md

### 覆盖率声明

测试文件表覆盖了 **42 个文件**中的约 **27 个**（除去标记删除的 DebugMetricsTests）。遗漏约 **15+ 个**实际存在的测试文件。

### 发现

#### F1 [高] 测试文件表缺项 — 遗漏大量现有测试文件

**文件**: `kb-test-workflow.md:19-53`
**原始声明**: 测试文件表列出所有核心测试文件。
**实际事实**: 以下文件存在但未被表收录：

| 遗漏的测试文件 | 行数 |
|---|---|
| `Core/EntitySlotTests.cs` | 410 |
| `Core/ChildrenEnumerableTests.cs` | 201 |
| `Core/RobustnessTests.cs` | — |
| `Core/FrameDeltaTests.cs` | — |
| `Core/FrameDeltaAttackSurfaceTests.cs` | — |
| `Core/ChangeTrackingInfrastructureTests.cs` | 125 |
| `Core/ChangeTrackingReplayTests.cs` | 195 |
| `Core/ComponentSchemaTests.cs` | 96 |
| `Core/SubmitReplayParityTests.cs` | — |
| `UserApi/WatchApiTests.cs` | — |
| `UserApi/WatchProjectedTests.cs` | — |
| `UserApi/ChangeQueryTests.cs` | — |
| `UserApi/ChangeQueryFilterTests.cs` | — |
| `Persistence/WorldDiffTests.cs` | — |
| `Persistence/NetworkSyncTests.cs` | — |
| `Persistence/ChangeTrackingSnapshotTests.cs` | — |
| `PropertyBased/ReplayConvergencePropertyTests.cs` | — |
| `PropertyBased/KnownLimitationTests.cs` | — |
| `Diagnostics/WorldValidatorTests.cs` | — |
| `Diagnostics/WorldDigestTests.cs` | — |
| `Diagnostics/EntityDumpTests.cs` | — |

**证据**: `glob "tests/MiniArch.Tests/**/*.cs"` 输出如上。
**建议**: 将遗漏文件补全到表格中，并更新覆盖范围描述。

#### F2 [中] `benchmark.ps1` 路径与脚本实际路径不一致

**文件**: `kb-test-workflow.md:88`
**原始声明**: 
```
- 运行 benchmark：`tools/scripts/benchmark.ps1` — 或 `dotnet run --project tests\MiniArch.Benchmarks -c Release -- command-buffer`
```
**实际事实**: 直接 `dotnet run` 命令中的路径 `tests\MiniArch.Benchmarks` 是正确的（项目确实位于 `tests/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj`）。但 `benchmark.ps1` 脚本本身有错误——第 10 行硬编码了 `benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj`，而仓库根目录下不存在 `benchmarks\` 目录（仅存在 `tests\` 下的版本）。

**证据**: 
- `benchmark.ps1:10`: `$project = Join-Path $repoRoot "benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj"`
- `fd -t d "MiniArch.Benchmarks"` 输出: `E:/godot/arch/miniArch/.worktrees/knowledge-consistency-audit-20260709/tests/MiniArch.Benchmarks/`
- 仓库根目录 `ls` 确认 `benchmarks/` 不存在

**建议**: 知识页本身准确（直接命令路径正确）。但开发者如果运行 `benchmark.ps1` 会失败。建议修复 `benchmark.ps1` 的路径，或者注明脚本有 bug。

#### F3 [中] `FrameDeltaDeterminismTests` 描述略显陈旧

**文件**: `kb-test-workflow.md:37`
**原始声明**: "跨 world replay 决定性、Submit vs Replay 收敛、序列化 round-trip 字节级一致性"
**实际事实**: 字节级 round-trip 一致性实际由 `SerializationRoundtripPropertyTests`（`PropertyBased/`）和 `CommandStreamTests` 覆盖。`FrameDeltaDeterminismTests`（833 行）的核心焦点是**两个独立 world 接收相同 delta 序列后的位级一致性**，而非序列化 round-trip。

**证据**: `FrameDeltaDeterminismTests.cs:30-79` — 测试名称和注释（`Empty_delta_sequence_keeps_both_worlds_empty`、`Same_delta_sequence_into_two_fresh_worlds_produces_identical_state`）
**建议**: 将描述改为 "跨 world replay 决定性（相同 delta → 相同最终状态）、Submit 与 Replay 收敛"，移除或修正 "序列化 round-trip 字节级一致性" 的描述。

#### F4 [低] `IntMathTests` 覆盖边界描述不完整

**文件**: `kb-test-workflow.md:46`
**原始声明**: "整数平方根 `IntMath.Isqrt` 边界覆盖（0/1/2/3/4/15/16/int.MaxValue）"
**实际事实**: 实际测试用例（12 个 `[InlineData]` + 3 个额外 `[Fact]`）还覆盖了 24、25、99、100、`int.MaxValue+1`、`long.MaxValue`、大完全平方根等。知识页只列出子集。

**证据**: `IntMathTests.cs:7-55` — 包含 `[InlineData(24L, 4L)]`、`[InlineData(100L, 10L)]`、`Isqrt_above_int_range_is_correct` 等
**建议**: 可以保留为概括性描述，但建议明确"包括但不限于"或更新为更完整的覆盖说明。

#### F5 [低] ComponentSchemaTests 等其他文件未被引用

**文件**: `kb-test-workflow.md:19-53`
类似 F1，`ComponentSchemaTests.cs` 等文件完全未提及。
**建议**: 随 F1 一并补充。

---

## 2. kb-ecs-comparison.md

### 覆盖率声明

覆盖 MiniArch vs Arch vs DefaultEcs 的吞吐量和内存稳定性对比，以及源码级机制对比和 S10 FrifloGameScenarios 排查结论。

### 发现

#### F6 [高] "GameTickSim 11 场景" — 实际为 13 场景

**文件**: `kb-ecs-comparison.md:32`
**原始声明**: "GameTickSim 11 场景"
**实际事实**: `ScenarioBenchmark.cs:15-29` 注册了 **13 个场景**：A-PureIteration、B-WideSingleComponent、F-MultiArchetypeIteration、G1-FragBaseline、G2-FragAftermath、H-MultiComponentJoin、I-SparseQuery、C-StructuralAddRemove、J-EntityCreationBurst、D-MassCreateDestroy、E-MixedFullTick、K-BulletHell、L-BulletHellBuffs。

**证据**: `ScenarioBenchmark.cs:14-29`
**建议**: 将 "11" 更新为 "13"，或具体说明包含哪些场景。

#### F7 [中] `ChunksOf<T>` 不是当前 MiniArch 的公开 API 名称

**文件**: `kb-ecs-comparison.md:36`
**原始声明**: "`ChunksOf<T>` + 直接 `Span<T>` 迭代 vs `EachSpan<T>` 在 K-BulletHell 下性能差距在 0.2% 噪声内"
**实际事实**: `ChunksOf<T>` 在 `src/MiniArch/` 源代码中不存在。MiniArch 实际使用 `Query.GetChunks()` → `ChunkView.GetSpan<T>()` 模式。`EachSpan` 是 Arch 的 API，`ScanPositionEachSpan`/`ScanLifetimeEachSpan` 是 GameTickSim 中的帮助函数。

**证据**: `rg "ChunksOf" src/` → 无结果；`rg "GetChunks\(\)" src/` → 大量结果
**建议**: 将 "`ChunksOf<T>`" 改为 "`GetChunks()` + `GetSpan<T>()`" 以匹配实际 API。

#### F8 [低] S10 性能数字不可复现验证

**文件**: `kb-ecs-comparison.md:63-72`
**原始声明**: S10 MixedLoad 的具体 ops/s 数字（MiniArch 2491、Friflo 1121 等）
**实际事实**: `tools/perf/FrifloGameScenarios.Perf/` 项目存在且 S10-MixedLoad 使用 Queue 实现。但具体数字是历史实测值，当前代码可能产生不同数值。
**建议**: 建议注明 "2026-07-XX 实测，仅供参考" 或附上 re-run 命令 `dotnet run -c Release --project tools/perf/FrifloGameScenarios.Perf`。

#### F9 [低] 512-bit bitmask 技术细节未验证

**文件**: `kb-ecs-comparison.md:51`
**原始声明**: "512-bit bitmask 匹配 vs hash-based full rescan"
**实际事实**: 这是架构性声明，阅读 `src/MiniArch/` 下的核心代码可以部分验证但不在此次审计范围内。
**建议**: 可保留，列为已知设计决策。建议在 `kb-ecs-comparison.md` 中链接到相关源码文件。

---

## 3. kb-gameticksim-scenarios.md

### 覆盖率声明

描述 GameTickSim 场景模块的架构、决策、认知模型和入口。

### 发现

#### F10 [高] M-BulletHellWarfare 不存在

**文件**: `kb-gameticksim-scenarios.md:35-36`
**原始声明**: 
> - `M-BulletHellWarfare` 在 K 的基础上测 buff 结构性 Add/Remove 和 archetype fragmentation。
> - `M-BulletHellWarfare` 增加 3 boss、homing、minion AI、多目标碰撞、4 状态独立 timer 和更高 create/destroy 压力。

**实际事实**: `ScenarioBenchmark.cs` 中只注册了 `K-BulletHell` 和 `L-BulletHellBuffs`。**没有任何 `M-BulletHellWarfare` 的场景、方法或引用**。

**证据**: 
- `ScenarioBenchmark.cs:14-29`: 场景注册列表（仅 A-K, L，无 M）
- `ScenarioBenchmark.cs:1040-1046`: `PrintSummary` 的 `scenarioNames` 列表（无 M）
- `rg "M-BulletHell|RunBulletHellWarfare|BulletHellWarfare" tools/perf/GameTickSim.Perf/` → 无结果

**建议**: 
1. **删除** M-BulletHellWarfare 的全部描述（第 35-36 行）以及所有引用（第 38-39 行的 4 状态 timer 和 player bullet Damage 规则）。
2. 或者如果该场景仍在开发中，添加 `TODO` 并注明未实现。

#### F11 [中] 描述 `PrintSummary` 的 `scenarioNames` 需同步时缺 M 场景

**文件**: `kb-gameticksim-scenarios.md:71`
**原始声明**: "新增场景后必须同步 `RunAll` 场景数组和 `PrintSummary` 的 `scenarioNames`，否则全量 summary 索引会错位。"
**实际事实**: 当前两者同步良好（各 13 个场景），但知识页自身描述了不存在的 "M-BulletHellWarfare"，自相矛盾。
**证据**: `ScenarioBenchmark.cs:14-29` 与 `ScenarioBenchmark.cs:1040-1046` 比较
**建议**: 修复 F10 后此问题消失。

#### F12 [中] 主入口 Program.cs 的 warmup 是固定 tick 数而非 2 秒

**文件**: `kb-gameticksim-scenarios.md:24`
**原始声明**: "固定 2 秒 warmup，然后按场景自己的 `durationSeconds` 测量 ops/s、ms/op、heap delta 和 GC"
**实际事实**: 该描述只对 `ScenarioBenchmark.MeasureTimed` 成立（第 949-951 行实现了 2 秒 warmup）。但 `Program.cs:71-72`（独立运行入口）使用 `GameTickData.WarmupTicks = 20`（固定 20 个 tick，非时间基准）。知识页没有区分这两个路径。

**证据**: 
- `GameTickScenario.cs:7`: `public const int WarmupTicks = 20;`
- `Program.cs:71-72`: `for (var i = 0; i < GameTickData.WarmupTicks; i++) tickFunc();`
- `ScenarioBenchmark.cs:949-951`: `while (warmupSw.Elapsed.TotalSeconds < 2) tickFunc();`

**建议**: 明确说明"`ScenarioBenchmark.RunAll` 使用 2 秒 warmup，`Program.cs` 直接入口使用固定 20 tick warmup"。

#### F13 [低] `GameTickComponents.cs` 路径描述缺少 namespace

**文件**: `kb-gameticksim-scenarios.md:23`
**原始声明**: "`tests/SharedInfrastructure/MiniArch.SharedInfrastructure/GameTickComponents.cs`：三方 benchmark 共享组件定义"
**实际事实**: 文件路径正确（已验证存在）。但未说明组件定义在 `MiniArchBenchmarks.GameTick` 命名空间下。
**证据**: `GameTickComponents.cs:1`: `namespace MiniArchBenchmarks.GameTick;`
**建议**: 可补充 namespace 信息以便快速定位。

---

## 4. kb-commandstream-game-perf.md

### 覆盖率声明

描述独立的 CommandStream 游戏稳态压测项目，覆盖 MiniArch、Friflo、Arch 三方对比。

### 发现

#### F14 [通过] 核心声明全部可验证

以下高优先级声明已逐一验证，均正确：

| 声明 | 位置 | 证据 |
|---|---|---|
| `tools/perf/CommandBufferGame.Perf/Program.cs` 为单文件 | L22 | 文件 1408+ 行，包含所有场景实现 |
| `MiniArch.Core.CommandStream.Submit()` | L23 | `Program.cs:379`: `_stream.Submit()` |
| Arch `CommandBuffer.Playback(world, true)` | L25 | `Program.cs:705`: `_buffer.Playback(_world, true)` |
| Friflo `ReuseBuffer = true` | L24 | `Program.cs:529`: `_buffer.ReuseBuffer = true` |
| ArchSteadyCombatWorld 存在 | L25 | `Program.cs:649`: `public sealed class ArchSteadyCombatWorld` |
| MiniArchSteadyCombatWorld (CommandBuffer 版) 已移除 | L38 | `rg "MiniArchSteadyCombatWorld" tools/perf/CommandBufferGame.Perf/` → 无结果 |
| 设计文档路径 | L55-56 | `docs/internal/plans/2026-06-09-commandbuffer-game-steady-design.md` 和 `-plan.md` 均存在 |
| `tools/perf/CommandBuffer.Perf/` 旧项目存在 | L16 | `tools/perf/CommandBuffer.Perf/Program.cs` 存在 |

#### F15 [低] Friflo API 命名有细微出入

**文件**: `kb-commandstream-game-perf.md:24`
**原始声明**: "`GetCommandBuffer()` + `ReuseBuffer = true` + `Playback()`"
**实际事实**: 代码中 `FrifloSteadyCombatWorld` 的 `RecordCommands` 使用 `_buffer.AddComponent()`、`_buffer.RemoveComponent<Burning>()`、`_buffer.DeleteEntity()` 等，`Playback()` 在 `RunTick()` 中被调用。`GetCommandBuffer()` 和 `ReuseBuffer` 在构造函数中设置（第 528-529 行）。描述正确。

暂无问题。

---

## 5. kb-hero-pipeline-regression.md

### 覆盖率声明

描述 HeroComing.Perf 回归门禁的架构、阈值、失败处理流程和历史参考信息。

### 发现

#### F16 [高] PipelineBenchmarkTests 实际运行 3 秒而非 20 秒

**文件**: `kb-hero-pipeline-regression.md:65-71`
**原始声明**: 列出 PipelineBenchmarkTests 的 cycles/sec（Movement 48,883、Simple Attack 25,946 等），数据日期 "2026-05-29"。
**实际事实**: 
1. `PipelineBenchmarkTests.cs` 中各测试方法名包含 "20Seconds" 但实际测量周期为 3 秒（`sw.ElapsedMilliseconds < 3000`，第 46 行）。
2. 该测试也不再输出 cycles/sec（它输出 `Report("Movement (position change)", sw, ticks)`——报告的是 ticks/3s）。当前代码中没有 `cycles/sec` 输出格式。
3. 数据日期 2026-05-29 距今已超过一个月，Hero pipeline 代码很可能已有变化，cycles/sec 数字严重过期。

**证据**: 
- `PipelineBenchmarkTests.cs:46`: `while (sw.ElapsedMilliseconds < 3000)`
- 完整文件确认无 cycles/sec 计算公式

**建议**: 
1. 将 "20Seconds" 测试名与实际 3 秒运行时对齐（或修改测试）。
2. **删除** 2026-05-29 的 cycles/sec 数字，或标记为 "历史参考，当前代码可能不同"。
3. 或者运行一次测试重新生成数字。

#### F17 [中] `--track-observer` 和 `--compare-old-value-tracking` 标记删除的声明正确

**文件**: `kb-hero-pipeline-regression.md:23-24`
**原始声明**: "~~`--track-observer`~~（已删除）"、"~~`--compare-old-value-tracking`~~（已删除）"
**实际事实**: 当前 `HeroComing.Perf/Program.cs` 只接受 `--check-baseline`、`--update-baseline`、`--help`。`--track-observer` 和 `--compare-old-value-tracking` 确实已完全移除。同时 `TrackValueChanges<T>()` 和 `CreateDenseValueDiff<TComponent,TValue,TProjector>()` 在 `src/MiniArch/` 中不存在。

**证据**: `Program.cs:29-34` 已知参数列表，无 --track-observer 或 --compare-old-value-tracking
**建议**: **无需操作** — 声明确认正确。

#### F18 [高] 阈值数字计算验证通过

**文件**: `kb-hero-pipeline-regression.md:35-36`
**原始声明**: "Movement 吞吐量：≥1642 rounds/s（baseline 的 80%）"、"Attack 吞吐量：≥997 rounds/s（baseline 的 80%）"
**实际事实**: 
- 2052.7 × 0.8 = 1642.16 → 取整 1642 ✓
- 1246.8 × 0.8 = 997.44 → 取整 997 ✓

**证据**: `Program.cs:334-335`: 使用 `(int)(movResult.throughput * 0.8)` 和 `(int)(atkResult.throughput * 0.8)`
**建议**: **通过**。阈值计算逻辑与代码一致。

#### F19 [中] 500 + 500 角色配置验证通过

**文件**: `kb-hero-pipeline-regression.md:19`
**原始声明**: "500 players + 500 enemies on 100x100 grid"
**实际事实**: 
- `Program.cs:15`: `const int CharacterCount = 1000;`
- `Program.cs:16-17`: `GridWidth = 100`, `GridHeight = 100`
- `Program.cs:126-127`: `int playerCount = CharacterCount / 2;` (=500), `int enemyCount = CharacterCount / 2;` (=500)
- 创建 `playersPerRow = 50`, 玩家位于上半区，敌人位于下半区

**证据**: `Program.cs:15-17,125-133`
**建议**: **通过**。配置验证无误。

#### F20 [中] 30 秒持续时间验证通过

**文件**: `kb-hero-pipeline-regression.md:12`
**原始声明**: "30 秒固定时长吞吐量测试"
**实际事实**: `Program.cs:18`: `const int DurationSeconds = 30;`
**证据**: `Program.cs:18,177`
**建议**: **通过**。

---

## 6. 跨页交叉引用

#### F21 [低] `kb-query-invalidation.md`、`kb-perf-harnesses.md`、`kb-profiling-workflow.md`、`kb-cache-optimization.md` 均存在

所有被引用的知识页均存在于 `.knowledge/` 中：
- `kb-query-invalidation.md`（被 kb-ecs-comparison.md:47 引用）✓
- `kb-perf-harnesses.md`（被 kb-hero-pipeline-regression.md:63 引用）✓
- `kb-profiling-workflow.md`（被 kb-hero-pipeline-regression.md:40 引用）✓
- `kb-cache-optimization.md`（被 kb-hero-pipeline-regression.md:50 引用）✓

**影响**: 无发现。所有交叉引用有效。

#### F22 [低] `test.ps1` 和 `benchmark.ps1` 均存在

`kb-test-workflow.md:87-88` 引用的脚本都存在：
- `tools/scripts/test.ps1` ✓
- `tools/scripts/benchmark.ps1` ✓（但路径有 bug，见 F2）

**建议**: 见 F2。

---

## 审计汇总

### 高风险（必须修复）

| # | 文件 | 行 | 严重性 | 摘要 |
|---|---|---|---|---|
| F10 | kb-gameticksim-scenarios.md | 35-36 | **高** | M-BulletHellWarfare 场景完全不存在 |
| F16 | kb-hero-pipeline-regression.md | 65-71 | **高** | PipelineBenchmarkTests 运行 3s 非 20s，cycles/sec 数字严重过期 |
| F1 | kb-test-workflow.md | 19-53 | **高** | 测试文件表遗漏 ~15+ 个文件 |

### 中风险（推荐修复）

| # | 文件 | 行 | 严重性 | 摘要 |
|---|---|---|---|---|
| F6 | kb-ecs-comparison.md | 32 | 中 | "11 场景"应为 13 |
| F7 | kb-ecs-comparison.md | 36 | 中 | `ChunksOf<T>` 非当前 API 名称 |
| F12 | kb-gameticksim-scenarios.md | 24 | 中 | warmup 描述只适用于 MeasureTimed，不适用于主入口路径 |
| F2 | kb-test-workflow.md | 88 | 中 | `benchmark.ps1` 脚本路径有 bug |
| F3 | kb-test-workflow.md | 37 | 中 | FrameDeltaDeterminismTests 描述与实际情况有偏差 |

### 低风险（建议修复）

| # | 文件 | 行 | 严重性 | 摘要 |
|---|---|---|---|---|
| F4 | kb-test-workflow.md | 46 | 低 | IntMathTests 覆盖边界描述不完整 |
| F5 | kb-test-workflow.md | - | 低 | ComponentSchemaTests 等文件未提及 |
| F8 | kb-ecs-comparison.md | 63-72 | 低 | S10 数字不可复现 |
| F13 | kb-gameticksim-scenarios.md | 23 | 低 | 缺少 namespace 信息 |
| F15 | kb-commandstream-game-perf.md | - | 低 | 无实质问题 |
| F9 | kb-ecs-comparison.md | 51 | 低 | 512-bit bitmask 架构声明未验证 |

### 通过验证的高风险声明

| # | 文件 | 声明 | 状态 |
|---|---|---|---|
| F18 | kb-hero-pipeline-regression.md | 阈值 1642 / 997 | ✅ 数学 + 代码一致 |
| F19 | kb-hero-pipeline-regression.md | 500+500 角色 | ✅ 代码验证通过 |
| F20 | kb-hero-pipeline-regression.md | 30 秒测试 | ✅ 代码验证通过 |
| F17 | kb-hero-pipeline-regression.md | --track-observer 已删除 | ✅ 确认已移除 |
| F14 | kb-commandstream-game-perf.md | 8 项声明 | ✅ 全部通过 |

### 使用的搜索命令汇总

```bash
# 文件存在性检查
fd "DebugMetricsTests.cs" tests/
glob "tests/MiniArch.Tests/**/*.cs"
glob "tools/perf/HeroComing.Perf/Program.cs"
glob "tools/perf/GameTickSim.Perf/ScenarioBenchmark.cs"
glob "tools/perf/CommandBufferGame.Perf/Program.cs"
glob "tools/perf/CommandBuffer.Perf/Program.cs"
glob "tests/HeroPipeline.Tests/PipelineBenchmarkTests.cs"
glob "tests/SharedInfrastructure/MiniArch.SharedInfrastructure/GameTick*.cs"
glob ".knowledge/kb-*.md"

# 内容搜索
rg "ChunksOf|EachSpan" src/
rg "M-BulletHell|RunBulletHellWarfare" tools/perf/GameTickSim.Perf/
rg "track-observer|compare-old-value-tracking" tools/perf/HeroComing.Perf/
rg "WarmupTicks" tests/SharedInfrastructure/
rg "durationSeconds" tools/perf/GameTickSim.Perf/ScenarioBenchmark.cs
rg "PhaseBreakdown|phase.*break" tools/perf/GameTickSim.Perf/ScenarioBenchmark.cs

# 代码读取
read -offset specific lines (Program.cs, tests, etc.)
```
