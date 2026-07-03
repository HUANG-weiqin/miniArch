---
title: Snapshot Persistence
module: MiniArch.Core Snapshot
description: Full-world snapshot save/load design for unmanaged components (WorldSnapshot.Save/Load, Clone, CaptureState/RestoreState), plus Checksum double mode
updated: 2026-07-03 (坑点修正 CaptureState/CommandStream 交互描述，实测确认 `Clear()` 复位 `_deferredSeq`)
---
# Snapshot Persistence

## 这个模块是干什么的

- 把 `World` 的当前活体数据导出成紧凑二进制 snapshot
- 从 snapshot 快速重建 archetype / chunk / entity metadata
- 保留 entity slot version 语义（`default(Entity)` 非法，活体 `Version > 0`）
- `Checksum()` / `CanonicalChecksum()` 双模式 peer 状态校验（见本章 Checksum 双模式段）

## 三套状态复制机制（职责正交，不可互相替代）

| 机制 | 产物 | 跨进程 | 用途 |
|---|---|---|---|
| `WorldSnapshot.Save/Load` | 版本化字节流 | ✅ | 持久化/网络/checksum |
| `World.Clone()` (`WorldClone.Clone`) | 全新独立 `World` | ❌ | 分支模拟/独立副本/长生命周期 checkpoint |
| `World.CaptureState/RestoreState` (`WorldStateSnapshot`) | opaque 句柄（绑定源 World） | ❌ | 高频原地回滚（GGPO 60fps，零分配稳态） |

> **概念唯一性**：这三者看似都在"复制世界状态"，但产物形状不同（字节流 / 新 World / 原地句柄），
> 各自服务于一个不可替代的场景。`Clone` 不应被推荐为高频回滚工具（每次产新 World），
> `CaptureState` 不能跨进程（含 raw internal 数组）。任何文档把它们混用即为漂移。

## 架构

- 核心组成：
  - `src/MiniArch/Core/WorldSnapshot.cs`：snapshot 二进制读写入口
  - `src/MiniArch/Core/WorldClone.cs`：内存直拷 World 克隆（零序列化）
  - `src/MiniArch/Core/World.cs`（+ partial 文件）：slot version、location、free id 重建桥接点
  - `src/MiniArch/Core/Archetype.cs` + `Archetype.Storage.cs`：按快照 chunk 精确导入实体批次

## WorldClone vs WorldSnapshot vs WorldStateSnapshot

- `WorldSnapshot.Save/Load`：走二进制序列化，支持跨进程传输
- `WorldClone.Clone`：纯内存直拷，跳过全部编解码，5-20× 快于 Snapshot 往返；产物是**新 World**
- `World.CaptureState/RestoreState`：原地 raw 数组拷贝，**池化句柄复用**，稳态零 GC；产物是**绑定源 World 的句柄**
- 前两者共享同一套 internal 重建 API（`world.Reset(slotCount)`, `SetSnapshotEntityVersion()`, `SetSnapshotLocation()`）；后者独立走 `WorldStateSnapshot` + `ArchetypeBackupEntry` + `HierarchyTable.CaptureState/RestoreState`
- v3 起 free list 直接序列化/反序列化（`WriteFreeList`/`ReadFreeList`），不再通过扫描 record 重建。Clone 用 `CopyFreeIdsFrom` 内存直拷。

### WorldStateSnapshot 生命周期（2026-06-30 重写）

**池化设计**：
- `World._stateSnapshotPool: Stack<WorldStateSnapshot>`（替换原单 spare slot）
- `CaptureState()`：池非空时 Pop（零分配），否则 `new WorldStateSnapshot()`（冷启动）；填充数据后 `_isRecycled = false`，返回给调用者
- `RestoreState(snap)`：校验 `snap._isRecycled == false`（否则 `InvalidOperationException`），恢复 world 状态，置 `_isRecycled = true`，`Clear()`，Push 回池
- 池容量自我稳定：连续 N 次 `CaptureState` 后乱序 restore，池就积累了 N 个 spare，下一轮 N 次 CaptureState 全部命中池 → 稳态零 GC

**IsRecycled 公共属性**：
- `WorldStateSnapshot.IsRecycled`：`true` 表示已 recycle 回池（不能再 RestoreState），`false` 表示调用者持有
- 用途：调试断言、防止 double-restore bug。之前重复 restore 同一 snapshot 会**静默污染 world 状态**（restore 到上次 restore 时的状态），现在 fail-fast

**支持 GGPO 多帧回滚窗口**：
```csharp
var ring = new WorldStateSnapshot[8];
for (int i = 0; i < 8; i++) { ring[i] = world.CaptureState(); Simulate(); }
// 检测到第 k 帧预测错误：
world.RestoreState(ring[k]);  // 其他 ring[i] 仍 live
world.RestoreState(ring[k+3]); // 可继续乱序 restore
```
之前单 spare 设计实质只支持 depth=1（>1 时第二次 CaptureState 强制分配），README 宣称的"GGPO-style 60fps"名实不符。本次修复让真实多帧窗口稳态零 GC。

**测试覆盖**（`tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`）：
- `Restored_snapshot_is_marked_recycled`
- `Restoring_same_snapshot_twice_throws`
- `Multi_frame_rollback_window_round_trips_out_of_order`
- `Multi_frame_window_is_zero_alloc_in_steady_state`（断言 CaptureState 复用 pooled 实例）

## Checksum 双模式

