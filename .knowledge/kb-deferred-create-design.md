---
title: Deferred Create — Multi-Host Lockstep Design
module: MiniArch.Core.CommandStream
description: DeferredEntities flag 让 Create/Clone 在 placeholder（多 host lockstep）和 immediate（单机/权威服务器）之间切换。Snapshot 按 flag 输出 placeholder delta 或 real-id delta；SubmitAndSnapshotAsync 始终输出 real-id delta。
updated: 2026-07-04
---

# Deferred Create — Multi-Host Lockstep Design

## 这个模块是干什么的

- 解决**多个 host 各自独立 World** 在帧同步（lockstep）下 Create/Clone 时 id 撞车的问题
- 通过 `CommandStream.DeferredEntities` flag 控制 `Create()`/`Clone()` 的行为：
  - `false`（默认）：立即从 World 分配 real id（单机快路径）
  - `true`：返回 placeholder `Entity(-1, seq)`，不碰任何 World（多 host lockstep）
- 这个模块**不负责**：
  - ~~跨网络传输~~ → 传输层未实现（`socket.Send` 是伪代码），见 `kb-lockstep-playbook.md` "尚未解决的网络问题"
  - ~~序列化协议~~ → **已实现**，见 `kb-command-stream.md` "FrameDelta wire format" 段（`AsSpan()` / `Deserialize()` 开箱即用）
  - host 间的时序协调（lockstep 框架的事）→ **端到端指南**见 `kb-lockstep-playbook.md`
  - 跨帧的 entity 引用走真实 Entity ID（见 TryResolvePlaceholder 节）

## 要应对的游戏用户场景

### 场景 A：纯单机游戏

- `DeferredEntities = false`（默认）
- `stream.Create()` 立刻 reserve real id，`stream.Submit()` materialize 进 world
- Submit 是快路径：不走 delta 序列化/解码

### 场景 B：帧同步（lockstep）多人游戏

- N 个 host 对等，每个 host 拥有**独立的 World**，id 分配器互不同步
- 每个 host 设 `DeferredEntities = true`
- 每帧流程：
  1. 每个 host 在自己的 CommandStream 里 record 命令（Create 返回 placeholder）
  2. 每个 host 调 `stream.Snapshot()` 把 record 转成 **placeholder delta**（不碰 World id allocator）
  3. `stream.Clear()` 清空 record（relay-only：不本地 apply）
  4. 每个 host 收集齐本帧所有 N 个 delta（包括自己的）
  5. 每个 host **按固定 host 顺序**串行 replay 这 N 个 delta 到本地 World
  6. 所有 host 的本地 World 最终状态等价（**且同一个 entity 在不同 host 上的 id 数值相同**——allocator 由 replay 序列同步驱动）
- **关键要求**：delta 里刚创建的 entity 必须用 placeholder（`Entity(-1, seq)`），因为 record 阶段不能跨 host 分配 id。但 replay 后所有 host 的 allocator 状态一致，真实 ID 跨 host 相同。

### 场景 C：权威服务器 + 镜像客户端

- `SubmitAndSnapshotAsync()` 始终输出 **real-id delta**（忽略 `DeferredEntities` flag）
- 服务器 apply 到本地 World 的同时，并行生成 real-id delta 转发给镜像客户端
- 客户端必须从 frame 0 完整重放以保持 id allocator 同步

## 已完成

### DeferredEntities flag 设计

| 组件 | `DeferredEntities = false`（默认） | `DeferredEntities = true` |
|---|---|---|
| `Create()` | `CreateImpl()` → 立即 reserve real id | `CreateDeferredImpl()` → placeholder `Entity(-1, seq)` |
| `Clone()` | `CloneImplImmediate()` → real id clone | deferred clone → placeholder |
| `Snapshot()` | `ResolveDeferredCreates()` → real-id delta | placeholder delta（throw on immediate entities） |
| `SubmitAndSnapshotAsync()` | real-id delta（始终，忽略 flag） | real-id delta（始终，忽略 flag） |
| `Submit()` | 两种模式都正常工作（resolve → materialize） | |

