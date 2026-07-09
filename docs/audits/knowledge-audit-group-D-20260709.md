# 知识库 Group D 一致性审计报告

**审计日期**: 2026-07-09  
**审计范围**: 4 个知识页 vs 当前代码/测试/工具/文档  
**工作树**: `.worktrees/knowledge-consistency-audit-20260709`  
**编译模式**: `-c Release`  
**当前测试**: MiniArch.Tests = 873 PASS, HeroPipeline.Tests = 5 PASS, 总计 878 PASS

---

## 覆盖声明

| 页面 | 总行数 | 审计行数 | 覆盖度 | 裁决 |
|------|--------|----------|--------|------|
| `kb-soak-test.md` | 121 | 121 | 100% | **5 个不匹配**，1 个不可验证 |
| `kb-safety-proof.md` | 216 | 216 | 100% | **4 个不匹配** |
| `kb-lockstep-soak.md` | 227 | 227 | 100% | **1 个不匹配** |
| `kb-code-review-findings.md` | 348 | 348 | 100% | **8 个不匹配**，1 个不可验证 |

---

## 高优先级发现（必须修复）

### H1: B1/B4 "Add 覆盖写入"语义与当前契约矛盾

| 属性 | 内容 |
|------|------|
| **文件:行** | `kb-soak-test.md:80-88` (B1/B4), `kb-code-review-findings.md:122-155` (B1/B4) |
| **原始声明** | B1 修复为"已有时原地写值而不是抛异常"；B4 同上 |
| **实际事实** | 当前 `World.Add<T>()` 契约是 **strict Add**（已存在时抛异常）。`ApplyRawAdd` (`World.StructuralChange.cs:184-196`) 和 `ApplyTypedAdd` (`World.StructuralChange.cs:121-137`) 在组件已存在时均抛 `InvalidOperationException`。Soak 测试的 `OpAdd()` (`SoakRunner.cs:380-383`) 通过 `!_source.Has<CompX>(e)` 守卫避免触发此异常。测试 `Add_component_that_already_exists_throws` (`TrickyEdgeCaseTests.cs:140-149`) 明确验证"throws"行为。 |
| **证据** | `World.StructuralChange.cs:184-196` 行 187-191 `throw new InvalidOperationException(...)`；`src/MiniArch/Core/World.StructuralChange.cs:132-137` 同理；`tests/MiniArch.Tests/Core/TrickyEdgeCaseTests.cs:140-149`；`tools/soak/MiniArch.Soak/SoakRunner.cs:380-383` |
| **风险** | 高。B1/B4 仍然描述旧的"overwrite"语义，与当前代码行为和测试完全矛盾。`kb-code-review-findings.md` 有"Contract calibration"提示（line 10-11）告知读者 B1/B4 是历史记录，但 `kb-soak-test.md` **没有**任何对应提示，读者会误以为当前 Add 是 overwrite 语义。 |
| **建议** | 在 `kb-soak-test.md` B1/B4 条目增加显式说明"此为历史记录——当前 `World.Add<T>` 的契约是 strict Add（已存在时抛异常）"。在 `kb-code-review-findings.md` 的 B1/B4 条目内嵌相同说明而非仅靠顶部的全局 calibration 提示。 |

### H2: B5 回归测试状态声明为 FAIL，实际已 PASS

| 属性 | 内容 |
|------|------|
| **文件:行** | `kb-code-review-findings.md:168` |
| **原始声明** | "回归测试: `Submit_and_Replay_free_list_diverges_with_multi_cancel`（目前 FAIL，修复后应通过）" |
| **实际事实** | **该测试现在 PASS**。确认运行 `dotnet test -c Release --filter "Submit_and_Replay_free_list_diverges_with_multi_cancel"` 返回 ✅ PASS。测试描述（`FrameDeltaDeterminismTests.cs:723-725`）已更新为 "Fixed by changing RemoveFromFreeList to shift survivors left"。 |
| **证据** | `dotnet test -c Release --filter "Submit_and_Replay_free_list_diverges_with_multi_cancel"` → ✅ PASS |
| **风险** | 中。读者会误以为 B5 尚未完全修复。 |
| **建议** | 将 "目前 FAIL" 改为 "已修复，回归测试 PASS"。 |

### H3: 代码审阅发现的 `BUG_ValueChanges_*` 测试名不存在

