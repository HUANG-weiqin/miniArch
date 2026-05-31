---
title: Snapshot Persistence
module: MiniArch.Core Snapshot
description: First-version full-world snapshot save/load design for unmanaged components
updated: 2026-05-26
---
# Snapshot Persistence

## 这个模块是干什么的

- 这个模块负责：
  - 把 `World` 的当前活体数据导出成紧凑二进制 snapshot
  - 从 snapshot 快速重建 archetype / chunk / entity metadata
  - 保留 entity slot version 语义，避免读档后把旧句柄错误复活
  - 明确 runtime layout 和 persistence format 之间的边界
- 这个模块不负责：
  - 跨版本迁移
  - 引用类型组件或含托管引用字段组件的持久化
  - 增量存档、局部存档或网络同步协议

## 架构

- 核心组成：
  - `src/MiniArch/Core/WorldSnapshot.cs`：snapshot 二进制读写入口
  - `src/MiniArch/Core/WorldClone.cs`：内存直拷 World 克隆（零序列化）
  - `src/MiniArch/Core/World.cs`：slot version、location、free id 重建桥接点
  - `src/MiniArch/Core/Archetype.cs`：按快照 chunk 精确导入实体批次
  - `src/MiniArch/Core/Chunk.cs`：暴露有效 entities 和逻辑列字节块给 snapshot/clone 层批量读写
- 数据流 / 控制流：
  - save 先枚举非空 archetype，再顺序写 header、entity slot versions、schema 表、archetype/chunk 记录和列原始字节块
  - load 先验证 header，再注册 schema 对应的 runtime `ComponentType`
  - load 通过专用 import 路径 materialize archetype/chunk，并最终重建 `_locations` 和 `_freeIds`

## WorldClone vs WorldSnapshot

- `WorldSnapshot.Save/Load`：走二进制序列化，支持跨进程/跨机器传输
- `WorldClone.Clone`：纯内存直拷，跳过全部编解码，预估 5-20× 快于 Snapshot 往返
- 两者共享同一套 internal 重建 API（ResetSnapshotState、ImportSnapshotChunk、SetSnapshotLocation 等）
- Clone 的关键优化：按逻辑列直接 raw byte copy，替代 per-row 读写；零 Stream 分配

## 决策

- 不直接把 `Chunk` 的运行时 `_data` 当作持久化格式；snapshot 格式仍按逻辑 archetype/chunk/column 顺序写入，避免把运行时 padding 或内部布局固化成协议。
- 第一版只支持 `unmanaged` 组件；flat chunk 存储层会先对托管引用组件 fail fast，snapshot 层也不能把托管引用字段伪装成可落盘字节。
- 存档里不写 `ComponentType.Value`，而写组件类型的稳定字符串标识；`ComponentType` 仍然只用于运行时热路径。
- 存档里必须写 entity slot versions，而不只写活体 entity version；否则空闲 id 在读档后会丢失版本递增语义。
- 当前 entity 句柄契约要求 `default(Entity)` 非法、活体 entity `Version > 0`；snapshot 恢复时也必须保持这一点，不能把 fresh/live entity 读回成 `v0`。
- load 不能通过 `Add/Set/Remove` 回放世界；那会破坏 chunk 边界并把读档退化成结构迁移流程。
- flat byte chunk 不改变 snapshot 的外部语义：save/load 仍保存每个逻辑列的有效 `rowCount` 个元素，clone 也只通过 `Chunk` 的 internal column byte API 复制相同 signature 的列。

## 认知模型

- 理解这个模块时，应该把它看成：
  - “逻辑 chunk 导出格式”，而不是“runtime chunk 内存镜像”
- 这个模块里最重要的抽象是：
  - `WorldSnapshot`
  - entity slot version table
  - schema table
  - archetype/chunk records
- 常见误解：
  - 以为 `ComponentType` 可以直接写进存档长期复用
  - 以为只保存活体 entity 的 `Id/Version` 就足够

## 入口

- 第一次读或加功能，先看：
  - `src/MiniArch/Core/WorldSnapshot.cs`：完整的 snapshot 格式和读写流程
  - `tests/MiniArch.Tests/Persistence/WorldSnapshotTests.cs`：round-trip、free slot version 和 unsupported component 的外部契约
  - `src/MiniArch/Core/Archetype.cs`：是否需要新的 chunk 导入策略
  - `src/MiniArch/Core/Chunk.cs`：列视图是否还能满足更复杂的持久化需求
- 修 bug，先看：
  - `src/MiniArch/Core/WorldSnapshot.cs`：schema/type 解析、列块读写和 header 校验
  - `src/MiniArch/Core/World.cs`：slot version、location、free id 重建是否一致

## 坑点

- 历史上容易出问题的地方：
  - 只存活体 entity version，导致读档后复用旧 id 时版本回退
  - 读档复用 `GetWritableChunk`，把快照 chunk 边界挤压重排
  - 直接把 runtime `ComponentType.Value` 当存档协议 id
- 容易误判的地方：
  - 以为 `struct` 就一定是 `unmanaged`
  - 以为 save/load 只要组件值对了，entity metadata 就一定对
- 改这里时要特别小心：
  - 组件类型解析当前只保证同版本读写；跨版本类型名变化会直接失配
  - 当前存档不压缩、不做校验和；如果后面要加，需要保持列块和 rowCount 的读取顺序不变
  - runtime chunk 内部可能有列对齐 padding；snapshot/clone 只能通过 `Chunk.WriteColumnTo` / `ReadColumnFrom` / `CopyColumnsFrom` 这类逻辑列 API 访问，不能直接落盘或复制整块 `_data`