### 已删除的 API

- ~~`CreateImmediate()`~~ — 已删除。`DeferredEntities = false` 时 `Create()` 就是 immediate。
- ~~`CloneImmediate()`~~ — 已删除。`DeferredEntities = false` 时 `Clone()` 就是 immediate。
- ~~`CloneConcurrentImmediate()`~~ — 死代码，随 `CloneImmediate` 一起删除。
- ~~`ICommandRecorder.CreateImmediate()`~~ / ~~`CloneImmediate()`~~ — 接口同步移除。

### Producer 端实现（CommandStream.cs）

- `Snapshot()` 按 flag 分支：`!DeferredEntities` → `ResolveDeferredCreates()` + `BuildDelta()`；`DeferredEntities` → `ThrowIfSnapshotHasImmediateEntities()` + `BuildDelta()`
- `EmitPendingEntitiesToDelta` 通过 `entity.Id < 0` 检查同时处理两种 entity：placeholder 跳过 cancelled 的（不 emit 任何 op），committed 的 emit `Reserve + Create`；immediate entity 正常 emit `Reserve + Create` with real id
- `Submit()` 仍然调 `ResolveDeferredCreates()`——两种模式下 Submit 都正确

### Consumer 端实现（World.cs ReplayCore）

- `_replayPlaceholderMap`：`Entity[]` 按 placeholder seq 索引，存储 `seq → local real` 映射
- **每帧 `mapLen = 0` 重置**：防止上一帧的 stale mapping 泄漏到当前帧。`EnsurePlaceholderMap` 在首次 Reserve 时重新初始化所有 slot 为 `Entity(-1, -1)` sentinel
- `ResolveReplayEntity(wireEntity, map, mapLen)`：`Id >= 0` 直接用（real-id delta）；`Id < 0` 查 map + bounds check（placeholder delta）
- `Reserve` op with `Id < 0`：`ReserveDeferredEntityBatch()` 分配 local id + 写入 map
- 所有其他 op（Create/Add/Set/AddChild/Destroy/...）的 entity 参数都走 `ResolveReplayEntity`
- `PreScanForCapacity` 跳过 placeholder entity（`Id < 0`）的 `maxEntityId` 追踪——它们的 real id 在 main pass 才分配
- `World.Replay()` 返回 `void`。占位符解析通过 `World.TryResolvePlaceholder()` 完成（详见下文）

### 历史 commit（已合入 main）

| Commit | 改动 |
|---|---|
| `882f76a` | 引入 deferred `Create()` / `Clone()`（返回 placeholder）。后被改为 flag 控制。 |
| `83de7d8` | `Id < 0` 短路优化 resolveMap 查找。 |
| `91d1d35` | resolveMap 从 `Dictionary<Entity, Entity>` 改为 `Entity[]` 按 seq 索引。 |
| `024de01` | `Clear` 自给自足（覆盖 Submit 异常路径的 id leak）。 |
| `d45514d` | `CommandStream.Clear` 改 public，支持中继模式。 |

### (已移除公共 API) TryResolvePlaceholder — 改用 EntitySlot

> **2026-07-04 更新**：`World.TryResolvePlaceholder()` 已改为 `internal`，不再暴露为公共 API。
> 使用 `EntitySlot` + `CommandStream.Track()` 代替手动解析。详见 `EntitySlot` 节。

**遗留设计说明**（仅供内部实现参考）：

**问题**：replay 后用户只有 `Entity(-1, seq)`，不知道它在本地 world 里变成了哪个 real entity。

**方案**：`World.ReplayCore()` 填充内部 `_replayPlaceholderMap`，`TryResolvePlaceholder()` 查询此映射：

```csharp
// 只对本帧有效（internal，仅 CommandStream 内部使用）
internal bool TryResolvePlaceholder(Entity placeholder, out Entity real);
```

