---
title: Managed Entity Sidecar Evaluation
module: MiniArch（用户 API 分层）
description: Entity -> managed object sidecar API 价值验证结论；当前 No-Go，保留证据路径与未来重开条件
updated: 2026-07-10
---

# Managed Entity Sidecar Evaluation

## 这个模块是干什么的

- 这个模块负责：
  - 记录 `Entity -> managed object` sidecar API 的价值验证结论。
  - 保存 No-Go 原因，避免未来重复探索同一个 public API。
  - 指向 ValueLab harness、正确性矩阵和性能原始数据。
- 这个模块不负责：
  - 提供生产 API。
  - 让 managed object 进入 archetype/chunk storage。
  - 定义 lockstep、Snapshot、FrameDelta、Checksum、Replay 语义。

## 架构

- 核心组成：
  - `tools/perf/ManagedEntityMap.ValueLab/`：实验 harness。
  - `docs/plans/2026-07-10-managed-entity-sidecar-value-report.md`：完整决策报告。
  - `tools/perf/ManagedEntityMap.ValueLab/results/2026-07-10-summary.md`：M4 性能摘要。
- 数据流 / 控制流：
  - 用真实 `World` / `Entity` 创建 workload。
  - 横向比较 NaiveDict、CompetentDictionary、RawDenseUnsafe、CompetentDenseUser、ManagedEntityMapPrototype。
  - 正确性矩阵证明 naive/raw 的 bug；性能矩阵证明 prototype 没有明显优于 competent dense user。

## 决策

- **No-Go**：当前不新增 `ManagedEntityMap<T>` public API。
- 库版打败 dictionary，但这不是足够门槛；真正门槛是 competent dense user。
- 在 100k / 1M live entities 下，ProtoMap 的 Get/TryGet/Remove 与 CompetentDenseUser 基本同量级，Set 反而更慢。
- `_maxTouchedExclusive` 对 TrimExcess/Clear 有价值，但这是可文档化的实现技巧，不足以单独生成 API。
- serialization 不进 v1；它需要 user codec、entity remap 和资源系统边界，会把简单 sidecar 扩张成持久化框架。

## 认知模型

- 把 managed sidecar 看成：**host-local dense array binding table**，不是 ECS component，不是确定性状态。
- 最重要的抽象：
  - `world.IsAlive(entity)`：唯一 liveness oracle。
  - `_versions[id]`：sidecar slot 的绑定标记，不是 liveness oracle。
  - `Align()`：冷路径 zombie cleanup，用来释放 managed references。
- 常见误解：
  - “打败 Dictionary 就值得进库”——不对，必须打败 competent dense user。
  - “serialization 是库天然优势”——不对，serialization 必须由用户 codec 定义，且 remap/资源系统才是难点。

## 入口

- 第一次读或重新评估，先看：
  - `docs/plans/2026-07-10-managed-entity-sidecar-value-report.md`：完整 Go/No-Go 报告。
  - `tools/perf/ManagedEntityMap.ValueLab/README.md`：实现模型、正确性矩阵、serialization 结论。
  - `tools/perf/ManagedEntityMap.ValueLab/results/2026-07-10-summary.md`：关键性能数据。
- 复现实验：
  - `dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --correctness-only`
  - `dotnet run -c Release --project tools/perf/ManagedEntityMap.ValueLab/ManagedEntityMap.ValueLab.csproj -- --entity-count 100000 --repetitions 5`

## 坑点

- 不要只和 `Dictionary<Entity,T>` 比；那是 strawman。
- 不要把 sidecar 接进 `MiniArch.Core`；它是 host-local user-layer helper。
- 不要承诺 sidecar 参与 lockstep/checksum/replay。
- 不要把 null 当 value；null 应表示 absence 或直接禁止。
- 如果未来重开，只接受新证据：prototype 必须稳定明显优于 competent dense user，或出现当前报告没有覆盖的强 serialization/remap 需求。
