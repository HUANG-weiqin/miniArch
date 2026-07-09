# 2026-07-09 Quality Hardening 无人值守路线

> 由 `autonomous-roadmap` 技能生成。确认本文档后，用 `executing-plans` 逐里程碑执行。

## 总体目标

持续发现并消除 miniArch 库的质量漏洞——测试覆盖盲区、过度防御浪费、交叉功能 bug——通过"猜想→证明→验证→记录"的证据闭环建立可审计的健壮性证据，不更改公共 API。

**核心工作流：扫描 → 猜想 → 写 BUG_ 测试（真 bug）或 kb-code-review-findings 条目（非 bug）→ 修复/记录 → 验证 → 知识库回写。**

**基线（2026-07-09）：**
- 分支：`main`
- 测试：MiniArch.Tests 825 + HeroPipeline.Tests 5 = **830 passing**（存在 xUnit1031 warning，非失败）
- 基线验证：`dotnet test -c Release` 通过（该命令完成 Release build + test）
- 性能门禁命令：`dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`（本次 roadmap 编写未重跑；后续任何源码架构改动必须运行。本路线采用实测校准的严格放行线：Movement ≥1850 rounds/s, Attack ≥1100 rounds/s, 内存稳定）
- Soak：224 seed × 5M+ 帧全 PASS（2026-07-06 安全证明）
- 已知未跟踪文件（**不得纳入本路线产物**）：`docs/plans/2026-07-09-watch-api-implementation.md`、`nul`

**什么是"证据闭环"：** 对每个猜想，要么产出 `BUG_` 前缀的回归测试（证明 bug 存在 + 已修复），要么产出 `kb-code-review-findings.md` 中的已验证非 bug 条目（含位置/猜想/结论/验证方式）。不承认"目视无问题"或"看起来没 bug"——必须有可复现的验证。

---

## 硬约束

| 类别 | 约束 |
|------|------|
| **公共 API** | **任何时候不得修改、新增、删除、重命名 public 类型/方法/属性/字段/构造器/运算符。** 连 `[EditorBrowsable(Never)]` 都不行。只允许新增 internal 或 private 代码。是否属于 public API 由程序集公开 surface 决定，**不由当前调用方数量决定**；即使只有测试/示例引用，public 符号仍然是 API。 |
| **数据结构偏好** | **Compact-first。** 能用 dense array / `Span<T>` / bitset / SoA / entity-id 直索引 / 小规模线性扫描解决的，不得引入 `Dictionary` / `HashSet` / `ConcurrentDictionary` / map-like side table。哈希容器只有在能证明带来明显优势（复杂度、吞吐、内存、实现简单性中至少一项有实测或严谨论证）时才允许，并且必须在 KB 中记录为什么数组/紧凑结构不够。 |
| **性能门禁** | 修改 `src/MiniArch/` 或 `tests/HeroPipeline.Tests/` 后必须跑 `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`。AGENTS.md 当前基础线是 80% 门槛，但本路线按用户指定使用更严格放行线：**Movement ≥1850 rounds/s, Attack ≥1100 rounds/s**；低于该线即视为本路线不通过。确定性豁免（纯文档、死代码删除等）见 AGENTS.md §5a。**不得传 `--update-baseline`。** |
| **编译配置** | 所有 `dotnet build`/`dotnet run`/`dotnet test` 涉及性能测量时强制 `-c Release`。 |
| **猜想验证** | 每轮审阅/扫描前 **必须先读 `.knowledge/kb-code-review-findings.md`** 的「安全猜想」和「已验证安全」段，避免重复验证已排除的猜想。 |
| **知识库回写** | 每个里程碑完成后必须更新受影响的 `.knowledge/` 页。新发现（无论是真 bug 还是排除的猜想）必须按 `kb-code-review-findings.md` 现有格式写回；如新增 KB 页，必须使用 `_template.md`。 |
| **门禁回归** | 一旦 perf gate 或 test 被破坏且 30 分钟内无法恢复，必须回退改动（`git stash` 或 `git checkout .`），记录原因，停止当前里程碑。 |
| **避免重复工作** | 执行 agent 在工作前必须读 `kb-design-rationale.md` §3（常见误判优化），避免提已被拒绝的优化方案。 |

---

## Milestones

