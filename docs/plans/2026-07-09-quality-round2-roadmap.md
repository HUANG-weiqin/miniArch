# 2026-07-09 Quality Round 2 无人值守路线

> 由 `autonomous-roadmap` 技能生成。确认本文档后，用 `executing-plans` 逐里程碑执行。

## 总体目标

在 M1-M8 质量硬化基础上，对当前测试覆盖最薄弱/最深层次的 5 个领域进行深度扫描：Soak 强化、Snapshot/Clone、Hierarchy、属性测试扩展、Data Layout 紧凑性审计。

**承诺不变：不修改公共 API、不刷新 baseline、不改 docs/plans 下其他文件。**

**基线（2026-07-09，M1-M8 完成后）：**
- 分支：`main`（已 merge M1-M8 共 13 commits）
- 工作树：`E:\godot\arch\miniArch\.worktrees\quality-hardening-20260709`
- 测试：MiniArch.Tests 869 + HeroPipeline.Tests 5 = **874 passing**
- 基线验证：`dotnet test -c Release` 通过
- 性能门禁：`dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`
- 严格放行线（本轮沿用实测校准值）：**Movement ≥1850 rounds/s, Attack ≥1100 rounds/s, 内存稳定**
- Soak：640 万帧零失败（2026-07-09，M7）
- 已知不可引用文件：`docs/plans/2026-07-09-watch-api-implementation.md`、`nul`

---

## 硬约束

| 类别 | 约束 |
|------|------|
| **公共 API** | **任何时候不得修改、新增、删除、重命名 public 类型/方法/属性/字段/构造器/运算符。** |
| **数据结构偏好** | **Compact-first。** 哈希容器只有在能证明带来明显优势时才允许。 |
| **性能门禁** | 修改 `src/MiniArch/` 或 `tests/HeroPipeline.Tests/` 后必须跑 perf gate。本轮严格阈值：Movement ≥1850, Attack ≥1100。**不得传 `--update-baseline`。** |
| **编译配置** | 所有 `dotnet build`/`dotnet run`/`dotnet test` 涉及性能测量时强制 `-c Release`。 |
| **猜想验证** | 每轮审阅/扫描前 **必须先读 `.knowledge/kb-code-review-findings.md`** 安全猜想段。 |
| **知识库回写** | 每个里程碑完成后更新受影响的 `.knowledge/` 页。新发现必须按 `kb-code-review-findings.md` 格式写回。 |
| **门禁回归** | 一旦 perf gate 或 test 被破坏且 30 分钟内无法恢复，回退改动并停止当前里程碑。 |
| **避免重复工作** | 先读 `kb-design-rationale.md` §3，不提已被拒绝的优化方案。 |

---

## Milestones

| # | 类型 | 里程碑 | ⏱ | 产出 | 验证 | 可跳过 |
|---|------|--------|----|------|------|-------|
| M1 | 🔧📖 | Soak 强化 + Watch API 新模式 | 2h | Watch 纳入 soak + 证据表更新 | soak sweep + Watch 验证全 PASS | yes |
| M2 | 🔧 | Snapshot/Clone 专题：跨世界 Restore + Chunked 交叉 | 2h | 新增测试 + KB 更新 | `dotnet test -c Release` | yes |
| M3 | 🔧 | Hierarchy 专题：深层嵌套 + 大量孩子 + 级联销毁交叉 | 2h | 新增测试 + KB 更新 | `dotnet test -c Release` | yes |
| M4 | 🔧 | 属性测试 FsCheck 扩展：更多 generator + 更多模块 | 1.5h | 新增属性测试 | `dotnet test -c Release` | yes |
| M5 | 🔧📖 | Data Layout 紧凑性审计：冷/暖路径哈希容器可压紧性 | 1.5h | 审计报告 + 如有删除则 perf 证据 | `dotnet build` + 如改源码则 perf gate | yes |

---

### M1：Soak 强化 + Watch API 新模式

**类型：** 🔧 Engine + 📖 Documentation

**目标：** 针对 Watch API（ChangeWatch/TransitionWatch）设计新 soak 模式，将其纳入长周期随机操作验证。当前 soak 只验证 CommandStream record/submit/replay 的实体生命周期与 checksum 收敛，**未覆盖 Watch pull-event 模型**。

