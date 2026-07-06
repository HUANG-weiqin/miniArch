---
title: 浸泡测试（Soak Test）
module: Soak
description: 长周期 ECS 正确性验证器——随机操作序列 + Submit/Replay 双路径校验
updated: 2026-07-06
---
# 浸泡测试（Soak Test）

> `tools/soak/MiniArch.Soak/` — 通过长时间随机操作序列验证 Submit 与 Replay 两条路径的世界状态收敛一致性。

## 这个测试是干什么的

- 核心职责：
  - 随机生成 Create / Destroy / Add / Set / Remove / Clone / AddChild / RemoveChild 操作序列
   - 每帧执行 `CommandStream.Submit()` → 序列化为 `FrameDelta` → `delta.Deserialize()`（实例方法零 GC）→ `Replay()` 到影子世界
  - 每帧校验：`WorldValidator` 结构不变量 + `CanonicalChecksum` 双世界一致 + `EntityCount` 对齐
  - 每 10000 帧执行全量 `WorldDiff` + `WorldSnapshot` 往返 + `World.Clone` 校验
- 这个测试不负责：
  - 性能测量或回归检测（那是 `HeroComing.Perf` 的事）
  - Edge case 的单步精确复现（有常规单元测试覆盖）

## 测试策略

```
随机种子 → 影子世界 + 6 阶段操作权重
  WarmUp (0-10%): 仅创建，无销毁
  StableMutate (10-40%): 均衡操作
  MigrationStorm (40-55%): 高频 Add/Remove（archetype 迁移）
  HierarchyStress (55-70%): 高频 AddChild/RemoveChild
  AllocatorChurn (70-90%): 高频 Create/Destroy（slot 回收+复用）
  Cooldown (90-100%): 慢速收敛
```

每帧 1~8 个随机操作。

## 发现的历史性 bug

浸泡测试已发现 **4 个库级 bug**，全部是 Submit 与 Replay 两条路径的操作顺序/跳过逻辑不一致导致的：

| # | bug | 根因 | 修复 |
|---|-----|------|------|
| B1 | `ApplyRawAdd` 重复 Add 抛异常 | Replay 路径无去重 | 已有时原地写值 |
| B2 | `Clear` 不释放已取消 batch 实体 | Submit 路径跳过释放 | 始终释放 |
| B3 | `ApplyHierarchyToWorld` 排序不同 | 字典 vs Entity.Id 排序 | 统一排序 |
| B4 | `ApplyTypedAdd` 重复 Add 抛异常（Submit） | Clone+Add 路径 | 已有时原地写值 |

详见 `.knowledge/kb-code-review-findings.md` 的「浸泡测试发现的 bug」节。

## 分配分析

miniArch 核心路径（Submit + Replay）每帧仅分配 ~4KB。99% 的分配来自每帧的 `WorldValidator.Validate` + `CanonicalChecksum`（诊断 API），这些是测试特有的过验证，不在生产路径中。

## 用法

```bash
# 默认 100000 帧，seed=1234567
dotnet run -c Release --project tools/soak/MiniArch.Soak

# 自定义
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --frames 5000 --seed 9999 --detail-interval 1000

# 随机种子
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --random-seed
```

## 输出示例

```
[   4000]   20%  ent= 5712↗  stable_mutate       ✓✓✓✓  ⚡ 299f/s  GC 1328/1325/1324  mem=5MB  ws=38MB  alloc=427024KB
[  20000]  100%  ent= 4499↘  cooldown            ✓✓✓✓  ⚡ 269f/s  GC 1739/1739/1739  mem=3MB  ws=39MB  alloc=1061518KB
══════════════ PASS ══════════════  20,000 frames in 00:01:14
```

## 约束

- 必须 `-c Release` 编译（与 NuGet 包一致）
- 影子世界的 `CommandStream` 全局复用（避免每帧分配）
- 随机操作保证对库合法：`_pendingRemoves` 防 Remove+Set/Add、`_pendingHierarchyChildren` 防级联销毁、`_pendingHierarchyParents` 防同帧循环