| # | 类型 | 里程碑 | ⏱ | 产出 | 验证 | 可跳过 |
|---|------|--------|----|------|------|-------|
| M1 | 🔧 | 公共 API 冻结哨兵 | 1h | `PublicApiSentinelTests.cs` 快照 API surface | `dotnet test -c Release` 全绿 | no |
| M2 | 🔧 | 已知 P2 设计债：CommandStream.Submit 预验证 | 2h | 修复 + 回归测试 + KB 更新 | `dotnet test -c Release` + perf gate | yes |
| M3 | 🔧 | 交叉功能红队矩阵 | 2h | `CrossFeatureParityTests.cs` 固定种子测试 | `dotnet test -c Release` | yes |
| M4 | 🔧 | Submit vs Replay/Restore metamorphic 扫描轮 | 2h | BUG_ 测试/kb-code-review-findings 条目 | `dotnet test` + perf gate（如改源码） | yes |
| M5 | 🔧 | 过度防御/热路径浪费/数据结构紧凑性删除轮 | 1.5h | 删除或"非优化"记录 + perf/内存证据 | `dotnet test` + perf gate | yes |
| M6 | 🔧 | 死代码/YAGNI 删除轮 | 1h | 确认删除的代码 + 验证 | `dotnet build` + `dotnet test` | yes |
| M7 | 🔧📖 | Soak/长周期压力矩阵 | 1h | 更新的 soak 证据 + KB 记录 | `--sweep 32 --frames 100000` 全 PASS | yes |
| M8 | 📖 | 文档/知识库校准收束 | 1.5h | KB 一致性更新 | 无测试改动；`dotnet build` 通过 | yes |
| R1+Rn | 🔧 | 模块轮转重复扫描 | 每轮≤2h | 视当轮发现而定 | 同前 | yes |

---

### M1：公共 API 冻结哨兵

**类型：** 🔧 Engine

**目标：** 新增一个测试文件 `tests/MiniArch.Tests/PublicApiSentinelTests.cs`，用反射枚举 `MiniArch` 程序集的所有 public 类型及其 public 成员，与一份签入的预期快照（`.verified.txt` 或 `.approved.cs`）对比。后续任何 agent 意外改动 public API 时该测试 RED。

**设计决策：**
- 使用 Verifiable 文件格式（纯文本 `.approved.cs` 即可，不引入 ApprovalTests NuGet 依赖）——直接 `[Fact]` 生成预期字符串并 `Assert.Equal(expected, actual)`。预期字符串内联在测试中或单独 `.cs` 文件。
- 覆盖：public types、public methods、public properties、public fields、public constructors、public operators / conversion operators、public nested types、public interfaces 及其成员。显式接口实现如构成可观察契约，也必须进入快照或在测试中明确说明排除理由。
- 不排除高噪声 API：泛型重载、运算符、构造器都进入基线；新增 public 方法会使测试 RED——迫使 agent 确认是否意图如此。
- 注意：这个测试本身**不改变** public API，只测量它。

**可能涉及的文件：**
- `tests/MiniArch.Tests/PublicApiSentinelTests.cs`（新增）

**产出：**
- Public API surface 快照（内联预期字符串）
- 所有 830+ 现存测试保持 GREEN
- 后续 agent 修改 public API 时第一条失败

**知识库：** 新增条目在 `kb-test-workflow.md` 的测试组织表"新增"行，或新增一个 `kb-public-api-sentinel.md` 说明设计和使用方式。

**验证：**
```bash
dotnet test -c Release --filter "PublicApiSentinel"
# + 全量回归确保不破坏别的测试
dotnet test -c Release
```

**跳过条件：** 不可跳过（M1 是所有后续 agent 的安全网基座）。

---

### M2：已知 P2 设计债 — CommandStream.Submit 预验证

**类型：** 🔧 Engine

**目标：** 解决 `kb-code-review-findings.md` P2 #1（R11 部分修改）：`CommandStream.Submit()` 的 `MaterializeAllPending` 成功后后续步骤失败无法回滚。建议修复：在 materialize 前预验证所有 slot 为 reserved 状态。

**实现策略：**
1. 在 `CommandStreamCore.Submit()` 中、`MaterializeAllPending` 之前插入一次预验证扫描。
2. 扫描待 materialize 的每个 pending entity batch，调用 `EntitySlotControl.IsReserved(id)` 或等价检查。
3. 预验证失败时抛出 `InvalidOperationException`（defense-in-depth，正常用户路径不可达）。
4. 预验证本身必须零分配（纯 range check + bool 读取）。
5. 新增回归测试：构造一个 `CommandStream` 其 pending entity 的 slot 被手动破坏的邪恶场景（通过 `EntitySlotControl` 或反射释放 slot），验证 Submit 抛出预期异常。
6. 如果在修复过程中发现相关但超出范围的 R11 问题（如 ReplayCore 无事务语义），记录到 kb-code-review-findings.md 而非在当前里程碑解决。不能 scope creep。

