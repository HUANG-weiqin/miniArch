---
title: MiniArch.Diagnostics 诊断工具
module: MiniArch.Diagnostics
description: ECS 世界的状态诊断工具集：比对、校验、检查、探查
updated: 2026-07-22
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
  - 结果类型在各自独立的 `*Result.cs` 文件中（`WorldDiffResult` 在 `WorldDiff.cs` 内，其余均独立文件）
- 数据流：
  - 所有工具是纯函数（`static` 方法），输入 `World`，输出不可变结果
  - 使用 `System.Security.Cryptography.SHA256.HashData(Stream)`（`HashBuilder` 内部 `MemoryStream` + `SHA256.HashData(Stream)`）而非 `IncrementalHash`——`IncrementalHash` 仍可用（`System.Security.Cryptography`），但 `MemoryStream` 方式对诊断场景更简洁
  - 内部通过 `HashBuilder` 类（`MemoryStream` + `SHA256.HashData`）累积数据

## 决策

- **同一程序集**：Diagnostics 放在 `src/MiniArch/Diagnostics/` 目录下，与核心代码同程序集，可以直接访问 `internal` 类型（`EntityRecord`、`Archetype`、`HierarchyTable` 等），无需 `InternalsVisibleTo`
- **Namespace 即文档**：`MiniArch.Diagnostics` 命名空间本身就传达了"这是 debug 工具"的信息，热路径代码不会意外引用
- **`HashBuilder` 替代 `IncrementalHash`**：使用 `MemoryStream` + `SHA256.HashData(Stream)` 而非 `IncrementalHash`（`IncrementalHash` 在 `System.Security.Cryptography` 中仍可用，但 `MemoryStream` 方式对累加再算的场景更简洁），在诊断场景下性能差异可忽略
- **结果不可变**：列表字段用 `ReadOnlyCollection<T>` 包装；公共签名必须保留 `byte[]` 的 hash/raw bytes 通过 getter 返回 defensive copy，hash 字典同时深复制 value array，不能只用 `ReadOnlyDictionary` 包装可变数组
- **确定性**：所有哈希按 entity ID 排序后再计算，保证相同逻辑状态 → 相同输出
- **Validator 必须双向取证**：entity record↔archetype row、child→parent↔parent→children 都从两种独立表示互相校验；不能从同一表读两次后把结果当成 bidirectional proof。bulk `World.Clear` 有意保留的 version-invalid hierarchy entry 不属于 live relation，不报错。

## 入口

- 第一次读或加功能，先看：
  - `src/MiniArch/Diagnostics/WorldDiff.cs`：最复杂的工具，理解了它就能理解其他三个
  - `tests/MiniArch.Tests/Diagnostics/`：测试文件展示每种工具的最常用场景

## 坑点

- `WorldDiff.Compare()` 需要两世界都处于 quiescent 状态（无 pending CommandStream 保留），否则抛出 `InvalidOperationException`
- `Signature` 类型没有 `[i]` 索引器，需要使用 `.AsSpan()` 才能按索引访问
- `Position`/`Velocity` 等测试组件在各测试文件中各自定义为 `file readonly record struct`，不是共享的
- `WorldValidator` 检测 pending 保留时使用 `EntitySlotCount > occupied + freeCount` 发出 Warning 而非 Error（因为有保留是合法状态）
- `WorldDigest.Total` 包含 `PerArchetype` 物理 row-order hash；batch destroy 与普通 loop 可能得到相同 logical state / free-list / component values 但不同 dense storage row order。比较 layout-independent 世界态时用 `World.CanonicalChecksum()` + `WorldDiff.Compare()`，或只比较 `WorldDigest` 的 Occupancy/FreeList/Hierarchy/PerComponent 域。
- `WorldDigestResult` 与 `ComponentInfo` 的 byte-array getter 每次返回副本；Diagnostics 本就不是热路径，不要缓存并期待引用相等，按内容比较。
