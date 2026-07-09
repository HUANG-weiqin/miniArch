# 知识一致性审计最终报告 — Knowledge Consistency Audit Final Report

**审计日期**: 2026-07-09  
**工作树**: `knowledge-consistency-audit-20260709` (branch: `audit/knowledge-consistency-20260709`)  
**提交基准**: `246ac7e`  
**审计者**: 6 组 agent 并行审计 + 人工验证  

---

## 1. 审计范围

| 维度 | 值 |
|------|-----|
| 审计目标 | `.knowledge/*.md` 全部 31 个文件（30 个 `kb-*.md` + 1 个 `INDEX.md`） |
| 排除 | `_template.md`（格式模板，非知识内容） |
| 总行数 | 4,739（lead 清理后状态） |
| 审计方法 | Inventory + 6 组并行 Agent 审计 → 合并修复计划 → 直接重新验证 → 知识库 patch → 过时字符串扫描 → lead 补充清理 |
| 每组覆盖度 | 100%（所有高风险声明逐一验证，所有过时声明定位） |

---

## 2. 审计流程

```
Phase 1: 初始清单建立
  └─ Inventory (31 个文件, 行数, metadata, 分组)
  
Phase 2: 6 组并行审计 (2026-07-09)
  ├─ Group A: Core ECS Runtime (5 页)     → 11 个不匹配
  ├─ Group B: CommandStream & Persistence (6 页) → 10 个问题
  ├─ Group C: Query & Glossary (4 页)     → 5 个问题
  ├─ Group D: Soak, Proof & Safety (4 页) → 18 个问题
  ├─ Group E: Test & Benchmark (5 页)     → 13 个问题
  └─ Group F: Workspace & Meta (7 页)     → 11 个问题
  
Phase 3: 合并修复计划
  └─ 去重合并 → 21 个受影响文件, ~60 个修复项 + 6 个 V 项
  
Phase 4: 直接重新验证 (V-1~V-6)
  ├─ V-1: cycles/sec 保留 + 3s 注      → 已解决
  ├─ V-2: S10 数据标记为历史            → 已解决
  ├─ V-3: SubmitAndSnapshot 标记为历史   → 已解决
  ├─ V-4: "6 套" → "多套"               → 已解决
  ├─ V-5: B8-B15 精确映射建立           → 已解决
  └─ V-6: B8/B15 RestoreState 等价测试确认 → 已解决

Phase 5: 知识库 patch (全部已应用)
  └─ 21 个 .knowledge 文件直接编辑

Phase 6: 连带修复
  └─ CONTRIBUTING.md + 6 个 PowerShell 脚本
  
Phase 7: 审计产物更新 (本报告)
  └─ Inventory 重新计算 → 修复计划更新 → 最终报告

Phase 8: lead 补充清理
  ├─ INDEX.md front matter 补充
  ├─ kb-change-tracking.md module/description 统一
  ├─ kb-repo-overview.md 措辞修正
  └─ CONTRIBUTING.md + 6 脚本确认同步
```

---

## 3. 覆盖矩阵

### 3.1 按文件

