---
title: BulletLockstep Demo — 多 host 弹幕游戏集成测试
module: samples.BulletLockstep.Demo
description: 用真实弹幕游戏场景端到端压测 miniArch 全部公共能力（placeholder lockstep / archetype 迁移 / hierarchy / chunked storage / 持久化 / 回滚）。沉淀 demo 中验证过的 lockstep 用法模式与踩过的坑。
updated: 2026-07-09
---

# BulletLockstep Demo — 多 host 弹幕游戏集成测试

## 这个模块是干什么的

- `samples/BulletLockstep.Demo/` 是 miniArch 的**端到端集成测试项目**，用真实弹幕游戏场景把库的全部公共能力走一遍
- 8 个 slice（2-9），每个 slice 端到端可跑（1000 帧 fuzz），所有 host `CanonicalChecksum` 字节级一致才算 PASS
- 这个模块**不负责**：
  - 渲染（永远不做）
  - 真实网络栈（in-process byte[] 交换）
  - 修改库本身（库零 IL 改动，确定性豁免门禁）

## 架构

### 两种 host 拓扑

| 拓扑 | slice | 库能力 |
|---|---|---|
| **P2P placeholder lockstep** | 2-7, 9 | `DeferredEntities=true`，每 host 独立 World + 独立 id allocator，placeholder delta 交换 |
| **Authority + Mirror** | 8 | `DeferredEntities=false`，1 个权威 host 用 `SubmitAndSnapshotAsync`，M 个 mirror replay real-id delta |

### 每帧时序（P2P 模式）

1. **Record**：每 host 在自己的 CommandStream 里 record 本帧意图（Create 走 placeholder）
2. **Snapshot + Clear**：每 host 输出 placeholder delta，relay-only 不本地 apply
3. **Replay**：所有 host 按 hostId 顺序串行 replay 所有 delta（placeholder → 本地 real id 映射）
4. **Deterministic systems**：所有 host 按相同顺序跑后处理系统（move / collision / status / boss AI 等）
5. **Checksum**：`CanonicalChecksum` 跨 host 字节比对

### 关键文件入口

- 第一次读：`samples/BulletLockstep.Demo/Program.cs`（slice 入口分发）、`LockstepSimulator.cs`（P2P 调度）、`AuthorityMirrorSimulator.cs`（权威拓扑）
- 改系统：`samples/BulletLockstep.Demo/Systems/`（每个系统一个文件）
- 看 slice 设计依据：`docs/internal/plans/2026-06-30-bullet-lockstep-coverage-design.md`

## 决策

### 决策 #1：所有变更都通过 delta，host 不本地 apply 自己的 record

P2P lockstep 模式下，host 的 Snapshot + Clear 后**不调 Submit**。本地 World 状态完全由 replay 决定。这样所有 host 的逻辑状态在每帧末严格等价。

### 决策 #2：跨帧引用 entity 走组件查询，不用 placeholder handle

kb `kb-deferred-create-design.md` 决策 #3 明确：placeholder 单帧有效。本 demo 中：
- 子弹的 `Target(int HostId)` 而非 `Target(Entity)` —— HomingBulletSteerSystem 通过 `PlayerTag.HostId` 查询定位目标玩家
- 子弹的 `FiredBy(int HostId)` 而非 entity ref —— 用于排序保证多 host 顺序一致

### 决策 #3：长生命周期 entity 的 Set/Destroy 走确定性后处理系统，不走 record

placeholder 只在 record 帧有效，跨帧 Set 长生命周期 entity 会带 host local id（每 host 不同）。所以：
- **玩家受伤**：`TickDamageSystem` 直接 `world.Access(player)` 写 Health/Shield
- **子弹销毁**：`BulletLifetimeSystem` 直接 `world.Destroy`
- **状态切换**：`StatusTimerSystem` 直接 `world.Remove<BurningTimer>`

所有系统按 `PlayerTag.HostId` 或 `WeakPointTag.Index` 等逻辑键排序，保证多 host 应用顺序一致。

### 决策 #4：位移用整型定点数（毫像素）

