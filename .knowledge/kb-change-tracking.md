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
  - **结构变更机制**：每个 `ChangeQuery<T>` 持有自己的 `List<Transition>`（只存匹配的 Entered/Exited，预过滤）。World 在每个结构操作时通过 `IChangeQuery.OnTransition` 将变更 dispatch 到所有注册的 query（`List<WeakReference<IChangeQuery>>`），每个 query 独立 filter 后存入自己的 log。Entered/Exited 在 dispatch 时确定（不在消费时）。5 个 hook 点不变（CreateInArchetype、DestroySingle、ApplyTypedAdd、RemoveBoxed、MoveEntityFromBytes、PlaceEntityInArchetype）。无人 Track 时 `_anyTrackingActive` gate 零成本；有人 Track 时每次结构操作做 N 次 `Matches()`（N = 活跃 query 数，典型 1-3）。
  - **ChangeQuery\<T>**：持 `_valueCursor: long`（ModifiedChunks 用，不变）和 `List<Transition> _transitions`（自有 log）。`Transitions()` 调用时 `ToArray()` + `Clear()`（auto-clear，复用内部数组零 GC）。
  - **全局 gate**：`Archetype._anyTrackingActive` / `World._anyTrackingActive`。无人 Track 时所有 hook 是一条预测不命中的 bool 分支，零成本。
- 数据流 / 控制流：
  - 写入路径（Set / CommandStream Submit / Replay）→ Archetype 3 个写入方法 bump 列版本号 → ChangeQuery.ModifiedChunks() 比对游标 → 返回脏 chunk
  - 结构路径（Create/Destroy/Add/Remove）→ World 遍历 `_changeQueries`（弱引用列表），调用存活 query 的 `IChangeQuery.OnTransition` → 每个 query 独立 `Matches()` 判断 → 匹配的 `Transition` 追加到 query 自有的 `_transitions` → 用户调用 `Transitions()` 时 drain（auto-clear）

## 决策

1. **Get=read/Set=write 契约**：值追踪只挂 Set；Get 拿 ref 不追踪（C# ref 无法拦截）。想追踪就必走 Set。不引入 GetForWrite（拒绝新 API 面）。
2. **per-archetype 列版本非 per-segment**：HP 类组件 archetype 是 flat 模式（实体少 <2MB 阈值），per-archetype = per-chunk = 最细粒度。chunked 模式跨 segment 过报，YAGNI 先不做 segment 级。
3. **instrument 在 Archetype 3 个写入方法 = 所有写入路径的公共底**：World.Set、EntityAccessor.Set、CommandStream Submit（ComponentStore.ApplyToWorld）、Replay（ApplyRawSet/ApplyRawAdd）全部自动覆盖，无需每条路径单独 instrument。
4. **自动 per-type opt-in**：没人 Track\<T> 则零成本；Track 后只有该类型付 bump。
5. **transition log 记 old→new signature 转移（非 per-component Added/Removed），Entered/Exited 在 dispatch 时确定**：一条原语覆盖所有 query 形态（With/Without/多组件）。各 query 在 `OnTransition` 时独立 `Matches()` 判断，只在匹配时写入自己的 `Transition` log。每条记录 12B（Entity+Kind），预过滤使 log 更紧凑，消费时无需再 match。
6. **ModifiedChunks/Transitions 急切物化（非 yield return）**：保证"调用即推进游标"语义——消费者即使只 MoveNext 一次（如 Assert.NotEmpty / LINQ First）游标也正确推进。冷路径可接受 List 分配。
7. **long epoch 无回绕**：~29000 年 @ 1M writes/s。
8. **transition log 不序列化、不 checksum**：ephemeral 渲染层状态，非确定 sim 状态。Snapshot save/load 不含它；Load 建新 World，observer 状态天然重置。
9. **Transitions() auto-clear（无需外部清理）**：drain 时 `ToArray()` + `List.Clear()` 复用内部数组，零 GC。不再需要 `World.ClearTransitionLog()`。不调用 `Transitions()` 的 query log 会增长——丢弃的 query 由 WeakReference 自动清理。
10. **fluent filter 复用 QueryDescription/QueryCache.Matches（而非独立 filter 机制）**：With/Without/WithAny 直接操作 `ChangeQuery._filter: QueryDescription`（默认 `new QueryDescription().With<T>()`），Transitions 用 `QueryCache.Matches(archetype)` 判断进出。决策理由见 `kb-design-rationale.md` §2.11——概念唯一：QueryDescription 已是唯一筛选抽象，QueryCache.Matches 已是唯一 archetype 匹配原语，ChangeQuery 复用两者，零新概念。
11. **transition log 挂在 ChangeQuery 而非 World**：每个 query 自管 `List<Transition>`，drain 即 clear，无需 world 级协调（ClearTransitionLog 已删）。API 更干净：用户不需要记得调 Clear。
12. **WeakReference 自动 prune**：World 通过 `List<WeakReference<IChangeQuery>>` 持有 query 引用。query 被 GC 后下次 dispatch 时自动清理 dead ref（swap-remove），不需要 IDisposable。

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
foreach (var t in hp.Transitions())        // 进/出（spawn/despawn 血条）
    if (t.Kind == TransitionKind.Entered) SpawnHealthBar(t.Entity);
    else DestroyHealthBar(t.Entity);
