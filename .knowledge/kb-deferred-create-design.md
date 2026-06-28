---
title: Deferred Create — Multi-Host Lockstep Design
module: MiniArch.Core.CommandStream
description: 已完成的 deferred create 重构 + 多 host placeholder-in-delta 改造的开放决策
updated: 2026-06-29
---

# Deferred Create — Multi-Host Lockstep Design

## 这个模块是干什么的

- 解决**多个 host 各自独立 World** 在帧同步（lockstep）下 Create/Clone 时 id 撞车的问题
- 提供"record 阶段不碰任何 World"的 placeholder 模型，把 id 分配推后到 replay
- 这个模块**不负责**：
  - 跨网络传输 / 序列化协议（FrameDelta 协议本身的事）
  - host 间的时序协调（lockstep 框架的事）
  - 跨帧的 placeholder 持久映射（明确放弃，见决策 #3）

## 要应对的游戏用户场景

### 场景 A：纯单机游戏

- 1 个 host，1 个 World
- 用户调 `stream.Create()` 拿到 placeholder，调 Add/Set/Link 等，最后 `stream.Submit()`
- placeholder 在 Submit 时被 resolve 成 host local real id，materialize 进 world
- **Submit 是为这个场景保留的快路径**：不走 delta 序列化/解码，直接 materialize

### 场景 B：帧同步（lockstep）多人游戏

- N 个 host（玩家）对等，每个 host 拥有**独立的 World**，id 分配器互不同步
- 每帧流程：
  1. 每个 host 在自己的 CommandStream 里 record 命令（Create/Add/Set/Link/Destroy 等）
  2. 每个 host 调 `stream.Snapshot()` 把 record 转成 FrameDelta，通过网络广播给所有其他 host
  3. 每个 host 收集齐本帧所有 N 个 delta（包括自己的）
  4. 每个 host **按固定 host 顺序**串行 replay 这 N 个 delta 到本地 World
  5. 所有 host 的本地 World 最终状态等价（但同一个 entity 在不同 host 上的 id 数值可能不同）
- **关键要求**：delta 里**不能携带任何 host local id**，否则不同 host 的 id 会撞车。必须用 placeholder，让每个 host 在 replay 时各自分配 local id。
- **跨帧引用**：本场景下业务代码如果想跨帧引用某个 entity，**不能用 placeholder**（placeholder 单帧有效）。需要业务自己维护一个跨 host 稳定的标识（比如 gameplay层面的 entity guid），replay 后通过查询组件定位本地 real id。这是业务层职责，不在 CommandStream 范围内。

### 场景 C：权威服务器 + 镜像客户端

- 1 个权威 host（服务器），N 个镜像客户端
- 服务器跑实体逻辑，定期 snapshot 状态发给客户端
- 客户端只是镜像（display），不产生命令
- **这个场景要求 host 和 client 的 id 同步**（客户端按服务器给的 id 引用 entity）
- 当前架构（Snapshot 输出 real id）已经满足这个场景
- **如果未来把 Snapshot 改成输出 placeholder delta，这个场景会破坏**。需要保留一条 real id delta 路径（决策 #1 的方案 B 或 C）

### 场景优先级

当前重点是**场景 B（lockstep 多人）**，这是 deferred 模型存在的核心理由。场景 A 由 Submit 覆盖（不动）。场景 C 暂时不是重点，但如果 Snapshot 改造会破坏它，需要在改造时显式处理。

## 已完成（已提交）

下面是已经合入 main 的改造，记录避免重做：

| Commit | 改动 |
|---|---|
| `882f76a` | 引入 deferred `Create()` / `Clone()`（返回 placeholder `Entity(-1, seq)`）+ `CreateImmediate()` / `CloneImmediate()` 逃生口。`ICommandRecorder` 接口同步扩展。HeroPipeline 4 个跨帧持有返回值的 Bootstrap 切到 Immediate。 |
| `83de7d8` | `Id < 0` 短路优化 `ReplacePlaceholders` / `ReplaceHierarchyPlaceholders` / `ReplaceUnavailablePlaceholders` / destroy 列表的字典查找。把 ~5-7% 回归拉回基线。 |
| `91d1d35` | resolveMap 从 `Dictionary<Entity, Entity>` 改为 `Entity[]` 按 seq 索引。反超基线（Movement 1755 / Attack 1082）。 |
| `024de01` | `World.TryReleaseReserved` 不抛异常版；`Clear` 改自给自足（不再假定 entity 已 materialize），覆盖 Submit 异常路径的 id leak。 |
| `d45514d` | `CommandStream.Clear` 改 public，支持中继模式（`Snapshot` + `Clear` 不本地 apply）。 |
| `e5c53d6` | `ICommandRecorder` 加 `Clear`；补 Submit 自动 Clear 的 XML doc。 |

