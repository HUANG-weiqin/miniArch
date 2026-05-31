---
title: Repository Overview
module: Workspace
description: How to navigate the repo and where to start
updated: 2026-04-12
---
# Repository Overview

## 这个模块是干什么的

- 这个模块负责：
  - 给后续 agent 提供仓库导航入口
  - 说明先读哪些文件，再读哪些文件
  - 约定常用构建、测试和验证命令
- 这个模块不负责：
  - ECS 运行时实现细节
  - 单元测试断言本身
  - 具体功能设计决策

## 架构

- 核心组成：
  - `AGENTS.md`：仓库级协作约束
  - `.knowledge/INDEX.md`：知识库目录
  - `README.md`：仓库级快速入口
  - `scripts/`：可直接执行的 build/test/verify 包装脚本
- 数据流 / 控制流：
  - 新 agent 先读 `AGENTS.md`
  - 再读 `.knowledge/INDEX.md`
  - 然后进入最贴近任务的 `kb-*.md`
  - 最后用 `scripts/verify.ps1` 复核改动

## 决策

- 把协作入口放在根目录，而不是散落在源码目录里，便于 agent 先找到入口再进入实现细节。
- 用脚本包一层 `dotnet` 命令，减少每次记忆项目路径的成本。
- 把知识页拆成单主题文件，避免一个大文档同时混合导航、架构和测试信息。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 仓库的“地图”和“操作台”
- 这个模块里最重要的抽象是：
  - 入口顺序
  - 可执行命令
- 常见误解：
  - 认为知识库可以替代源码阅读
  - 认为脚本只是便利项，而不是协作稳定性的一部分

## 入口

- 第一次读或加知识页，先看：
  - `README.md`：仓库级快速入口
  - `.knowledge/INDEX.md`：知识页目录
  - `.knowledge/_template.md`：知识页模板
- 修协作入口，先看：
  - `scripts/verify.ps1`：统一验证命令

## 坑点

- 历史上容易出问题的地方：
  - 只有源码，没有导航，agent 容易重复读错文件
  - 只有 `dotnet` 命令，没有统一脚本入口，路径容易写散
- 容易误判的地方：
  - 以为 `.knowledge` 只是文档装饰
  - 以为 README 和知识页可以互相替代
- 改这里时要特别小心：
  - 知识页改名后必须同步更新 `INDEX.md`
  - 脚本命令变化后必须同步更新 README
  - 新增可复用脚本后，必须同步补到知识页入口里