在同步游戏场景中，用户可能在 record+submit 循环外附加 Watch 监听：
- `ChangeWatch<TComponent>.Snapshot()` + `Diff()` 检测值变化
- `TransitionWatch.Snapshot()` + `Diff()` 检测实体进入/离开 query filter
- 跨 World（源 + Replay 影子）Watch diff 收敛性

**新增 soak 模式（在 `MiniArch.Soak` 中实现）：**

1. **Watch + Submit 循环**：每帧 record → Submit → ChangeWatch.Diff + TransitionWatch.Diff。验证 diff 结果与预期操作一致，且无 baseline 漂移。
2. **Watch + Replay 收敛**：源 world Submit + Watch diff；影子 world Replay 相同 delta + Watch diff。两 world 的 diff 结果必须字节级一致。
3. **Watch + RestoreState 回退**：Snapshot baseline → world 操作 → RestoreState → 再次 Snapshot + Diff。Diff 应为空（world 回到了 snapshot 时刻），验证 baseline 被正确回退。
4. **多 Watch 并发**：同一帧内多个 ChangeWatch（不同组件类型）+ 多个 TransitionWatch（不同 query filter）同时工作，无相互干扰。
5. **Watch + 结构变更交错**：组件值变更 + 实体创建/销毁 + hierarchy 变更在同一帧内混合，Watch diff 正确反映净变化。

**实现策略：**
- 优先在现有 soak 框架中新增操作类型（而非新建 soak 程序）
- 每个新模式在每帧随机选择执行（protobuf-like weighted random）
- 新增模式默认参与 checksum 对比（源 vs 影子），确保 Watch 操作不影响主世界状态收敛
- 如 soak 框架不支持 Watch 操作注入，则在 `tests/MiniArch.Tests/` 下新增 `SoakWatchTests.cs` 作为独立 soak，用固定种子跑长序列

**可能涉及的文件：**
- `tools/soak/MiniArch.Soak/`（新增 Watch 操作类型）
- `tests/MiniArch.Tests/`（如另起独立 soak 测试）
- `src/MiniArch/`（如发现 BUG 则修复）
- `.knowledge/kb-soak-test.md`
- `.knowledge/kb-code-review-findings.md`
- `.knowledge/kb-change-tracking.md`

**产出：**
- Watch API 纳入 soak 验证（新增 3+ 模式）
- 全 PASS 报告
- 如发现 FAIL：BUG 测试 + 修复 + perf gate
- KB 更新

**知识库：** 更新 `kb-soak-test.md`（Watch soak 新模式描述）、`kb-code-review-findings.md`（如有 BUG）、`kb-change-tracking.md`（如有新发现）。

**验证：**
```bash
# Watch soak 独立运行
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 50000 --quiet
# 或如果改为测试内运行：
dotnet test -c Release --filter "SoakWatch"
# 全量回归
dotnet test -c Release
```

**跳过条件：** 如果 Watch API 的 soak 测试需要大规模重构 soak 框架（超出 2h 预算），则在 `tests/` 下建独立 soak 文件缩小范围，或缩小到只验证最基本的 ChangeWatch + Submit 模式。

---

### M2：Snapshot/Clone 专题

**类型：** 🔧 Engine

**目标：** 填充 Snapshot/Clone 与交叉功能的盲区：

| 重点 | 描述 |
|------|------|
| 跨世界 Restore | World A snapshot → World B RestoreState，验证 checksum 一致 |
| Chunked + Snapshot | Chunked archetype（超过 1 segment）→ Save/Load/Restore，验证数据完整 |
| Snapshot + Hierarchy | 深层 hierarchy 快照后 Restore，层级关系保持 |
| Clone + Snapshot | Clone 实体后 Snapshot，Restore 后 clone 语义正确 |

**每个场景至少一个 fixed-seed 测试，命名 `X_SnapshotClone_<scenario>`。**

**可能涉及的文件：**
- `tests/MiniArch.Tests/`（新增或扩展现有 Snapshot 测试文件）
- `.knowledge/kb-snapshot-persistence.md`
- `.knowledge/kb-code-review-findings.md`

