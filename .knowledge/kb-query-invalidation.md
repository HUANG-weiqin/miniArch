---
title: Query Invalidation System
module: MiniArch.Core Query
description: Incremental append-only query invalidation — archetype count comparison, only new archetypes scanned
updated: 2026-06-08 (增量 append 替代全量重建)
---
# Query Invalidation System

## 这个模块是干什么的

- 检测 Query 匹配的 archetype 集合是否发生变化
- 决定何时刷新 Query 的快照（archetype 和 chunk 列表）
- 仅扫描新增 archetype，append 到已有快照，避免全量重建

## 架构

- 核心组成：
  - **World.ArchetypeCount**：当前 archetype 数组长度（archetype 创建时原子增长）
  - **Query._lastArchetypeCount**：上次刷新时记录的 archetype 数量
  - **Query._requiredMask/_excludedMask/_anyMask**：构造时预计算的 filter bitmask，不可变
  - **Query._refreshLock**：double-check locking 用于并发只读场景

- 数据流：
  1. 新 Archetype 创建时 `PublishArchetypeSnapshot()` 原子替换更大的 archetype 数组
  2. Query 访问 `MatchedArchetypes`/`GetChunkSpan()` 时调用 `EnsureRefreshed()`
  3. `if (_world.ArchetypeCount != _lastArchetypeCount)` 数量变化则进入 `Refresh()`
  4. `Refresh()` 下用 lock double-check → `AppendNewArchetypes()`
  5. `AppendNewArchetypes()` 只扫描 `[lastArchetypeCount .. currentCount)` 的新 archetype，匹配的 append 到已有快照

- Chunk 快照：每个匹配的 archetype 贡献 1 个 chunk。所有迭代器路径正确处理 `Count == 0` 的 chunk。

## 决策

- **增量 append 而非全量重建**（借鉴 Friflo）：archetype 一旦创建不会删除，签名不变。之前匹配的结果永远有效，只需扫描新增部分。消除全局版本号导致的所有 query 全量重建问题。
- **archetype 数量代替全局版本号**：`ArchetypeCount` 直接用 archetype 数组长度，语义更清晰，无需额外维护 `_archetypeVersion` 字段。
- **filter mask 预计算到构造时**：`_requiredMask`/`_excludedMask`/`_anyMask` 在 Query 构造时一次性计算为 readonly 字段，匹配时直接使用，无需每次 refresh 重复计算。
- **删除 scratch 双缓冲机制**：全量重建需要 double-buffer swap（避免读写冲突）。增量 append 直接写入 snapshot 数组，不再需要 scratch 数组。
- **256-bit ComponentMask 手动展开**：4×ulong 逐个比较，手动展开无循环。在 <64 组件的典型场景下只需 1 条 AND 指令。与 Friflo 的 Vector256 方案在性能上等价，但零硬件依赖、零 conditional compilation。
- **不再过滤空 archetype**：flatten 后每个 archetype 只有一个 chunk，包含空的 chunk 不会造成迭代开销（`Count == 0` 的 chunk 在所有迭代器路径中跳过）。

## 认知模型

- 一个基于 archetype 数量比较的增量缓存失效系统。利用 archetype 不可变的约束，只关心"有没有新的 archetype"，只扫描新增部分。

## 入口

- `src/MiniArch/Core/Query.cs`：`EnsureRefreshed()`、`Refresh()` 和 `AppendNewArchetypes()`
- `src/MiniArch/Core/World.QueryCache.cs`：Query 缓存管理、archetype snapshot 发布
- `src/MiniArch/Core/World.cs`：`ArchetypeCount` 属性

## 坑点

- archetype 数组是 append-only：如果未来需要删除 archetype，需要重新引入全量重建或标记删除机制
- `AppendNewArchetypes()` 使用 volatile write 发布快照，并发只读场景安全
- Query 缓存代码在 `World.QueryCache.cs` partial 文件中，修改时要注意 partial 类的编译范围

## 竞品对比

| | miniArch | Arch | Friflo |
|---|---|---|---|
| 失效检测 | archetype 数量比较 | Archetypes 集合 hash code | archetype 数量比较 |
| 重建策略 | **增量 append** | 全量重建 | **增量 append** |
| 匹配算法 | 4×ulong 手动展开 | uint[] + SIMD Vector | Vector256 / 4×long fallback |
