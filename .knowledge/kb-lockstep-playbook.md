---
title: Lockstep Playbook
module: Meta
description: End-to-end guide for implementing lockstep multiplayer on miniArch — from recording commands to peer synchronization and divergence detection
updated: 2026-06-30 (新建)
---
# Lockstep Playbook

## 结论

这是帧同步的**导航 spine 页**——把分散在 5 个 kb 页中的信息串成一条线。每一步只给结论 + API 调用顺序，深度细节在各子页。

## 端到端流程

### 1. 初始化（所有 host）

```csharp
var world = new World();
var stream = new CommandStream(world);
// 多 host lockstep 模式：
stream.DeferredEntities = true;  // Create() 返回 placeholder Entity(-1, seq)
```

> 详见 `kb-deferred-create-design.md`

### 2. 每帧 tick（每个 host 独立执行）

```
┌─ Host A ──────────────────────────────────────────────┐
│ 2a. 录制命令                                            │
│   stream.Create(...); stream.Set(...); stream.Destroy(e);│
│                                                        │
│ 2b. 生成 delta                                         │
│   var delta = stream.Snapshot();  // placeholder delta │
│   stream.Clear();                 // 中继模式不清 world │
│                                                        │
│ 2c. 发送                                               │
│   socket.Send(delta.AsSpan());    // buffer 就是 wire  │
└────────────────────────────────────────────────────────┘

         ┌─ 网络 ─┐
         ▼        ▼

┌─ Host B ──────────────────────────────────────────────┐
│ 2d. 接收 N 个 host 的 delta                            │
│   var deltaA = FrameDelta.Deserialize(socketA.Recv());│
│   var deltaB = FrameDelta.Deserialize(socketB.Recv());│
│                                                        │
│ 2e. 按固定 host 顺序 replay                            │
│   world.Replay(deltaA);  // placeholder→local id 映射  │
│   world.Replay(deltaB);                                │
│                                                        │
│ 2f. 执行游戏逻辑（query + system）                      │
│   foreach (var chunk in world.Query(desc).GetChunks())│
│       ...                                              │
│                                                        │
│ 2g. 帧末 checksum 校验（可选，每隔 N 帧）                │
│   var hash = world.Checksum();  // 32 bytes SHA-256    │
│   // 广播 hash，与所有 peer 比较                         │
└────────────────────────────────────────────────────────┘
```

> **关键**：所有 host 必须从 frame 0 完整重放。不支持断点续传/增量同步（见 `kb-command-stream.md` "关键约束"）。

### 3. 多帧合并（可选，网络优化）

```csharp
// 把多帧 delta 合并成一个发送，省网络头开销
var merged = FrameDelta.Merge(delta1, delta2);
// Merge 是纯 Array.Copy 拼接，不做语义折叠
// 详见 kb-command-stream.md Merge 段
```

## 决定性行为

| 性质 | 状态 | 验证测试 |
|------|------|---------|
| 同一 delta 序列重放到 N 个 world → 状态一致 | ✅ | `FrameDeltaDeterminismTests` |
| Submit(源) == Replay(副本) | ✅（避开同帧 Remove+Add 同组件） | `Submit_*_converges_with_replay` |
| 跨帧 id 回收正确 | ✅（Merge 是纯拼接） | `CB_destroy_then_recycle_round_trip_preserves_id_allocation` |
| Placeholder→local id 映射 | ✅ | `kb-deferred-create-design.md` ReplayCore 段 |

## Divergence 检测

| 场景 | API | 详见 |
|------|-----|------|
| 同 delta 序列的 peer 间校验（标准 lockstep） | `world.Checksum()` | `kb-snapshot-persistence.md` Checksum 段 |
| 不同构造路径的 world 间校验 | `world.CanonicalChecksum()` | `kb-snapshot-persistence.md` Checksum 段 |

## 尚未解决的网络问题（OUT OF SCOPE）

| 问题 | 状态 | 备注 |
|------|------|------|
| 传输层（UDP/TCP/WebSocket） | ❌ | `socket.Send` 是伪代码，transport 由调用方实现 |
| 丢包/乱序重排 | ❌ | Merge 不排序，调用方需按帧号排序后 Merge |
| Late-join / 断线重连 | ❌ | 无 checkpoint / id remap 机制 |
| Divergent peer resync | ❌ | `EnsureReplayReservation` 抛异常而非尝试对齐 |
| Client prediction + rollback | ⚠️ 有基础 | `CaptureState/RestoreState` 支持 GGPO 式原地回滚，但无 netcode 层集成 |

## 相关页面

| 步骤 | 深度文档 |
|------|---------|
| 步骤 1 初始化 | `kb-deferred-create-design.md`（DeferredEntities flag 全貌） |
| 步骤 2a-2b 录制+生成 delta | `kb-command-stream.md`（CommandStream API + FrameDelta wire format） |
| 步骤 2c-2d 序列化 | `kb-command-stream.md`（AsSpan / Deserialize） |
| 步骤 2e replay | `kb-command-stream.md`（ReplayCore + EnsureReplayReservation） |
| 步骤 3 Merge | `kb-command-stream.md`（Merge + id 回收段） |
| 步骤 2g checksum | `kb-snapshot-persistence.md`（Checksum 双模式段） |
| Rollback 基础 | `kb-snapshot-persistence.md`（CaptureState/RestoreState） |
