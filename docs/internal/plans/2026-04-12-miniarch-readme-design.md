# MiniArch README 设计

## 目标

- 为项目提供一份真正可交付的 README，而不是只面向仓库维护者的简短导航。
- 清晰区分 `MiniArch.Ecs` 的普通用户 API 和 `MiniArch.Core` 的 advanced API。
- 让读者一眼看出哪些 API 可以安全用于并发只读、哪些 API 支持多线程 recording、哪些 API 明确不能并发写。
- 提供几个最常用的最小例子，降低首次接入成本。

## 当前问题

- 根目录 `README.md` 目前只覆盖仓库结构和 agent 工作流，不足以作为对外文档。
- `src/MiniArch/README.md` 虽然提到了 API layering，但仍然是摘要，尚未逐个公开 API 说明。
- 当前仓库已经形成了明确的普通层 / advanced 层边界，但文档还没有把这条边界讲透。

## 方案选项

### 方案 A：把所有内容都塞进根目录 README

- 优点：
  - GitHub 首页直接可见。
- 缺点：
  - 文档会过长，仓库导航和 API 参考会混在一起。
  - 根 README 后续会同时承担“仓库入口”和“库参考手册”，维护成本高。

### 方案 B：根目录 README 做入口，`src/MiniArch/README.md` 做完整 API 手册

- 优点：
  - 单一职责更清晰。
  - 根 README 仍然适合快速了解仓库。
  - `src/MiniArch/README.md` 可以放心展开每个公开 API，而不会挤占仓库首页。
- 缺点：
  - 需要读者多点一次链接。

## 推荐方案

- 采用方案 B。

理由：

- 这最符合当前仓库已经形成的结构：根目录负责仓库层，`src/MiniArch` 更适合承载运行时 API 文档。
- 用户要求“详细介绍每个公开 API”，这种内容天然更适合放在库 README，而不是仓库首页。

## 文档结构

### 根目录 `README.md`

- 简要介绍项目目标。
- 列出仓库结构与常用脚本。
- 链接到：
  - `src/MiniArch/README.md`
  - `.knowledge/INDEX.md`
  - `scripts/verify.ps1`

### `src/MiniArch/README.md`

- 项目定位与能力范围
- API 分层选择指南
- 并发标记约定
- 最小示例
- `MiniArch.Ecs` 全部公开 API
- `MiniArch.Core` 全部公开 API
- 并发/线程安全边界
- 常见坑点

## 重点表达约定

- 普通用户 API 明确标记为“推荐默认使用”。
- advanced API 明确标记为“只有在你要控制查询、chunk、snapshot、command buffer 或结构细节时再用”。
- 多线程相关标记统一使用：
  - `MT-Read`：支持 world 无写入时的并发只读
  - `MT-Record`：支持多线程 command recording
- 明确说明“无 API 支持并发写 world”。

## 验证方式

- 先核对源码公开成员与知识页结论是否一致。
- 修改后运行测试，至少确保测试工程通过。
- 再让 3 个不同角色的子 agent 审核 README：
  - 普通 Unity / Godot 风格游戏逻辑开发者
  - 性能/工具链导向的 advanced ECS 使用者
  - 关注并发与 command buffer 语义的系统开发者
- 只有三者都认为 README 可用，才作为交付完成。
