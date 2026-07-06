---
title: Change Tracking（变更追踪）
module: MiniArch.Core
description: Track<T> 游标驱动的原生变更追踪——ModifiedChunks 管值写入、Transitions 管成员进出，供渲染层/UI 反应式消费
updated: 2026-07-06
---

# Change Tracking（变更追踪）

## 这个模块是干什么的

- 这个模块负责：
  - 给用户一个 `world.Track<T>()` 返回的 `ChangeQuery<T>` 游标对象，枚举两类变更：`ModifiedChunks()`（被 Set\<T> 写过的 chunk，值变更）和 `Transitions()`（进入/离开 {T} 集合的实体，结构变更，有序 Entered/Exited）。
  - 游标全内化，用户不接触任何版本号。典型场景：渲染层对 HP 变化做反应（血条 spawn/update/despawn）。
- 这个模块不负责：
  - 追踪通过 `Get<T>` 返回 ref 后直接改字段的变更——C# ref 无法拦截，想追踪值变更必须走 `Set<T>`。
  - 跨帧存储变更历史（transition log 是 ephemeral 渲染层状态，非确定 sim 状态）。

## 架构

- 核心组成：
  - **值变更机制**：per-(Archetype, componentColumn) 的 `long` 版本号，在 Archetype 的 3 个写入 chokepoint（`SetComponentAtTyped`、`SetComponentAtFlat`、`WriteComponentRaw`）处 bump。全局 `World._writeEpoch`（long，无回绕）作单调时钟。
  - **结构变更机制**：append-only 的 `List<TransitionEntry>`，每条 `(Entity, OldArchetype?, NewArchetype?)`。5 个 hook 点：CreateInArchetype（Create/Clone 共享）、DestroySingle、ApplyTypedAdd（迁移分支）、RemoveBoxed、MoveEntityFromBytes（Replay Add 迁移）、PlaceEntityInArchetype（Submit/Replay Create）。消费者对 old/new signature 各 match 一次推出 Entered/Exited。
  - **ChangeQuery\<T>**：持 `_valueCursor: long` 和 `_transitionCursor: int`，每次调用 `ModifiedChunks()` 或 `Transitions()` 即推进游标。
  - **全局 gate**：`Archetype._anyTrackingActive` / `World._anyTrackingActive`。无人 Track 时所有 hook 是一条预测不命中的 bool 分支，零成本。
- 数据流 / 控制流：
  - 写入路径（Set / CommandStream Submit / Replay）→ Archetype 3 个写入方法 bump 列版本号 → ChangeQuery.ModifiedChunks() 比对游标 → 返回脏 chunk
  - 结构路径（Create/Destroy/Add/Remove）→ TransitionEntry 追加到 log → ChangeQuery.Transitions() 比对 old/new archetype signature → 返回 Entered/Exited

## 决策

1. **Get=read/Set=write 契约**：值追踪只挂 Set；Get 拿 ref 不追踪（C# ref 无法拦截）。想追踪就必走 Set。不引入 GetForWrite（拒绝新 API 面）。
2. **per-archetype 列版本非 per-segment**：HP 类组件 archetype 是 flat 模式（实体少 <2MB 阈值），per-archetype = per-chunk = 最细粒度。chunked 模式跨 segment 过报，YAGNI 先不做 segment 级。
3. **instrument 在 Archetype 3 个写入方法 = 所有写入路径的公共底**：World.Set、EntityAccessor.Set、CommandStream Submit（ComponentStore.ApplyToWorld）、Replay（ApplyRawSet/ApplyRawAdd）全部自动覆盖，无需每条路径单独 instrument。
4. **自动 per-type opt-in**：没人 Track\<T> 则零成本；Track 后只有该类型付 bump。
5. **transition log 记 old→new signature 转移（非 per-component Added/Removed）**：一条原语覆盖所有 query 形态（With/Without/多组件），消费者 match 两边推出 Entered/Exited。
6. **ModifiedChunks/Transitions 急切物化（非 yield return）**：保证"调用即推进游标"语义——消费者即使只 MoveNext 一次（如 Assert.NotEmpty / LINQ First）游标也正确推进。冷路径可接受 List 分配。
7. **long epoch 无回绕**：~29000 年 @ 1M writes/s。
8. **transition log 不序列化、不 checksum**：ephemeral 渲染层状态，非确定 sim 状态。Snapshot save/load 不含它；Load 建新 World，observer 状态天然重置。
9. **无 compaction（MVP）**：transition log 单调增长，长会话内存特征见坑点。YAGNI，soak 观测后再说。

## 认知模型

Track\<T> 是一个游标：值写入 bump 列版本、结构变更进 transition log，消费者各自推进游标。"变更" = 写脏（非"值不等"）。

常见误解：
- **误解**：`Get<T>` 后改字段会触发变更追踪。→ 实际不会，C# ref 无法拦截，必须走 `Set<T>`。
- **误解**：`ModifiedChunks` 通知"值变了"。→ 实际通知的是"被写脏了"，写相同值也算"变更"。

## 性能特征

实测，BenchmarkDotNet，Ryzen 7 5700X3D / .NET 8 Release：
- **Set\<T> of tracked type**：+~1.65 ns/op（~16%），来自一条预测中的 bool 分支 + 一个 long store。非 tracked 类型零开销。
- **tracking OFF（默认）**：HeroComing.Perf 门禁零回归（Movement 2068 / Attack 1250，均高于原 baseline 1917/1205）。
- **结构 ops（Create+Destroy）**：+~16% median，首次激活时 log 预热容量 256 避免冷启动 resize 尖峰。

## 入口

- `src/MiniArch/ChangeQuery.cs`：`ChangeQuery<T>` 公共 API，`ModifiedChunks()` / `Transitions()` 实现
- `src/MiniArch/Transition.cs`：`Transition` / `TransitionKind` 类型定义
- `World.Track<T>()`：入口方法

最小示例：
```csharp
var hp = world.Track<HP>();
foreach (var t in hp.Transitions())
    if (t.Kind == TransitionKind.Entered) SpawnHealthBar(t.Entity);
    else DestroyHealthBar(t.Entity);
foreach (var chunk in hp.ModifiedChunks())
{
    var span = chunk.GetSpan<HP>();
    // update health bars from current values
}
```

## 坑点

- `Get<T>` 返回 ref 后直接改字段**不追踪**（C# ref 无法拦截）。想追踪值变更必走 `Set<T>`。
- 批量 `chunk.GetSpan<T>()` 后改 span **不追踪**（同上）。HP 这类事件驱动单实体改不受影响；Position 那种批量写的组件本来也不需要 per-change 通知。
- chunked 模式（>2MB archetype）下版本是 per-archetype，跨 segment 过报——一个大 archetype 里任一实体改了，所有 segment 的 chunk 都算 Modified。HP 类小 archetype 是 flat 模式，不受影响。
- transition log 无 compaction：长会话（1h+）结构性操作单调累积内存。MVP 接受，soak 观测后若成问题再加 min-cursor shift 压缩。
- snapshot restore 后 observer 状态重置（Load 建新 World）。用户需重新 `Track<T>()` 并丢弃旧 ChangeQuery（其游标已失效）。已在 ChangeQuery XML doc 注明。
- transition log 条目持有 Archetype 引用——restore 后这些 archetype 可能失效，故 restore 必须清空 log（Load 建新 World 自然满足）。
