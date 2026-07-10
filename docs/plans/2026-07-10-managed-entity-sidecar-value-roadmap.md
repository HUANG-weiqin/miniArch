# 2026-07-10 Managed Entity Sidecar 价值验证无人值守路线

> 由 `autonomous-roadmap` 技能生成。确认本文档后，用 `executing-plans` 在独立 worktree 中逐里程碑执行。本文档只规划，不执行。

## 1. Goal + Baseline

### 目标

独立探索 `Entity -> managed object` sidecar API 是否值得进入 miniArch：先假设“不值得做”，只有在正确性、性能、API/序列化价值上拿到充分证据，才推荐 Go；否则输出 No-Go 报告，不实现生产 API。

最终只允许两种结论：

1. **Go**：证明库级 API 明显优于用户合理手写，并给出最佳实现/API/理由。
2. **No-Go**：无法证明优势，不进入库，只提交证据报告。

### 当前基线

- 当前分支：`main`（`git status --short --branch`：`## main...origin/main`）
- 最近提交：`6088b6e feat: reject LayoutKind.Auto components for cross-host determinism`
- 已记录测试基线：`MiniArch.Tests 900 + HeroPipeline.Tests 5` 通过（见 `.knowledge/kb-component-bucket-index-mvp-report.md`）
- 执行 agent 开始前必须重跑基线：

```bash
dotnet test -c Release --nologo -v q
```

- 如任何里程碑修改 `src/MiniArch/` 或 `tests/HeroPipeline.Tests/`，还必须运行：

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

## 2. Hard Constraints

### 项目级硬约束

- 开始前必须阅读：`AGENTS.md`、`.knowledge/INDEX.md`、`.knowledge/kb-design-rationale.md`、`.knowledge/kb-core-ecs.md`、`.knowledge/kb-component-bucket-index-mvp-report.md`。
- 文档/计划/报告优先中文；源码标识符和代码注释使用英文。
- 性能测量永远使用 `-c Release`。
- 不修改 `src/MiniArch/Core/`。
- 不承诺 managed sidecar 参与 lockstep、FrameDelta、Snapshot、Checksum、Replay。
- managed sidecar 定位为 **host-local / optional / non-deterministic / non-thread-safe**。
- 先证伪再证成：不能只打败 naive `Dictionary<Entity,T>`；必须与“会写代码的用户”的合理手写版本比较。
- 若新增/更新 `.knowledge/`，必须遵守模板和 `INDEX.md` 同步规则。
- 任何生产源码改动完成前必须使用 `verification-before-completion` 流程确认测试输出。

### 本次会话级约束

- 使用独立 worktree，例如 `.worktrees/managed-sidecar-value/`；不得污染当前 `main` 工作树。
- 探索原型优先放在 `tools/perf/ManagedEntityMap.ValueLab/` 或 `tests/*`，不要直接把 public API 塞进 `src/MiniArch/`。
- 如果证据不足，必须停止在报告阶段；不要“顺手实现”。
- 用户要的不是一个漂亮 API，而是证明：库级 API 比用户自己手写更值得。

### Go / No-Go 判定门槛

必须同时回答以下问题：

1. **正确性**：库版是否系统性规避用户手写常见 bug？是否有可运行测试证明？
2. **性能**：库版是否明显优于 `Dictionary` 系合理实现，并接近 raw dense array？
3. **API 价值**：用户写到正确版本需要多少代码、多少隐含知识、多少坑？
4. **序列化价值**：序列化边界是否是库版明显优于手写的核心理由？若不是，v1 是否应排除序列化？

建议默认门槛：

- `Get/TryGet/Set` 在 100k live entities 下，应显著快于 `Dictionary<int, Entry<T>>` 合理实现；若无法达到，必须有更强正确性/序列化证据补足。
- 相比 raw dense array unsafe baseline，安全库版慢幅应可解释；若慢幅 >20%，必须说明为什么仍值得。
- 若 competent user implementation 在 <50 行内即可达到同等正确性和性能，则倾向 No-Go。

## 3. Milestones

