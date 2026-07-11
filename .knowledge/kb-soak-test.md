---
title: 浸泡测试（Soak Test）— 库安全证明
module: Soak
description: 长周期 ECS 正确性验证器——随机操作序列 + Submit/Replay 双路径校验 + 多维度安全证明矩阵
updated: 2026-07-11
---
# 浸泡测试（Soak Test）— 库安全证明

> `tools/soak/MiniArch.Soak/` — 通过长时间随机操作序列验证 Submit 与 Replay 两条路径的世界状态收敛一致性。已发现并修复 **6 个库级 bug（B1-B6）**，另有 **B7-B17 来自代码审阅 / 用户报告**（详见 kb-code-review-findings.md）。当前 **259 seed × 6.4M+ 帧全 PASS**（含 224 diversity sweep + 长时/边界/鲁棒等补充 seed）。

## 这个测试是干什么的

- 核心职责：
  - 随机生成 Create / Destroy / Add / Set / Remove / Clone / AddChild / RemoveChild 操作序列
  - 每帧执行 `CommandStream.Submit()` → 序列化为 `FrameDelta` → `Replay()` 到影子世界
  - 每帧校验：`WorldValidator` 结构不变量 + `CanonicalChecksum` 双世界一致 + `EntityCount` 对齐
  - 每 10000 帧执行全量 `WorldDiff` + `WorldSnapshot` 往返 + `World.Clone` 校验
  - 每 100 帧抽样 RefModel（独立 oracle）验证 CompA/CompB 值
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

默认每帧 1~8 个随机操作；boundary 测试可达 100 ops/f。

## Batch 模式（证明矩阵工具）

| 模式 | 用途 | 命令 |
|------|------|------|
| `--sweep N` | N 个连续 seed 并行正确性验证 | `--sweep 32 --frames 100000` |
| `--determinism` | 同 seed 跑 2 次，比对最终 checksum | `--determinism --frames 200000` |
| `--quiet` | 抑制逐帧输出，只留汇总 | 配合 sweep 使用 |

Sweep 模式下每个 seed 独立报告 GC/alloc（per-seed baseline），第一个 FAIL 立即停止。

## 库安全证明矩阵（2026-07-09）

### 结论：全维度 PASS

| 维度 | 配置 | 结果 |
|------|------|------|
| **Diversity Sweep** | 32 seed (1234567+) × 100K 帧 | ✅ 32/32 PASS |
| **Diversity Sweep** | 64 seed (42+) × 100K 帧 | ✅ 64/64 PASS |
| **Diversity Sweep** | 128 seed (1000+) × 50K 帧 | ✅ 128/128 PASS |
| **Long-Run Stability M7** | 3 seed × 1M 帧 (42, 1234567, 987654) | ✅ 全 PASS (gen0 46-52, managed 39-82MB) |
| **Ultra-Long Run** | 1 seed × 5M 帧 | ✅ PASS (gen2=154, managed=62MB, 3min55s) |
| **Determinism** | 同 seed 跑 2 次 | ✅ 字节级一致 (969BAEF1...) |
| **Boundary M7: Tiny World** | seed=111 cap=100 ops/f=50, 200K 帧 | ✅ PASS |
| **Boundary: Huge World** | cap=50000, 100K 帧 | ✅ PASS (peak 50004 ent) |
| **Boundary: Extreme Density** | ops/f=100, 100K 帧 | ✅ PASS |
| **Boundary: Version Rollover** | cap=10 floor=1, 500K 帧 | ✅ PASS (353K creates, gen0=8) |
| **Robustness Tests** | 19 wire-fuzz + adversarial | ✅ 全 PASS |
| **Architecture Regression** | HeroComing.Perf gate | ✅ 见 kb-hero-pipeline-regression.md（当前 baseline Movement 2052.7, Attack 1246.8） |

**总计：259 个不同 seed（224 diversity sweep + 长时/边界/鲁棒补充），全部 PASS。6.4M 帧无内存泄漏。**

### 内存与 GC 特征

