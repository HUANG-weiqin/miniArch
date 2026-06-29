# Agent 运行说明

本项目将可复用知识沉淀在 `.knowledge/`。

## 工作原则

- 在开始推进前，应先明确：任务目标、验证条件、什么才算完成。
- 如果当前信息不足以定义完成标准，必须先补齐完成标准，再继续推进。

## 设计原则

- **激进 YAGNI**：能删就删，删不了的做到最小。不预留"以后可能用到"的东西，已有的如果不必要也删掉。具体表现：
  - 概念唯一：同一事物只保留一个表示，冗余表示即使已成公共 API 也必须砍掉。
  - 名字诚实：名字必须描述当前行为，不承载历史。`Playback()` 实际是编译就叫 `Compile()`。
  - 拒绝预计算：能从现有状态推算出来的东西不预先存储（如反向快照从 archetype 推算）。
  - 纯函数优先：优先无副作用的静态方法，避免可变状态和生命周期管理。
  - 先跑通再优化：从最简实现开始，只在需求被证实时才增加复杂度。

## 项目目标与语言规范

- 核心目标：
  - 构建一个简单高效完备的ECS 架构
- 文档与交流：
  - 计划、文档、评审、协作沟通尽量使用中文。
- 代码与注释：
  - 源码标识符与代码注释使用英文。

## 性能基准测试铁律

- **永远用 `-c Release`**：NuGet 依赖（如 Arch）是 Release 编译的预编译包。用 Debug 编译的自有代码对比 Release 编译的第三方包，会产生完全误导的结果。所有 `dotnet run`/`dotnet build` 涉及性能测量时，必须加 `-c Release`。
- **测量范围要对齐**：确保对比的是同一阶段（如仅 record+submit，不含 world 创建/setup）。
- **分段测量找瓶颈**：发现性能差异时，先拆分 record vs submit 分段计时，定位瓶颈再优化。

## 工具偏好

- 查找文件优先使用 `fd`。
- 纯文本搜索优先使用 `rg`。
- 结构化代码搜索、按语法批量改动或需要避免纯文本误命中时，优先使用 `sg`（ast-grep）。
- 涉及 Godot 编辑器启动、项目导入、构建解方案或无头验证时，优先使用 Godot CLI；终端/无头场景优先 `Godot_v4.4-stable_mono_win64_console.exe`。

## 1) 开始工作前的必读路径

1. 读取 `.knowledge/INDEX.md`。
2. 按具体任务找到最匹配的知识页。
3. 按需读取对应的 `.knowledge/kb-*.md` 文件，优先看：
   - `这个模块是干什么的`
   - `架构`
   - `决策`
   - `认知模型`
   - `坑点`

如果没有匹配主题，可以继续工作，但如果有可服务于本项目核心目标(通用ECS架构)的知识必须在结束前补充/更新知识页。

## 2) 知识库写入规则（强制）

在 `.knowledge/` 下新增或更新文件时，必须严格遵守：

- 每个知识页必须使用 `.knowledge/_template.md` 作为模板。
- front matter 必须完整且有效：
  - `title`
  - `module`
  - `description`
  - `updated`
- 内容结构先写结论，再写细节。
- 保持单一事实来源；优先链接，不重复拷贝。
- 模块或关键词变化时，必须同步更新 `.knowledge/INDEX.md`。

## 3) 知识页契约

每个知识页应保持“单文件、单主题、先结论后细节”的结构。
如果需要新增主题，请先更新 `.knowledge/INDEX.md`，再新增对应 `.knowledge/kb-*.md` 文件。

## 4) 完成门禁

任务完成后在给出最终回复前，必须确认：

1. 工作任务已经完成，且测试通过。
2. 已查阅相关 `.knowledge` 页。
3. 新学习（如有）已按模板规则写回知识库。
4. `updated` 已更新，且 `.knowledge/INDEX.md` 仍然准确。

## 5) 架构变更回归门禁（强制）

任何改动 `src/MiniArch/` 或 `tests/HeroPipeline.Tests/` 的架构变更，提交前必须执行：

```bash
dotnet run -c Release --project tools/perf/HeroComing.Perf
```

检查输出：
- 吞吐量低于阈值（Movement ≥1407 rounds/s, Attack ≥854 rounds/s）→ **回退改动**
- 内存持续增长 → **回退改动**
- 崩溃或异常 → **回退改动**

> 阈值取 baseline 的 80%，随 `kb-hero-pipeline-regression.md` 的 baseline 更新而同步。

### 5a) 确定性豁免（不必跑门禁）

下列改动**确定不可能影响运行时性能**，可跳过 `HeroComing.Perf` 门禁（仍需 `dotnet build` + `dotnet test` 通过）：

- **纯文档**：XML doc（`<summary>`/`<remarks>`/`<param>` 等）、行内注释、`.knowledge/` 下文件
- **死代码删除**：已通过调用方搜索确认零 caller 的方法/类型/字段
- **同类 partial 间迁移**：把方法/字段在同一 `partial class` 的多个 `.cs` 文件间挪动（JIT 看到合并后的同一类型，IL 不变）
- **重命名 private/internal 符号**：仅 metadata name 变化，不影响运行时查找（前提：未使用反射按名查找）
- **纯格式/空白**：缩进、换行、`using` 排序

**判断标准**：改动要么零 IL 差异，要么差异仅限于删除从未被调用的 IL。任一不满足时**必须跑门禁**——拿不准就跑。

### 5b) 回退流程

- `git stash` 或 `git checkout .` 恢复到改动前状态
- 分析失败原因，修复后重新测试

测试会自动更新 `.knowledge/kb-hero-pipeline-regression.md` 中的 baseline 数据。

详细说明见 `.knowledge/kb-hero-pipeline-regression.md`。