| # | 文件 | 行数 | 审计发现 | 修复 | 剩余问题 |
|---|------|------|---------|------|---------|
| 1 | `INDEX.md` | 89 | M1 无 front matter | ✅ lead 已补充 front matter | 0 |
| 2 | `kb-architecture-review.md` | 231 | 3 严重 + 2 中 + 2 信息 | 全部已应用 | 0 |
| 3 | `kb-bullet-lockstep-demo.md` | 117 | 3 中 | 全部已应用 | 0 |
| 4 | `kb-cache-optimization.md` | 137 | 1 中 + 1 信息性注 | 全部已应用 | 0 |
| 5 | `kb-change-tracking.md` | 149 | 0（2 信息性 → 已修复） | ✅ lead 已统一 module 并精简 description | 0 |
| 6 | `kb-changelog.md` | 379 | 0 事实错误（旧阈值/文件名均为历史记录） | 已补"历史记录/当前以基线页为准"标注 | 0 |
| 7 | `kb-chunk-storage.md` | 195 | 1 中 + 1 低 | 全部已应用 | 0 |
| 8 | `kb-code-review-findings.md` | 350 | 3 高 + 2 中 + 3 低 | 全部已应用 | 0 |
| 9 | `kb-command-stream.md` | 377 | 3 高 + 2 中 | 全部已应用 | 0 |
| 10 | `kb-commandstream-game-perf.md` | 71 | 0 | N/A | 0 |
| 11 | `kb-core-ecs.md` | 177 | 2 高 + 4 中 | 全部已应用 | 0 |
| 12 | `kb-deferred-create-design.md` | 234 | 0 | N/A | 0 |
| 13 | `kb-design-rationale.md` | 329 | 0 | N/A | 0 |
| 14 | `kb-ecs-comparison.md` | 74 | 2 中 | 全部已应用 | 0 |
| 15 | `kb-ecs-diagnostics.md` | 56 | 1 中 + 1 中 | 全部已应用 | 0 |
| 16 | `kb-gameticksim-scenarios.md` | 70 | 1 高 + 1 中 | 全部已应用 | 0 |
| 17 | `kb-glossary.md` | 71 | 1 高 | 全部已应用 | 0 |
| 18 | `kb-hero-pipeline-regression.md` | 75 | 1 高 | 全部已应用 | 0 |
| 19 | `kb-hierarchy-runtime.md` | 55 | 1 低 | 全部已应用 | 0 |
| 20 | `kb-lockstep-playbook.md` | 112 | 0 | N/A | 0 |
| 21 | `kb-lockstep-soak.md` | 227 | 1 中 + 1 低 | 全部已应用 | 0 |
| 22 | `kb-parallel-query.md` | 236 | 1 中 + 1 中 | 全部已应用 | 0 |
| 23 | `kb-perf-harnesses.md` | 70 | 1 高 + 3 中 | 全部已应用 | 0 |
| 24 | `kb-profiling-workflow.md` | 59 | 0 | N/A | 0 |
| 25 | `kb-query-invalidation.md` | 78 | 1 中 + 1 低 | 全部已应用 | 0 |
| 26 | `kb-repo-overview.md` | 68 | 1 低(措辞) | ✅ lead 已修正措辞 | 0 |
| 27 | `kb-safety-proof.md` | 216 | 3 中 | 全部已应用 | 0 |
| 28 | `kb-snapshot-persistence.md` | 135 | 2 高 | 全部已应用 | 0 |
| 29 | `kb-soak-test.md` | 121 | 1 高 + 3 中 + 1 低 | 全部已应用 | 0 |
| 30 | `kb-test-workflow.md` | 131 | 1 高 + 3 中 | 全部已应用 | 0 |
| 31 | `kb-throughput-workflow.md` | 50 | 0 | N/A | 0 |

### 3.2 按内容类别

| 内容类别 | 覆盖文件数 | 修正项 | 状态 |
|---------|-----------|--------|------|
| API 签名 / 类名 / 方法名 | 20+ | ~25 处 | ✅ 全部对齐 |
| 文件路径 / 行号引用 | 15+ | ~20 处 | ✅ 全部更新 |
| 计数（partial 文件 / 测试 / 场景） | 8 | ~12 处 | ✅ 全部修正 |
| 性能数值 / 阈值 | 6 | ~8 处 | ✅ 全部同步 |
| 设计决策 / 行为描述 | 10+ | ~6 处 | ✅ 全部校准 |
| 已删除文件 / API 引用 | 6 | ~8 处 | ✅ 全部清理 |
| 跨页一致性 | 全库 | ~4 组 | ✅ 全部对齐 |
| 术语定义 | 1 (glossary) | 1 | ✅ 已更新 |

---

## 4. 受影响 vs 未受影响的知识页

### 需要修复（21 页 — 审计 agent 修复 + 4 页 lead 可选清理）

