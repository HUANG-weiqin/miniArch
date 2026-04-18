---
title: Profiling Workflow
module: Workspace
description: Reusable CPU sampling workflow for locating MiniArch hotspots without instrumenting runtime hot paths
updated: 2026-04-16
---
# Profiling Workflow

## 这个模块是干什么的

- 这个模块负责：
  - 记录仓库内可复用的 CPU sampling 流程
  - 说明什么时候该用独立 profiling runner，而不是直接采样 BenchmarkDotNet
  - 给后续优化任务提供稳定的命令模板、采样窗口和结果解读规则
- 这个模块不负责：
  - 替代 benchmark 报表
  - 替代功能正确性测试

## 架构

- 核心组成：
  - `benchmarks/MiniArch.Benchmarks/QueryProfilingRunner.cs`
  - `scripts/profile-query.ps1`
  - `dotnet-trace`
- 数据流 / 控制流：
  - profiling runner 先用 benchmark world 构造固定场景
  - runner 预热后打印目标进程 `PID`
  - 外部采样器附加到该进程，在固定时间窗内抓调用栈样本
  - 采样结束后再用 `dotnet-trace report ... topN` 看热点函数
- 和其他模块的交互方式：
  - 依赖 `MiniArch.Benchmarks` 提供固定 workload
  - 依赖 `MiniArch.Core` 暴露真实热路径
  - 和 `.knowledge/kb-test-workflow.md` 互补：前者讲验证口径，这页讲采样方法

## 决策

- 结论：优先用“独立 runner + 外部采样”，不要在运行时代码里加 `Stopwatch`、计数器或临时日志。
- 原因：插桩会改写热路径，尤其是在 query、chunk 遍历、结构迁移这种微小循环里，很容易把真实热点掩盖掉。
- 也不要优先采样 BenchmarkDotNet 子进程，因为它会带入 fork、warmup、harness 自身的样本噪声；只有当仓库里没有独立 runner 时才退回这种做法。

## 认知模型

- 理解这个模块时，应该把它看成：
  - 一套“先固定 workload，再附加采样，再读 topN”的通用定位流程
- 这个模块里最重要的抽象是：
  - `hot` 模式：看 steady-state traversal 热点
  - `cold` 模式：看 refresh / matching / build 热点
- 常见误解：
  - 认为 `cold` 比 `hot` 更“真实”；实际上两者回答的是不同问题
  - 认为 top1 包装函数就是最终优化点；sample profiler 会把被内联的方法折叠到调用者上

## 入口

- 如果是第一次读这个模块，先看：
  - `scripts/profile-query.ps1`：仓库内现成的 profiling 入口
  - `benchmarks/MiniArch.Benchmarks/QueryProfilingRunner.cs`：runner 如何构造 workload 和控制采样窗口
- 如果是修 bug，先看：
  - `dotnet-trace list-profiles`：确认当前工具支持的 profile 名称
- 如果是加功能，先看：
  - `scripts/profile-query.ps1`：照这个模式为别的热点路径加独立 runner

## 坑点

- 历史上容易出问题的地方：
  - 采样窗口覆盖了 startup delay、world 构建或等待逻辑，结果 topN 里全是 `WaitHandle`、`Monitor.Wait`
  - 直接采样 BenchmarkDotNet 子进程，把 harness/warmup 当成运行时热点
  - 只看一份 `inclusive` 排名就下结论，没有结合 workload 语义判断是不是包装函数
- 容易误判的地方：
  - `QueryProfilingRunner.Execute` 这种包装函数占比高，不代表 runner 本身慢；通常表示内部循环中的方法被内联了
  - `BuildMatchingArchetypes` 占比低，不代表 query 快；它只说明当前 workload 的瓶颈不在 archetype matching
- 改这里时要特别小心：
  - 如果要附加到已运行进程，必须等 runner 打印真实目标 `PID` 后再 attach
  - 如果想看 refresh/matching 热点，不要用 `hot`；如果想看 steady-state traversal，也不要用 `cold` 混入 refresh

## 标准流程

- 安装工具：
  - `dotnet tool install --global dotnet-trace`
- 列出 profile：
  - `dotnet-trace list-profiles`
- 直接启动并采样一个子进程：
  - `dotnet-trace collect --profile dotnet-sampled-thread-time --duration 00:00:05 -o profiles\\sample.nettrace -- dotnet benchmarks\\MiniArch.Benchmarks\\bin\\Release\\net8.0\\MiniArch.Benchmarks.dll profile-query --scenario with-all --temperature cold --entity-count 100000 --duration 8 --warmup 1 --startup-delay 0`
- 读取热点函数：
  - `dotnet-trace report profiles\\sample.nettrace topN -n 20 --inclusive`
- 如果需要避开启动噪声：
  - 先运行 `scripts/profile-query.ps1 -StartupDelaySeconds 5`
  - 读 stdout 里的 `PID`
  - 再用 `dotnet-trace collect --profile dotnet-sampled-thread-time --duration 00:00:05 -p <PID> -o profiles\\attach.nettrace`

## 工作负载维度

`profile-query` 支持三种工作负载，用于定位不同层级的查询热点：

- `entity`：仅遍历 entity ID，用于定位 chunk 遍历和 outer loop 热点
- `component-row-wise`：逐行访问组件，适合定位 `GetComponent<T>()` 的热点
- `component-span`：批量 span 访问，适合定位 `GetComponentSpan<T>()` 和向量化机会

推荐命令：

- entity 热点：`powershell -ExecutionPolicy Bypass -File scripts/profile-query.ps1 -Workload entity -Scenario with-all -Temperature hot -DurationSeconds 5 -WarmupIterations 2`
- component-row-wise 热点：`powershell -ExecutionPolicy Bypass -File scripts/profile-query.ps1 -Workload component-row-wise -Scenario with-all -Temperature hot -DurationSeconds 5 -WarmupIterations 2`
- component-span 热点：`powershell -ExecutionPolicy Bypass -File scripts/profile-query.ps1 -Workload component-span -Scenario with-all -Temperature hot -DurationSeconds 5 -WarmupIterations 2`

## 结果解读规则

- 先分清 workload：
  - `hot` 主要回答 chunk/row steady-state 遍历谁最慢
  - `cold` 主要回答 refresh、matching、query build 谁最慢
  - `entity` vs `component-span` 的样本差异可以揭示 component accessor 的相对成本
- 再分清统计口径：
  - `inclusive` 适合看一整段调用链哪块最重
  - `exclusive` 适合看函数自身消耗
- 如果 top1 是包装函数：
  - 先检查它内部是不是简单循环
  - 再去看被它调用的方法是否可能被 JIT 内联
  - 必要时临时加 `COMPlus_JitNoInline=1` 做一次拆分采样，但不要把这个环境变量当常规口径

## 当前已验证的 query 采样结论

- `with-all + cold + 100000 entities` 下，主要热点落在执行遍历阶段，而不是 `Query.BuildMatchingArchetypes`
- `BuildMatchingArchetypes` 在采样中只占很小比例，说明 query 目前的主要瓶颈不在 archetype matching
- 这类结果后续要和 benchmark 结果一起看：sample profiling 负责找热点位置，benchmark 负责看修改前后是否真的更快

## 关联模块

- `kb-repo-overview.md`：仓库入口和脚本组织
- `kb-test-workflow.md`：benchmark / verify / profiling 在验证流程里的位置
- `scripts/profile-query.ps1`：当前可直接复用的采样入口
