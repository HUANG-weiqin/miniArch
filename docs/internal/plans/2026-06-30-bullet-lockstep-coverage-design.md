# BulletLockstep 长期覆盖设计

> Branch: `feat/bullet-lockstep-demo` · 目标：把 miniArch 库的**所有公共能力**用真实弹幕游戏场景端到端压一遍。

## 1. 为什么要做这个设计

Slice 1-3 已经端到端验证了**多 host lockstep 框架契约**（placeholder Create、CanonicalChecksum、CaptureState/RestoreState）。但离"压测库的确定性"还差很远 —— 目前只走了 1 个 archetype、240 entities、2 个简单系统，库一大半能力没碰到。

本设计的目标：**用一套真实可玩的弹幕游戏，把 README 列出的每个 feature 都映射到具体游戏机制**，确保覆盖不留死角。

## 2. 覆盖矩阵（库能力 → 测试 slice → 触发机制）

> 更新于 2026-06-30，9 个 slice 全部跑通后。

| 库能力 | 已覆盖 | 触发 slice | 触发机制 |
|---|---|---|---|
| **Archetype ECS**（World/Entity/QueryDescription） | ✅ | S2-S9 | 多 archetype 查询遍布所有系统 |
| **CommandStream**（record） | ✅ | S2-S9 | Create/Add 在每个 slice；Set 在 S6 命中扣血 |
| **FrameDelta + Replay** | ✅ | S1-S9 | placeholder Create/Set/Add/AddChild/Destroy op 全部走通 |
| **World.Clone()**（深拷贝 World） | ✅ | S9 Phase A+C | 独立 world 前向运行 + replay-buffer 回滚 |
| **WorldSnapshot**（二进制序列化） | ✅ | S8 Phase A | MemoryStream round-trip 字节级无损 |
| **SubmitAndSnapshotAsync()**（流水线 submit+delta） | ✅ | S8 Phase B | Authority + Mirror 拓扑 |
| **Query filtering**（With/Without/WithAny） | ⚠️ With/Without 全用，**WithAny 未用** | S2-S9 | 多 bullet archetype 查询；WithAny 待补 |
| **Parallel iteration**（ForEachChunkParallel） | ✅ | S7 | BulletMoveSystem 升级为并行 |
| **Entity accessor**（Access()） | ✅ | S4 S6 | TickDamageSystem + CollisionSystem 多组件批量读写 |
| **Ref-return access**（GetRef / chunk span） | ✅ | S2+ | chunk span 遍布系统 |
| **Batch Creation**（CreateMany） | ❌ **未直接测** | - | S7 用了多次 Stream.Create 替代；World.CreateMany API 未触发 |
| **Entity Hierarchy**（AddChild/RemoveChild + cascade destroy） | ✅ | S5 | Boss + 5 WeakPoint AddChild，cascade destroy 验证 |
| **GC-friendly**（0/0/0 稳态） | ⚠️ 12/0/0 standard，scale 模式 Gen2 | - | FrameDelta[] 已 pool，checksum byte[] 待 pool |
| **Archetype 迁移**（Add/Remove on existing） | ✅ | S4 | BurningTimer + Shield Add/Remove 完整循环，4 变体并存 |
| **FrameDelta.Merge** | ✅ | S9 Phase B | 20 delta 合并为 1，replay 等价于逐帧 |
| **Chunked storage**（多 segment archetype） | ✅ | S7 scale | 36K 实体触发 chunked 切换，checksum 一致 |
| **跨帧 entity 引用 workaround**（kb 决策 #3） | ✅ | S5 S6 | Target(int HostId)、FiredBy(int HostId) 查询定位 |

### 仍未覆盖（待补 slice）

- **WithAny 查询**：当前所有查询都用 With/Without。WithAny 用于「至少有 N 个组件之一」的 OR 匹配，可在未来 slice 加「状态效果系统」时一并测试（query WithAny<BurningTimer, FrozenTimer, PoisonTimer>）
- **World.CreateMany**：S7 用 `Stream.Create × N` 替代，未直接调用 immediate API。可在 Slice 8 authority 模式下补一个 boss pattern 用 `World.CreateMany` 批量创建（real-id 路径）
- **0/0/0 GC**：standard 模式仍有 Gen0 ~12 次/1000 帧（来自 checksum byte[] + 偶发 List 扩容）。优化点：checksum byte[] 用 stackalloc 或 ArrayPool、CollisionSystem 的 HashSet 用排序去重替代