所有 Position/Velocity 用 `int`（单位 = 1/1000 pixel），完全规避 IEEE 754 跨硬件不确定性。

### 决策 #5：CollisionSystem 按逻辑键排序 hits

hits 按 `(bulletFiredBy, bulletSpawnFrame, playerHostId)` 排序后应用 damage + destroy。entity id 在 placeholder 模式下跨 host 不同，**不能**用作排序键。

## 认知模型

- 理解这个 demo 时，把它看成：**「确定性状态机 + 帧间 delta 交换」**
  - 状态机：所有 host 跑同样的 record + 同样的 replay + 同样的 systems → 同样的下一状态
  - delta 交换：placeholder 模式让 host 间不需要同步 id allocator
- 最重要的抽象：
  - **placeholder**：单帧有效的 entity 引用，让 Create 在多 host 间正确传播
  - **逻辑键**：跨 host 稳定的标识（PlayerTag.HostId、SpawnFrame+ FiredBy），用于排序和查询
- 常见误解：
  - 「CanonicalChecksum 忽略 entity id」—— **错**。它包含 `entity.Id` 和 `entity.Version`，所以多 host 必须用相同顺序 replay 让 id 一致
  - 「record 可以 Set 任何 entity」—— **错**。Set 长 lifecycle entity 会带 host local id，跨 host replay 时指向错对象

## 入口

- 第一次读或加 slice：先看 `Program.cs` 的 `RunSliceN`，再看 `LockstepSimulator.Tick`
- 改系统：每个 `Systems/*.cs` 一个职责，注释顶部说明用法
- 排查行为偏差：
  - 多 host checksum 不一致 → 系统顺序是否所有 host 一致？是否按逻辑键排序？
  - FrameDelta replay 报错 → placeholder 是否单帧使用？记录时是否带了 real id 跨 host？

## 坑点

### 历史/已修复的坑

- **`LockstepSimulator._deltaBuffer` 跨 tick 复用**（Slice 7 引入的 GC 优化）：保存 deltas 数组引用跨 tick 会 alias，所有 entry 最后指向同一帧的 delta。修复：`TickAndSnapshotDeltas` 拷贝数组。Slice 3 和 Slice 9 都踩过。
- **Phase A 系统顺序**：clone 上手动跑系统时，必须严格按 `LockstepSimulator.RunSystems` 的顺序，否则与原 world 分叉。Slice 9 Phase A 第一次写错系统顺序导致 clone diverge。

### 改这里时要特别小心

- **`LockstepSimulator.RunSystems`**：系统顺序变化会立即破坏多 host 一致性。任何重排要先跑 Slice 2-9 全部回归。
- **`ReplaceHostWorld`**：会替换 `_hosts[i]` 引用，依赖 host 引用的外部代码会失效。
- **`BulletLifetimeSystem` / `CollisionSystem` 等使用静态 buffer 的系统**：不能并发调用（simulator 是单线程模型）。

## 性能参考（4 host × 1000 帧 standard 模式）

下表仅列出已测量 slice；Slice 3（hierarchy 全链路 + CommandStream record）和 Slice 9（Phase A deterministic replay + clone）暂未单独测量。

| Slice | 配置 | 单帧 | GC | 备注 |
|---|---|---|---|---|---|
| 2 | 仅子弹 | 0.6 ms | 12/0/0 | 基线 |
| 4 | +玩家 archetype 迁移 | 0.9 ms | 13/0/0 | |
| 5 | +Boss +hierarchy +homing | 0.6 ms | 13/0/0 | |
| 6 | +collision 空间网格 | 0.5 ms | 408/1/0 | GC 升高因 grid 重建 |
| 7 scale | 600 子弹/帧 × 200 帧 | 7.6 ms | 327/239/145 | 触发 chunked storage |
| 8 | Authority + Mirror | 0.5 ms | 10/1/0 | SubmitAndSnapshotAsync 路径 |

GC budget 优化（推到 0/0/0）的下一步：FrameDelta[] 完全栈分配 + checksum byte[] 用 ArrayPool。
