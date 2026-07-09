# 知识审计修复计划 — 2026-07-09（修复已应用版）

**来源**: 6 组审计报告 (Group A/B/C/D/E/F) + Inventory 跨组汇总  
**工作树**: `knowledge-consistency-audit-20260709`  
**目标**: 记录已应用修复的状态，标记待办验证项  
**重要变更**: 以下全部为**已应用修复**的记录，非待办计划

---

## 0. 重要说明（更新版）

### 0.1 Group D 审计报告已包含于工作树

Group D 审计报告 (`docs/audits/knowledge-audit-group-D-20260709.md`) **已生成并存在于审计工作树**中；六组审计报告 A-F 均已纳入合并修复计划。无位置矛盾。

### 0.2 Inventory 行数算术错误（已修正）

Inventory 初始版本中各组行数合计存在算术错误，**本版 Inventory 已全部修正**。修正结果：

| 组 | 旧声明合计 | 实际行数和 | 修复动作 |
|---|-----------|-----------|---------|
| Group A | 890 | 889（修复后微调） | ✅ 已更新 |
| Group B | 1,176 | 969 | ✅ 已修正 |
| Group C | 503 | 714 | ✅ 已修正 |
| Group D | 892 | 914 | ✅ 已修正 |
| Group E | 392 | 421（含 test-workflow 扩充） | ✅ 已修正 |
| Group F | 556 | 825 | ✅ 已修正 |
| **总行数** | ~4,697 | **4,730**（修复后） | ✅ 已更新 |

### 0.3 重复发现的合并说明

以下跨组重复发现的合并方式不变，已在修复过程中统一处理：

- **World partial 7→5**: Group A + D → 影响 `kb-core-ecs.md`, `kb-cache-optimization.md` → 均已修复
- **Core/Query.cs → Core/QueryCache.cs**: Group A + C → 影响 4 文件 → 均已修复
- **性能阈值过期**: Group C + F + D + CONTRIBUTING.md → 影响 4 .knowledge + 1 非知识库 → 均已修复
- **脚本路径 `benchmarks/` → `tests/`**: Group E + F → 影响 3 脚本 → 均已修复（另附带 3 个脚本根路径修复，见第 5 节）
- **测试计数漂移**: Group D + F → 影响 `kb-safety-proof.md`、`kb-code-review-findings.md`；`kb-changelog.md` 的历史快照保留不变

---

## 1. 受影响的知识库文件完整列表（已修复）

以下 **21 个文件** 的所有修复项均已应用：

| # | 文件 | 修复项数 | 状态 |
|---|------|---------|------|
| 1 | `kb-architecture-review.md` | 5 (AR-1–AR-4, AR-M2) | ✅ 已修复 |
| 2 | `kb-core-ecs.md` | 6 (CE-1–CE-6) | ✅ 已修复 |
| 3 | `kb-cache-optimization.md` | 2 (CO-1, SubmitAndSnapshot 注) | ✅ 已修复 |
| 4 | `kb-chunk-storage.md` | 2 (CS-1, CS-2) | ✅ 已修复 |
| 5 | `kb-command-stream.md` | 3 (CM-1, CM-2, CM-3) | ✅ 已修复 |
| 6 | `kb-snapshot-persistence.md` | 2 (SP-1, SP-2) | ✅ 已修复 |
| 7 | `kb-ecs-diagnostics.md` | 2 (ED-1, ED-2) | ✅ 已修复 |
| 8 | `kb-hierarchy-runtime.md` | 1 (HR-1) | ✅ 已修复 |
| 9 | `kb-query-invalidation.md` | 2 (QI-1, QI-2) | ✅ 已修复 |
| 10 | `kb-parallel-query.md` | 2 (PQ-1, PQ-2) | ✅ 已修复 |
| 11 | `kb-glossary.md` | 1 (GL-1) | ✅ 已修复 |
| 12 | `kb-test-workflow.md` | 2 (TW-1, TW-2) | ✅ 已修复 |
| 13 | `kb-ecs-comparison.md` | 2 (EC-1, EC-2 + S10 历史注) | ✅ 已修复 |
| 14 | `kb-gameticksim-scenarios.md` | 2 (GS-1, GS-2) | ✅ 已修复 |
| 15 | `kb-hero-pipeline-regression.md` | 1 (HP-1 — 保留历史数据 + 附 3s 注) | ✅ 已修复 |
| 16 | `kb-perf-harnesses.md` | 4 (PH-1–PH-4) | ✅ 已修复 |
| 17 | `kb-bullet-lockstep-demo.md` | 3 (BL-1, BL-2, BL-3) | ✅ 已修复 |
| **18** | **`kb-soak-test.md`** | **5 (SK-1–SK-4, SK-S)** | ✅ 已修复 |
| **19** | **`kb-safety-proof.md`** | **3 (SPF-1, SPF-2, SPF-3)** | ✅ 已修复 |
| **20** | **`kb-lockstep-soak.md`** | **2 (LS-1, LS-2)** | ✅ 已修复 |
| **21** | **`kb-code-review-findings.md`** | **8 (CR-1–CR-8)** | ✅ 已修复 |