`kb-architecture-review.md` · `kb-bullet-lockstep-demo.md` · `kb-cache-optimization.md` · `kb-chunk-storage.md` · `kb-code-review-findings.md` · `kb-command-stream.md` · `kb-core-ecs.md` · `kb-ecs-comparison.md` · `kb-ecs-diagnostics.md` · `kb-gameticksim-scenarios.md` · `kb-glossary.md` · `kb-hero-pipeline-regression.md` · `kb-hierarchy-runtime.md` · `kb-lockstep-soak.md` · `kb-parallel-query.md` · `kb-perf-harnesses.md` · `kb-query-invalidation.md` · `kb-safety-proof.md` · `kb-snapshot-persistence.md` · `kb-soak-test.md` · `kb-test-workflow.md`

### 零事实问题（6 页，审计未检出错 — 可选 metadata/措辞由 lead 清理）

| 文件 | 原因 | lead 清理动作 |
|------|------|--------------|
| `kb-deferred-create-design.md` | 零错误 | N/A |
| `kb-lockstep-playbook.md` | 纯导航页，全部验证通过 | N/A |
| `kb-commandstream-game-perf.md` | 全部声明通过验证 | N/A |
| `kb-profiling-workflow.md` | 全部验证通过 | N/A |
| `kb-throughput-workflow.md` | 全部验证通过 | N/A |
| `kb-design-rationale.md` | 零错误 | N/A |
| `kb-changelog.md` | 历史快照，不需更新 | N/A |

**已由 lead 清理的可选/信息性项**：
- ✅ `INDEX.md`：M1 front matter 已补充
- ✅ `kb-change-tracking.md`：M3 module 已统一、M4 description 已精简
- ✅ `kb-repo-overview.md`：措辞已修正为"协作验证入口"

---

## 5. 关键修复类别

### 5.1 事实性错误（最严重，已修复）

| 错误 | 影响 | 修复方式 |
|------|------|---------|
| World partial 文件数说 7 实为 5 | 2 页 | 全部改正为 5 |
| 引用已删除文件 `ComponentColumnMap.cs`、`SpanHelper.cs` | 1 页 | 删除条目或更新引用 |
| `M-BulletHellWarfare` 场景不存在 | 1 页 | 删除相关描述 |
| Add 语义描述为"overwrite"但实际是 strict Add | 2 页 | 增加历史校准说明 |

### 5.2 路径/行号引用失效

- `Core/Query.cs` → `Core/QueryCache.cs`（影响 6+ 文件）
- `World.Checksum.cs` 完全不存在（影响 1 文件）
- `World.ReplayCore` 行号偏 215 行（影响 1 文件）
- 方法名 `SortTypesAndOffsets` 等全部过时（影响 1 文件）

### 5.3 过期数据

| 数据 | 旧值 | 新值 |
|------|------|------|
| HeroComing.Perf baseline | 1512 / 959 | 2052.7 / 1246.8 |
| 回归阈值 | 1210 / 767 | 1642 / 997 |
| 测试计数 | 多个旧值（713~872） | 统一 873+5=878 |
| GameTickSim 场景数 | 11 | 13 |
| ParallelQuery 测试数 | 14 | 18 |

### 5.4 契约描述校准

- **`World.Add<T>`**：从旧文件描述的"overwrite"语义 → 增加显式说明"此为历史，当前 strict Add (throws)"
- **B5 回归测试状态**：从"目前 FAIL" → "已修复，当前 PASS"
- **B8-B15 测试名**：从旧的 `BUG_ValueChanges_*` / `BUG_RestoreState_*` → 当前 `Watch_*` / `X_Watch_*` / `CrossFeatureParityTests` 名

---

## 6. 保留的历史参考声明（不构成当前声明）

以下内容经审计后**故意保留为历史参考**，并已加明确标注：