**⚠️ 关键约束**：`TryResolvePlaceholder` 只解析**最近一次 `ReplayCore()` 产生的占位符**。下一个 `ReplayCore()` 会覆盖内部映射表，
同一个 `Entity(-1, seq)` 会解析到不同的实体或失败。

## 决策

### 决策 #1：SubmitAndSnapshotAsync 输出哪种 delta？

**已定**：始终输出 real-id delta（忽略 `DeferredEntities`）。用于权威服务器 + 镜像客户端场景。如果多 host lockstep 需要 placeholder delta，用 `Snapshot()` + `DeferredEntities = true`。

### 决策 #2：immediate entity 和 Snapshot(placeholder) 共存

**已定**：严格禁止。`DeferredEntities = true` 时 `Snapshot()` 检测到 `Id >= 0` 的 batch entity 即抛 `InvalidOperationException`。

### 决策 #3：跨帧引用真实 Entity ID

**已定**：`Resolve()` 返回的是真实 Entity ID。由于 allocator 由 replay 序列唯一驱动（所有 host replay 相同的 Reserve/Create/Release/Destroy 序列），**真实 Entity ID 跨 host 一致**，可以直接存组件跨帧使用。不需要保留 placeholder 或 mapping。

```csharp
world.Replay(delta);
if (world.TryResolvePlaceholder(placeholder, out var real))
{
    // real 在 host A/B/C 上都是 Entity(3, 1)
    // 可以存到组件里，下帧拿出来用
    world.Set(foo, new Bar { Target = real });
}
```

### 决策 #4：replay 端 placeholder→local real 映射的数据结构

**已定**：`Entity[]` 按 seq 索引。每帧 `mapLen = 0` 重置，`EnsurePlaceholderMap` lazy 扩容 + 初始化 sentinel。`TryResolvePlaceholder` 直接查询此内部映射。

## 用户指南：组件 Entity 字段自动解析

从 `Component<T>` 组件字段可以直接存放 `Entity` 值，指向同帧 deferred 创建的实体。

```csharp
public record struct AIFollow(Entity Target);  // 普通组件，含 Entity 字段

var stream = new CommandStream(world) { DeferredEntities = true };

var target = stream.Create();   // Entity(-1, 0) — placeholder
var drone = stream.Create();    // Entity(-1, 1)
stream.Add(drone, new AIFollow { Target = target });

stream.Submit();  // 或 Snapshot() + Replay()
// drone.AIFollow.Target 自动解析为真实的 target Entity ID ✅
```

### 哪些场景会自动解析

| 场景 | Submit | Replay |
|---|---|---|
| 新建实体引用另一个新建实体 | ✅ | ✅ |
| 已有实体引用新建实体 | ✅ | ✅ |
| 自引用（A → A） | ✅ | ✅ |
| Clone 出来的实体引用其他实体 | ✅（源组件的 ID 是实时的） | ✅ |
| 被引用实体已取消 Destroy | ⚠️ placeholder 保持未解析（无对应实体） | ⚠️ 同上 |

### 约束

- 组件类型必须使用 `LayoutKind.Sequential`（C# 默认）。`LayoutKind.Auto` 会抛出 `InvalidOperationException`
- 只扫描**直接** `Entity` 字段，不递归嵌套结构体
- 跨帧引用不要用 placeholder，用 `TryResolvePlaceholder()` 拿真实 ID 后存组件

### 实现原理（概要）

`EntityFieldResolver` 运行时反射一次组件类型的 `Entity` 字段偏移量，缓存永久。Submit/Replay 在写入前逐字段检查——如果是 placeholder 则查 resolveMap 替换为真实 ID。Replay 路径用 `ArrayPool<byte>` scratch 做解析，不修改 delta buffer。

详细路径见上文"Producer 端实现"和"Consumer 端实现"节。

## 用户指南：EntitySlot —跨帧追踪 deferred entity

当你在 deferred mode 下 `Create()` 拿到 placeholder 后，除了塞进组件字段（自动解析）外，还可以用 `EntitySlot` 追踪解析结果：