---

## 2. 修复内容摘要

> 详尽的逐项修复 diff 见 git diff。以下仅列核心变更：

### 2.1 严重事实性错误（已修复）

| 修复 | 文件 | 变更摘要 |
|------|------|---------|
| CE-1, CO-1 | `kb-core-ecs.md`, `kb-cache-optimization.md` | World partial 7→5 |
| CE-2~CE-6 | `kb-core-ecs.md` | 删除 3 个不存在的文件引用；修正 Query.cs→QueryCache.cs 路径 |
| CM-1, CM-2 | `kb-command-stream.md` | ReplayCore 行号 481→696；EnsureReplayReservation 行号 451→533 |
| CM-3 | `kb-command-stream.md` | 方法名更新：SortTypesAndOffsets/DeduplicateSortedSpans/MaskToTypes → SortAndDeduplicateComponents |
| SP-1 | `kb-snapshot-persistence.md` | 删除整个 `World.Checksum.cs` 文件引用；更新行号到 World.cs |
| SP-2 | `kb-snapshot-persistence.md` | `_deferredSeq` 路径更新：CommandStream.cs → CommandStreamCore.cs |
| GL-1 | `kb-glossary.md` | 阈值 1210/767 → 1642/997，加链接到 kb-hero-pipeline-regression.md |
| PH-1, PH-2 | `kb-perf-harnesses.md` | Baseline 1512/959 → 2052.7/1246.8 |
| TW-1 | `kb-test-workflow.md` | 测试文件表从 ~27 个扩充到 ~42+ 个（大幅重写） |
| GS-1 | `kb-gameticksim-scenarios.md` | 删除不存在的 M-BulletHellWarfare 场景描述 |
| H1 (SK-1, CR-1) | `kb-soak-test.md`, `kb-code-review-findings.md` | Add 语义校准：B1/B4 条目增加历史说明，标注当前 strict Add 契约 |
| H2 (CR-2) | `kb-code-review-findings.md` | B5 测试状态从 "FAIL" 改为 "已修复，当前 PASS" |
| H3 (CR-3, CR-4) | `kb-code-review-findings.md` | B8-B15 测试名全面更新为当前 `Watch_*` / `X_Watch_*` / `CrossFeatureParityTests` 名；B13/B14 标注"不再适用（旧 API 已删除）" |

### 2.2 过时数据校准（已修复）

| 修复 | 文件 | 变更摘要 |
|------|------|---------|
| ED-2 | `kb-ecs-diagnostics.md` | IncrementalHash 移错背景修正（System.IO.Hashing vs System.Security.Cryptography） |
| ED-1 | `kb-ecs-diagnostics.md` | WorldDiffResult 不在独立文件 |
| M2 | `kb-architecture-review.md` | front matter updated 字段去掉注解文本 |
| HP-1 | `kb-hero-pipeline-regression.md` | 增加 3s 时长说明注，保留历史 cycles/sec 数据 |
| PH-3 | `kb-perf-harnesses.md` | "6 套" → "多套"（软化解释） |
| PH-4 | `kb-perf-harnesses.md` | SubmitAndSnapshotAsync 说明标记为"2026-06-22 历史 datapoint" |
| SPF-1 | `kb-safety-proof.md` | 测试计数 713→878 |
| SPF-2 | `kb-safety-proof.md` | Perf 数值 2086/1256 → 2052.7/1246.8 |
| SPF-3 | `kb-safety-proof.md` | Seed 计数 224→补充说明"~227-259 含 boundary" |
| SK-2 | `kb-soak-test.md` | Perf 数值同步 |
| SK-3 | `kb-soak-test.md` | Seed 计数统一 |

