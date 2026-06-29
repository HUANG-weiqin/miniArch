# BulletLockstep 长期实施 Plan

> 配套设计文档：`2026-06-30-bullet-lockstep-coverage-design.md`（先读那篇理解覆盖目标）
> 每个 slice 严格按顺序做，做完跑完三档验证再进下一个。
> 已完成 Slice 1-3（commits `ee92510` / `f5679be`）。

## 通用工作流（每个 slice 都遵守）

1. 起一个 task：新增组件/系统/host 行为
2. 改 `Program.cs` 加 slice 模式入口（沿用 `args[0]` = slice 号）
3. 跑三档验证：
   - **Smoke**：`dotnet run -c Release -- <slice> 2 100`
   - **Standard**：`dotnet run -c Release -- <slice> 4 1000`
   - **Scale**（S7+）：`dotnet run -c Release -- <slice> 8 1000`
4. 全部 PASS 才 commit
5. 更新设计文档覆盖矩阵 + 本 plan 的 ✅ 标记

## Slice 4 — 状态系统与 archetype 迁移

**目标**：让玩家/子弹在多 archetype 间迁移，覆盖 Add/Remove + EntityAccessor。

### Task 4.1 — 新增状态组件
- 文件：`Components.cs`
- 加：`BurningTimer(int Remaining)`、`Shield(int Cur, int Max)`、`PowerupState(int Type, int Remaining)`
- 玩家 archetype 变体：base / +Shield / +BurningTimer / +Shield+BurningTimer（4 个组合）

### Task 4.2 — 重写玩家受伤为 EntityAccessor
- 文件：`Systems/PlayerDamageSystem.cs`（新）
- 用 `World.Access(playerEntity)` 一次拿 Health + Shield 引用，扣血先走 Shield 再走 Health
- 验证：单 host 跑通，无编译错误

### Task 4.3 — 状态计时系统（Remove 触发迁移）
- 文件：`Systems/StatusTimerSystem.cs`（新）
- query `BurningTimer` 和 `PowerupState`，递减 Remaining；到 0 → `world.Remove<BurningTimer>(e)`
- **关键**：Remove 是结构性变更，会触发 archetype 迁移 → 验证迁移后查询仍能找到该 entity
- 收集要 Remove 的 entity → 按 entity id 排序 → 依次 Remove（保证多 host 顺序一致）

### Task 4.4 — Burning 注入（Add 触发迁移）
- 改 `LockstepHost.RecordFrame`：每 host 每 K 帧开火一颗燃烧弹（带 `BurningTimer` + `BulletTag` 标记）
- 改命中系统（如果已有）/ 加 mock：玩家被燃烧弹击中 → `world.Add<BurningTimer>(player, …)`
- 用 placeholder Add 在 record 阶段记录"燃烧弹创建"，replay 时各 host 映射 → 命中后用确定性系统 Add 到玩家
- 验证：游戏过程出现 4 个 archetype 变体并存，Standard 模式 1000 帧 checksum 一致

### Task 4.5 — Shield 道具（Add/Remove 完整循环）
- 加 `PowerupTag` 实体（瞬态），玩家拾取 → `world.Add<Shield>` + 设 powerup 倒计时
- Shield 归零（被命中扣完或 PowerupState 到期）→ `world.Remove<Shield>`
- 验证：玩家 archetype 在 base/+Shield 间反复横跳，1000 帧 checksum 一致

**Slice 4 完成条件**：
- [ ] Smoke/Standard 全 PASS
- [ ] 至少 4 个玩家 archetype 变体并存
- [ ] Add/Remove 在 record 路径和确定性系统路径都走过
- [ ] EntityAccessor 至少在 1 个系统用上

## Slice 5 — Hierarchy 与 Boss

**目标**：覆盖 Link/Unlink + cascade destroy + 复杂状态机 + WithAny 查询。

### Task 5.1 — Boss 与 WeakPoint 组件
- `Components.cs` 加：`BossTag`、`AIPattern(int Id, int Phase)`、`WeakPointTag`、`LocalOffset(int Dx, int Dy)`
- authority host（HostId=0）创建 1 个 Boss + 5 个 WeakPoint
- 用 `world.Link(boss, weakpoint)` 建立 5 个父子关系

### Task 5.2 — Boss AI 状态机系统
- `Systems/BossAISystem.cs`（新）：query BossTag + AIPattern，按 frame 推进 phase
- 每 phase 切换弹幕 pattern（暂时只换 SpawnFrame 频率，S7 再加 CreateMany）
- Set AIPattern 字段

