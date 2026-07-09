---
title: 多 host Lockstep 浸泡测试 — 网络同步收敛证明
module: LockstepSoak
description: N host placeholder lockstep 长周期随机操作收敛验证器——所有 host 全走 Replay 路径，每帧跨 host CanonicalChecksum 字节级比对。补齐单 host soak（Submit vs Replay 双路径）无法覆盖的 DeferredEntities=true 多 host 交错收敛性。
updated: 2026-07-09
---
# 多 host Lockstep 浸泡测试 — 网络同步收敛证明

> `tools/lockstep-soak/MiniArch.LockstepSoak/` — 真正的多 host P2P placeholder lockstep 正确性证明工具。与单 host soak（`kb-soak-test.md`）是**姊妹工具，证明不同的性质**。

## 这个模块是干什么的

- 核心职责：验证 **N 个独立 host 全部走 Replay 路径**时，每帧跨 host `CanonicalChecksum` 字节级收敛。
  - 每 host 独立 World + 独立 id allocator + `CommandStream{DeferredEntities=true}`
  - 每帧：每 host record 随机 op → Snapshot 出 placeholder delta → Clear（relay 模式不 Submit）→ 所有 host 按固定 hostId 顺序串行 Replay 全部 N 个 delta → 跨 host checksum 比对
  - 复用单 host soak 的 6 阶段相位（WarmUp/StableMutate/MigrationStorm/HierarchyStress/AllocatorChurn/Cooldown）+ 8 种 op 词汇
- 这个测试**负责**证明：
  - `DeferredEntities=true` 整条分支（CreateDeferredImpl / EmitPendingEntitiesToDelta placeholder 分支 / EnsureReplayReservation / `_replayPlaceholderMap` 生命周期）的长期稳定性
  - N 个 delta 交错 Replay 时 **id allocator / free-list 跨 host 收敛**（B5/B6 类 cancel+reorder 分歧在多 host 下的表现）
  - placeholder 同帧引用（EntityFieldResolver 自动解析）在多 host 下正确
- 这个测试**不负责**：
  - Submit vs Replay 双路径对比（那是单 host soak 的事，`kb-soak-test.md`）
  - 多 host 对**同一 real entity** 的并发结构变更（这是非法 lockstep 用法——见决策 #3，正确做法走确定性后处理系统，BulletLockstep demo 已覆盖）
  - 性能测量（那是 HeroComing.Perf 的事）

## 与单 host soak 的区别（关键）

| 维度 | 单 host soak (`kb-soak-test.md`) | 多 host lockstep soak（本页） |
|------|------|------|
| **拓扑** | 1 source + 1 shadow | N host（默认 4，可配 2/8） |
| **flag** | `DeferredEntities=false` | `DeferredEntities=true` |
| **源路径** | source 走 `Submit()` | 所有 host 走 `Snapshot()` + `Clear()`（relay，不 Submit） |
| **副本路径** | shadow 走 `Replay()` | 所有 host（含产生者自己）走 `Replay()` |
| **证明的性质** | 同一 host 两条代码路径收敛 | N host 全 Replay 路径收敛 |
| **delta 交错** | 单 delta | N delta 每帧按 hostId 顺序串行 replay |
| **历史发现** | 6 个库 bug（B1-B6，全是 Submit vs Replay 分歧） | 暂无（v1 证明矩阵 PASS） |

**核心洞察**：单 host soak 证明"Submit 路径 == Replay 路径"。多 host lockstep soak 证明"所有 host 都走 Replay 路径时收敛"。前者抓 Submit/Replay 分歧（B1-B6），后者抓多 host id allocator/free-list 交错分歧。两者**互补，不可替代**。

## 架构

### 每帧时序（P2P placeholder lockstep）

```
for frame in 1..TotalFrames:
    phase = GetPhase(frame)
    # Phase 1: 每 host 独立 record（per-host RNG，种子 = baseSeed*1000003 + hostId）
    for h in 0..N-1:
        RebuildAlive(host[h])          # query With<OwnerTag> 过滤本 host 拥有的 real entity
        host[h].CreatedThisFrame.Clear()
        for op in 1..random(1..MaxOpsPerFrame):
            RandomOp(host[h])          # 目标池 = realOwned + createdThisFrame
    # Phase 2: Snapshot + Clear（relay，不 Submit）
    deltas[h] = host[h].Stream.Snapshot(); host[h].Stream.Clear()
    # Phase 3: 所有 host replay 全部 N delta（固定 hostId 顺序，无吞异常）
    for h in 0..N-1:
        for d in 0..N-1:
            host[h].Stream.Replay(deltas[d])
    # Phase 4: 校验
    Verify(frame, phase)               # EntityCount + CanonicalChecksum 每帧跨 host 比对
```

