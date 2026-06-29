---
title: Deferred Create — Multi-Host Lockstep Design
module: MiniArch.Core.CommandStream
description: DeferredEntities flag 让 Create/Clone 在 placeholder（多 host lockstep）和 immediate（单机/权威服务器）之间切换。Snapshot 按 flag 输出 placeholder delta 或 real-id delta；SubmitAndSnapshotAsync 始终输出 real-id delta。
updated: 2026-06-29
---

# Deferred Create — Multi-Host Lockstep Design

## 这个模块是干什么的

- 解决**多个 host 各自独立 World** 在帧同步（lockstep）下 Create/Clone 时 id 撞车的问题
- 通过 `CommandStream.DeferredEntities` flag 控制 `Create()`/`Clone()` 的行为：
  - `false`（默认）：立即从 World 分配 real id（单机快路径）
  - `true`：返回 placeholder `Entity(-1, seq)`，不碰任何 World（多 host lockstep）
- 这个模块**不负责**：
  - ~~跨网络传输~~ → 传输层未实现（`socket.Send` 是伪代码），见 `kb-lockstep-playbook.md` "尚未解决的网络问题"
  - ~~序列化协议~~ → **已实现**，见 `kb-command-stream.md` "FrameDelta wire format" 段（`AsSpan()` / `Deserialize()` 开箱即用）
  - host 间的时序协调（lockstep 框架的事）→ **端到端指南**见 `kb-lockstep-playbook.md`
  - 跨帧的 placeholder 持久映射（明确放弃，见决策 #3）

## 要应对的游戏用户场景

### 场景 A：纯单机游戏

- `DeferredEntities = false`（默认）
- `stream.Create()` 立刻 reserve real id，`stream.Submit()` materialize 进 world
- Submit 是快路径：不走 delta 序列化/解码

### 场景 B：帧同步（lockstep）多人游戏

- N 个 host 对等，每个 host 拥有**独立的 World**，id 分配器互不同步
- 每个 host 设 `DeferredEntities = true`
- 每帧流程：
  1. 每个 host 在自己的 CommandStream 里 record 命令（Create 返回 placeholder）
  2. 每个 host 调 `stream.Snapshot()` 把 record 转成 **placeholder delta**（不碰 World id allocator）
  3. `stream.Clear()` 清空 record（relay-only：不本地 apply）
  4. 每个 host 收集齐本帧所有 N 个 delta（包括自己的）
  5. 每个 host **按固定 host 顺序**串行 replay 这 N 个 delta 到本地 World
  6. 所有 host 的本地 World 最终状态等价（但同一个 entity 在不同 host 上的 id 数值可能不同）
- **关键要求**：delta 里**不能携带任何 host local id**，否则不同 host 的 id 会撞车。

### 场景 C：权威服务器 + 镜像客户端

- `SubmitAndSnapshotAsync()` 始终输出 **real-id delta**（忽略 `DeferredEntities` flag）
- 服务器 apply 到本地 World 的同时，并行生成 real-id delta 转发给镜像客户端
- 客户端必须从 frame 0 完整重放以保持 id allocator 同步

## 已完成

### DeferredEntities flag 设计

| 组件 | `DeferredEntities = false`（默认） | `DeferredEntities = true` |
|---|---|---|
| `Create()` | `CreateImpl()` → 立即 reserve real id | `CreateDeferredImpl()` → placeholder `Entity(-1, seq)` |
| `Clone()` | `CloneImplImmediate()` → real id clone | deferred clone → placeholder |
| `Snapshot()` | `ResolveDeferredCreates()` → real-id delta | placeholder delta（throw on immediate entities） |
| `SubmitAndSnapshotAsync()` | real-id delta（始终，忽略 flag） | real-id delta（始终，忽略 flag） |
| `Submit()` | 两种模式都正常工作（resolve → materialize） | |

### 已删除的 API

- ~~`CreateImmediate()`~~ — 已删除。`DeferredEntities = false` 时 `Create()` 就是 immediate。
- ~~`CloneImmediate()`~~ — 已删除。`DeferredEntities = false` 时 `Clone()` 就是 immediate。
- ~~`CloneConcurrentImmediate()`~~ — 死代码，随 `CloneImmediate` 一起删除。
- ~~`ICommandRecorder.CreateImmediate()`~~ / ~~`CloneImmediate()`~~ — 接口同步移除。