### Task 5.3 — 追踪弹（WithAny 跨 archetype 查询）
- 新 bullet archetype：HomingBullet = BulletTag + Position + Velocity + `Target(int HostId)` + `TurnRate`
- HomingBulletSteerSystem：query WithAny<Target>，按 Target host 玩家的当前位置调整 Velocity 方向
- 跨 host 引用：用 `Target(int HostId)` 而非 entity 引用（遵守 kb 决策 #3）
- 验证：HomingBullet 与 BasicBullet 共存，WithAny 过滤命中正确

### Task 5.4 — Hierarchy delta 路径
- 在 `LockstepHost.RecordFrame` 里加 1 个 Link op（如道具挂到玩家），Snapshot 输出 Link delta
- 验证：placeholder Link 在所有 host replay 后都建立正确父子关系

### Task 5.5 — Cascade destroy
- Boss 死亡（Health <= 0）：`world.Destroy(boss)` → 库自动 cascade 销毁 5 个 WeakPoint
- 验证：所有 host 上 Boss + 5 WeakPoint 同时消失，checksum 一致

**Slice 5 完成条件**：
- [ ] Smoke/Standard 全 PASS
- [ ] Link/Unlink 在 record 和直接调用两条路径都走过
- [ ] cascade destroy 触发 ≥ 5 个子节点同时消失
- [ ] WithAny 查询至少有 1 处真实使用

## Slice 6 — 真实碰撞

**目标**：跨 archetype 配对查询 + Set/Destroy 风暴 + 空间网格。

### Task 6.1 — 确定性空间哈希网格
- `Systems/SpatialGrid.cs`（新）：按 `int` 坐标分桶（cellSize = 10000 milli-pixel）
- 同 cell 内 O(k²) 配对（k 一般很小）
- **关键**：桶内顺序按 entity id 排序，保证多 host 一致

### Task 6.2 — Bullet × Player 碰撞系统
- `Systems/CollisionSystem.cs`（新）：
  - 每 frame 重建网格（query BulletTag 填入）
  - query PlayerTag，每个玩家查所在 cell + 8 邻居的所有子弹
  - 距离 < radius → 命中
- 命中后：
  - `world.Set<Health>(player, …)` 或通过 Access 批量改
  - `world.Destroy(bullet)`
  - 若是燃烧弹 → `world.Add<BurningTimer>(player, …)`
- 命中应用按 (bulletId, playerId) 排序，保证多 host 一致

### Task 6.3 — 玩家死亡处理
- Health <= 0 → `world.Remove<PlayerTag>(player)`（让玩家不再参与碰撞，但保留 entity）
- 或直接 Destroy 玩家（看 S5 是否需要保留作 Boss target）
- 触发 Explosion spawn（Task 7.x 再做大规模）

**Slice 6 完成条件**：
- [ ] Smoke/Standard 全 PASS
- [ ] 平均每帧 ≥ 10 次 Set + Destroy 命中
- [ ] 碰撞顺序按 entity id 排序，多 host 一致
- [ ] 燃烧弹命中正确触发 archetype 迁移（与 S4 联动）

## Slice 7 — 规模与并行

**目标**：CreateMany + ForEachChunkParallel + chunked storage + GC 推到 0/0/0。

### Task 7.1 — CreateMany 弹幕 pattern
- `Systems/BossPatternSystem.cs`（新）：Boss 进入 ring phase → `world.CreateMany(count, …)` 或在 record 阶段批量 Create
- 注意：placeholder 模式下 CreateMany 也要支持，验证库行为
- 一次发射 100-500 颗子弹

### Task 7.2 — BulletMove 升级为并行
- `Systems/BulletMoveSystem.cs`：单 chunk fast-path → 多 chunk 用 `ForEachChunkParallel`
- 验证：写入 span 是线程安全的（不同 chunk 不同线程）；无 structural change

### Task 7.3 — 触发 chunked storage
- 单 archetype 实体数推到 > chunk 阈值（看 kb-chunk-storage.md 的具体值，约几千）
- 验证：chunked CaptureState 在多 host 上一致

### Task 7.4 — Pool 帧级分配
- FrameDelta[] 复用（simulator 持有 1 个 buffer，每帧重用）
- checksum byte[] 复用（用 stackalloc 或 ArrayPool）
- 目标：standard 模式 GC 0/0/0

**Slice 7 完成条件**：
- [ ] Scale 模式（30K+ entities）单帧 < 5ms
- [ ] GC 0/0/0 standard 模式
- [ ] chunked storage 路径触发并 checksum 一致
- [ ] ForEachChunkParallel 至少在 BulletMoveSystem 用上

## Slice 8 — 持久化与权威拓扑

**目标**：WorldSnapshot + SubmitAndSnapshotAsync + 双拓扑都跑。

### Task 8.1 — WorldSnapshot round-trip
- 中段 `WorldSnapshot.Save(stream, world)` → `WorldSnapshot.Load(stream)` 重建 World
- 验证：重建的 World 与原 World `CanonicalChecksum` 字节级一致

