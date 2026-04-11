# Knowledge Index

`.knowledge/` 是当前默认的模块知识库。这里按“先读什么、再读什么”的方式组织，不按历史写作顺序组织。

## 文件简介

- `kb-repo-overview.md`：仓库导航、协作入口和脚本使用方式
- `kb-core-ecs.md`：`MiniArch.Core` 的运行时架构说明
- `kb-test-workflow.md`：测试组织、验证方式和常见回归点

## 模块地图

- `Workspace` -> `kb-repo-overview.md`
- `MiniArch.Core` -> `kb-core-ecs.md`
- `MiniArch.Tests` -> `kb-test-workflow.md`

## 快速入口

- 想先找仓库入口，先看 `kb-repo-overview.md`。
- 想理解 ECS 运行时，先看 `kb-core-ecs.md`。
- 想理解测试覆盖和验证方式，先看 `kb-test-workflow.md`。
- 想理解“为什么边界这么划”，先看各模块页里的 `决策`。
- 想理解“怎么读这个模块”，先看各模块页里的 `入口`。
- 想排查行为偏差，先看各模块页里的 `坑点` 和对应测试文件。
- 新增知识页时，先把它挂到这里，再写模块正文。