| # | Type | Milestone | Time | Output | Verification | Skipable |
|---|------|-----------|------|--------|-------------|----------|
| M0 | 📖 | Worktree 与基线确认 | 0.5h | 独立 worktree + 基线测试结果 | `git status` + `dotnet test -c Release --nologo -v q` | no |
| M1 | 📖 | 竞品/用户手写模型定义 | 1h | 对比实现清单 + 正确性场景矩阵 | 文档/注释审阅，无需测试 | no |
| M2 | 🔧 | ValueLab benchmark harness | 2h | 新 perf 项目，包含 naive/competent/dense/library prototype | `dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab` | no |
| M3 | 🔧 | 正确性红队场景 | 1.5h | slot reuse/zombie/null/placeholder 等测试或 harness assertions | targeted test/perf harness PASS | no |
| M4 | 🔧 | Release 性能与内存矩阵 | 2h | Get/Set/TryGet/Remove/Align/GC 表格 | Release perf runs，保存原始结果 | no |
| M5 | 🔧📖 | 序列化价值探索 | 2h | codec/remap/WorldSnapshot 关系结论；可选 PoC | targeted smoke test 或设计报告 | yes |
| M6 | 📖 | Go/No-Go 决策报告与最佳实现建议 | 1.5h | 最终报告 + 知识库更新 | `dotnet test -c Release --nologo -v q`；如改源码跑 perf gate | no |

---

### M0: Worktree 与基线确认

**Type:** 📖 Documentation / Safety

**Goal:** 在独立 worktree 中建立可回滚探索环境，并确认当前基线可通过。

**Files likely touched:**

- 不改生产文件。
- 可能创建 worktree：`.worktrees/managed-sidecar-value/`。

**Output:**

- 独立 worktree 已创建。
- 记录当前 commit hash、分支、baseline test 输出。
- 若 baseline 已坏，停止并报告，不开始探索。

**Knowledge:** 无新增知识；只记录在最终报告中。

**Verification:**

```bash
git status --short --branch
dotnet test -c Release --nologo -v q
```

**Skip if:** 不可跳过。

---

### M1: 竞品/用户手写模型定义

**Type:** 📖 Documentation / Design

**Goal:** 明确定义“用户自己手写”的合理边界，防止只打败 strawman。

**必须纳入比较的实现：**

1. **Naive Dictionary**：`Dictionary<Entity, T>`。
2. **Competent Dictionary**：`Dictionary<int, Entry<T>>`，entry 内含 version/value，并用 `world.IsAlive(entity)` 校验。
3. **Raw Dense Array Unsafe**：`T?[] values + int[] versions`，不做完整安全检查，作为性能上界/错误示例。
4. **Competent Dense User**：用户可能手写的正确 dense array：`world.IsAlive` + version + Align + Remove。
5. **Proposed Library Map**：候选库 API/实现。

**正确性场景矩阵：**

- Destroy 后、Align 前不能读到僵尸引用。
- Destroy + slot reuse 后，旧 entity 不能读到旧对象或新对象。
- 新 entity 未 `Set` 前不能误读旧 slot 对象。
- `Remove(entity)` 只删 sidecar，不影响 World。
- `Align()` 后僵尸引用被清空，可被 GC。
- `Set(entity, null)` 行为明确：建议禁止，null 表示 absence。
- placeholder / negative id / out-of-range 安全失败，不数组越界。
- 跨 World entity handle：建议定义为调用方错误；如无法检测，文档必须明确。

**Files likely touched:**

- `tools/perf/ManagedEntityMap.ValueLab/README.md` 或最终报告草稿。

**Output:**

- 一张实现对比表：每个实现支持哪些正确性场景、预期性能、代码复杂度。
- 明确 Go/No-Go 判定阈值，如需调整本 roadmap 默认门槛，必须写明理由。

**Knowledge:** 最终并入 M6 知识库；M1 不单独更新 KB。

**Verification:**

```bash
# 文档审阅即可；M2 开始用可运行 harness 验证
git diff -- docs/plans tools/perf/ManagedEntityMap.ValueLab
```

**Skip if:** 不可跳过；没有合理竞品定义，后续数据无意义。

---

### M2: ValueLab benchmark harness

**Type:** 🔧 Engine / Experiment

**Goal:** 建立可重复运行的 Release perf harness，比较所有用户手写版本与候选库版本。

**Files likely touched:**

- 新增：`tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj`
- 新增：`tools/perf/ManagedEntityMap.ValueLab/Program.cs`
- 可选：`tools/perf/ManagedEntityMap.ValueLab/README.md`

**Harness 要求:**