**可能涉及的文件：**
- `src/MiniArch/Core/CommandStreamCore.cs`（添加预验证）
- `src/MiniArch/Core/EntitySlotControl.cs`（可能暴露 `IsReserved` 为 internal）
- `tests/MiniArch.Tests/Core/CommandStreamTests.cs` 或新增文件

**产出：**
- 预验证逻辑（internal，不暴露为 public API）
- 回归测试（邪恶场景验证 Submit throw）
- `kb-code-review-findings.md` 更新：P2 #1 标记为已修复 + 修复位置
- 所有测试 + perf gate 通过

**知识库：** 更新 `kb-code-review-findings.md` 中 P2 #1 状态为已修复（含修复位置和验证方式）。如果新增了 internal 工具方法，对应更新 `kb-command-stream.md`。

**验证：**
```bash
dotnet test -c Release
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

**跳过条件：** 预验证发现根因涉及更深架构变更（必须改 public API）时，记录分析结果到 kb-code-review-findings.md，跳过 M2。

---

### M3：交叉功能红队矩阵

**类型：** 🔧 Engine

**目标：** 为 CommandStream、Replay、DeferredEntities、Hierarchy、ChangeTracking、Snapshot 六个模块的交叉组合编写小规模 fixed-seed 测试和属性测试，填补当前测试网中可能存在的交叉功能盲区。

**测试矩阵（每个格子至少一个 fixed-seed 测试 + 可选 FsCheck 属性）：**

| 组合 | 重点 |
|------|------|
| CommandStream + Hierarchy | AddChild 录制 → Submit → Replay 层级完整；级联 Destroy 跨 batch 边界 |
| CommandStream + DeferredEntities | Placeholder 在 Submit/Snapshot/Replay 各路径的一致解析 |
| CommandStream + ChangeTracking | `CommandStream.Set<T>` 经 Submit 落地的 value change 是否能被 TrackValueChanges 捕获（B12 回归加固） |
| Replay + Snapshot | 同帧 Replay + SnapshotInto 后的 RestoreState 正确性 |
| Hierarchy + ChangeTracking | AddChild/RemoveChild 触发的 transition 事件（含级联）是否正确进入 TransitionLog |
| Snapshot + Hierarchy | Save/Load/Restore 后层级关系保持 |
| DeferredEntities + Hierarchy | Placeholder parent 在 Replay 端先 Reserve 后 SetParent |
| ChangeTracking + RestoreState | RestoreState 后 TrackValueChanges/TrackTransitions 语义正确（B8/B15 回归加固） |
| 三者以上 | Submit → Snapshot → RestoreState → Replay → Compare 的任意随机序列（交由 soak 为主，手工 seed 为辅） |

**每条测试必须标注：**
- `BUG_regression_<bug-number>`（如果是对已修 bug 的加固）
- `X_<feature1>_<feature2>_<scenario>`（新加交叉测试）

**可能涉及的文件：**
- `tests/MiniArch.Tests/CrossFeatureParityTests.cs`（新增）
- 也可能直接在现有的测试文件中追加

**产出：**
- 至少 9+ 条交叉功能测试
- 如发现 bug，`BUG_` 前缀测试 + 修复
- 如确认无 bug，贡献给 kb-code-review-findings.md 的安全猜想段

**知识库：** 更新 `kb-test-workflow.md` 的测试组织表增加新文件条目；更新 `kb-code-review-findings.md` 增加新猜想（如有）。

**验证：**
```bash
dotnet test -c Release
```

**跳过条件：** 如果交叉测试难度超出 2h 预算（比如发现需要大量 mock/基础设施），记录当前进度、缩小范围、跳过剩余格子。

---

### M4：Submit vs Replay/Restore metamorphic 扫描轮

**类型：** 🔧 Engine

**目标：** 系统性地对 Submit、Replay、Restore 三条路径进行 metamorphic parity 扫描。核心思想：同一个操作序列经三条路径执行后，world 状态应字节级一致。

**扫描策略：**
1. 选取已有的 soak seed（如 `seed=111, cap=100, ops/f=50`）和 M3 中新发现的模式。
2. 对每个模式，构造三路径执行：
   - **路径 A：Submit** → 录制 + Submit 到 world
   - **路径 B：Replay** → 录制 + 序列化 FrameDelta + Replay 到影子 world
   - **路径 C：Restore** → 快照 + Submit + RestoreState
3. 每帧比较三 world 的 `CanonicalChecksum` + `EntityCount` + `WorldValidator`。
4. 如果发现分歧：
   - 缩小到最小复现序列
   - 写 `BUG_` 前缀回归测试
   - 修复（如属于源码改动，补 perf 证据）
5. 如果确认无分歧：
   - 记录到 `kb-code-review-findings.md` 安全猜想段（格式：位置/猜想/结论/验证方式）

**每发现一个分歧后：** 记录并修复，然后重新扫描所有已扫模式确保无回归。

**可能涉及的文件：**
- `tests/MiniArch.Tests/` 下新增 `BUG_` 测试
- `src/MiniArch/` 下如有修复
- `.knowledge/kb-code-review-findings.md` 更新

**产出：**
- 扫描报告（seed × 路径 × round count）
- 发现的 bug → BUG_ 测试 + 修复
- 非 bug → kb-code-review-findings.md 安全猜想条目
- 通过 perf gate（如涉及源码改动）

**知识库：** `kb-code-review-findings.md` 安全猜想段更新；如发现新 bug，也更新 `kb-soak-test.md`（发现的 bug 表）。

**验证：**
```bash
dotnet test -c Release
# 如涉及源码改动：
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