foreach (var chunk in hp.ModifiedChunks()) // 值写入（更新血量）
    { var span = chunk.GetSpan<HP>(); ... }
// 无需 ClearTransitionLog — Transitions() 已自动清零
```

### Fluent Filter API

`ChangeQuery<T>` 现在支持链式筛选方法：`.With<TU>()`、`.Without<TU>()`、`.WithAny<TU>()`。它们在 `Track<T>()` 的隐式 `With<T>` 基础上附加签名约束。

**语义要点**：

- T 始终是值追踪列——`ModifiedChunks()` 只检查 T 列的版本号。With/Without/WithAny **不**额外追踪其他列，仅用于限定 Transitions 的成员集和 ModifiedChunks 扫描的 archetype 范围。
- 默认（不调用任何 Fluent 方法）行为 = 仅 `With<T>`，向后兼容无 filter 版本。
- `Transitions()` 的 Entered/Exited 语义从"实体的 archetype 是否含 T"升级为"实体 archetype **是否匹配整个筛选签名集**"。
- `ModifiedChunks()` 仅扫描满足筛选条件的 archetype——不含 TU 或含 Dead 的 archetype 直接跳过，`chunk.GetSpan<T>` 即可安全访问关联数据。

**关键示例**——血条场景：

```csharp
var hp = world.Track<HP>().Without<Dead>();
// Add<Dead> → Exited（实体离开 {HP, !Dead} 集合，血条 despawn）
// Remove<Dead> → Entered（实体重新进入，血条 respawn）
```

**约束**——先配置后消费：

必须在第一次 `ModifiedChunks()` 或 `Transitions()` 枚举之前调用 `.With()`/`.Without()`/`.WithAny()`。枚举开始后仍可继续调用，但仅对下次枚举生效。已在 XML doc 注明。

## 坑点

- `Get<T>` 返回 ref 后直接改字段**不追踪**（C# ref 无法拦截）。想追踪值变更必走 `Set<T>`。
- 批量 `chunk.GetSpan<T>()` 后改 span **不追踪**（同上）。HP 这类事件驱动单实体改不受影响；Position 那种批量写的组件本来也不需要 per-change 通知。
- chunked 模式（>2MB archetype）下版本是 per-archetype，跨 segment 过报——一个大 archetype 里任一实体改了，所有 segment 的 chunk 都算 Modified。HP 类小 archetype 是 flat 模式，不受影响。
- transition log 在 drain 时自动清零（`Transitions()` 内部 `ToArray()` + `List.Clear()`），复用内部数组零 GC。不调用 `Transitions()` 的 query log 会增长——丢弃的 query 由 WeakReference 自动 prune。
- snapshot restore 后 observer 状态重置（Load 建新 World，`_changeQueries` 为空）。旧 ChangeQuery 持旧 World 引用，WeakReference 在旧 World 上 prune。用户需重新 `Track<T>()`。