## 3. 游戏设计

### 3.1 角色与组件清单

> 命名约定：长生命周期 entity 用 Tag 组件标识，瞬态用 SpawnFrame 标识。

**玩家**（长生命周期，每 host 一个）
- `PlayerTag(HostId)`、`Position`、`Velocity`、`Health(int Cur, int Max)`
- 可变附加：`Shield(int)`（护盾）、`PowerupState(int Type, int Remaining)`（道具 buff）

**Boss**（长生命周期，权威 host 创建）
- `BossTag`、`Position`、`Health`、`AIPattern(int PatternId, int Phase)`
- 通过 `AddChild` 挂载多个 `WeakPoint`

**WeakPoint**（长生命周期，Boss 子节点）
- `WeakPointTag`、`Position`、`Health`、`LocalOffset(int Dx, int Dy)`（相对 Boss）

**子弹类型**（瞬态，确定性系统销毁）—— 多 archetype 触发查询过滤
| 类型 | 组件 | 触发的库特性 |
|---|---|---|
| BasicBullet | `BulletTag`、`Position`、`Velocity`、`Damage`、`SpawnFrame` | 基础 archetype |
| BurningBullet | Basic + `BurningTimer(int Remaining)` | **Add/Remove archetype 迁移**：timer 到期 → Remove → 退化为 Basic |
| HomingBullet | BulletTag + Position + Velocity + `Target(int HostId)` + `TurnRate` | 不同 archetype，触发 WithAny 过滤 |
| LaserBullet | BulletTag + Position + `Direction` + `Length` + Damage | 无 Velocity 的不同 archetype |

**Powerup**（瞬态道具）
- `PowerupTag`、`Position`、`Velocity`、`PowerupType`

**特效**（瞬态，纯视觉）—— 测大规模瞬态 spawn
- `ExplosionTag`、`Position`、`SpawnFrame`

### 3.2 游戏机制 → 库特性映射

| 游戏机制 | 库特性 | 实现要点 |
|---|---|---|
| 玩家移动 | 确定性系统 + Set / chunk span | 已在 Slice 1-2 实现 |
| 子弹飞行 | chunk span 直写 | 已实现，S7 升级为 ForEachChunkParallel |
| 子弹击中玩家 → 扣血 + 销毁子弹 | **Set + Destroy 跨 archetype** | S6 空间网格碰撞，扣 HP（Set），销毁子弹（Destroy） |
| 燃烧弹击中 → 玩家 Add `<Burning>` | **Add 触发 archetype 迁移** | S4 状态系统 |
| 燃烧计时到期 → Remove `<Burning>` | **Remove 触发 archetype 迁移** | S4 状态系统 |
| 护盾道具拾取 → Add `<Shield>` | **Add 迁移** | S4 |
| 护盾耗尽 → Remove `<Shield>` | **Remove 迁移** | S4 |
| Boss 死亡 → 子节点 weakpoint 全部销毁 | **Hierarchy cascade destroy** | S5 |
| Boss 切换 AI phase → 子弹 pattern 改变 | **Set + 状态机** | S5 |
| Boss 一次发射圆形弹幕 100 发 | **CreateMany batch** | S7 |
| Boss 发射追踪弹（Target 指向某 host 玩家） | **WithAny 查询 + 跨 host 引用 workaround** | S5 用 `Target(int HostId)` |
| 全场子弹位移（10K+） | **ForEachChunkParallel + chunked storage** | S7 |
| 玩家受伤后 HP 减少等 Set 风暴 | **Set-heavy workload** | S6（CommandStream 主战场） |
| 玩家受伤特效 spawn（大量瞬态 Explosion） | **大规模 Create** | S7 |
| 玩家状态多组件读写（HP+Shield+Powerup） | **EntityAccessor.Access()** | S4 |
| 游戏存档到磁盘 + 读档 | **WorldSnapshot binary** | S8 |
| 权威 host 转发状态给镜像 | **SubmitAndSnapshotAsync** | S8 |
| 回滚 replay buffer（预测 N 帧） | **World.Clone()** | S9 |
| 网络优化：3 帧 delta 合并发送 | **FrameDelta.Merge** | S9 |