| 位置 | 内容 | 标注 |
|------|------|------|
| `kb-hero-pipeline-regression.md:65-71` | 2026-05-29 的 cycles/sec 数据表 | 注：实际运行 ~3 秒，命名 `_20Seconds` 为历史遗留，数值用于 before/after 对比 |
| `kb-ecs-comparison.md:63-72` | S10 MixedLoad ops/s 数字（MiniArch 2491 / Friflo 1121） | > 以下数据来自 2026-06 历史排查...不反映当下性能状态 |
| `kb-perf-harnesses.md:41` | SubmitAndSnapshotAsync 1818 rounds/s | 标记为"2026-06-22 历史 datapoint" |
| `kb-cache-optimization.md:88-89` | Movement-Stream 1818 / Attack-Stream 1101 | 标记为历史独立测量，注明不与 HeroComing baseline 直接比较 |
| `kb-code-review-findings.md` B1/B4 | Add "overwrite" 语义描述 | 内嵌说明"此为历史，当前 `World.Add<T>` 是 strict Add" |
| `kb-code-review-findings.md` B13/B14 | 旧 `ValueChanges<T>` / `SharedTrackerRegistry` 回归测试 | 标记为"不再适用（旧 API 已删除）" |
| `kb-changelog.md` 多处 | 历史测试计数 837/869 | 历史快照保留，数据反映当时状态 |

**以上所有保留的历史声明都不会被读者误读为当前库行为的依据**，因每一处都有明确的上文校准或内嵌说明。

---

## 7. 验证状态

### 7.1 审计中已运行的验证

| 验证 | 何时运行 | 结果 |
|------|---------|------|
| `dotnet build -c Release` | Group C/F/D 审计时 | ✅ PASS |
| `dotnet test -c Release MiniArch.Tests` (873 测试) | Group D 审计时 | ✅ 873/873 PASS |
| `dotnet test -c Release HeroPipeline.Tests` (5 测试) | Group D 审计时 | ✅ 5/5 PASS |
| `dotnet test --filter "Submit_and_Replay_free_list_*"` | Group D 审计时 | ✅ PASS（确认 B5 修复） |
| `dotnet test --filter "FullyQualifiedName~SubmitReplayParityTests"` (13 测试) | Group D 审计时 | ✅ PASS |
| `dotnet test --filter "FullyQualifiedName~RobustnessTests"` (19 测试) | Group D 审计时 | ✅ PASS |
| 文件名存在性检查 (`glob`, `rg`) | 全部 6 组 | ✅ 全部匹配 |
| 类/方法签名检查 (`rg`) | 全部 6 组 | ✅ 全部匹配 |
| 行号验证 (`rg -n`) | 全部 6 组 | ✅ 全部已更新 |
| INDEX.md 链接完整性 | Group F | ✅ 全部 30 kb-* 文件存在 |
| 旧 API 删除确认 (`rg` 零匹配) | 全部 6 组 | ✅ 全部已确认 |

### 7.2 最终验证（lead 已运行）

| 验证 | 命令 | 结果 |
|------|------|------|
| 全量 release 编译 | `dotnet build -c Release` | ✅ PASS，0 warning / 0 error |
| 全量单元测试 | `dotnet test -c Release` | ✅ PASS，MiniArch.Tests 873/873，HeroPipeline.Tests 5/5 |
| 回归门禁 | `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline` | ✅ PASS：Movement 1901.6 ≥ 1642；Attack 1137.0 ≥ 997；Memory OK |
| PowerShell 测试入口 | `tools/scripts/test.ps1 -Configuration Release -Filter PublicApiSentinel...` | ✅ PASS，证明脚本 repoRoot/path 修复有效 |
| 过时字符串扫描 | `grep`/`rg` stale patterns across `.knowledge` | ✅ 仅保留显式标注为历史记录的 changelog/历史 datapoint |

---

## 8. 残余风险评估

### 8.1 低残余风险（已处理）

