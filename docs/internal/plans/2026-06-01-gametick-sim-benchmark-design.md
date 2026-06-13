# GameTickSim 基准测试设计

## 问题

现有基准测试偏微基准（单一操作、少组件 <5），无法反映真实游戏的混合操作场景。
DefaultEcs 对比显示：Set/CreateDestroy 等单操作 DefaultEcs 更快，但 archetype 模型在查询/批量读写上占优。
需要一个综合场景来验证全貌。

## 设计

### 结构

```
perf/GameTickSim.Perf/
  GameTickSim.Perf.csproj
  Program.cs                    -- 入口：跑测试、输出报告、更新知识库
  TickRunner.cs                 -- 引擎特定的 tick 实现

shared/MiniArch.SharedInfrastructure/
  GameTickComponents.cs          -- 50 组件类型、Archetype 枚举、Entity 分布
```

### 50 组件类型

- Tag/标记 5 个（空 struct）
- 单值 20 个（int/float）
- 双值/中等 15 个（2-4 字段）
- 大型 5 个（Matrix4x4, [int × 8] 等）
- 共享 5 个（entity ref 数组等）

### 12 种 Archetype

Player(20), MeleeEnemy(18), RangedEnemy(18), BossEnemy(22), NPC(10),
Pet(12), Projectile(6), StaticObject(8), Destructible(12),
Environment(5), Trap(8), LootDrop(7)

### 12 系统/tick

| # | 类型 | Query | 操作 |
|---|------|-------|------|
| 1 | Create | — | 造 50 entity |
| 2 | Set | With(P,V) | Position += Velocity |
| 3 | Set | With(AIState) | 更新 AI timer |
| 4 | AddRemove | With(Health) | +/- Buff |
| 5 | Set | With(Health,Damage) | 扣血 |
| 6 | Add | With(Health≤0) | Add DeadTag |
| 7 | Destroy | With(DeadTag) | 销毁 ~20 |
| 8 | Read | With(P,Faction) | 遍历 |
| 9 | Read | With(P,Size) | 遍历 |
| 10 | Set | With(Health,Mana) | 回血 |
| 11 | Read | With(Buff) | 读到期 |
| 12 | AddRemove | With(Buff到期) | Remove Buff |

### 测量

- 指标：ticks/second（固定时长 5s）
- GC 监控：Gen0/1/2、heap delta
- 验证：跨引擎 checksum 一致
- 输出：终端表格 + 知识库更新

### 实现策略

- MiniArch: 结构变更用 CommandBuffer, Set/Read 直接操作
- Arch: 结构变更用 Arch CommandBuffer
- DefaultEcs: 即时操作

## 设计决策

1. WithAll 语义查询——更贴近真实游戏
2. 每 tick 固定 spawn/destroy 数量——为了可复现
3. 报告包含内存指标——发现内存泄漏
4. 与 HeroComing.Perf 同级别——门禁回归检查