### Task 8.2 — 权威服务器 + 镜像客户端
- 新增 `LockstepAuthorityHost.cs`：`DeferredEntities=false`，调 `SubmitAndSnapshotAsync` 得 real-id delta
- 新增 `LockstepMirrorHost.cs`：从 frame 0 完整 replay real-id delta，不本地 record
- 验证：authority + 3 mirror 的 checksum 一致

### Task 8.3 — 双拓扑一致性交叉验证
- 同一组 input 跑两遍：
  - P2P lockstep（Slice 1-7 模式）
  - Authority + Mirror（Slice 8 模式）
- 验证：两者最终 World 状态逻辑等价（CanonicalChecksum 一致）

**Slice 8 完成条件**：
- [ ] WorldSnapshot round-trip 字节级无损
- [ ] Authority + Mirror 模式 checksum 一致
- [ ] SubmitAndSnapshotAsync 真正用上（pipelined path）

## Slice 9 — 高级 netcode

**目标**：World.Clone() replay buffer + FrameDelta.Merge。

### Task 9.1 — Replay buffer via World.Clone
- 每 host 维护最近 60 帧的 `World.Clone()` 深拷贝
- 模拟预测：本地 record + Submit（不走 Snapshot 路径）→ 校验外部权威 delta → 不一致则从 buffer 取最近一致帧 Clone → 重 replay

### Task 9.2 — FrameDelta.Merge 网络优化
- 每 3 帧把 3 个 placeholder delta `Merge` 成 1 个发送
- 远端一次性 replay 合并后的 delta
- 验证：Merge 后的 delta replay 与逐帧 replay 产生相同 checksum

### Task 9.3 — Slice 3 升级为真实预测回滚
- 把 Slice 3 的 CaptureState/RestoreState 改造为 World.Clone() 路径
- 模拟 host 0 抢跑 N 帧 → 收到真实 delta → Clone revert → replay

**Slice 9 完成条件**：
- [ ] World.Clone() 在 replay buffer 真实用上
- [ ] FrameDelta.Merge 后的 replay 等价于逐帧 replay
- [ ] 预测失败 → 回滚恢复链路完整

## 收尾任务

### Task F1 — .knowledge 知识页
- 文件：`.knowledge/kb-bullet-lockstep-demo.md`
- 模板：`.knowledge/_template.md`
- 内容：多 host 弹幕 demo 的 lockstep 用法总结 + 踩过的坑（如跨帧 entity 引用、archetype 迁移顺序、chunked storage 一致性）
- 更新 `.knowledge/INDEX.md` 加新条目

### Task F2 — README 链接
- 主 README.md 加 demo 入口链接到 `samples/BulletLockstep.Demo/`
- 一句话说明：可运行的多 host 帧同步 demo

### Task F3 — 覆盖矩阵复核
- 检查 `2026-06-30-bullet-lockstep-coverage-design.md` 第 2 节所有 ❌ 转 ✅
- 漏的补 slice 或显式标"不做+原因"

## 进度追踪

| Slice | 重点 | 状态 | Commit |
|---|---|---|---|
| 1 | Placeholder Create pipeline | ✅ | `ee92510` |
| 2 | 确定性后处理系统 | ✅ | `ee92510` |
| 3 | CaptureState/RestoreState 回滚 | ✅ | `f5679be` |
| 4 | Archetype 迁移 / EntityAccessor | ⏳ | - |
| 5 | Hierarchy / Boss / WithAny | ⏳ | - |
| 6 | 真实碰撞 / Set+Destroy 风暴 | ⏳ | - |
| 7 | 规模 / 并行 / 0 GC | ⏳ | - |
| 8 | WorldSnapshot / 权威拓扑 | ⏳ | - |
| 9 | World.Clone / FrameDelta.Merge | ⏳ | - |
| F | .knowledge / README | ⏳ | - |

## 风险与决策点

- **Slice 4-5 之间可能有依赖**：如果 Slice 5 的 Boss 战需要燃烧弹效果，得先做 S4 的状态系统。按编号顺序做最稳。
- **Slice 6 碰撞系统的性能**：30K 子弹 × N 玩家碰撞，O(n²) 不可行，必须空间网格。如果 Slice 7 规模上不去，可能要在 S6 就上空间网格。
- **Slice 7 chunked storage 阈值**：得查 `kb-chunk-storage.md` 确认阈值，可能需要人为压实体数才能触发。
- **Slice 8 双拓扑 checksum 一致性**：P2P 用 placeholder id，Authority+Mirror 用 real id。CanonicalChecksum 应该忽略 id 差异（按设计），但需实测。
- **Slice 9 World.Clone 成本**：60 帧深拷贝可能很重，需要测性能。如果不达标，改为环形 CaptureState buffer。