- 不修改 `src/MiniArch/Core/`。
- 使用真实 `MiniArch.World` 和真实 `Entity`，不要 mock entity 语义。
- 支持参数：entity count（10k/100k/1M）、destroy ratio、operation mix、repetitions、warmup。
- 输出 CSV 或 markdown 表格，至少包含：ops/s、ns/op、allocated bytes、GC count、peak/steady memory。
- 每个实现必须共享同一 workload：相同 entity 创建序列、destroy 序列、slot reuse 序列、query/access 序列。

**候选库原型最低 API:**

```csharp
public sealed class ManagedEntityMapPrototype<T> where T : class
{
    public ManagedEntityMapPrototype(World world, int initialCapacity = 256);
    public void Set(Entity entity, T value);
    public T Get(Entity entity);
    public bool TryGet(Entity entity, out T value);
    public bool Remove(Entity entity);
    public void Align();
    public void Clear();
    public void TrimExcess();
    public int Count { get; }
}
```

**关键实现原则:**

- `world.IsAlive(entity)` 是唯一存活真相。
- `_versions[id]` 只是本 map 的占用/绑定标记，不是 liveness oracle。
- `Align()` 只清理，不补齐。
- `Set(null)` 禁止。
- 非线程安全，harness 不测试并发。

**Output:**

- 可运行 perf 项目。
- 所有实现可在同一命令中横向比较。

**Knowledge:** M6 汇总；本里程碑不单独写 KB，除非发现会影响现有知识页的事实。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 10000 --repetitions 3
```

**Skip if:** 不可跳过；没有 harness 就没有证据。

---

### M3: 正确性红队场景

**Type:** 🔧 Engine / Experiment

**Goal:** 用可执行场景证明哪些用户手写方案会错、候选库版如何避免。

**Files likely touched:**

- `tools/perf/ManagedEntityMap.ValueLab/Program.cs`
- 可选新增：`tests/MiniArch.Tests/UserApi/ManagedEntityMapValueLabTests.cs`（如果决定把 correctness 放到测试项目；注意这仍是探索，不代表生产 API）

**必测场景:**

1. **ZombieBeforeAlign**：Destroy 后、Align 前，`TryGet(oldEntity)` 必须 false；`Get` 必须抛。
2. **SlotReuseOldHandle**：旧 handle false，新 handle未 Set 前 false。
3. **SlotReuseAfterSetNew**：Set 新 handle 后，旧 handle仍 false，新 handle true。
4. **AlignClearsZombie**：Destroy 多个实体后 `Align()` 清空 `_values`，`Count` 正确下降。
5. **RemoveDoesNotDestroyWorld**：`Remove(entity)` 后 `world.IsAlive(entity)` 仍 true。
6. **InvalidEntitySafety**：default entity、negative id、placeholder、out-of-range 均安全失败。
7. **NullPolicy**：`Set(entity, null)` 行为与设计一致（建议 throw）。

**Output:**

- 每种实现的 correctness 表：PASS/FAIL，并标注失败原因。
- 对 naive/unsafe 手写失败场景做最小复现。
- 如果 competent dense user 也全 PASS，必须记录其代码量和复杂度。

**Knowledge:** M6 汇总到新 KB 或最终报告。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --correctness-only
```

如放入测试项目：

```bash
dotnet test -c Release --filter "ManagedEntityMapValueLab"
```

**Skip if:** 不可跳过；正确性是 API 价值的第一理由。

---

### M4: Release 性能与内存矩阵

**Type:** 🔧 Engine / Experiment

**Goal:** 用 Release 数据量化库版与用户手写的真实差异。

**Files likely touched:**

- `tools/perf/ManagedEntityMap.ValueLab/*`
- `artifacts/managed-sidecar-value/` 或 `tools/perf/ManagedEntityMap.ValueLab/results/`（保存原始结果；不要提交巨大文件）

**测量矩阵:**

| 维度 | 值 |
|---|---|
| Entity count | 10k, 100k, 1M |
| Live mapping ratio | 10%, 50%, 100% |
| Destroy ratio before Align | 0%, 10%, 50% |
| Operation | Set, Get, TryGet hit, TryGet miss, Remove, Align, Clear, TrimExcess |
| Implementation | Naive Dictionary, Competent Dictionary, Raw Dense Unsafe, Competent Dense User, Proposed Library Map |

**输出表至少包含:**

- ops/s 或 ns/op。
- Gen0/Gen1/Gen2 次数。
- allocated bytes/op。
- retained memory 估算。
- Align 扫描范围：capacity vs `_maxTouchedExclusive`。
- 是否触发 LOH（大 `T?[]` / `int[]` 数组）。