**跳过条件：** 2h 内无法完成一轮完整扫描（如 discover 复杂 bug 需要更多调试时间），记录当前进度到 kb-code-review-findings.md，跳过当前轮。

---

### M5：过度防御 / 热路径浪费 / 数据结构紧凑性删除轮

**类型：** 🔧 Engine

**目标：** 识别热路径上"试过有可测性能损失"的可删除防御检查，以及能被更紧凑结构替代的内部 map/set/side-table。与 `kb-design-rationale.md` §3.11（已有证据：防御检查零可测影响）不矛盾——那条的证据是针对 `Get<T>`/`GetRef<T>`/`GetRecordFast`，其他路径可能不同。

**规则（严格）：**
- **必须有 perf 证据**证明删除某检查后 throughput 提升 > 3%（超过噪声），**且**有测试覆盖该检查触发的正确性场景。
- 正确性场景的测试必须 PASS（无论检查是否存在）。
- 只在以下路径考虑：循环中的高频检查、每帧调用 10000+ 次的检查、`Set<T>` 热路径中可合并的 range check。
- **不得删除：** `Get<T>`/`GetRef<T>`/`GetRecordFast` 上的存活检查（已有 §3.11 证据证明零代价）。
- **不得删除：** EntityFieldResolver 的 fail-fast（B2 修复的核心防御）。
- **默认怀疑：** `Dictionary` / `HashSet` / map-like side table。凡是 key 能映射到 dense id、component id、archetype index、batch index 或小规模固定集合的，优先评估 array/span/bitset/linear scan。保留哈希容器必须写明：为什么紧凑结构不够、哈希结构带来的优势、对应 perf/内存证据或严格复杂度理由。

**执行流程：**
1. 用 `dotnet run -c Release --project tools/perf/HeroComing.Perf` 跑当前 baseline。
2. 扫描候选：高频防御检查、`Dictionary`/`HashSet`/map-like 容器、可由 dense id 直索引表示的 side table。
3. 猜想某检查可删或某结构可压紧。
4. 改动 → 跑 perf gate/相关微基准/内存观测 → 对比 baseline。
5. 如果吞吐提升 < 3%、内存收益不明显、或复杂度上升超过收益 → **恢复原代码**，记录到 kb-code-review-findings.md 为"已排除猜想（含位置、perf/内存数据、结论）"。
6. 如果提升 ≥ 3% 或内存/结构紧凑性收益明确，且正确性测试全过 → 提交改动，记录优化证据。

**可能涉及的文件：**
- `src/MiniArch/` 下任意热路径文件
- `.knowledge/kb-code-review-findings.md`
- `.knowledge/kb-design-rationale.md`（如有必要更新 §3.11）

**产出：**
- 已删除的检查（如有，附 perf 证据）
- 或"无删除"报告（所有候选都被证据驳回）
- 所有测试 + perf gate 通过