### 校验层级

| 层 | 频率 | 内容 |
|----|------|------|
| EntityCount 跨 host | 每帧 | host[0].EntityCount vs 其余 |
| CanonicalChecksum 跨 host | 每帧（核心证明） | host[0] 字节 vs 其余 |
| WorldValidator | 每 ValidateInterval（默认 100） | 每 host 结构不变量 |
| RefModel spot-check | 每 100 帧 | host[0] CompA/CompB 值（独立 oracle） |
| Checkpoint | 每 CheckpointInterval（默认 10000） | 跨 host WorldDiff + 每 host Snapshot 往返 + Clone 自洽 |

## 决策

### 决策 #1：OwnerTag 稳定所有权（不用 modulo-by-id）

每 host Create 时立即 `Add(e, new OwnerTag(host.HostId))` 烙印创建者。host 的 alive 列表 = `query With<OwnerTag>` 过滤 `HostId == 本 host`。

**为什么不用 modulo-by-id**（`entity.Id % HostCount == HostId`）：
- id 回收后所有权**漂移**——一个 entity 这帧属于 host 0，被 destroy 后 id 回收，新 entity 复用该 id 可能 modulo 到 host 1，所有权不稳定。
- OwnerTag 是创建时烙印的稳定标记，不随 id 回收改变。

**为什么需要所有权分区**：让每 host 只操作自己创建的 entity → 跨 host 零冲突 → 所有 delta 天然合法 → **无需吞任何 replay 异常**。

### 决策 #2：不测多 host 对同一 real entity 的并发结构变更

这是**非法 lockstep 用法**。正确做法：多 host 对共享 real entity 的 Set/Add/Remove/Destroy 走**确定性后处理系统**（所有 host 跑相同系统，自然一致），不走 record delta。见 `kb-bullet-lockstep-demo.md` 决策 #3。BulletLockstep demo（`samples/BulletLockstep.Demo/`）已覆盖这条路径。

本工具聚焦 record 阶段的合法用法：每 host 只 Create + 操作自己拥有的 entity。

### 决策 #3：replay 异常永不吞（任何异常 = FAIL）

Phase 3 的 replay 循环**无 try/catch 吞异常**。外层只有一个 `try/catch(Exception)` 用于打印 `FailWithException` 诊断后 `return false`，**不继续跑**。

理由：一个 host 产生的合法 delta 在任何 host replay 都不应抛异常。如果抛，要么是库 bug，要么是 op 生成产生了非法序列——两者都必须暴露。

### 决策 #4：op 目标池 = realOwned + createdThisFrame

每 host 帧头 `RebuildAlive` 填充 realOwned（前序帧残留的本 host real entity）。`OpCreate` 把新 placeholder 追加到 `createdThisFrame`。所有 op（Destroy/Add/Set/Remove/Clone/AddChild/RemoveChild）从两者并集取。

- 对 realOwned 的 real entity：Destroy/Add/Set/Remove/Clone/AddChild 全部合法（本 host 独占）。
- 对 createdThisFrame 的 placeholder：Add/Set/AddChild 合法（同帧引用，EntityFieldResolver 自动解析）；**Destroy 一个 placeholder = 同帧 cancel**（覆盖 B5/B6 cancel+reorder 路径）。

placeholder（Id<0）跳过 `world.IsAlive` 检查（它还没 materialize）；real entity（Id>=0）才查 IsAlive。用 `Entity.IsPlaceholder` 区分。

## 认知模型

- 理解这个工具时，把它看成：**「N 个确定性状态机 + 帧间 placeholder delta 交换」**
  - 状态机：所有 host 跑相同的 record（独立 op）+ 相同的 replay（相同 delta 序列）→ 相同的下一状态
  - delta 交换：placeholder 让 host 间不需同步 id allocator——allocator 由 replay 序列联合驱动
- 最重要的抽象：
  - **OwnerTag**：稳定所有权，让跨 host 冲突不可能发生
  - **placeholder cancel**：同帧 Create+Destroy 一个 placeholder = cancel，这正是 B5/B6 free-list 分歧的触发域
