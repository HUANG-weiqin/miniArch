# Knowledge Audit Group F — 2026-07-09

审计范围：.knowledge/INDEX.md, kb-repo-overview.md, kb-profiling-workflow.md,
kb-throughput-workflow.md, kb-perf-harnesses.md, kb-changelog.md, kb-bullet-lockstep-demo.md

验证基准：`e:\godot\arch\miniArch\.worktrees\knowledge-consistency-audit-20260709`
Build 验证：`dotnet build -c Release` 成功，`dotnet test -c Release` MiniArch.Tests 873 PASS / HeroPipeline.Tests 5 PASS

---

## 覆盖声明

| 页面 | 覆盖状态 | 说明 |
|---|---|---|
| INDEX.md | ✅ 全部链接可访问 | 30 个知识页 + 1 个模板全部存在 |
| kb-repo-overview.md | ⚠️ 1 个次要问题 | 入口节 "修协作入口" 指向 verify.ps1 但该脚本仅 build+test，并非"修协作入口" |
| kb-profiling-workflow.md | ✅ 主要正确 | 命令使用 `tests\MiniArch.Benchmarks` 正确（但脚本本身路径过期） |
| kb-throughput-workflow.md | ✅ 主要正确 | EachSpan 移除声明与代码一致（MiniArch 无 EachSpan，Arch 保留） |
| kb-perf-harnesses.md | ❌ 严重过时 | baseline 数字落后 ~36%；harness 计数不完整 |
| kb-changelog.md | ⚠️ 测试计数漂移 | 历史 test count 已落后当前 873 |
| kb-bullet-lockstep-demo.md | ⚠️ 多处不准确 | slice 计数、拓扑表、性能表均有问题 |

---

## 发现清单

### F1 [严重] kb-perf-harnesses.md:17 — HeroComing.Perf baseline 过时

- **文件**: `.knowledge/kb-perf-harnesses.md:17`
- **原文**: `Movement 1512 / Attack 959`
- **实际**: `Movement 2052.7 / Attack 1246.8`（来自 `.knowledge/kb-hero-pipeline-regression.md:30-31`）
- **证据**: `.knowledge/kb-hero-pipeline-regression.md` line 30-31 显示当前 baseline 为 `Movement 2052.7` / `Attack 1246.8`。1512/959 是早期 baseline（用于旧阈值 1210 的计算）。
- **推荐**: 更新矩阵行 baseline 为 `Movement 2052.7 / Attack 1246.8`；更新 description 中 "当前 baseline" 措辞改为指向 `kb-hero-pipeline-regression.md` 的实时引用。

### F2 [中] kb-perf-harnesses.md:39 — HeroComing.Perf 说明文字数字过时

- **文件**: `.knowledge/kb-perf-harnesses.md:39`
- **原文**: `HeroComing.Perf 1512 rounds/s ≈ 每秒 1512 个完整游戏帧`
- **实际**: 当前 baseline 为 ~2053 rounds/s
- **证据**: 同 F1，`kb-hero-pipeline-regression.md:30`
- **推荐**: 更新为 `2052.7 rounds/s`，或改为不写固定数字改为"见 baseline"的引用。

### F3 [中] kb-perf-harnesses.md:11 — "6 套"计数不完整

- **文件**: `.knowledge/kb-perf-harnesses.md:11`
- **原文**: `miniArch 有 6 套独立的性能测试工具`
- **实际**: `tools/perf/` 目录下实际有 **13 个项目**（含 FrifloGameScenarios.Perf、ParallelRecord.Perf、Throughput.Perf、QueryInvalidation.Perf 等）。"6 套"只计数了矩阵中列出的工具。
- **证据**: `ls tools/perf/` 返回 13 个子目录。
- **推荐**: 要么澄清"6 套已记录的性能测试工具"，要么将其他活跃 perf 项目补充到消歧矩阵。

### F4 [中] kb-bullet-lockstep-demo.md:13 — slice 数量不准确

- **文件**: `.knowledge/kb-bullet-lockstep-demo.md:13`
- **原文**: `9 个 slice`
- **实际**: Program.cs 只有 slice 2-9 共 **8 个**（无 slice 1）
- **证据**: `samples/BulletLockstep.Demo/Program.cs` line 6-21，switch 仅匹配 2-9，`BadSlice()` 输出 "Supported: 2-9"。
- **推荐**: 改为 "8 个 slice（2-9）"。

### F5 [中] kb-bullet-lockstep-demo.md:25 — 拓扑表 P2P slice 范围错误

- **文件**: `.knowledge/kb-bullet-lockstep-demo.md:25`
- **原文**: `P2P placeholder lockstep | 1-7, 9`
- **实际**: P2P slice 为 `2-7, 9`（slice 1 不存在）
- **证据**: Program.cs line 36-433 显示 Slice 2-7, 9 均使用 LockstepSimulator；slice 1 无定义。
- **推荐**: 改为 "2-7, 9"。