| 风险 | 评估 | 缓解措施 |
|------|------|---------|
| 行号漂移（KB→代码） | 修复时已更新，但未来代码变更仍可能漂移 | 已大量使用方法名替代脆弱行号；KB 定位为"指南"，不承诺行号永久精确 |
| 测试计数漂移 | 已统一为当前 873+5，但新增测试会再次过时 | CR-5 已使用通配表述降低维护成本 |
| 性能阈值漂移 | 已全部同步，但 baseline 刷新后需跟进 | 阈值集中维护于 `kb-hero-pipeline-regression.md`，其他页改为引用 |
| 未纳入主消歧矩阵的 `tools/perf/` 项目 | 13 个项目中仅主工具在矩阵中展开 | PH-3 已软化措辞为"多套"，不再声称矩阵覆盖全部 perf 项目；未来可按需扩矩阵 |

### 8.2 已知未修复项

**本次审计范围内无已知未修复的 .knowledge 内容/metadata 问题。**

所有审计发现的过时/不一致项已通过以下方式解决：
- **21 个文件**的严重/中等/低严重问题 → 审计 agent 修复已应用
- **INDEX.md** front matter 缺失 → lead 已补充
- **kb-change-tracking.md** module/description → lead 已统一/精简
- **kb-repo-overview.md** 措辞 → lead 已修正为"协作验证入口"
- **M5**（kb-architecture-review.md module 字段建议 `Meta`） → 设计决策保留当前值，非错误

> 备注：M5 仅为信息性建议（module 当前 `MiniArch.Core` 技术上正确，但该页实际覆盖整个架构）。经 lead 评估后保持当前值不变。

### 8.3 范围边界

- **代码正确性**：本次目标是 KB 声明与代码的一致性；代码自身正确性由 873+5 测试和 perf 门禁覆盖。
- **主题完整性**：本次按现有 INDEX.md 的模块地图审计全部现存知识页；是否新增未来主题不属于本次目标。
- **Godot 集成**：当前知识库没有 Godot 编辑器/项目配置主题页；因此本次不扩展到 Godot 编辑器验证。

---

## 9. 结论

**经过 6 组并行审计、合并去重、直接重新验证（V-1~V-6）、知识库 patch、连带修复、审计产物修正以及 lead 最终清理，本项目知识库 (`./knowledge/`) 中不再存在任何未解决的未知/过时知识声明或 metadata 问题。**

- 全部 31 个目标文件已审计
- 21 个文件共修复 ~60 项不一致
- 6 个需验证项已通过直接验证决策全部解决
- 7 个连带文件（CONTRIBUTING.md + 6 脚本）已同步修复
- 8 个审计产物问题已修正
- **3 项先前标记为"可选/信息性"的 metadata/措辞问题已由 lead 清理解决**：
  - ✅ INDEX.md front matter 补充
  - ✅ kb-change-tracking.md module 统一 + description 精简
  - ✅ kb-repo-overview.md 措辞修正
- 所有保留的历史参考声明均已被明确标注，不会造成误解

**最终确定性验证已完成**：`dotnet build -c Release`、`dotnet test -c Release`、`HeroComing.Perf --check-baseline` 全部通过。

---

## 附录：文件变更统计

```
审计 agent 修复 — 21 .knowledge 文件变更:
   161 insertions(+), 129 deletions(-)

lead 清理 — 3 .knowledge 文件 + 2 非知识库变更:
  .knowledge/INDEX.md                 | +7 lines (front matter + 内容微调)
  .knowledge/kb-change-tracking.md    | module/description 更新
  .knowledge/kb-repo-overview.md      | 措辞修正
  CONTRIBUTING.md                     | 阈值 1210/767 → 1642/997
  6 个工具脚本                        | $repoRoot 路径修正

3 docs/audits/ 审计产物:
  knowledge-audit-inventory-20260709.md       (行数/metadata 更新 — 本版)
  knowledge-audit-repair-plan-20260709.md     (lead 清理项标记 — 本版)
  knowledge-audit-final-report-20260709.md    (本文件，本版更新)
```

---

*报告生成日期: 2026-07-09 | 审计工作树: knowledge-consistency-audit-20260709*
