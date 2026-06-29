---
title: Snapshot Persistence
module: MiniArch.Core Snapshot
description: Full-world snapshot save/load design for unmanaged components, plus World.Checksum()/CanonicalChecksum() for peer state verification
updated: 2026-06-29 (Checksum 加固：Archetype 存储零填充、CanonicalChecksum 含 free list)
---
# Snapshot Persistence

## 这个模块是干什么的

- 把 `World` 的当前活体数据导出成紧凑二进制 snapshot
- 从 snapshot 快速重建 archetype / chunk / entity metadata
- 保留 entity slot version 语义（`default(Entity)` 非法，活体 `Version > 0`）
- `World.Checksum()` 一行调用得到世界状态的 SHA-256 hash，用于帧同步 peer 间状态校验

## 架构

- 核心组成：
  - `src/MiniArch/Core/WorldSnapshot.cs`：snapshot 二进制读写入口
  - `src/MiniArch/Core/WorldClone.cs`：内存直拷 World 克隆（零序列化）
  - `src/MiniArch/Core/World.cs`（+ partial 文件）：slot version、location、free id 重建桥接点
  - `src/MiniArch/Core/Archetype.cs` + `Archetype.Storage.cs`：按快照 chunk 精确导入实体批次

## WorldClone vs WorldSnapshot

- `WorldSnapshot.Save/Load`：走二进制序列化，支持跨进程传输
- `WorldClone.Clone`：纯内存直拷，跳过全部编解码，5-20× 快于 Snapshot 往返
- 两者共享同一套 internal 重建 API（`world.Reset(slotCount)`, `SetSnapshotEntityVersion()`, `SetSnapshotLocation()`）
- v3 起 free list 直接序列化/反序列化（`WriteFreeList`/`ReadFreeList`），不再通过扫描 record 重建。Clone 用 `CopyFreeIdsFrom` 内存直拷。

## Checksum 双模式

- **`ComputeChecksum`（`world.Checksum()`）**：快速 lockstep 校验，包含 slot versions + archetype 实体（按 ID 排序）+ component 原始字节 + hierarchy 链接。依赖同路径 replay（archetype 创建顺序、swap-remove 历史一致），不可用于不同构造路径的 world 间比较。
- **`ComputeCanonicalChecksum`（`world.CanonicalChecksum()`）**：仅活实体（ID + Version + component）+ hierarchy + free list，全量按 ID 排序。同一逻辑状态的不同构造路径（replay / snapshot-load / 手工构造）产生相同 hash。
- **Padding 字节安全**：Archetype 存储使用 `GC.AllocateArray`（零初始化）分配，组件 struct 的 padding 字节确定为 0，避免跨 peer 因未初始化内存产生 hash 差异。
- **Free list 纳入 canonical checksum**：两 host 活实体相同但 free list 不同时（如一个 peer 未正确销毁实体），canonical checksum 可检测到差异。

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