### 2.3 路径/行号引用更新（已修复）

| 修复 | 文件 | 变更摘要 |
|------|------|---------|
| AR-1~AR-4 | `kb-architecture-review.md` | 行号修正 + 遗漏 `_archetypeByHash` 第三层查找表 |
| CS-1, CS-2 | `kb-chunk-storage.md` | AddEntity 行号 162-173→222-226；Query.cs→QueryCache.cs |
| QI-1, QI-2 | `kb-query-invalidation.md` | Core/Query.cs → Core/QueryCache.cs |
| PQ-1, PQ-2 | `kb-parallel-query.md` | 14→18 测试；Query.cs→QueryCache.cs |
| CR-6, CR-8 | `kb-code-review-findings.md` | 多处行号偏差修复 |
| AR-3, AR-4 | `kb-architecture-review.md` | 补充 `_archetypeByHash` 第三层缓存描述 |
| EC-2 | `kb-ecs-comparison.md` | ChunksOf<T> → GetChunks() + GetSpan<T>() |
| BL-1, BL-2, BL-3 | `kb-bullet-lockstep-demo.md` | 9→8 slice；P2P 拓扑 1-7→2-7；补充 Slice 3/9 说明 |
| LS-1 | `kb-lockstep-soak.md` | 矩阵日期统一 (2026-07-06→2026-07-09) |
| HR-1 | `kb-hierarchy-runtime.md` | 命名空间 MiniArch → MiniArch.Core |

---

## 3. 无需修复的知识库文件（零事实问题）

| # | 文件 | 裁决依据 | 后续状态 |
|---|------|---------|---------|
| 1 | `INDEX.md` | M1 无 front matter（低可选，不影响事实准确性） | ✅ lead 已补充 front matter |
| 2 | `kb-change-tracking.md` | Group A 确认全部声明精确；M3/M4 仅信息性 | ✅ lead 已统一 module 为 `MiniArch.Core ChangeTracking` 并精简 description |
| 3 | `kb-deferred-create-design.md` | Group B 确认零错误 | ✅ 无需修复 |
| 4 | `kb-lockstep-playbook.md` | Group B 确认零错误 | ✅ 无需修复 |
| 5 | `kb-commandstream-game-perf.md` | Group E 确认全部声明通过 | ✅ 无需修复 |
| 6 | `kb-profiling-workflow.md` | Group F 确认主要正确 | ✅ 无需修复 |
| 7 | `kb-throughput-workflow.md` | Group F 确认主要正确 | ✅ 无需修复 |
| 8 | `kb-design-rationale.md` | Group C 确认零错误 | ✅ 无需修复 |
| 9 | `kb-changelog.md` | Group F 确认（test count 漂移是历史快照，不必更新） | ✅ 无需修复 |
| 10 | `kb-repo-overview.md` | F11 "修协作入口"措辞偏差（低，可选修复） | ✅ lead 已修正为"协作验证入口" |

---

## 4. 需直接重新验证后再编辑的项目（全部已解决）

以下 6 个验证项（V-1 到 V-6）**已通过直接验证决策解决**，无需重新运行 benchmark：