**产出：**
- 新增 6+ 条测试
- 如发现 BUG：BUG_ 前缀测试 + 修复 + perf gate

**知识库：** 更新 `kb-test-workflow.md` 测试表；如有发现更新 `kb-code-review-findings.md`。

**验证：**
```bash
dotnet test -c Release
# 如涉及 src 改动：
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

**跳过条件：** 发现需要大量基础设施才能测试的场景，缩小范围后跳过剩余格子。

---

### M3：Hierarchy 专题

**类型：** 🔧 Engine

**目标：** Hierarchy 模块的深度覆盖：

| 重点 | 描述 |
|------|------|
| 深层嵌套 | 10+ 层 AddChild 链，验证各级 parent/child 关系 |
| 大量孩子 | 单个 parent 下 100+ child，批操作正确性 |
| 级联销毁 + Snapshot | Cascade destroy → Snapshot → Restore，层级完整性 |
| Hierarchy + CommandStream | 录制期 AddChild/RemoveChild 交错操作 → Submit 与 Replay 收敛 |
| 混合：嵌套 + 级联 + 跨 batch | 同一帧内多个 batch 的层级操作 |

**每个场景至少一个 fixed-seed 测试，命名 `X_Hierarchy_<scenario>`。**

**可能涉及的文件：**
- `tests/MiniArch.Tests/`（新增测试文件或扩展现有）
- `.knowledge/kb-hierarchy-runtime.md`
- `.knowledge/kb-code-review-findings.md`

**产出：**
- 新增 6+ 条测试
- 如发现 BUG：BUG_ 前缀测试 + 修复 + perf gate

**知识库：** 更新 `kb-test-workflow.md`；如有发现更新 `kb-code-review-findings.md` 和 `kb-hierarchy-runtime.md` 坑点段。

**验证：**
```bash
dotnet test -c Release
```

**跳过条件：** 如果 Hierarchy 模块已有足够的覆盖（M3 中已覆盖基础组合），可跳过深层扩展。

---

### M4：属性测试 FsCheck 扩展

**类型：** 🔧 Engine

**目标：** 扩展现有的 `SerializationRoundtripPropertyTests.cs`（当前 200 次 FsCheck iteration），增加：
1. **iteration 数翻倍**：从 200 → 500
2. **更多模块的属性测试**：
   - CommandStream 属性：任意操作序列 → Submit 后 entity count 非负
   - Hierarchy 属性：任意 AddChild/RemoveChild 序列 → 无环、无孤儿
   - World lifecycle 属性：任意 Create/Destroy 序列 → entity count 正确

**重点：** 属性测试应验证不变式（invariant），而不是模拟特定场景。优先选取"如果违反一定出 bug"的不变式。

**可能涉及的文件：**
- `tests/MiniArch.Tests/PropertyBased/SerializationRoundtripPropertyTests.cs` 或新建 `*PropertyTests.cs`
- `.knowledge/kb-test-workflow.md`
- `.knowledge/kb-code-review-findings.md`

**产出：**
- 新增 5+ 条属性测试（每条含 FsCheck `Prop`）
- 如发现 BUG：BUG_ 前缀回归测试 + 修复 + perf gate

**知识库：** 更新 `kb-test-workflow.md` 属性测试段。

**验证：**
```bash
dotnet test -c Release
```

**跳过条件：** 如果属性测试框架在当前项目设置下不能稳定工作（FsCheck 的 seed 复现、CI 兼容性），可缩小范围或跳过。

---

### M5：Data Layout 紧凑性审计

**类型：** 🔧 Engine + 📖 Documentation

**目标：** 扫描 `src/MiniArch/` 下冷/暖路径中的 `Dictionary`/`HashSet`/map-like side table，评估能否用 dense array / bitset / SoA / entity-id 直索引 / 小规模线性扫描替代。

**范围：** M5 只查了热路径（Submit/Query 循环内）。本里程碑覆盖 **非热路径但数据结构不合理** 的情况。

**规则：**
- 必须有**可测量的优势**才能替换（吞吐提升 ≥3%，或内存节省 ≥10%，或实现复杂度明显降低）
- 如果无法替换：记录到 `kb-code-review-findings.md` 为"已排除猜想"
- 如果可替换：替换 + perf 证据 + 测试

**可能涉及的文件：**
- `src/MiniArch/` 下任意文件
- `.knowledge/kb-code-review-findings.md`
- `.knowledge/kb-design-rationale.md`（如果新增或修改 §3 条目）

**产出：**
- 审计报告（每个候选 + 结论 + 证据）
- 如替换：perf 证据 + 测试

**知识库：** 更新 `kb-code-review-findings.md`；如有需要更新 `kb-design-rationale.md`。

**验证：**
```bash
dotnet build -c Release
dotnet test -c Release
# 如改 src：
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