- 常见误解：
  - 「改 host 的 RNG 种子能制造 desync」——**错**。host[1] 只有一个实例，改它的 RNG 只改变它产生的 delta 内容，但所有 host 仍 replay 同一个 delta → 仍收敛。要制造 desync，必须让某个 host **replay 不同的 delta 序列**（如跳过一个 delta）。
  - 「多 host 该测两个 host 同帧 destroy 同一 entity」——**错**。real entity 二次 destroy 是库的安全 no-op，但 Add/Set 到已销毁 entity 会抛异常。这种并发结构变更在正确 lockstep 用法里走确定性系统，不走 record。

## 入口

- 第一次读：`tools/lockstep-soak/MiniArch.LockstepSoak/LockstepSoakRunner.cs`
  - `RunCore()`：主循环（Phase 1-4）
  - `RebuildAlive()`：OwnerTag 过滤
  - `PickTarget()`：目标池合并
  - `Verify()`：跨 host 校验
  - `Fail()`/`DumpDesync()`/`FailWithException()`：诊断输出
- 改 op 词汇：`OpCreate`/`OpDestroy`/.../`OpRemoveChild`
- 跑：见下文用法

## 坑点

### v1 审查教训：三种"削弱检测能力"的反模式（已修正）

v1 实现为了让烟跑 PASS 犯了三个错，**让"PASS"变成"测得不够狠"**。修正记录在此防止复发：

1. **replay 异常吞**（`catch (InvalidOperationException) { /* expected */ }`）——工具是检测器，吞掉 desync = 自废武功。修正：任何异常 = FAIL。
2. **modulo-by-id ownership**（`entity.Id % HostCount`）——基于"防两 host 同帧 destroy 同一 entity"的错误前提（库对 real entity 二次 destroy 本就是 no-op），代价是削弱跨 host 交错（B5/B6 的温床），且 id 回收后所有权漂移。修正：OwnerTag 稳定所有权。
3. **用modulo/吞异常回避冲突**——根因是测了非法用法（多 host record 对同一 real entity 并发操作）。修正：用 OwnerTag 让冲突不可能发生，而非回避。

### 阴性对照（证明检测通道通畅）

为证明工具不是"恒 PASS"，做过阴性对照：临时让 host[1] 跳过 replay `deltas[2]`（模拟一个 host 漏帧）。结果**第 1 帧**即被 EntityCount + CanonicalChecksum 抓到，诊断完整（occupancy/freeList digest + op log + repro 命令）。这证明：
- 检测在第 1 帧就触发，无延迟检测盲区
- 任何 host 状态分叉当帧即被抓

### v1 已知局限

- **RefModel 仅 host[0]**：v1 简化，只对 host[0] 做 CompA/CompB 值 spot-check。理由：checksum 已证所有 host 收敛，RefModel 仅补检测"全 host 同步算错"的共享路径 bug。可扩展到全 host。
- **real-entity 跨 host 操作未覆盖**：见决策 #2，这是设计选择不是缺陷（合法用法走确定性系统）。
- **SubmitAndSnapshotAsync / Authority+Mirror 路径未覆盖**：本工具只测 P2P placeholder 模式。Authority 拓扑的 fuzz 是后续工作（BulletLockstep Slice 8 有手工覆盖）。
- **EntityFieldResolver 组件字段自动解析**：本工具的测试组件（CompA/B/C/D/OwnerTag）不含 `Entity` 字段，故该路径不在 fuzz 内。已由定点测试 `NetworkSyncTests.T8_Entity_field_placeholder_resolves_across_hosts` 覆盖（多 host placeholder delta 交错下，组件 `Entity` 字段解析为 real id 且跨 host 一致）。如需 fuzz 覆盖，给测试组件加一个 `Entity` 字段即可。

## 用法

```bash
# 单跑（默认 4 host）
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak

# 多 seed sweep
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --sweep 8 --frames 2000 --hosts 4

# boundary 高密度（B5/B6 cancel 域，必须 floor < cap）
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --seed 111 --hosts 4 --entity-cap 100 --entity-floor 20 --ops-per-frame 50 --frames 5000

# 确定性验证（同 config 跑两次比 host[0] checksum）
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --determinism --frames 2000 --hosts 3

# 不同 host 数
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --frames 1500 --hosts 8
```

退出码：0 = PASS，1 = FAIL（含诊断）。

## 证明矩阵（2026-07-06）