- **`world.Checksum()`**（`World.Checksum.cs:11` → `WorldSnapshot.cs:159`）：快，依赖同构造路径（archetype 顺序、swap-remove 历史一致）。输入：所有 slot version + 非空 archetype + hierarchy。不包含 free list。用于同 delta 序列驱动的 peer 间检测分叉。
- **`world.CanonicalChecksum()`**（`World.Checksum.cs:20` → `WorldSnapshot.cs:221`）：慢，逻辑等价的世界在不同构造路径下产同一 hash。输入：仅活实体（id+version+组件）+ hierarchy + free list。排序后的规范输出。用于不同路径（replay / snapshot-load / 手工构造）的世界间比较。

### Padding 字节安全
Archetype 存储使用 `GC.AllocateArray`（零初始化）分配，组件 struct padding 确定为 0，避免跨 peer 因未初始化内存产生 hash 差异。

### 决策
- **双模式而非单模式**：Checksum 快且足够 lockstep 场景；CanonicalChecksum 慢但容不同路径。
- **SHA-256 而非 XXHash64**：密码学安全 hash 避免对抗性 netcode 碰撞，且 hash 不在热路径。
- **Free list 纳入 canonical**：fast 假设 delta 驱动 → free list 一致；canonical 用于不同路径 → free list 可能分叉。
- **Padding 零初始化保证在 storage 层而非 checksum 层**：storage 层分配即确定，不做则 hash 不可靠。

### 坑点
- `Checksum` 依赖同构造路径：不同 archetype 创建顺序/swap-remove 历史 → 不同 hash。逻辑相等但路径不同的 world 会误报。
- `CanonicalChecksum` 仍依赖 `ComponentType.Value` 进程内一致性：跨进程比较时双方 `ComponentRegistry` 注册顺序必须一致。
- 两个 checksum 不适用于含托管引用组件的 world（构造时已 fail fast）。

### 入口
- `src/MiniArch/World.Checksum.cs`、`src/MiniArch/Core/WorldSnapshot.cs:159-271`
- `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`

### CRC32 尾部校验（v4 格式）

2026-07-01 新增：`WorldSnapshot.Save` 在写入所有 body 字节后追加 `Crc32.HashToUInt32` 校验值。
`Load` 在 v4 格式下先验证 CRC 再解析 body，损坏时抛出 `InvalidDataException("CRC mismatch at offset ...")`。
v3 格式仍可读且跳过 CRC 校验。

格式结构：`[magic:4][version=4:4][body:...][crc32:4]`。body 通过 `MemoryStream` 缓冲后再算 CRC，
避免直接写入输出流后无法追加尾部。对低频 Save 操作可接受的单次分配。

## 决策

- 不把运行时 `Chunk._data` 当作持久化格式——snapshot 按逻辑列写入
- 第一版只支持 `unmanaged` 组件；含托管引用组件在 chunk 构造时 fail fast
- 存档写组件类型的稳定字符串标识，不写运行时 `ComponentType.Value`
- 存档写 entity slot versions（不只活体 entity version）
- load 不能通过 `Add/Set/Remove` 回放世界——那会破坏 chunk 边界
- **Save 字节规范化（2026-06-21）**：`CollectPersistedArchetypes` 按 signature 字典序排序 archetype；`WriteArchetype` 内按 entity.Id 升序排 row index，column payload 同步按排序后 row 顺序写。这样 Save 字节不再依赖 archetype 字典迭代顺序和 archetype 内部 row 顺序（受 swap-remove 影响），可在"逻辑等价但内部路径不同"的两个 world 上产生相同字节 → SHA256/XXHash64 等可用作 client-server diverge 校验。Load 不变（按字节顺序恢复，自然规范化）。冷路径性能略降（每个 component 逐 row 拷贝而非批量写），但 Save 不在游戏循环热路径上。

## 认知模型

- "逻辑 chunk 导出格式"，而不是 "runtime chunk 内存镜像"

## 入口

- `src/MiniArch/Core/WorldSnapshot.cs`
- `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`
- `tests/MiniArch.Tests/Persistence/WorldCloneTests.cs`

## 坑点

- 只存活体 entity version 导致读档后复用旧 id 时版本回退
- 读档复用 `GetWritableChunk` 会挤压重排快照 chunk 边界
- `struct` 不一定是 `unmanaged`
- 跨版本类型名变化直接失配（当前不压缩、无校验和）
- internal 重建 API 分布在 World 的多个 partial 文件中，修改时需确认编译范围
- **`CaptureState` 后使用 `CommandStream.Record` 再 `RestoreState` 是安全的**：`Clear()` 会重置 `_deferredSeq = 0`（见 `CommandStream.cs:1436`），`RestoreState` 恢复 World 的 entity allocator 状态。实测验证过：capture → stream record+snapshot+replay → restore → 重复同一序列 → checksum 一致。坑点不在 `_deferredSeq`，而在以下场景：
  - **`RestoreState` 后如果继续录制而不 `Clear`**：`_deferredSeq` 不会自动回退（它不在 World 里）。但在正常 GGPO 流程中，`RestoreState` 后应该是重新录制修正后的输入，此时先 `Clear()` 再 `Record` 即可。
  - **`CaptureState` 前后 `CommandStream` 的 pending batch 状态**（`_frozen.PendingBatch`、`_frozen.PendingBatchCount` 等）不在 World 内，不被 capture/restore。如果 capture 前 stream 有未 snapshot 的 batch，restore 后这些 batch 会残留。**建议 capture 前先 `Snapshot()` + `Clear()` 确保 stream 干净。**
- **推荐模式**：
  - **纯 GGPO（无录制）**：`CaptureState` → 直接读写组件 → `RestoreState` → 重新跑系统。
  - **录制 + 回滚**：capture 前先 `stream.Snapshot()` + `stream.Clear()` 排空，然后 `CaptureState()` → 录制/Replay/跑系统 → `RestoreState()` → `stream.Clear()` → 重新录制修正输入 → `Snapshot` → `Replay` → 跑系统。