**跳过条件：** M5 已经对所有热路径做了审计；冷/暖路径的哈希容器大多在诊断/缓存/初始化路径上，替换价值可能极低。可快速扫描后跳过。

---

## Blocker Decision Tree

```
Stuck → 30min timer
  ├─ Can fix → fix（test + perf gate + KB）
  ├─ Cannot fix but baseline OK → skip milestone, document
  └─ Cannot fix + baseline broken → restore
       └─ git stash / git checkout . → recover → report
```

| 症状 | 行动 |
|------|------|
| `dotnet build` 失败 | 修第一个错误 |
| `dotnet test` 测试失败 | 确认是否是新测试的预期 RED |
| Perf gate 失败 | 回退改动，记录到 kb-code-review-findings.md |
| Soak FAIL | 缩小复现 → BUG_ 测试 → 修复 → 验证 |
| 30 分钟卡住 | 跳过当前里程碑，记录原因 |

---

## 显式非目标

| 非目标 | 原因 |
|--------|------|
| ❌ **修改公共 API** | 硬约束。任何 public API 改动都不允许。 |
| ❌ **执行 Watch API 计划** | 另一任务，涉及公共 API 变更。 |
| ❌ **刷新 baseline（`--update-baseline`）** | 只有人工确认才做。 |
| ❌ **无证据重构** | 任何代码改动必须有测试或 perf 证据支撑。 |
| ❌ **为了方便引入哈希容器** | Compact-first。新增必须证明优势。 |
| ❌ **引入新 NuGet 依赖** | 除非绝对必要且经人工审批。 |
| ❌ **修改/删除 `docs/plans/2026-07-09-watch-api-implementation.md` 或 `nul`** | 计划产物不包含这些文件。 |

---

## 执行契约

确认后，执行 agent 获得以下授权：

### 必须做
- **逐里程碑执行**，按 M1→M2→M3→M4→M5 顺序
- **每里程碑完成后**：
  1. 运行验证命令并确认通过
  2. 更新 `.knowledge/` 文件
  3. 本地 commit：`git add -A && git commit -m "feat: R2-M<N> <title>"`
  4. 检查 `git status` 和 `git diff HEAD`
- **每轮审阅/扫描前**先读 `kb-code-review-findings.md` 安全猜想段
- **所有性能测量用 `-c Release`**
- **所有正确性验证用 `dotnet test -c Release`**

### 绝不能做
- **不 push**
- **不 scope creep**
- **不改 public API**
- **不传 `--update-baseline`**
- **不引用/修改 `docs/plans/2026-07-09-watch-api-implementation.md` 或 `nul`**

### 中止条件
- `git stash` / `git checkout .` 后仍无法恢复 → **STOP**。向用户报告。

---

## 报告模板

完成后输出必须按以下结构：

```
## 里程碑进度
- R2-M1 <title>: [done/partial/skipped]
- R2-M2 <title>: [done/partial/skipped]
- ...

## 测试状态
- 当前测试数：XXX
- `dotnet test -c Release`: [PASS/FAIL]
- 新增/修改测试数：YYY

## 发现的真 bug
| BUG_ 测试 | 模块 | 状态 |

## 已排除猜想
| # | 模块 | 猜想 | 结论 | 验证方式 |

## Perf / Soak 证据
- HeroComing.Perf --check-baseline：Movement XXX / Attack XXX（阈值 YYY）
- Soak 数据：seed × frames × result

## 知识库更新
- `kb-code-review-findings.md`：更新了 / 未改
- `kb-soak-test.md`：更新了 / 未改

## 下一步最小行动
- 继续 / 报告完成 / [需人工介入]
```
