---
title: Profiling Workflow
module: Workspace
description: Reusable CPU sampling workflow for locating MiniArch hotspots without instrumenting runtime hot paths
updated: 2026-06-01
---
# Profiling Workflow

## 这个模块是干什么的

- 记录仓库内可复用的 CPU sampling 流程
- 说明什么时候该用独立 profiling runner，而不是直接采样 BenchmarkDotNet

## 架构

- 核心组成：
  - `shared/MiniArch.SharedInfrastructure/QueryProfilingRunner.cs`
  - `scripts/profile-query.ps1`
  - `dotnet-trace`
- 数据流 / 控制流：
  - profiling runner 先用 benchmark world 构造固定场景 → runner 预热后打印目标进程 PID → 外部采样器附加到该进程 → 采样结束后 `dotnet-trace report ... topN` 看热点

## 决策

- 优先用"独立 runner + 外部采样"，不要在运行时代码里加 Stopwatch 或临时日志
- 不要优先采样 BenchmarkDotNet 子进程（带入 fork、warmup、harness 噪声）

## 认知模型

- 一套"先固定 workload，再附加采样，再读 topN"的通用定位流程

## 入口

- `scripts/profile-query.ps1`：仓库内现成的 profiling 入口
- `shared/MiniArch.SharedInfrastructure/QueryProfilingRunner.cs`：runner workload 构造

## 坑点

- 采样窗口覆盖 startup delay 会使 topN 里全是 `WaitHandle` / `Monitor.Wait`
- top1 包装函数占比高不代表 runner 慢（被内联方法折叠到调用者）
- `hot` vs `cold`：hot 看 steady-state traversal，cold 看 refresh / matching 热点

## 标准流程

- 安装：`dotnet tool install --global dotnet-trace`
- 直接启动并采样子进程：`dotnet-trace collect --profile dotnet-sampled-thread-time --duration 00:00:05 -o profiles\\sample.nettrace -- dotnet benchmarks\\MiniArch.Benchmarks\\bin\\Release\\net8.0\\MiniArch.Benchmarks.dll profile-query --scenario with-all --temperature cold --entity-count 100000 --duration 8 --warmup 1 --startup-delay 0`
- 读热点：`dotnet-trace report profiles\\sample.nettrace topN -n 20 --inclusive`

## 工作负载维度

- `entity`：仅遍历 entity ID，定位 chunk 遍历和 outer loop 热点
- `component-row-wise`：逐行访问组件，定位 `GetComponent<T>()` 热点
- `component-span`：批量 span 访问，定位 `GetComponentSpan<T>()` 和向量化机会

## 结果解读规则

- `inclusive` 看调用链总重，`exclusive` 看函数自身消耗
- 如果 top1 是包装函数，先检查内部是不是简单循环，再确认是否被 JIT 内联