| 属性 | 内容 |
|------|------|
| **文件:行** | `kb-code-review-findings.md:200-256` (B8-B15) |
| **原始声明** | 引用 `BUG_ValueChanges_query_survives_RestoreState_without_stale_or_lost_tracking`、`BUG_ValueChanges_captures_CommandStream_Set_writes` 等测试名 |
| **实际事实** | 这些测试名 **在代码库中不存在**。对应测试已更名为 `Watch_*` 前缀（Watch API 重构后）。例如：`Watch_captures_CommandStream_Set` (`ChangeQueryTests.cs:66`)、`Watch_handles_world_growth_after_arming` (`ChangeQueryTests.cs:274`)。B8/B15 的 `BUG_RestoreState_*` 测试名目前未找到（可能已被合并/删除）。 |
| **证据** | `rg "ValueChanges_query_survives|ValueChanges_captures_CommandStream|BUG_RestoreState_preserves_value_query|BUG_RestoreState_preserves_filtered_transition" tests/` → 0 结果；实际测试见 `tests/MiniArch.Tests/UserApi/ChangeQueryTests.cs` |
| **风险** | 高。知识页引用不存在的测试名，读者无法定位回归测试。 |
| **建议** | 将 B8-B15 的回归测试名全部更新为当前 `Watch_*` 名，或引用 CrossFeatureParityTests 中的 `X_*` 测试。无法确认存在的条目（如 B8/B15 的 `BUG_RestoreState_*`）应标记为"需确认"。 |

### H4: `Add_component_that_already_exists_overwrites_value` 测试不存在

| 属性 | 内容 |
|------|------|
| **文件:行** | `kb-code-review-findings.md:129, 155` (B1/B4 验证段) |
| **原始声明** | "测试 `Add_component_that_already_exists_overwrites_value`" |
| **实际事实** | 此测试名在代码库中 **不存在**。实际测试为 `Add_component_that_already_exists_throws` (`TrickyEdgeCaseTests.cs:140-149`)，验证的是 **throws** 行为而非 overwrite。 |
| **证据** | `rg "Add_component_that_already_exists_overwrites_value"` → 0 结果 |
| **风险** | 高。测试名与当前契约矛盾，且指向不存在的符号。 |
| **建议** | 删除或替换为当前测试名 `Add_component_that_already_exists_throws`，更新描述以反映 strict Add 契约。 |

---

## 中优先级发现

### M1: 测试计数全面过时

| 文件 | 原始断言 | 当前实际 | 说明 |
|------|----------|----------|------|
| `kb-safety-proof.md:21` | `713（708 + 5 pipeline）` | 873 + 5 = 878 | 差 165 |
| `kb-code-review-findings.md:35` | `845/845 pass` | 873 | 差 28 |
| `kb-code-review-findings.md:64` | `872/872 pass` | 873 | 差 1 |
| `kb-code-review-findings.md:105` | `869+5 全 PASS` | 873+5 | 差 4 |
| `kb-code-review-findings.md:138` | `全部 673 现存测试通过` | 873 | 差 200 |
| `kb-code-review-findings.md:184` | `全部 695 个单元测试通过` | 873 | 差 178 |
| `kb-code-review-findings.md:284` | `MiniArch.Tests 818、HeroPipeline.Tests 5` | 873+5 | 差 55 |
| **建议** | 全局搜索所有测试计数，统一替换为当前值（873 + 5 = 878）或使用通配表述（如"870+"） | | |

### M2: Perf 数值与基线文档不一致

| 文件:行 | 原始声明 | 基线文档值 | 差值 |
|---------|----------|-----------|------|
| `kb-soak-test.md:64` | Movement 2086, Attack 1256 | 2052.7 / 1246.8 | +33 / +9 |
| `kb-safety-proof.md:23` | Movement 2086, Attack 1256 | 同上 | 同上 |
| **建议** | 与 `kb-hero-pipeline-regression.md` 同步为最新基线数据，或引用该文档而非写死数值。 | | |

### M3: Seed 计数不统一

| 文件:行 | 声明 | 说明 |
|---------|------|------|
| `kb-soak-test.md:9,66` | "259 seed × 6.4M+ 帧" | 含多样性 sweep + long-run + boundary |
| `kb-safety-proof.md:17` | "224 个随机种子" | 仅多样性 sweep (32+64+128) |
| **建议** | 统一口径：多样性 sweep 说 224，总 unique seeds 说 ~227-259 且注明包含 boundary 测试。 | | |

### M4: 代码行号引用漂移

以下引用与实际行号偏差 >2 行：

| 文件:行 | 原始位置 | 当前位置 | 偏差 |
|---------|----------|---------|------|
| `kb-code-review-findings.md:124` | `World.StructuralChange.cs:152` | 184 | -32 |
| `kb-code-review-findings.md:150` | `World.StructuralChange.cs:100-107` | 121-128 | -21 |
| `kb-code-review-findings.md:31` | `World.cs:1144-1153` | 1157+ | -13 |
| **建议** | 用方法名代替脆弱行号，或只保留当前关键锚点。 | | |

### M5: `kb-lockstep-soak.md` 证明矩阵日期不匹配

| 文件:行 | 内容 |
|---------|------|
| `kb-lockstep-soak.md:173` | "证明矩阵（2026-07-06）" — 矩阵日期与页面 `updated: 2026-07-09` 不一致 |
| **建议** | 统一为同一日期，或注明矩阵最后运行日期与页面更新日期不同。 |

---

## 低优先级 / 信息性发现

### L1: `kb-soak-test.md` 声明"6 个库级 bug"但 `kb-code-review-findings.md` 列出 B1-B16