### 当前 placeholder 契约（重要）

- placeholder `Entity(-1, seq)`，seq 在 CommandStream 内单帧递增、resolve 后归零
- placeholder **仅在 record → Snapshot/Submit 之间有效**，之后外部持有的引用失效（不能跨帧）
- 要跨帧持有 entity 引用，必须用 `CreateImmediate` / `CloneImmediate`（拿到 host local real id）

## 关键 bug：Snapshot 时仍然分配 host world id（**未修复**）

### 现象

`CommandStream.Snapshot` 当前实现：

```csharp
public FrameDelta Snapshot()
{
    ResolveDeferredCreates();   // ← 从 host world 分配 real id
    var delta = new FrameDelta();
    BuildDelta(delta);
    return delta;
}
```

`ResolveDeferredCreates` 调 `_world.ReserveDeferredEntityBatch()`，**placeholder 在 Snapshot 出 delta 之前就已经替换成 host world 的 real id**。delta 里写的是 host local id。

### 后果

deferred 在 record 阶段不碰 world，但 **Snapshot 时破功**。两个 host 各自独立 world，都 record + Snapshot：
- Host A delta: `Reserve(id=5), Create(id=5, ...)`
- Host B delta: `Reserve(id=5), Create(id=5, ...)`

合并 / 转发到 replica 时 id 撞车。当前架构实际只支持"单一权威 world + N 个 mirror replica（要求 id 同步）"，**不是真正的多 host 各自独立 world**。

### 正确架构

```
record        → placeholder Entity(-1, seq)，不碰任何 world
Snapshot      → delta 里保留 placeholder，不调 ResolveDeferredCreates
每个 world    → replay delta 时给 placeholder 分配本地 real：
                维护 placeholder→local real 映射（per-delta）
                delta 内交叉引用 placeholder 时查映射替换
```

### 锁步模型下的 placeholder 唯一性（已澄清）

**不需要 placeholder 全局唯一**。lockstep 下：
- N 个 host，每帧各自产出 1 个 delta
- 每个 host 收集 N 个 delta（含自己）
- **按固定 host 顺序串行 replay**：先 host 0，再 host 1，再 host 2 ...
- 每个 delta 内部 placeholder 是局部 seq，replay 时各自 resolve，互不冲突

所以 `Entity(-1, seq)` 在单 delta 内唯一就够。

## 待决策（开放问题）

### 决策 #1：SubmitAndSnapshotAsync 输出哪种 delta？

`SubmitAndSnapshotAsync` 当前走 `SwapOutState` → `ResolveDeferredCreates` → `BuildFromFrozen`，输出的是 **real id delta**。用于"单机同时备份 + 转发给同步 mirror"。

如果 `Snapshot` 改成输出 placeholder delta，两个 API 行为不一致。三个方案：

- **方案 A**：`SubmitAndSnapshotAsync` 也输出 placeholder delta。语义统一，但破坏 mirror（要求 id 同步）场景。
- **方案 B**：保留 `SubmitAndSnapshotAsync` 输出 real id delta（mirror 用），新增 `SnapshotForRelay()` 输出 placeholder delta（多 host 用）。两个 API 并存，复杂度增加。
- **方案 C**：废弃 `SubmitAndSnapshotAsync`。多 host 场景下 host 自己也走 `Snapshot() + Replay()`，不再有 Submit 快路径。最纯粹，host 性能略损。

**当前倾向**：方案 A，统一语义。mirror 场景如果真有需求再单独考虑。

### 决策 #2：immediate entity 和 Snapshot 共存

`CreateImmediate` 在 Create 时就 reserve 了 host local id。如果用户在同一帧 record 里混用 immediate 和 deferred，然后 Snapshot：
- deferred 部分 → 应该是 placeholder
- immediate 部分 → 已经是 host real id