### 3.3 系统清单（确定性后处理）

每个系统在所有 host 上**以相同顺序**运行：

| 系统 | 输入 | 输出 | 触发库特性 |
|---|---|---|---|
| PlayerMoveSystem | query PlayerTag + Velocity | chunk span 写 Position | GetRef / span |
| BulletMoveSystem | query BulletTag + Velocity | span 写 Position | S7 改 ForEachChunkParallel |
| HomingBulletSteerSystem | query Target + Velocity | 改 Velocity 朝向 Target host 玩家 | WithAny 过滤 |
| StatusTimerSystem | query BurningTimer / PowerupState.Remaining | 到期 → schedule Remove | Add/Remove 迁移 |
| CollisionSystem | query BulletTag × PlayerTag（空间网格） | 扣 HP（Set）+ 销毁子弹（Destroy）+ Add `<Burning>` | **跨 archetype 查询 + Set/Destroy/Add 风暴** |
| LifetimeSystem | query SpawnFrame | 超龄 Destroy | 排序保证顺序 |
| BossAISystem | query BossTag + AIPattern | 切 phase + Set AIPattern + spawn 弹幕 | 状态机 + CreateMany |
| ExplosionSpawnSystem | 玩家受伤事件（query Health 变化） | Create `<ExplosionTag>` | 大规模瞬态 |

### 3.4 多 host 拓扑（Slice 8 切换）

- **Slice 1-7：纯 P2P lockstep** —— N host 对等，每 host 独立 World + 独立 allocator，placeholder delta 交换
- **Slice 8：权威服务器 + 镜像客户端** —— 1 个 authority host（DeferredEntities=false，SubmitAndSnapshotAsync）+ M 个 mirror host（从 frame 0 完整 replay real-id delta）。**两种拓扑都跑 checksum 一致性校验**。

## 4. Slice 划分与覆盖目标

每个 slice 必须：
1. 端到端可跑（1000 帧 fuzz），所有 host checksum 字节级一致
2. 触发该 slice 重点的库能力
3. 不破坏前面的 slice（增量叠加）
4. 有明确的"何时算完成"验证条件

### Slice 4 — 状态系统与 archetype 迁移
**重点**：Add/Remove 触发 archetype 迁移、EntityAccessor、多 archetype 查询过滤
- 燃烧弹击中玩家 → Add `<Burning>` + BurningTimer
- StatusTimerSystem：到期 Remove `<Burning>`
- 护盾道具拾取/耗尽 → Add/Remove `<Shield>`
- 玩家受伤系统改用 `World.Access(player)` 读写 HP + Shield
- **完成条件**：游戏过程中至少有 4 个不同 archetype 的玩家变体（裸玩家 / +Shield / +Burning / +Shield+Burning）并存，checksum 一致

### Slice 5 — Hierarchy 与 Boss
**重点**：AddChild/RemoveChild、cascade destroy、复杂状态机
- Boss + 多个 WeakPoint 通过 `AddChild` 挂载
- BossAISystem 切换 phase、发射追踪弹（Target = 某玩家 HostId）
- Boss 死亡 → cascade destroy 所有 weakpoint
- 玩家死亡 → RemoveChild 父子关系（如适用）
- **完成条件**：Boss 持续 spawn 弹幕并切换 phase，最终死亡时 cascade 销毁 5+ weakpoint，checksum 一致

### Slice 6 — 真实碰撞
**重点**：跨 archetype 查询、Set/Destroy 风暴、多 host 引用 workaround
- 确定性空间哈希网格（按整型坐标分桶）
- BulletTag × PlayerTag 全配对查询 + 距离判定
- 命中：`Set<Health>`（扣血）+ `Destroy`（销毁子弹）+ 可能 `Add<Burning>`（如果是燃烧弹）
- 玩家死亡 → 移除 PlayerTag / 触发 Explosion spawn
- **完成条件**：玩家每帧平均受 10+ 次命中（Set+Destroy 混合），checksum 一致；GC 0/0/0