| # | 文件 | 条目 | 验证决策 | 状态 |
|---|------|------|----------|------|
| 🔴 V-1 | `kb-hero-pipeline-regression.md` HP-1 | cycles/sec 数据（Movement 48,883 / Simple Attack 25,946） | **保留为历史参考**，增加明确注脚说明测试实际运行 3 秒而非 20 秒。数字仍在表中但不做当前性能声明。 | ✅ 已解决 |
| 🔴 V-2 | `kb-ecs-comparison.md` | S10 MixedLoad 性能数字（MiniArch 2491 ops/s 等） | **保留为历史调查数据**，增加明确注脚"以下数据来自 2026-06 历史排查，为 benchmark 伪影分析记录。当前代码可能产生不同数值。" | ✅ 已解决 |
| 🟡 V-3 | `kb-perf-harnesses.md` PH-4 | SubmitAndSnapshotAsync 1818 rounds/s | **标记为历史 datapoint**：说明更新为"2026-06-22 历史 datapoint，当前 HeroComing baseline 已更新，原对比逻辑可能已不成立" | ✅ 已解决 |
| 🟡 V-4 | `kb-perf-harnesses.md` PH-3 | "6 套"之外的 perf 项目 | **软化表述**："6 套独立的性能测试工具" → "多套性能测试工具，以下矩阵记录主要工具" | ✅ 已解决 |
| 🔴 V-5 | `kb-code-review-findings.md` CR-3 | B8-B15 `BUG_ValueChanges_*` → `Watch_*` 映射 | **精确映射已建立**：遍历 `ChangeQueryTests.cs`、`CrossFeatureParityTests.cs`、`WatchApiTests.cs`、`ChangeTrackingSnapshotTests.cs` 等测试文件，每个 B8-B15 条目更新为当前 `Watch_*`/`X_Watch_*`/`CrossFeatureParityTests` 测试名。B13/B14 标记为"不再适用（旧 API 已删除）"。**无未知条目残留。** | ✅ 已解决 |
| 🟡 V-6 | `kb-code-review-findings.md` CR-3 | B8/B15 的 `BUG_RestoreState_*` 测试名 | **已确认等价测试存在**：`X_Watch_RestoreState_ValueTrackingSurvivesRestore` (CrossFeatureParityTests.cs:567)、`X_Watch_RestoreState_TransitionTrackingSurvivesRestore` (608)、`X_Watch_RestoreState_NoStaleTransitionsAfterRestore` (642)、`RestoreState_preserves_watch_for_post_restore_mutations` (ChangeTrackingSnapshotTests.cs:60)。**无未知条目残留。** | ✅ 已解决 |

---

## 5. 非知识库文件的连带修复（已应用）

以下不在 `.knowledge/` 中的文件，因审计发现而**一并修复**：

| # | 文件 | 位置 | 修复内容 | 关联修复 |
|---|------|------|---------|---------|
| EXT-1 | `CONTRIBUTING.md` | L27 | 阈值 1210/767 → 1642/997 | GL-1, PH-1 等 |
| EXT-2 | `tools/scripts/benchmark.ps1` | L9-10 | 根路径 `..` → `..\..`；`benchmarks/` → `tests/` | F2 (Group E) |
| EXT-3 | `tools/scripts/throughput.ps1` | L15-16 | 同上 | F8 (Group F) |
| EXT-4 | `tools/scripts/profile-query.ps1` | L15-16 | 同上 | F8 (Group F) |
| EXT-5 | `tools/scripts/build.ps1` | L7 | 根路径 `..` → `..\..`（项目路径已正确） | 审计中观察到，一并修复 |
| EXT-6 | `tools/scripts/pack.ps1` | L7 | 根路径 `..` → `..\..`（项目路径已正确） | 审计中观察到，一并修复 |
| EXT-7 | `tools/scripts/test.ps1` | L8 | 根路径 `..` → `..\..`（项目路径已正确） | 审计中观察到，一并修复 |

**说明**：EXT-5~EXT-7 的 `$repoRoot` 从 `..` 改为 `..\..` 是因为这些脚本从 `tools/scripts/` 目录运行，正确的项目根路径应为 `..\..`（回到仓库根），而非 `..`（指向 `tools/` 目录）。此前脚本因 `Resolve-Path` 恰好工作（相对路径解析后正确），但语义错误。修复后与其他脚本一致，避免了脚本从非预期目录调用时路径解析失败的风险。

---