- 核心路径（Submit + Replay + Deserialize）稳态零 GC
- gen0 ~17/100K 帧（全部来自 harness RefModel 的 record 分配，非库路径）
- managed memory 1M 帧后 14-27MB，5M 帧后 62MB（线性增长来自 oracle Dictionary，非泄漏）
- network 73-75 B/frame（FrameDelta 序列化极紧凑）

## 发现的历史性 bug

浸泡测试已发现 **6 个库级 bug（B1-B6）**，全部是 Submit 与 Replay 两条路径的操作顺序/跳过逻辑不一致导致的。另有 **B7-B17 来自代码审阅 / 用户报告**（详见 kb-code-review-findings.md）。

| # | bug | 根因 | 修复 | 发现方式 |
|---|-----|------|------|---------|
| B1 | `ApplyRawAdd` 重复 Add 抛异常 | Replay 路径无去重 | **历史修复**：`ApplyRawAdd` 改为覆盖写入（内部分支，非公 API）；当前 `World.Add<T>` 公 API 为 strict Add（已存在抛异常） | 默认配置 |
| B2 | `Clear` 不释放已取消 batch 实体 | Submit 路径跳过释放 | 始终释放 | 默认配置 |
| B3 | `ApplyHierarchyToWorld` 排序不同 | 字典 vs Entity.Id 排序 | 统一排序 | 默认配置 |
| B4 | `ApplyTypedAdd` 重复 Add 抛异常（Submit） | Clone+Add 路径 | **历史修复**：`ApplyTypedAdd` 改为覆盖写入（内部分支，非公 API）；当前 `World.Add<T>` 公 API 为 strict Add（已存在抛异常） | 默认配置 |
| B5 | Replay free-list 顺序分歧 | `RemoveFromFreeList` swap-remove 破坏 survivor 顺序 | shift-remove (Array.Copy) | **cap=100 ops/f=50** |
| B6 | Cancelled batch free-list 顺序分歧 | record 阶段按 destroy 顺序 push，wire 按 batch 创建顺序 emit | `AlignCancelledBatchFreeListOrder` | **cap=100 ops/f=50** |

> **B5/B6 的教训**：默认配置（ops/f=8, cap=5000）下单帧操作密度低，cancel+reorder 的概率极小，32-seed sweep × 1M 帧都无法触发。只有 boundary 测试（ops/f=50, cap=100）的高密度交错才暴露。**证明矩阵必须包含极端密度配置。**

详见 `.knowledge/kb-code-review-findings.md` 的 B5/B6 条目。

## 用法

```bash
# 单 seed 默认跑
dotnet run -c Release --project tools/soak/MiniArch.Soak

# 32-seed diversity sweep
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --sweep 32 --frames 100000

# 确定性验证
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --determinism --frames 200000

# Boundary: 极端密度
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --seed 111 --entity-cap 100 --ops-per-frame 50 --frames 200000

# Boundary: 大世界
dotnet run -c Release --project tools/soak/MiniArch.Soak -- --seed 222 --entity-cap 50000 --frames 100000
```

## 约束

- 必须 `-c Release` 编译（与 NuGet 包一致）
- 影子世界的 `CommandStream` + `FrameDelta` 全局复用（避免每帧分配）
- 随机操作保证对库合法：`_pendingRemoves` 防 Remove+Set/Add、`_destroyedThisFrame`+`HasAncestorDestroyedThisFrame` 防级联销毁重复、`_pendingHierarchyParents`/`WouldCreateCycle` 防同帧循环

## 后续增强方向

- Oracle 扩展：当前只跟踪 CompA/CompB 值，可扩展到 CompC/CompD + hierarchy 关系
- Async 路径：soak 只测 `Submit` + `SnapshotInto`，未覆盖 `SubmitAndSnapshotAsync` / `SubmitAndSnapshotIntoAsync` / `SubmitFromFrozen` 路径
- 更深 hierarchy：当前 AddChild 只做浅层，深层嵌套（10+ 层）的级联销毁顺序可进一步压测