### Slice 7 — 规模与并行
**重点**：CreateMany、ForEachChunkParallel、chunked storage
- Boss pattern 切到"环形弹幕"：`CreateMany(100)` 一次发射 100 颗子弹
- BulletMoveSystem 升级为 `ForEachChunkParallel`
- 实体总数推到 10K-50K（触发 chunked storage 多 segment 路径）
- Pool FrameDelta[] 与 checksum byte[] 把 GC 推到 0/0/0
- **完成条件**：30K+ entities 稳态，单帧 < 5ms，GC 0/0/0，checksum 一致

### Slice 8 — 持久化与权威拓扑
**重点**：WorldSnapshot、SubmitAndSnapshotAsync
- 模式 A：游戏中段 `WorldSnapshot.Save` 到内存 stream → 新建 host `Load` → 验证 checksum 一致
- 模式 B：1 个 authority host 用 `DeferredEntities=false` + `SubmitAndSnapshotAsync`，M 个 mirror host 从 frame 0 完整 replay real-id delta
- **完成条件**：两种模式下，所有 host checksum 一致；存档 round-trip 字节级无丢失

### Slice 9 — 高级 netcode
**重点**：World.Clone()、FrameDelta.Merge
- Replay buffer：每 host 维护最近 60 帧的 `World.Clone()` 快照，预测 → 校验失败 → Clone revert + 重 replay
- 网络优化：每 3 帧把 3 个 FrameDelta `Merge` 成 1 个发送，远端一次性 replay
- **完成条件**：Merge 后的 delta replay 与逐帧 replay 产生相同世界状态（checksum 一致）；预测失败后回滚恢复

## 5. 全局验证策略

每个 slice 跑三种 fuzz：

| 模式 | 帧数 | host 数 | 验证 |
|---|---|---|---|
| **Smoke** | 100 | 2 | 快速反馈，checksum 一致 |
| **Standard** | 1000 | 4 | 默认，checksum + GC budget |
| **Scale**（S7+） | 1000 | 4-8 | 30K+ entities，单帧 < 5ms，GC 0/0/0 |

**预算**（随 slice 推进收紧）：
- Slice 4-6：GC ≤ 50/0/0，allocation ≤ 50KB/frame
- Slice 7+：GC = 0/0/0，allocation ≤ 5KB/frame（接近真实游戏预算）

## 6. 不做（YAGNI 边界）

- ❌ 渲染（永远不做，console checksum 输出即可）
- ❌ 真实网络栈（in-process byte[] 交换）
- ❌ 跨进程测试（同进程多 World 实例已足够）
- ❌ 完整游戏循环（无主菜单、无 scoring、无关卡设计）
- ❌ 输入设备接入（用伪随机 + 脚本化 input）

## 7. 已知风险

| 风险 | 缓解 |
|---|---|
| Archetype 迁移破坏 placeholder 跨帧引用 | 严格遵守 kb 决策 #3：跨帧引用走组件查询，不用 placeholder handle |
| chunked storage 多 segment 在多 host 上一致性 | 依赖库的 chunked CaptureState（kb-chunk-storage 已有测试），demo 只验证端到端 |
| ForEachChunkParallel 写入组件 vs CommandStream 并发 record | 平行模式只跑后处理系统（无 record），ParallelRecording 不开 |
| WorldSnapshot 跨 host 字节级一致 | snapshot 是状态序列化，不含 id allocator 信息，需注意 frame 0 同步语义 |
| 大规模 Set/Destroy 在确定性顺序上抖动 | 所有"收集-修改"型系统强制按 entity id 排序后再应用 |

## 8. 完成定义（整体项目）

- 所有 slice 1-9 跑通 standard 模式（1000 帧 × 4 host 全 checksum 一致）
- 覆盖矩阵第 2 节每个 ❌ 转为 ✅
- `.knowledge/` 新增 1 个知识页总结多 host 弹幕 demo 的 lockstep 用法与坑点
- README 链接 demo 作为可运行示例