```csharp
var slot = stream.Track(stream.Create());
stream.Add(slot, new Health(100));  // 隐式转换 → slot.Value，placeholder 阶段组件自动解析
stream.Submit();
// slot 现在是 real Entity ✅，跨帧持有也有效
world.Get<Health>(slot);            // 隐式转换，等价于 slot.Value
```

`EntitySlot` 有到 `Entity` 的隐式转换（`implicit operator Entity`），可以直接传给任何接受 `Entity` 的方法。

### 锁步中继模式

```csharp
var slot = stream.Track(stream.Create());
var myDelta = stream.Snapshot();
stream.Clear();
// ... 网络交换 ...
foreach (var delta in allDeltas)
    stream.Replay(delta);   // 自动识别自己的 delta，解析 slots
slot.Value  // real Entity ✅
```

`stream.Replay(delta)` 包装了 `world.Replay(delta)`，通过 `FrameDelta.OriginStream` 自动检测哪个 delta 是自己产的，只在自己的 delta replay 后解析 tracked slots。

### 约束

- EntitySlot 不能当组件用（含引用类型，不是 unmanaged）。组件里用 `Entity`（`slot.Value` 或隐式转换）。
- 非延迟模式下 Track 零分配（Entity 内联存储）。
- 延迟模式下每个 Track 分配一个小的 `Slot` 对象（opt-in）。
- `EntitySlot → Entity` 隐式转换始终返回当前最佳值（placeholder 或 real），安全无副作用。

## 坑点

- **placeholder 失效契约**：Snapshot/Submit 后外部 placeholder 引用变成 stale。合法使用下不会触发（placeholder 仅在 record → Snapshot/Submit 之间有效）。
- **cancelled deferred vs cancelled immediate**：cancelled deferred emit 什么都不 emit（不占 id）；cancelled immediate emit Reserve+Release（占 id 槽位保持 host/replica 同步）。
- **跨帧引用用真实 ID**：placeholder 不跨帧，但 `TryResolvePlaceholder()` 返回的真实 ID 可以跨帧直接存组件用。见决策 #3。
- **`_replayPlaceholderMap` 跨帧清零**：`mapLen` 在 `ReplayCore` 入口重置为 0。如果不清零，上一帧的 stale mapping 会被 malformed delta 静默误用。
- **Destroy 已取消的 deferred placeholder 必须是 no-op（非并行）**：`Destroy()` 的 `else` 分支调 `AppendDestroy`。当一个 deferred placeholder 的 batch 已被级联取消（`CancelPendingDescendants` 把它一起 cancel，或同一 placeholder 被第二次 `Destroy`），`TryGetPendingBatch` 返回 false → 落到 `else`。若不守护 `!entity.IsPlaceholder`，会把一个**没有对应 Reserve** 的 placeholder Destroy op 写进 delta，每个 replay 端都抛 "Unresolved placeholder entity"，而产生端自己不 replay 所以不报错——silent lockstep hazard。最小复现：同一个 deferred placeholder `Destroy` 两次（不需要 link）。修复在 `CommandStream.Destroy` 的 `else if (!entity.IsPlaceholder)` 守护；回归测试 `KnownLimitationTests.Deferred_destroy_twice_on_same_placeholder_cancels_cleanly` 与 `Deferred_link_then_destroy_BOTH_endpoints_replays_cleanly`。real entity（`Id >= 0`）的二次 destroy 不受影响（replay 端 `if (IsAlive)` 已是安全 no-op）。
- **并行模式永不取消 batch（因此不受上一条影响）**：`Destroy` 并行分支只 `AppendDestroyConcurrent`，不走 `CancelPendingEntity`/`CancelPendingDescendants`。所以 deferred+并行下每个 batch 都 commit，placeholder destroy 在 `ResolveDeferredCreates` 时 resolve 成 real entity——delta 里永远不会有未 resolve 的 placeholder。代价：同一帧 create+destroy 在并行模式 emit `Reserve+Create+Destroy`（每个 replica 都先 create 再 destroy，浪费但正确收敛），而非非并行模式的"取消为空"。
