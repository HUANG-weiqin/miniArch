---
title: Repository Overview
module: Workspace
description: How to navigate the repo and where to start
updated: 2026-06-30 (加 build/test quickstart 段)
---
# Repository Overview

## 这个模块是干什么的

- 给后续 agent 提供仓库导航入口
- 说明先读哪些文件，再读哪些文件
- 约定常用构建、测试和验证命令

## 架构

- 核心组成：
  - `AGENTS.md`：仓库级协作约束
  - `.knowledge/INDEX.md`：知识库目录
  - `src/MiniArch/`：ECS 运行时源码（Core/ + Query.cs 等顶层文件）
  - `tests/`：单元测试与 pipeline 测试（含 `SharedInfrastructure/` 共享 runner）
  - `tools/perf/`：独立吞吐量 perf 项目（HeroComing.Perf 等）
  - `tools/scripts/`：可直接执行的 build/test/verify/profiling 包装脚本
- 数据流 / 控制流：
  - 新 agent 先读 `AGENTS.md` → `.knowledge/INDEX.md` → 最贴近任务的 `kb-*.md`
  - 验证改动通过 `tools/scripts/verify.ps1`

## 决策

- 协作入口放在根目录，便于 agent 先找到入口再进入实现细节
- 用脚本包一层 `dotnet` 命令，减少每次记忆项目路径的成本
- 共享基础设施（ThroughputRunner、BenchmarkWorldFactory、QueryProfilingRunner 等）放在 `tests/SharedInfrastructure/MiniArch.SharedInfrastructure/`，避免 benchmarks 和 perf 项目重复定义
- 知识页按单主题文件拆分，避免一个文档混合导航、架构和测试信息

## 快速开始（构建 / 测试）

```bash
# 全量构建（必须 -c Release 用于性能测量）
dotnet build miniArch.sln -c Release

# 运行全部测试
dotnet test miniArch.sln -c Release

# 运行特定测试（按测试名过滤）
dotnet test miniArch.sln -c Release --filter "Chunked_mode_*"

# 回归门禁（架构变更后必跑）
dotnet run -c Release --project tools/perf/HeroComing.Perf

# 生成知识库新页面前先看模板
# .knowledge/_template.md
```

## 认知模型

- 仓库的"地图"和"操作台"

## 入口

- 第一次读：`README.md` → `.knowledge/INDEX.md` → `.knowledge/_template.md`
- 修协作入口：`tools/scripts/verify.ps1`

## 坑点

- 知识页改名后必须同步更新 `INDEX.md`
- 脚本命令变化后必须同步更新 README
- 新增共享基础设施后，必须同步更新对应的知识页路径
- World 和 Archetype 现在是 partial 类，修改时注意 partial 文件的编译范围
