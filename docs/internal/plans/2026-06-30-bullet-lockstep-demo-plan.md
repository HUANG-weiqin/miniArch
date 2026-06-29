# BulletLockstep — 多 host 帧同步弹幕游戏 Demo 实现计划

> Branch: `feat/bullet-lockstep-demo` · Worktree: `miniArch-bullet-lockstep/`

## 目标

构建一个**最小可运行的帧同步弹幕游戏 demo**，作为 miniArch 的真实场景集成测试。
通过 N 个对等 host（独立 `World` + 独立 id allocator）每帧交换 placeholder delta 并互相 replay，
最终所有 host 的 `World.CanonicalChecksum()` 在每一帧都字节级一致 —— 这是库核心契约的端到端验证。

## 验证条件（什么才算完成）

| Slice | 完成标准 | 验证方式 |
|---|---|---|
| 1 | 2 个 host 各自 record，每帧交换 placeholder delta 互相 replay，**1000 帧 fuzz 后所有 host 的 CanonicalChecksum 在每一帧都完全一致** | `dotnet run -c Release` 控制台输出 per-frame checksum equal |
| 2 | 玩家飞船 + 子弹 spawn/destroy/位移系统跑通；多 host 仍然 checksum 一致；GC 0/0/0 | 同上 + 输出 GC 计数 |
| 3 | 单 host 抢跑预测帧 → checksum 不匹配 → `World.Clone()` revert 后再次 replay → 一致 | 同上 + 日志显示 rollback 次数 |

## 不做（YAGNI）

- ❌ 真实网络传输（in-process 交换 `FrameDelta.AsSpan()` bytes 即可，模拟网络层）
- ❌ 渲染（帧同步本身不需要，console 输出 checksum 即可证明）
- ❌ 客户端预测 + 服务端权威模型（用纯 P2P lockstep 即可）
- ❌ 跨进程 / 跨机测试（同进程内多 `World` 实例已足够验证库契约）
- ❌ 跨帧 entity 引用（kb 明确不支持；本 demo 跨帧引用通过查询组件定位）
- ❌ 高级玩法（Boss / 道具 / 关卡）—— 仅玩家 + 子弹 + 简单位移

## 技术约束（来自库设计）

- 组件必须 `unmanaged` value type，无引用字段
- 位移用 **整型定点数**（`int`，单位 = 1/1000 像素）而非 `float`，确保跨硬件确定性
- 每帧时序由 demo 自己控制（lockstep 框架不属于库职责）
- 必须用 `-c Release` 跑性能相关测试

## 架构

```
samples/BulletLockstep.Demo/
├── BulletLockstep.Demo.csproj      # console, net8.0, 引用 MiniArch
├── Components.cs                    # unmanaged 组件：Position/Velocity/PlayerTag/BulletTag/...
├── LockstepHost.cs                 # 单个 host：World + CommandStream + 输入
├── LockstepSimulator.cs            # 调度 N 个 host，每帧收集 delta、按 host 顺序 broadcast replay
├── Systems/
│   ├── PlayerMoveSystem.cs         # 玩家根据输入改 Velocity/Position
│   ├── BulletSpawnSystem.cs        # 玩家每隔 N 帧发射子弹（Create）
│   ├── BulletMoveSystem.cs         # 子弹位置更新
│   ├── BulletLifetimeSystem.cs     # 超时 Destroy 子弹
│   └── FiringSystem.cs             # 输入 → 触发 spawn
├── InputProvider.cs                # 确定性伪随机输入（基于 frame number + host id）
├── ChecksumLogger.cs               # per-frame checksum 收集 + 比较
└── Program.cs                      # 入口：跑 1000 帧模拟 + 报告
```

## 每帧时序（Slice 1 核心）

> **Slice 1 用 real-id delta mode（`DeferredEntities=false`）**：所有 host 从**完全一致**的确定性初始 World 出发（同 hostId 顺序 spawn N 个玩家），所有 host 的 entity id allocator 同步。Slice 2 才切到 placeholder mode（每 host 独立 allocator）测更难场景。

每个 host 在 frame F：

1. **Record 阶段**：在自己的 `CommandStream`（`DeferredEntities=false`）里 record 本帧意图（`Set`/`Create`/`Destroy`/`Add`/`Remove`），不碰 World
2. **Snapshot**：`var delta = stream.Snapshot()` → 得到 real-id delta（每个 host 的 id 已经同步，所以别的 host replay 也对得上）
3. **Clear**：`stream.Clear()` —— demo host 不本地 apply（relay-only，全部由 replay 决定最终状态）
4. **Broadcast**：把 delta 提交给 simulator
5. **Replay 阶段**：simulator 把本帧所有 N 个 host 的 delta **按 host id 升序**串行 replay 到**每个** host 的本地 World
6. **Tick Systems**：在 replay 之后再跑确定性的纯查询系统（位移、生命周期）—— 这些不产生命令，直接 mutate 组件
7. **Checksum**：每帧末每个 host 计 `CanonicalChecksum`，simulator 比对全部一致才进入下一帧