**必须特别分析:**

- `world.IsAlive(entity)` 安全校验的边际成本。
- `_maxTouchedExclusive` 对 Align 的收益。
- `Dictionary<int, Entry<T>>` 与 dense array 在 miss/hit 混合场景下的差异。
- 100k slot Align 是否可作为 cold path 接受。

**Output:**

- 一份性能/内存矩阵。
- 一个明确结论：性能是否足以构成库级 API 价值。

**Knowledge:** M6 汇总。

**Verification:**

```bash
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 10000 --repetitions 5
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 100000 --repetitions 5
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 1000000 --repetitions 3
```

**Skip if:** 不可跳过；没有性能证据不得推荐 Go。

---

### M5: 序列化价值探索

**Type:** 🔧📖 Engine / Design Experiment

**Goal:** 判断序列化是否应成为 v1 价值主张，还是推迟到 v2。

**必须回答:**

1. 序列化是否必须由用户提供 codec？默认答案应是：是。
2. 最小 codec 形态是什么？例如：

```csharp
public interface IManagedEntityMapCodec<T> where T : class
{
    void Write(BinaryWriter writer, T value);
    T Read(BinaryReader reader);
}
```

3. `MapSnapshot` 应存什么？候选：`(entity.Id, entity.Version, payload)`。
4. `WorldSnapshot` load 后是否保持 entity id/version？如保持，可直接 bind；如不保持，是否需要 entity remap？
5. 跨 host load 后 managed value 是否有确定性要求？建议不承诺，只做 host-local restore。
6. 如果 value 是 Godot/Unity 对象引用，序列化应该存 asset id/path，而不是对象本身；模块只编排，不理解资源系统。

**Files likely touched:**

- `tools/perf/ManagedEntityMap.ValueLab/*`（可选 codec smoke test）
- `docs/plans/` 或最终报告草稿

**Output:**

- 明确建议：序列化进 v1 / v2 / 不做。
- 若建议 v1，给出最小 API 和一个 smoke test。
- 若建议 v2，说明 v1 为什么只做 runtime map。

**Knowledge:** M6 汇总。

**Verification:**

```bash
# 如果实现 codec smoke test：
dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --serialization-smoke
```

**Skip if:** 若 M2-M4 已经 No-Go（性能/正确性/API 价值不足），可跳过序列化 PoC，只写设计结论。

---

### M6: Go/No-Go 决策报告与最佳实现建议

**Type:** 📖 Documentation / Decision

**Goal:** 收束所有证据，给出可审计的产品判断。

**Files likely touched:**

- 新增：`docs/plans/2026-07-10-managed-entity-sidecar-value-report.md` 或同目录 report。
- 若结论对未来 agent 有长期价值，新增：`.knowledge/kb-managed-entity-sidecar-evaluation.md`，并更新 `.knowledge/INDEX.md`。
- 如果只是短期探索且 No-Go，可将结论保留在 `docs/plans/*-report.md`，但仍需在相关 KB（如 `kb-design-rationale.md` 或新 KB）记录“No-Go 原因”，避免重复探索。

**报告必须回答:**

1. 我们比用户手写强在哪里？
2. 证据是什么？包括性能表、bug 场景、内存数据。
3. 如果推荐做，最佳实现是什么？
4. 如果不推荐做，为什么不值得进库？
5. 序列化是否应进 v1？还是 v2？
6. 是否需要新增包/命名空间，而不是放进 core？
7. 最小 API surface 是什么？是否需要 `Clear` / `TrimExcess` / `Count`？每个 public 成员都要有理由。
8. 这个模块是否应命名为 `ManagedEntityMap<T>`、`EntityObjectMap<T>`、`ExternalBinding<T>` 或其他？名字必须诚实反映 host-local sidecar。

**推荐报告结构:**

```markdown
# Managed Entity Sidecar Value Report

## Verdict
- Go / No-Go

## Why This Exists / Why Not

## Correctness Evidence

## Performance Evidence

## API Complexity Evidence

## Serialization Decision

## Recommended Implementation

## Risks and Non-Goals

## Commands Run
```

**Knowledge:**

- 若 Go：新增 KB 记录模块定位、API、非目标、性能结论、坑点，并更新 `INDEX.md`。
- 若 No-Go：在 `kb-design-rationale.md` 常见误判/拒绝方案或新 KB 中记录拒绝理由，避免未来重复。