### Producer 端实现（CommandStream.cs）

- `Snapshot()` 按 flag 分支：`!DeferredEntities` → `ResolveDeferredCreates()` + `BuildDelta()`；`DeferredEntities` → `ThrowIfSnapshotHasImmediateEntities()` + `BuildDelta()`
- `EmitPendingEntitiesToDelta` 通过 `entity.Id < 0` 检查同时处理两种 entity：placeholder 跳过 cancelled 的（不 emit 任何 op），committed 的 emit `Reserve + Create`；immediate entity 正常 emit `Reserve + Create` with real id
- `Submit()` 仍然调 `ResolveDeferredCreates()`——两种模式下 Submit 都正确

### Consumer 端实现（World.cs ReplayCore）

- `_replayPlaceholderMap`：`Entity[]` 按 placeholder seq 索引，存储 `seq → local real` 映射
- **每帧 `mapLen = 0` 重置**：防止上一帧的 stale mapping 泄漏到当前帧。`EnsurePlaceholderMap` 在首次 Reserve 时重新初始化所有 slot 为 `Entity(-1, -1)` sentinel
- `ResolveReplayEntity(wireEntity, map, mapLen)`：`Id >= 0` 直接用（real-id delta）；`Id < 0` 查 map + bounds check（placeholder delta）
- `Reserve` op with `Id < 0`：`ReserveDeferredEntityBatch()` 分配 local id + 写入 map
- 所有其他 op（Create/Add/Set/Link/Destroy/...）的 entity 参数都走 `ResolveReplayEntity`
- `PreScanForCapacity` 跳过 placeholder entity（`Id < 0`）的 `maxEntityId` 追踪——它们的 real id 在 main pass 才分配

### 历史 commit（已合入 main）

| Commit | 改动 |
|---|---|
| `882f76a` | 引入 deferred `Create()` / `Clone()`（返回 placeholder）。后被改为 flag 控制。 |
| `83de7d8` | `Id < 0` 短路优化 resolveMap 查找。 |
| `91d1d35` | resolveMap 从 `Dictionary<Entity, Entity>` 改为 `Entity[]` 按 seq 索引。 |
| `024de01` | `Clear` 自给自足（覆盖 Submit 异常路径的 id leak）。 |
| `d45514d` | `CommandStream.Clear` 改 public，支持中继模式。 |

## 决策

### 决策 #1：SubmitAndSnapshotAsync 输出哪种 delta？

**已定**：始终输出 real-id delta（忽略 `DeferredEntities`）。用于权威服务器 + 镜像客户端场景。如果多 host lockstep 需要 placeholder delta，用 `Snapshot()` + `DeferredEntities = true`。

### 决策 #2：immediate entity 和 Snapshot(placeholder) 共存

**已定**：严格禁止。`DeferredEntities = true` 时 `Snapshot()` 检测到 `Id >= 0` 的 batch entity 即抛 `InvalidOperationException`。

### 决策 #3：跨帧引用 placeholder

**已定**：明确放弃。placeholder 单帧有效。业务需要跨帧引用 entity 时：
- 单机：用 `DeferredEntities = false`，`Create()` 返回 real id
- 多 host：业务自己维护跨 host 稳定标识（gameplay 层 entity guid），replay 后通过查询组件定位本地 real id

### 决策 #4：replay 端 placeholder→local real 映射的数据结构

**已定**：`Entity[]` 按 seq 索引。每帧 `mapLen = 0` 重置，`EnsurePlaceholderMap` lazy 扩容 + 初始化 sentinel。

## 坑点

- **placeholder 失效契约**：Snapshot/Submit 后外部 placeholder 引用变成 stale。合法使用下不会触发（placeholder 仅在 record → Snapshot/Submit 之间有效）。
- **cancelled deferred vs cancelled immediate**：cancelled deferred emit 什么都不 emit（不占 id）；cancelled immediate emit Reserve+Release（占 id 槽位保持 host/replica 同步）。
- **跨帧引用**：明确不支持。见决策 #3。
- **`_replayPlaceholderMap` 跨帧清零**：`mapLen` 在 `ReplayCore` 入口重置为 0。如果不清零，上一帧的 stale mapping 会被 malformed delta 静默误用。