**知识库：** 更新 `kb-code-review-findings.md` 安全猜想段或 `kb-design-rationale.md` §3。

**验证：**
```bash
dotnet test -c Release
dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline
```

**跳过条件：** 认为所有热路径检查已有 §3.11 覆盖，目前路径无候选。仍然跑一轮 baseline + 记录"跳过 — 无候选"。

---

### M6：死代码/YAGNI 删除轮

**类型：** 🔧 Engine

**目标：** 删除所有零调用的 internal/private 方法、字段、类型。不碰 public API。

**执行流程：**
1. 运行 `.\tools\scripts\deadcode.ps1`（如不存在则用 `rg` + 调用方分析手工扫描）。
2. 对每个候选零调用符号：
   - 确认不是 public API
   - 确认不是被反射调用的（搜索 `nameof`、`typeof`、`GetMethod`）
   - 确认不会被类外的 internal 调用方使用（全量搜索项目）
   - 删除
3. 对类内部私有方法：如果只有同类其他私有方法调用的（无外部调用），也列入候选。
4. 对 YAGNI 候选：某个 internal 方法/字段/类只有测试引用但没有生产调用方 → 考虑删除（除非未来 2 个里程碑内明确需要）。
5. 删除后 `dotnet build -c Release` + `dotnet test -c Release`。

**特别注意：**
- 不要删除 `Debug.Assert`、`[Conditional("DEBUG")]` 方法（已有设计决策支持保留）。
- 不要删除 XML doc 中引用的方法名或示例代码中的调用（非编译引用，不影响删除）。
- 不要删除 `cctor`（静态构造器）、析构函数（除非确定零控制流可达）。
- 不要删除 `override` 方法（即使看似零调用——运行时通过虚表调用）。

**可能涉及的文件：**
- `src/MiniArch/` 下多个文件
- `tests/MiniArch.Tests/` 下如果测试引用了被删代码，测试也必须更新或删除

**产出：**
- 删除清单 + 删除理由（confirm zero caller）
- `dotnet build` + `dotnet test` 通过
- （可选）如有 YAGNI 删除影响测试，更新测试文件

**知识库：** 记录数量级的"已删除行数"到 `kb-code-review-findings.md` 或 `kb-changelog.md`。不需要逐条记录。

**验证：**
```bash
dotnet build -c Release
dotnet test -c Release
```

**跳过条件：** deadcode.ps1 扫描结果为空，或所有候选方法因上述"不要删除"规则被排除。

---

### M7：Soak / 长周期压力矩阵

**类型：** 🔧 Engine + 📖 Documentation

**目标：** 运行 `MiniArch.Soak` 的多 seed sweep，确认当前代码没有退化的正确性分歧。更新 KB 中的 soak 证据。

**执行：**
```bash
# 1. Standard sweep（快速验证）
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 100000 --quiet

# 2. 如果全 PASS → 跑 3 个 1M 帧长稳
for seed in 42 1234567 987654; do
  dotnet run -c Release --project tools/soak/MiniArch.Soak -- --seed $seed --frames 1000000 --quiet
done

# 3. Boundary：极端密度（如果没有在 M3/M4 中跑过）
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --seed 111 --entity-cap 100 --ops-per-frame 50 --frames 200000 --quiet
```

**如果某个 seed FAIL：**
- 记录到 kb-code-review-findings.md + kb-soak-test.md
- 标记为 BUG 并停止当前里程碑
- 修复后再重跑该 seed

**产出：**
- 全 PASS 报告（seed × frames × result）
- 如发现 FAIL：BUG 测试 + 修复

**知识库：** 更新 `kb-soak-test.md` 的安全证明矩阵表（更新日期 + seed 数 + 总帧数）。必要时更新 `INDEX.md`。

**验证：**
```bash
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 8 --frames 50000 --quiet
```
（缩短版快速验证）

**跳过条件：** soak 上次运行（2026-07-06）已经 224 seed 全 PASS，且 M1-M6 没有改动源码，可根据实际情况跳过长稳。但 sweep 32 × 100K 至少跑一次确认当前基线未退化。

---

### M8：文档/知识库校准收束

**类型：** 📖 Documentation