**Verification:**

```bash
dotnet test -c Release --nologo -v q
```

若任何生产源码或 HeroPipeline 相关测试被改动：

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

**Skip if:** 不可跳过；这是本次无人值守探索的交付物。

## 4. Blocker Decision Tree

```text
Stuck → start 30min timer
  ├─ Can fix → fix, rerun milestone verification
  ├─ Cannot fix but baseline passes → skip optional milestone, document why
  └─ Cannot fix and baseline broken → restore baseline first
       └─ If cannot restore → STOP. Report to user.
```

| Symptom | Action |
|---|---|
| Baseline test failure in M0 | STOP；不要开始探索 |
| Perf harness compile failure | 修第一个错误；必要时 `dotnet clean && dotnet build` 排除 stale obj |
| Competent user implementation 和库版性能差距很小 | 不强行优化；转向正确性/API/序列化证据；仍不足则 No-Go |
| Raw dense unsafe 明显快很多 | 量化安全检查成本；若成本不可接受，推荐 No-Go 或缩窄 API |
| Align 1M 过慢 | 先测 `_maxTouchedExclusive`；再考虑 active-id dense list；不要过早加入复杂结构 |
| 序列化遇到 entity remap 不确定 | STOP 在设计报告中列出不确定性，不做生产 API |
| 想改 `src/MiniArch/Core/` | STOP；本路线禁止 core 入侵 |
| 想新增 public API | 只有 Go 结论后才允许进入后续单独 implementation plan；本路线不直接落生产 API |

## 5. Explicit Non-Goals

- 不实现 managed ECS component；不让 managed object 进入 archetype/chunk storage。
- 不修改 `MiniArch.Core`。
- 不参与 lockstep determinism、FrameDelta、Snapshot、Checksum、Replay。
- 不支持反向索引（value -> entity）。
- 不支持多线程安全；并发由调用方用外部锁同时保护 World + sidecar。
- 不做 reflection deep serializer / BinaryFormatter 风格自动序列化。
- 不为 Godot/Unity 对象提供资源系统适配；最多提供用户 codec 边界。
- 不在证据不足时“先做了再说”。

## 6. Execution Contract

On user confirmation, the executing agent is authorized to:

- Work milestone by milestone, in order.
- Create and use an isolated git worktree for all experiments.
- Add experimental perf/test projects under `tools/perf/ManagedEntityMap.ValueLab/` or equivalent isolated path.
- Run listed verification commands.
- Update `.knowledge/` and docs as each milestone produces durable facts.
- Create local commits after each milestone gate passes.
- NEVER push.
- Skip or stop according to the blocker decision tree.

The executing agent must NOT:

- Broaden scope beyond this roadmap.
- Modify `src/MiniArch/Core/`.
- Add production public API before the Go/No-Go report is accepted by the user.
- Refactor unrelated architecture.
- Update performance baselines with `--update-baseline`.
- Continue after baseline is broken and cannot be restored.

### Suggested commit policy

Use local commits only, for restore points:

```bash
git add -A && git commit -m "chore: baseline managed sidecar value lab"
git add -A && git commit -m "test: add managed sidecar correctness matrix"
git add -A && git commit -m "perf: measure managed sidecar alternatives"
git add -A && git commit -m "docs: report managed sidecar value verdict"
```

If the final verdict is No-Go, preserve the report and minimal evidence files; discard throwaway prototype code if it has no future value.

## 7. Reporting Template

The executing agent's final output MUST follow this structure:

```markdown
## Unattended Session Report

### Completion
- M0 Worktree/baseline: [done/partial/skipped]
- M1 Competitor model: [done/partial/skipped]
- M2 ValueLab harness: [done/partial/skipped]
- M3 Correctness red-team: [done/partial/skipped]
- M4 Perf/memory matrix: [done/partial/skipped]
- M5 Serialization exploration: [done/partial/skipped]
- M6 Decision report: [done/partial/skipped]

### Verdict
- Go / No-Go:
- One-sentence reason:

### Test Stats
- Baseline command:
- Final verification command:
- HeroComing.Perf: [not required / pass / fail]

### Evidence Summary
- Correctness:
- Performance:
- API complexity:
- Serialization:

### Key Findings
- What worked:
- What failed:
- What surprised us:

### Next Steps
- If Go, smallest next implementation action:
- If No-Go, what to document to avoid repeat exploration:
- Risks to address:
```
