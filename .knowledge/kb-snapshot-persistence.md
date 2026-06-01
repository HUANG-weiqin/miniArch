---
title: Snapshot Persistence
module: MiniArch.Core Snapshot
description: Full-world snapshot save/load design for unmanaged components, plus WorldClone for zero-serialization in-memory copy
updated: 2026-06-01
---
# Snapshot Persistence

## 这个模块是干什么的

- 把 `World` 的当前活体数据导出成紧凑二进制 snapshot
- 从 snapshot 快速重建 archetype / chunk / entity metadata
- 保留 entity slot version 语义（`default(Entity)` 非法，活体 `Version > 0`）

## 架构

- 核心组成：
  - `src/MiniArch/Core/WorldSnapshot.cs`：snapshot 二进制读写入口
  - `src/MiniArch/Core/WorldClone.cs`：内存直拷 World 克隆（零序列化）
  - `src/MiniArch/Core/World.cs`：slot version、location、free id 重建桥接点
  - `src/MiniArch/Core/Archetype.cs` + `Chunk.cs`：按快照 chunk 精确导入实体批次

## WorldClone vs WorldSnapshot

- `WorldSnapshot.Save/Load`：走二进制序列化，支持跨进程传输
- `WorldClone.Clone`：纯内存直拷，跳过全部编解码，5-20× 快于 Snapshot 往返
- 两者共享同一套 internal 重建 API（ResetSnapshotState、ImportSnapshotChunk、SetSnapshotLocation）

## 决策

- 不把运行时 `Chunk._data` 当作持久化格式——snapshot 按逻辑列写入
- 第一版只支持 `unmanaged` 组件；含托管引用组件在 chunk 构造时 fail fast
- 存档写组件类型的稳定字符串标识，不写运行时 `ComponentType.Value`
- 存档写 entity slot versions（不只活体 entity version）
- load 不能通过 `Add/Set/Remove` 回放世界——那会破坏 chunk 边界

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