**目标：** 校准所有 `.knowledge/` 页的内容与当前代码状态一致。确保：
- 所有 KB 页的 `updated` 字段为最新日期。
- `INDEX.md` 中模块地图准确（没有遗漏模块，没有已删除的模块）。
- 所有 KB 文件使用 `_template.md` 模板结构（front matter 完整、内容有层次）。
- `kb-code-review-findings.md` 中所有已验证猜想条目格式符合 4 字段要求（位置/猜想/结论/验证）。
- 死代码删除后被引用的 KB 页（如某 KB 提到已删除的方法）更新移除引用。
- 公共 API 冻结哨兵（M1）的工作原理得到 KB 覆盖。

**产出：**
- KB 页 updated 日期更新
- 格式一致性修复（如有）
- 交叉链接补全（如 M1-M7 新增知识点链接到 INDEX）

**验证：**
```bash
dotnet build -c Release  # 确保无 build break（文档改动不影响）
# 人肉确认：检查 INDEX.md + 各 KB 页 front matter + updated 日期
```

**跳过条件：** ─（收束轮不可跳过，至少检查一次 KB 一致性）。

---

### R1+Rn：模块轮转重复扫描

**类型：** 🔧 Engine（每轮）

**目标：** M1-M8 完成后，选取一个模块（按 `INDEX.md` 模块地图的优先级或按"上次扫描距今最久"），进入"猜想→验证→记录"循环。每轮 ≤2h，产出与 M3~M6 类似的产出。

**可选的轮转顺序（每轮由执行 agent 自主选择其一）：**
1. **CommandStream 专题**：pending batch deduplicate、frame delta serialize/deserialize、Submit vs SubmitFromFrozen parity
2. **Hierarchy 专题**：深层嵌套、大量孩子、级联销毁 + snapshot 交叉
3. **ChangeTracking 专题**：更多的 multi-capture + filter 组合边界
4. **Snapshot/Clone 专题**：跨世界 Restore、Chunked archetype + snapshot 交叉
5. **DeferredEntities 专题**：offset mode、placeholder 解析极端边界
6. **Query/Invalidation 专题**：冷启动 + 高频结构变更交错
7. **Data Layout 专题**：审计内部 `Dictionary` / `HashSet` / side table，寻找 dense array / bitset / SoA 替代；无证据不改，有证据才删/换

**R1 的启动条件：** M1-M8 全部完成（或跳过）后。执行 agent 可选运行 R1 或报告完成。

---

## Blocker Decision Tree

```
新问题出现 → 启动 30 分钟计时器
  ├── 能修 → 修（测试 + perf gate + KB 回写）
  ├── 不能修但基线可用 → 跳过当前里程碑，记录原因
  └── 不能修 + 基线被破坏 → 先恢复基线
       ├── git stash / git checkout . / git reset HEAD
       └── 确认 `dotnet test -c Release` + perf gate 恢复
            ├── 恢复成功 → 记录失败原因，尝试下一个里程碑
            └── 无法恢复 → STOP。向用户报告。
```

**具体诊断表：**

| 症状 | 行动 |
|------|------|
| `dotnet build` 失败 | 修第一个错误。`dotnet clean && dotnet build` 排除残留 obj |
| `dotnet test` 测试失败 | 确认是否是新改代码的预期失败（如刚写的 RED 测试）。否则回退 |
| Perf gate 失败（吞吐量 < 阈值） | 回退改动。记录到 kb-code-review-findings.md |
| Perf gate 失败（内存持续增长） | 回退改动。检查是否有数组/队列泄漏 |
| 发现 public API 被意外修改 | 回退。加强 M1 sentinel |
| 猜想无法验证 | 记录为"无法验证的猜想"，放 kb-code-review-findings.md，不阻塞 |
| 30 分钟卡住 | 跳过当前里程碑。记录：所在模块、猜想、卡住原因、后续 step |
| Soak 发现 FAIL | 根据 BUG 流程处理：缩小复现 → BUG_ 测试 → 修复 → 验证 → 继续 |

---

## 显式非目标

执行 agent **必须不**：