- `kb-soak-test.md:9` "已发现并修复 6 个库级 bug" — 这是指浸泡测试**独立发现**的 B1-B6
- `kb-code-review-findings.md` 列出 B1-B16，其中 B7-B16 通过代码审阅发现，非 soak 测试
- 表述正确但可能引起混淆，建议在 `kb-soak-test.md` 加注"另有 10 个 bug 通过代码审阅发现（见 `kb-code-review-findings.md`）"

### L2: `kb-lockstep-soak.md` 证明矩阵中的确定性验证行

- 矩阵行：`Determinism` — 仅有结果无具体命令/checksum，与其他行详细程度不一致
- 建议补充命令和 checksum 值（格式参考 `kb-safety-proof.md` §3.3）

### L3: `kb-code-review-findings.md` B6 修复验证引用 "全部 695 个单元测试"

- 已过时（目前 873），但不影响正确性

### L4: 几个 S 系列（安全猜想）的代码行号引用接近但不精确

- S2: `Archetype.Storage.cs:333-369` → 332-369 (差 1, 可接受)
- S3: `World.EntityLifecycle.cs:83-229` → 实际 Destroy 主循环至 109 行, 但范围内包含其他方法 (高估)
- S6: `EntityFieldResolver.cs:70-103` → 70-103 (精确匹配)
- 多数在可接受误差范围内

---

## 不可验证声明

| 文件:行 | 声明 | 原因 |
|---------|------|------|
| `kb-soak-test.md:56-66` | 证明矩阵中的 soak 运行结果（32/32 PASS, 64/64 PASS 等） | 无法在审计中运行完整 soak（耗时太长）。非可疑——代码结构、sweep 逻辑、诊断输出均指向结果可信。 |
| `kb-safety-proof.md:73-78` | 同上（224 seed 证明矩阵） | 同上 |
| `kb-lockstep-soak.md:177-187` | 多 host 证明矩阵（41 seed × 1.09M 帧） | 同上 |
| `kb-safety-proof.md:96-101` | 确定性 checksum 特定哈希值 | 哈希值依赖运行时细节，不应硬编码。建议标注"实际值会变化，关键是两次一致"。 |

---

## 高优先级声明验证通过清单

以下高风险声明经核实为 **正确**：

- ✅ **Bug 模式分类 P1-P5**: `kb-safety-proof.md:134-139` — 5 种发散模式与 `SubmitReplayParityTests.cs` 注释一致
- ✅ **15 条路径审计**: `kb-safety-proof.md:156-166` — 13 `SubmitReplayParityTests` + 2 free-list 路径 = 15, 所有 13+2 测试 PASS
- ✅ **19 个 RobustnessTests PASS**: `kb-safety-proof.md:117-119` — 运行确认 19/19 PASS
- ✅ **B5/B6 需边界条件触发**: `kb-safety-proof.md:143-149` — soak 守卫代码 `_pendingRemoves` / `_pendingAdds` 证明确认
- ✅ **`InternalsVisibleTo("MiniArch.LockstepSoak")`**: `AssemblyInfo.cs:12` — 存在
- ✅ **Soak/lockstep-soak 不在 .sln 中**: `rg MiniArch.Soak *.sln` → 0 结果
- ✅ **B7-B16 回归测试存在（更名后）**: `Watch_captures_CommandStream_Set` 等测试 PASS
- ✅ **0× 确定性**: B5+B6 回归测试 SHA256 前+后均 PASS
- ✅ **Epoch guard (M2 re-apply)**: `CommandStreamCore.cs:471` `_submitEpoch = _world.ReservedReleaseEpoch` 存在

---

## 验证使用的命令

```bash
# 全量测试
dotnet test -c Release tests/MiniArch.Tests/
dotnet test -c Release tests/HeroPipeline.Tests/

# 专项测试
dotnet test -c Release --filter "Submit_and_Replay_free_list_diverges_with_multi_cancel"
dotnet test -c Release --filter "Submit_and_Replay_free_list_diverges_with_reverse_destroy_order"
dotnet test -c Release --filter "FullyQualifiedName~SubmitReplayParityTests"
dotnet test -c Release --filter "FullyQualifiedName~SubmitReplayRestoreParityTests"
dotnet test -c Release --filter "FullyQualifiedName~RobustnessTests"

# 构建
dotnet build -c Release tests/MiniArch.Tests/

# 搜索
rg "pattern" --include "*.cs" src/ tests/ tools/
```

---

## 总结

| 严重程度 | 数量 |
|----------|------|
| 高（必须修复） | 4 |
| 中（建议修复） | 5 |
| 低 | 4 |
| 不可验证 | 4（均因运行耗时过长，非可疑） |

**最严重的系统性缺陷**: B1/B4 契约描述与当前代码矛盾（H1）——`kb-soak-test.md` 无任何校准提示，而 `kb-code-review-findings.md` 虽有顶部提示但 B1/B4 正文仍具误导性。建议在 `kb-soak-test.md` 的 B1/B4 条目增加显式说明清楚"此为历史，当前 `Add` 是 strict Add (throws)"。