> 注：步骤 6 的位移是直接 `world.Set` 而非走 CommandStream。Slice 1 先用 record-only 模式（所有变更都通过 delta）保证 host 完全等价；Slice 2 再决定位移走 record（更接近真实游戏）还是 direct（更快）。

## Slice 1 任务拆解（最先做）

### Task 1.1 — 项目骨架
- 新建 `samples/BulletLockstep.Demo/BulletLockstep.Demo.csproj`（net8.0 console）
- `<ProjectReference>` 指向 `src/MiniArch/MiniArch.csproj`
- 把它加入 `miniArch.sln`

### Task 1.2 — 最小组件 + 输入
- `Components.cs`：`Position(int X, int Y)`、`Velocity(int X, int Y)`、`PlayerTag(int HostId)`
- `InputProvider.cs`：给定 `(hostId, frame)` 返回确定性的 `(dx, dy)`

### Task 1.3 — LockstepHost
- 每个 host 持有 `World`、`CommandStream`（`DeferredEntities=true`）、自己的 hostId
- `RecordFrame(frame)`：玩家根据输入 record 一个 `Set<Position>`（先纯 Set，不 Create，确保 Slice 1 跑通流程）

### Task 1.4 — LockstepSimulator
- 持有 N 个 host
- `Tick(frame)`：每 host record+snapshot+clear → 收集 deltas → 对每个 host 按 hostId 升序 replay 全部 deltas → 校验 checksum
- 用 `frame 0` spawn 玩家（通过共享初始化 delta 或在 World 构造后直接 Create — Slice 1 选后者，简单）

### Task 1.5 — Program + 跑 1000 帧
- 创建 2 个 host，每个 host 在自己 World 里预先 Create 一个 PlayerTag（hostId=i）+ Position(0,0) + Velocity(0,0)
- 跑 1000 帧，每帧 record 一个 `Set<Velocity>` + `Set<Position>`（基于输入），交换 delta replay
- 每帧输出 checksum 是否一致；最后报告总通过率

## Slice 2 任务（Slice 1 跑通后）

- 子弹组件 + `BulletSpawnSystem`：玩家每 5 帧 `Create()` 一颗子弹（验证 placeholder→local id 在多 host 上的正确性）
- `BulletLifetimeSystem`：子弹 60 帧后 `Destroy()`
- 验证 GC 0/0/0（用 `GC.GetTotalAllocatedBytes()` 比较）

## Slice 3 任务（可选）

- 一个 host 故意 record 错误输入 → checksum 不一致 → simulator 触发 `World.Clone()` revert + 重 replay 正确 delta

## 决策记录

### 决策 1：Slice 1 用 Set-only，不 Create
**已定**：先证明 record+snapshot+replay+checksum 流程在所有 host 上字节级一致，再引入 Create 的 placeholder 映射复杂度。

### 决策 2：位移走 record 而非 direct mutate
**已定**：所有变更都通过 CommandStream → Snapshot → Replay 路径。这样所有 host 的 World 状态**只**由 replay 决定，host 本地 record 不碰自己的 World，是真正的 P2P lockstep 模型。

### 决策 3：Slice 1 所有 host 共享一致初始 World（real-id mode）
**已定**：Slice 1 的初始化阶段，**每个 host 各自**在 frame -1 调用相同的 `InitWorld(world, hostCount)` —— 按 hostId=0..N-1 顺序 `world.Create()` 玩家。由于每个 host 都做完全一样的事，所有 host 的 entity id allocator 完全同步，玩家 0 在所有 host 上都是 entity id=1。
Slice 2 切到 placeholder mode（每 host 独立 allocator）再测真正的多 host lockstep 难题。

### 决策 4：用整型不用浮点
**已定**：所有位移/位置用 `int`（毫像素），完全规避 IEEE 754 跨硬件不确定性。这是 demo 的"确定性教学"价值所在。

## 风险

- **跨帧引用 entity**：kb 明确不支持。如果某个系统需要"上一帧的子弹 entity"，必须用 `QueryDescription` 查询，不能用 placeholder 引用。
- **ComponentRegistry 跨 host 同步**：所有 host 在同进程内，ComponentRegistry.Shared 全局共享，自动同步，无需关心。
- **每帧 ReplayCore 的 placeholder map reset**：库内部已处理（`mapLen=0` per frame），demo 不需关心。
