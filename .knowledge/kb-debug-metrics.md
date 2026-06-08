---
title: Debug Metrics
module: MiniArch.Core
description: 已删除 — DebugMetrics 子系统在 YAGNI 重构中被完全移除
updated: 2026-06-08 (全量删除确认)
---
# Debug Metrics

## 状态：已删除

`DebugMetrics.cs` 整个文件已在 YAGNI 重构中被删除。以下内容也一并移除：
- `WorldDebugMetrics` struct
- `CommandBufferDebugMetrics` struct
- `World` 和 `CommandBuffer` 中的 `#if DEBUG` 计数器累加语句
- API：`World.GetDebugMetrics()` / `World.GetDebugReport()` / `CommandBuffer.GetDebugMetrics()` / `CommandBuffer.GetDebugReport()`
- 测试文件 `tests/MiniArch.Tests/Core/DebugMetricsTests.cs`

## 替代方案

如果未来需要 debug 计数器，建议：
- 使用 `dotnet-trace` / `EventSource` 外部采样
- 在 benchmark 中用 `PERF_DIAG` 条件编译临时埋点（不在 Release 中保留）

## 历史

- 删除原因：YAGNI — 计数器从未在 Release 中使用，维护成本 > 收益
- 删除 commit：v1.1.8 YAGNI cleanup 系列