### 结论：全维度 PASS — 41 seed × 1,090,000 帧零 desync

| 组 | 配置 | seeds | 总帧 | 结果 | GC(gen0/1/2) |
|----|------|-------|------|------|------|
| Diversity A | `--sweep 8 --frames 10000 --hosts 4`（seed 1234567+） | 8 | 80K | ✅ | 1-2/0-2/0-1 |
| Diversity B | `--sweep 8 --frames 10000 --hosts 4 --seed 1000` | 8 | 80K | ✅ | 1-2/0-2/0-1 |
| **Boundary 高密度（B5/B6 域）** | `--sweep 16 --frames 50000 --hosts 4 --seed 111 --entity-cap 100 --entity-floor 20 --ops-per-frame 50` | 16 | **800K** | ✅ | 20-21/3-4/1 |
| 多 host 2 | `--sweep 4 --frames 10000 --hosts 2` | 4 | 40K | ✅ | 0-1/0-1/0 |
| 多 host 8 | `--sweep 4 --frames 10000 --hosts 8` | 4 | 40K | ✅ | 2-4/0-3/0-2 |
| Long-run | `--seed 42 --frames 50000 --hosts 4 --validate-interval 10`（全 6 phase） | 1 | 50K | ✅ | 5/1/0 |
| 阴性对照 | host[1] 跳过 delta[2] | — | 1 | ✅ 第 1 帧抓到 desync |
| Determinism | 同 config 跑两次 | — | — | ✅ 字节级一致 |

**总计：41 seed × 1.09M 帧，零 desync。** Boundary 域 16 seed × 800K 帧高强度覆盖 cancel+reorder 路径。8 host 拓扑（最重）也稳定。

### 内存与网络特征
- 核心路径稳态零 GC（gen2 几乎为 0）
- 网络紧凑：~54-61 B/frame（Placeholder delta 序列化极紧凑），boundary 高密度时 ~200 B/frame
- 8 host 拓扑 alloc 线性增长但 gen2 受控

> 单 host soak 是 224 seed × 5M 帧。多 host 计算量是单 host 的 N 倍（N host × 每帧 checksum），当前 41 seed 是务实深度。发布前可继续扩 seed（尤其是 boundary 域），但 Boundary 16 seed × 800K 帧已覆盖 B5/B6 触发条件。

## 约束

- 必须 `-c Release`（与 NuGet 包一致）
- `entity-floor` 必须 < `entity-cap`，否则 cap/floor 调节矛盾
- 不加进 `miniArch.sln`（独立 `dotnet run` 工具，与 soak 一致）
- `InternalsVisibleTo("MiniArch.LockstepSoak")` 已在 `src/MiniArch/Properties/AssemblyInfo.cs`（访问 `ActiveHierarchyForTesting`）

## 发布门禁（发布负责人决定）

任何改动下列路径的变更，**发布前必须跑本工具的矩阵**（正确性门禁，区别于 `HeroComing.Perf` 的性能门禁）：
- `CommandStream` / `CommandStreamCore`（record / Snapshot / Submit / Clear）
- `World.Replay` / `ReplayCore` / `EnsureReplayReservation`
- `DeferredEntities` flag 相关分支（`CreateDeferredImpl` / `EmitPendingEntitiesToDelta` / `_replayPlaceholderMap`）
- `EntityFieldResolver`（placeholder 字段自动解析）

最小门禁跑：
```bash
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --sweep 8 --frames 10000 --hosts 4
dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak -- --sweep 8 --frames 50000 --hosts 4 --seed 111 --entity-cap 100 --entity-floor 20 --ops-per-frame 50
```
任一 FAIL → 回退改动。这与 AGENTS.md §5 性能门禁并行，两者都过才放行。

> 单 host soak（`kb-soak-test.md`）覆盖 Submit vs Replay 双路径；本工具覆盖多 host 全 Replay 收敛。两者互补，改 CommandStream/Replay 时**两个都要跑**。

## 相关页面

- 单 host soak（Submit vs Replay 双路径）→ `kb-soak-test.md`
- Lockstep 端到端指南 → `kb-lockstep-playbook.md`
- DeferredEntities 设计 → `kb-deferred-create-design.md`
- BulletLockstep demo（确定性系统层覆盖）→ `kb-bullet-lockstep-demo.md`
- 已修复的库 bug（B1-B6）→ `kb-code-review-findings.md`