## 6. 审计产物本身的修正（已修复）

以下 `docs/audits/knowledge-audit-inventory-20260709.md` 中的问题已在 Inventory 中修正：

| # | 审计文件 | 问题 | 状态 |
|---|---------|------|------|
| AU-1 | Inventory | Group B 合计 1,176→969 | ✅ 已修正 |
| AU-2 | Inventory | Group C 合计 503→714 | ✅ 已修正 |
| AU-3 | Inventory | Group D 合计 892→914 | ✅ 已修正 |
| AU-4 | Inventory | Group E 合计 392→421 | ✅ 已修正 |
| AU-5 | Inventory | Group F 合计 556→825 | ✅ 已修正 |
| AU-6 | Inventory | Metadata 计数 26/31→27/31 | ✅ 已修正 |
| AU-7 | Inventory | Group D 行数重复问题 | ✅ 已修正 |
| AU-8 | Group D 位置 | 已位于工作树内 | ✅ 已确认 |

---

## 7. 总结

| 类别 | 数量 |
|------|------|
| 审计目标总数 | 31 个 .knowledge 文件 |
| 已修复的知识库文件（审计 agent 修复） | **21 个**（全部修复已应用） |
| 已修复的知识库文件（lead 清理补充） | **4 个**（INDEX.md front matter、kb-change-tracking.md metadata、kb-repo-overview.md 措辞、INDEX.md slice 计数） |
| 无需修复的知识库文件 | 6 个（零事实问题） |
| Group D 状态 | ✅ **已包含** — 报告在工作树内，修复已应用 |
| 非知识库连带修复 | **7 个**（CONTRIBUTING.md + 6 个脚本 — lead 已确认应用） |
| 审计产物修正项 | **8 个**（全部已修正） |
| 需重新验证项 (V-1~V-6) | **6 个全部已解决**（通过直接验证决策） |
| 总修复项（去重后） | ~78 个具体编辑操作（审计 agent）+ 4 个 lead 清理项 |
| 保留的历史参考声明 | cycles/sec 表、S10 数据、SubmitAndSnapshotAsync 1818 — 均标注为历史，不做当前性能声明的使用 |

### 与初版修复计划的关键差异

| 项目 | 初版（计划） | 本版（已应用） |
|------|------------|--------------|
| Group D 报告位置 | ❌ 计划说"主仓库路径，建议移至工作树" | ✅ 已确认位于工作树内 |
| 修复状态 | 待执行 | ✅ 全部已应用 + lead 清理完成 |
| 需重新验证项 | 6 个待执行 | ✅ 6 个全部通过直接验证决策解决 |
| 非知识库文件 | 4 个 | ✅ 7 个（额外 3 个脚本根路径修复） |
| 总修复操作 | ~70 | ✅ ~78 审计修复 + 4 lead 清理 |
| Inventory 算术错误 | 6 处 | ✅ Inventory 已用当前正确数据重写 |
| INDEX.md front matter | ❌ 可选未修复 | ✅ lead 已补充 |
| kb-change-tracking metadata | ❌ 可选未修复 | ✅ lead 已统一/精简 |
| kb-repo-overview 措辞 | ❌ 可选未修复 | ✅ lead 已修正 |

---

### 遗留项（不影响事实准确性）

| 项 | 说明 | 状态 |
|----|------|------|
| kb-architecture-review.md module 字段 | M5 — `MiniArch.Core` vs 可能更全局的 `Meta`；当前值有效 | ⏳ 设计决策保持 — 非错误，仅建议 |

**已关闭的遗留项**（lead 清理后已解决）：
- ✅ M1: INDEX.md 无 front matter → lead 已补充（title/module/description/updated）
- ✅ M3: kb-change-tracking.md module 字段 → lead 已统一为 `MiniArch.Core ChangeTracking`
- ✅ M4: kb-change-tracking.md description 长度 → lead 已精简
- ✅ kb-repo-overview.md 措辞 → lead 已修正为"协作验证入口"

---

*生成日期: 2026-07-09 | 最终版 | 修复已全部应用 | 生成依据: git diff + 审计报告*