| 非目标 | 原因 |
|--------|------|
| ❌ **修改公共 API** | 整个路线的硬约束。任何 public API 改动（新增/修改/删除/重命名）都不允许。如有需要公共 API 变动的发现，记录到 kb-code-review-findings.md 并建议后续人工审批。 |
| ❌ **执行 Watch API 计划** | `docs/plans/2026-07-09-watch-api-implementation.md` 属于另一任务，且涉及公共 API 变更。本路线不碰该文件，不 stage，不引用为产出。 |
| ❌ **重开已被 `kb-design-rationale.md` §3 拒绝的优化** | 所有 §3 条目已有明确拒绝理由。如果有新的证据类型（如新的 .NET 版本 JIT 行为变化）可以挑战 §3 结论，但必须单独记录、不在此路线中实施。 |
| ❌ **刷新 baseline（`--update-baseline`）** | baseline 刷新只有人工确认后才做。本路线所有 perf 比较只读 baseline，不写。 |
| ❌ **无证据重构** | 任何代码改动都必须有测试或 perf 证据支撑。纯"风格的"重构（重命名、提取方法、格式化（超出 AGENTS.md 纯格式/空白豁免范围））不允许。 |
| ❌ **为了方便引入哈希容器** | `Dictionary` / `HashSet` / map-like side table 不是默认工具。除非证明它们比紧凑结构带来更大优势，否则不得新增；已有哈希容器也应在 M5/Rn 中被审计是否可压紧。 |
| ❌ **引入新 NuGet 依赖** | 除非绝对必要且经人工审批。 |
| ❌ **修改非本仓库文件** | 只改 `src/MiniArch/`、`tests/`、`.knowledge/` 下的文件。 |
| ❌ **修改/删除/docs/plans 下其他文件** | `docs/plans/2026-07-09-watch-api-implementation.md` 和 `nul` 不得改动、删除、stage。不引用它们为本路线产物。 |

---

## 执行契约

确认后，执行 agent 获得以下授权：

### 必须做
- **逐里程碑执行**，按 M1→M2→...→M8→R1 顺序（可跳过，但不能颠倒依赖顺序。M1 必须最先，因为后续 agent 需 API sentinel 保护）。
- **每里程碑完成后**：
  1. 运行该里程碑指定的验证命令并确认通过
  2. 更新受影响的 `.knowledge/` 文件
  3. 本地 commit：`git add -A && git commit -m "feat: M<N> <title>"`
  4. 检查 `git status` 和 `git diff HEAD`，确保没有纳入未跟踪文件（特别是 `docs/plans/2026-07-09-watch-api-implementation.md` 和 `nul`）
- **每轮审阅/扫描前**先读 `.knowledge/kb-code-review-findings.md` 安全猜想段。
- **所有性能测量用 `-c Release`**。
- **所有正确性验证用 `dotnet test -c Release`**。

### 绝不能做
- **不 push**
- **不 scope creep**：只做里程碑里定义的事。如果在工作中发现额外问题，记到 kb-code-review-findings.md 的"待查"段，不在当前里程碑处理。
- **不改 public API**
- **不传 `--update-baseline`**
- **不引用/修改 `docs/plans/2026-07-09-watch-api-implementation.md` 或 `nul`**
- **不继续** 在基线被破坏且无法恢复时

### 中止条件
- `git stash` 或 `git checkout .` 后仍无法恢复 `dotnet test` + perf gate → **STOP**。向用户报告完整上下文。

---

## 报告模板

每个里程碑完成后（或整个路线中断/完成时），输出必须按以下结构：

```
## 里程碑进度
- M1 <title>: [done/partial/skipped]
- M2 <title>: [done/partial/skipped]
- ...

## 测试状态
- 当前测试数：XXX
- `dotnet test -c Release`: [PASS/FAIL]
- 新增/修改测试数：YYY

## 发现的真 bug
| BUG_ 测试 | 模块 | 状态 |
|-----------|------|------|
| BUG_xxx | CommandStream | 已修复 |

## 已排除猜想
| # | 模块 | 猜想 | 结论 | 验证方式 |
|---|------|------|------|----------|
| S9 | Hierarchy | ... | ✅ 非 bug | 代码走读 ... |

## 已删除的浪费
- 死代码：XX 行
- 过度防御：XX 行（附 perf 证据）
- YAGNI：XX 行

## API diff 状态
- 公共 API 冻结哨兵：PASS（无变化）

## Perf / Soak 证据
- HeroComing.Perf --check-baseline：Movement XXX / Attack XXX（阈值 YYY）
- Soak sweep 32×100K：全 PASS / FAIL（如 FAIL 附 seed）

## 知识库更新
- `kb-code-review-findings.md`：更新了 / 未改
- `kb-soak-test.md`：更新了 / 未改
- `kb-changelog.md`：更新了 / 未改

## 下一步最小行动
- 继续 M<N> / 报告完成 / [具体问题需人工介入]
```