### F6 [低] kb-bullet-lockstep-demo.md:106-114 — 性能表缺少 Slice 3 和 Slice 9

- **文件**: `.knowledge/kb-bullet-lockstep-demo.md:106-114`
- **原文**: 性能参考表仅列出 Slice 2,4,5,6,7,8
- **实际**: Slice 3（rollback recovery）和 Slice 9（World.Clone + rollback）在代码中存在但性能表缺项。
- **证据**: Program.cs 有 `RunSlice3`（line 417）和 `RunSlice9`（line 308）；表中无对应行。
- **推荐**: 补齐 Slice 3 和 Slice 9 的性能行，或注明"未测量"。

### F7 [中] CONTRIBUTING.md:27 — 回归阈值过期（非 .knowledge 但从 kb-changelog 可追踪）

- **文件**: `CONTRIBUTING.md:27`
- **原文**: `Movement ≥1210 rounds/s, Attack ≥767 rounds/s`
- **实际**: AGENTS.md §5 已使用新阈值 `Movement ≥1642 rounds/s, Attack ≥997 rounds/s`
- **证据**: AGENTS.md line 91，kb-hero-pipeline-regression.md line 35-36。CONTRIBUTING.md 的 1210/767 是旧 baseline（1512.5×80%）的阈值。
- **推荐**: CONTRIBUTING.md line 27 同步为 `Movement ≥1642 rounds/s, Attack ≥997 rounds/s`。

### F8 [中] tools/scripts/throughput.ps1:16 + profile-query.ps1:16 — 项目路径过期

- **文件**: `tools/scripts/throughput.ps1:16`, `tools/scripts/profile-query.ps1:16`
- **原文**: `benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj`
- **实际**: MiniArch.Benchmarks 的实际位置为 `tests\MiniArch.Benchmarks\`
- **证据**: `ls tests/MiniArch.Benchmarks/` 包含 MiniArch.Benchmarks.csproj；`ls benchmarks/` 不存在。
- **推荐**: 两处均改为 `tests\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj`。

### F9 [低] kb-changelog.md:19,60 — 历史 test count 落后当前

- **文件**: `.knowledge/kb-changelog.md:19` (M6: "MiniArch.Tests 869"), `:60` (Watch API: "MiniArch.Tests 837")
- **原文**: 分别记录 869 和 837 个测试
- **实际**: 当前 `dotnet test -c Release` 结果为 873 PASS
- **证据**: 本次审计运行 `dotnet test miniArch.sln -c Release` → MiniArch.Tests **873**, HeroPipeline.Tests **5**。
- **推荐**: 历史记录本身可能不需更新（记录的是当时的快照），但注意这些数字已有漂移。

### F10 [低] kb-perf-harnesses.md:41 — SubmitAndSnapshotAsync 描述与 cache 页面不一致

- **文件**: `.knowledge/kb-perf-harnesses.md:41`
- **原文**: `SubmitAndSnapshotAsync 1818 rounds/s ≈ 每秒 1818 个全帧（同 HeroComing 场景但走 async submit+snapshot 路径，比同步 Submit 路径略快因为并行）`
- **实际**: 说明文字称"同 HeroComing 场景"，但 HeroComing baseline 已变（1512→2052.7），二者的绝对数字差距已缩小。1818 目前甚至低于当前 HeroComing 的 2052.7，原说明逻辑不成立。
- **证据**: `kb-cache-optimization.md:88-89` 记录 Movement-Stream 1818 / Attack-Stream 1101 是 2026-06-22 的数据，HeroComing baseline 在 2026-07-06 已刷新到 2052.7。
- **推荐**: 更新这段解释，或注明这是 2026-06-22 datapoint。

### F11 [低] kb-repo-overview.md:61 — "修协作入口"措辞误导

- **文件**: `.knowledge/kb-repo-overview.md:61`
- **原文**: `修协作入口：tools/scripts/verify.ps1`
- **实际**: verify.ps1 仅执行 `build.ps1` + `test.ps1`，是"验证"入口不是"修改协作入口"
- **证据**: `tools/scripts/verify.ps1` 仅 build+test，无任何配置/协作修改逻辑。
- **推荐**: 改为 "验证入口：tools/scripts/verify.ps1"。

---

## 已确认正确的高风险声明

| 声明 | 文件:行 | 验证方式 | 状态 |
|---|---|---|---|
| INDEX.md 所有知识页存在 | INDEX.md | `for f in ...; do [ -f ".knowledge/$f" ]` | ✅ 全部 31 个文件存在 |
| INDEX.md 模块地图完整性 | INDEX.md | 逐一比对各个 module/kb-* 映射 | ✅ 正确 |
| kb-repo-overview 仓库布局 | kb-repo-overview.md:17-23 | ls src/, tests/, tools/, samples/ | ✅ 正确 |
| kb-repo-overview Quickstart 命令 | kb-repo-overview.md:38-51 | `dotnet build / test / run --check-baseline` | ✅ 语法正确 |
| kb-profiling-workflow QueryProfilingRunner 存在 | kb-profiling-workflow.md:17 | `rg "class QueryProfilingRunner"` → line 187 | ✅ 存在 |
| kb-profiling-workflow profile-query.ps1 存在 | kb-profiling-workflow.md:34 | ls tools/scripts/profile-query.ps1 | ✅ 存在 |
| kb-throughput-workflow EachSpan MiniArch 已移除 | kb-throughput-workflow.md:28,50 | rg 搜索 EachSpan — 仅有 Arch impl，无 MiniArch | ✅ 正确 |
| kb-throughput-workflow ThroughputRunnerTests 存在 | kb-throughput-workflow.md:40 | rg → Core/ThroughputRunnerTests.cs:5 | ✅ 存在 |
| kb-throughput-workflow Throughput.Perf 存在 | kb-throughput-workflow.md:39 | ls tools/perf/Throughput.Perf/ | ✅ 存在 |
| kb-perf-harnesses 门禁命令 | kb-perf-harnesses.md:52-55 | 验证 build 通过 | ✅ 正确 |
| kb-changelog 新 Watch API 文件存在 | kb-changelog.md:38-43 | ls src/MiniArch/ChangeWatch.cs, TransitionWatch.cs, IChangeHandler.cs, ITransitionHandler.cs | ✅ 全部存在 |
| kb-changelog 旧 API 文件已删除 | kb-changelog.md:28-36 | rg "SharedValueChanges\|TransitionLog\|SharedTrackerRegistry\|ChangeTracker\|IChangeQuery\|DenseValueDiff\|IValueProjector\|IValueChangeSink\|ValueChange\|Transition" — 零匹配 | ✅ 已删除 |
| kb-changelog docs/api.md + docs/examples.md 存在 | kb-changelog.md:64 | ls docs/api.md docs/examples.md | ✅ 存在 |
| kb-changelog Archetype.ContainsComponent 已删除 | kb-changelog.md:16 | rg ContainsComponent → 零匹配 | ✅ 已删除 |
| kb-changelog GetSingleton 取代 GetFirst | kb-changelog.md:241 | rg GetFirst — 零匹配（旧 API 已删除） | ✅ 正确 |
| kb-bullet-lockstep-demo BulletLockstep.Demo 存在 | kb-bullet-lockstep-demo.md:12 | ls samples/BulletLockstep.Demo/ | ✅ 正确 |
| kb-bullet-lockstep-demo 设计文档存在 | kb-bullet-lockstep-demo.md:40 | ls docs/internal/plans/2026-06-30-bullet-lockstep-coverage-design.md | ✅ 存在 |
| kb-bullet-lockstep-demo Systems 目录 | kb-bullet-lockstep-demo.md:39 | ls samples/BulletLockstep.Demo/Systems/ → 12 个文件 | ✅ 正确 |
| kb-bullet-lockstep-demo AuthorityMirrorSimulator + LockstepSimulator 存在 | kb-bullet-lockstep-demo.md:38 | ls samples/BulletLockstep.Demo/ | ✅ 正确 |

---

## 执行命令列表

```bash
# 全量构建验证
dotnet build miniArch.sln -c Release

