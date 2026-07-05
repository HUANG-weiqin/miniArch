---
title: MiniArch.Diagnostics 诊断工具
module: MiniArch.Diagnostics
description: ECS 世界的状态诊断工具集：比对、校验、检查、探查
updated: 2026-07-05
---

# MiniArch.Diagnostics 诊断工具

## 这个模块是干什么的

- 这个模块负责：
  - 提供纯函数式的 ECS 世界诊断工具，不依赖外部库
  - `WorldDiff.Compare()` — 两个世界按 slot 逐 entity 比对，锁定 divergence 位置
  - `WorldValidator.Validate()` — 世界内部结构性不变量检查
  - `EntityDump.Describe()` — 单个 entity 的全状态报告（组件类型/值、层级关系）
  - `WorldDigest.Compute()` — 按状态域（occupancy、free-list、hierarchy、组件类型、archetype）分桶的 SHA-256 哈希，用于快速缩小 checksum mismatch 范围
- 这个模块不负责：
  - 不负责网络层序列化/反序列化
  - 不负责 UI 展示（所有输出是纯数据 + `ToString()`）
  - 不负责热路径性能优化（诊断工具默认不优化，按需调用）
  - 不修改世界状态（所有操作只读）

## 架构

- 核心组成：
  - `WorldDiff.cs` — 两世界比较，输出 `WorldDiffResult`
  - `WorldValidator.cs` — 不变量检查，输出 `ValidationResult`
  - `EntityDump.cs` — 实体状态探查，输出 `EntityReport`
  - `WorldDigest.cs` — 分域哈希，输出 `WorldDigestResult`
  - 所有结果类型在各自独立的 `*Result.cs` 文件中
- 数据流：
  - 所有工具是纯函数（`static` 方法），输入 `World`，输出不可变结果
  - 使用 `System.Security.Cryptography.SHA256.HashData(Stream)` 替代 `IncrementalHash`（后者在 System.IO.Hashing 8.x 中被移除）
  - 内部通过 `HashBuilder` 类（`MemoryStream` + `SHA256.HashData`）累积数据

## 决策

- **同一程序集**：Diagnostics 放在 `src/MiniArch/Diagnostics/` 目录下，与核心代码同程序集，可以直接访问 `internal` 类型（`EntityRecord`、`Archetype`、`HierarchyTable` 等），无需 `InternalsVisibleTo`
- **Namespace 即文档**：`MiniArch.Diagnostics` 命名空间本身就传达了"这是 debug 工具"的信息，热路径代码不会意外引用
- **`HashBuilder` 替代 `IncrementalHash`**：`System.IO.Hashing` 8.0.0 移除了 `IncrementalHash`，改用 `MemoryStream` + `SHA256.HashData()`，在诊断场景下性能差异可忽略
- **结果不可变**：所有列表字段用 `ReadOnlyCollection<T>` 包装，防止误修改
- **确定性**：所有哈希按 entity ID 排序后再计算，保证相同逻辑状态 → 相同输出

## 入口

- 第一次读或加功能，先看：
  - `src/MiniArch/Diagnostics/WorldDiff.cs`：最复杂的工具，理解了它就能理解其他三个
  - `tests/MiniArch.Tests/Diagnostics/`：测试文件展示每种工具的最常用场景

## 坑点

- `WorldDiff.Compare()` 需要两世界都处于 quiescent 状态（无 pending CommandStream 保留），否则抛出 `InvalidOperationException`
- `Signature` 类型没有 `[i]` 索引器，需要使用 `.AsSpan()` 才能按索引访问
- `Position`/`Velocity` 等测试组件在各测试文件中各自定义为 `file readonly record struct`，不是共享的
- `WorldValidator` 检测 pending 保留时使用 `EntitySlotCount > occupied + freeCount` 发出 Warning 而非 Error（因为有保留是合法状态）