delta 里混了 placeholder 和 host real id，replay 端遇到 real id 时 `EnsureReplayReservation` 会要求 replica world 同步 → **破坏多 world 独立性**。

两个方向：
- **严格**：Snapshot 时检测到 record 里有 immediate entity 就抛异常。强制多 host 路径全用 deferred。符合 AGENTS.md "概念诚实"和 fail-fast。
- **宽容**：文档警告，允许混用但 replica 必须同步。

**当前倾向**：严格禁止。Snapshot 路径下 record 里出现 immediate entity 是用户错误。

### 决策 #3：跨帧引用 placeholder

明确**放弃**。理由：
- placeholder 单帧有效，要跨帧必须 `CreateImmediate`
- `CreateImmediate` 仍然是 host local id，多 host 下会撞 —— 但 immediate 设计目标就是单机/快路径，多 host 场景不该用
- 真要跨帧引用且多 host，需要 placeholder 持久化（每 host 一个跨帧 seq，或者 frame+seq 复合标识），复杂度大且需求未明确

### 决策 #4：replay 端 placeholder→local real 映射的数据结构

按 seq 索引（和 CommandStream 内部 resolveMap 思路一致）：
- 长度 = delta 内最大 seq + 1（lazy 扩容）
- `placeholderMap[seq] = localReal`
- 查询：`if (e.Id < 0) localReal = placeholderMap[e.Version]`

**已定**：array + seq 索引（前面 Dictionary→array 改造已验证更快）。

## 实施计划（接续工作时按此顺序）

1. **Snapshot 改造**：删 `Snapshot` 内 `ResolveDeferredCreates()` 调用。`BuildDelta` 内 `if (entity.Id < 0) continue` 改为正常 emit placeholder（保留 cancelled placeholder 跳过）。
2. **replay 端加 placeholder→local real 映射**：
   - `World.Replay` 入口建 `Entity[] placeholderMap`（lazy 扩容）
   - 每个 op 处理前先 `ResolveEntity(e)`：`Id >= 0` 直接用，`Id < 0` 查 map
   - `Reserve(placeholder)` → `ReserveDeferredEntity()` + 写 map
   - 其他 op（Create/Add/Set/Link/Destroy/...）的 entity 参数都走 ResolveEntity
3. **混用 immediate 检测**：Snapshot 入口扫一遍 `_batchEntities`，如果有 `Id >= 0` 且来自 immediate create（需要标记位区分 immediate vs replay-reserved real），抛 `InvalidOperationException`。
4. **测试改造**：`FrameDeltaDeterminismTests` 里大量"source id == replica id"的断言要改成"状态等价"（两边都有相同组件、相同 hierarchy，但 id 数值可能不同）。
5. **SubmitAndSnapshotAsync**：按决策 #1 的选择改造（倾向方案 A）。
6. **新增多 host 集成测试**：模拟 3 个 host 各自 world + 各自 record + 收集 delta + 串行 replay，验证最终 3 个 world 状态等价。

## 入口

- 第一次读：本文件的「关键 bug」「待决策」两节
- 实施 placeholder-in-delta 改造时：
  - `src/MiniArch/Core/CommandStream.cs:Snapshot`（line ~405）
  - `src/MiniArch/Core/CommandStream.cs:EmitPendingEntitiesToDelta`（line ~722，placeholder 跳过逻辑）
  - `src/MiniArch/Core/World.cs:Replay`（line ~540，加 placeholder 映射）

## 坑点

- **placeholder 失效契约**：Snapshot/Submit 后外部 placeholder 引用变成 stale，再 `Add(placeholder, ...)` 会 fallback 到 ComponentStore 路径，Submit 时 world 找不到 entity → 行为未定义。合法使用下不会触发。
- **Submit 异常路径 id leak**：已通过 `Clear` 自给自足修复（commit `024de01`），但底层机制值得记住：Materialize 抛异常 → finally Clear → TryReleaseReserved 把 reserved id 还给 world。
- **cancelled deferred vs cancelled immediate**：cancelled deferred emit 什么都不 emit（不占 id）；cancelled immediate emit Reserve+Release（占 id 槽位保持 host/replica 同步）。Snapshot 改造后这个不对称会变得更重要。
- **跨帧引用**：明确不支持。业务需要跨帧持有 entity，必须用 `CreateImmediate`（单机）或者自己维护外部映射（多 host，自行设计）。