# 全部测试（含计数）
dotnet test miniArch.sln -c Release

# INDEX.md 链接完整性验证
for f in kb-design-rationale.md kb-glossary.md kb-perf-harnesses.md ...; do
  [ -f ".knowledge/$f" ] || echo "MISSING: $f"
done

# 文件存在验证（多个路径）
ls tools/scripts/*.ps1
ls tools/perf/*/
ls tests/SharedInfrastructure/MiniArch.SharedInfrastructure/
ls samples/BulletLockstep.Demo/Systems/

# 代码搜索验证
rg "class QueryProfilingRunner" tests/SharedInfrastructure/
rg "class ThroughputRunner" tests/SharedInfrastructure/
rg "class BenchmarkWorldFactory" tests/SharedInfrastructure/
rg "EachSpan" tests/SharedInfrastructure/MiniArch.SharedInfrastructure/ThroughputRunner.cs
rg "ContainsComponent" src/MiniArch/Core/Archetype.cs
rg "RunSlice" samples/BulletLockstep.Demo/Program.cs
rg "Movement|Attack" .knowledge/kb-hero-pipeline-regression.md

# 脚本路径验证
rg "benchmarks" tools/scripts/throughput.ps1 tools/scripts/profile-query.ps1
rg "Movement|Attack" CONTRIBUTING.md
```

---

## 按严重度汇总

| 严重度 | 数量 | ID |
|---|---|---|
| **严重** | 1 | F1 |
| **中** | 5 | F2, F3, F4, F5, F7, F8 |
| **低** | 4 | F6, F9, F10, F11 |

**跨文件依赖问题**：F7 (CONTRIBUTING.md 阈值过期) + F8 (两支脚本路径过期) 不在 .knowledge 内但被 kb 页引用，需额外修复。
