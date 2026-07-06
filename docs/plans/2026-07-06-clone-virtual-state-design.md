# Record-time 虚拟状态 Clone

## Goal

重新设计 `CommandStream.Clone`，使其在 record 调用那一刻读取 source 的**完整逻辑状态**（组件 + 子树）并深克隆到 clone batch。统一 materialized source 与 pending source 的语义：**clone = 调用时刻的快照，之后对 source 的操作不影响 clone**。

解决的问题：
1. 当前 `CloneCore`（`CommandStreamCore.cs:269`）用 `TryGetLocation` 直接拒绝 pending source
2. 当前 `CloneComponents`（`:307`）只读 archetype storage，忽略同批次 pending 的 Add/Set/Remove（反直觉的 bug 制造机）
3. pending source + pending AddChild(materialized child) 的 hierarchy 歧义（曾被视为"死胡同"）

## 背景：为什么 record 时展开能拿到快照

CommandStream 的 record 阶段把命令按时间顺序录入，但分散到不同存储（component store / pending batch / HierarchyByChild / DestroyEntities）。submit 不按 record 时间顺序，而是按类型分阶段（Create→Hierarchy→Ops→Destroy），这是批处理性能选择。

**关键洞察**：record 时展开 Clone，读的时刻就是调用时刻——此刻之后 record 的操作物理上还没进 store/batch，天然读不到。因此 record-time 展开天然实现"调用时刻快照"，零额外机制。延迟到 submit 则读不到快照（submit 分阶段重组了顺序）。

## 方案选择

| 方案 | 组件语义 | hierarchy | wire format | 结论 |
|---|---|---|---|---|
| A 纯延迟 | submit 最终态（无快照） | 绕开死胡同 | 需新增 op | 否决：无快照语义，用户在意 |
| B 纯 record 展开 | 快照 | 死胡同无解 | 不变 | 否决：hierarchy 死胡同 |
| C 混合（组件 record + hierarchy 延迟） | 快照 | 绕开死胡同 | 需新增 op | 否决：语义分裂（root 快照但子树 final-state）、descendant id 分配坑、三条路径全受影响 |
| **D 虚拟状态 record-time** | **快照** | **绕开死胡同** | **不变** | **采用** |

顾问评估否决了 C（语义分裂 + wire format + descendant id 三大坑），提出 D 作为更干净的统一方案。

## 核心机制：虚拟当前状态

Clone 展开时构造两层"虚拟视图"。

### 虚拟组件状态

| source 形态 | base | overlay | 合并 |
|---|---|---|---|
| materialized（TryGetLocation 成功） | archetype storage 组件+值 | 遍历所有 component store 找 source 的 entries（此刻已 record），按 store 内顺序 last-wins | Set 覆盖值 / Remove 删类型 / Add 增类型+值 |
| pending（TryGetPendingBatch 成功） | — | batch 链表 effective state（跳过 Removed，last-wins 去重） | 复用 `MaterializeFromBatchBuffer`（`:930`）的遍历逻辑 |

### 虚拟 hierarchy view

```
虚拟 children(source) = world真实children(source)
                      ∪ { pending AddChild 中 parent=source 的 child }
                      − { pending RemoveChild 中 parent=source 的 child }
```

- Clone 开始时**一次性扫描 `HierarchyByChild` 构建临时 parent→children 反向索引**（仅 clone 路径开销）
- 每个虚拟 child 递归 Clone（虚拟组件 + 虚拟子树）
- **防环**：遍历时记录已访问集，检测到环抛错

### 死胡同化解

`pending father + AddChild(father, materialized son)`：
- 虚拟视图里 son 是 father 的 child（intent 提前生效）
- 深克隆 son → son2（读 son 的 archetype storage，record-time 快照）
- father2 → son2，father(submit后) → son。每实体一个父亲。不是共享引用、不是丢失、不是擅自复制（用户 AddChild 明确声明了归属）

## 数据流（三条路径都不变）

```
record: Clone(source) → 读虚拟状态 → 展开成标准命令写入 batch/component store
         (Create clone + 组件 + AddChild clone descendants)
submit:  不变（clone 已是标准 pending 命令）
snapshot:不变（clone 已展开，走现有 Create/组件/AddChild wire）
wire:    不变（无新 op）
```

决定性优势：单一机制、零 wire format 变更、三条路径统一。

## 错误处理与边界

| 场景 | 行为 |
|---|---|
| stale source（既非 alive 也非 pending） | 抛错 |
| 同批次先 Destroy(source) 再 Clone(source) | 抛错（source 在 DestroyEntities 或 batch canceled） |
| Clone(source) 后 Destroy(source) | clone 已写入 batch，不受影响；submit 后 clone 存活、source 销毁 |
| 虚拟 hierarchy 环 | 检测 + 抛错 |
| 同组件多次操作 | last-wins（和 submit ApplyToWorld 对齐） |
| Destroy 后的临终克隆需求 | 调换顺序 Clone(x); Destroy(x); |

## 并发限制

ParallelCommandStream 的 component store 用 ThreadLocal 锁外 append，clone-time snapshot 不保证看到并发写。**契约：parallel recording 中同一 source 的 Clone 与并发组件写冲突由用户避免**。第一版只实现单线程 CommandStream 的完整语义，parallel 限制写入文档。

## 实现拆解

| 改动点 | 估计 |
|---|---|
| `CloneCore` 放宽：允许 pending source + Destroy 检测 | 小 |
| 组件 pending batch copy（复用 MaterializeFromBatchBuffer 思路） | ~80-150 LOC |
| 组件 materialized overlay scan（全扫描 store，O(帧内 entries)/clone） | ~180-300 LOC |
| hierarchy 虚拟视图：反向索引 + 合并 + 防环 + 递归深克隆 | 中等 |
| 不做索引优化（先全扫描，跑 perf 再决定） | — |

性能约束：clone 路径承担扫描成本，Add/Set/Remove/Submit 零额外开销。

## 测试矩阵

**materialized source（overlay 合并）**：Set/Remove/Add 后 Clone 反映操作；clone 后改 source 不影响 clone（快照）；Destroy 后 Clone 抛错；Clone 后 Destroy clone 存活

**pending source**：Create+Add/Remove 后 Clone last-wins 正确；组件 Entity 字段引用 placeholder 自动解析

**hierarchy**：materialized 子树深克隆；pending AddChild(materialized child) 后 Clone 得 child 副本且原 child 单父亲；pending AddChild(pending child) 后 Clone child 深克隆；RemoveChild 后 Clone 子树排除；clone 后 AddChild(source) 不影响 clone；虚拟环抛错

**跨路径收敛**：Submit vs Snapshot+Replay（DeferredEntities true/false）；SubmitAndSnapshotAsync

**门禁**：`dotnet test -c Release` + `HeroComing.Perf --check-baseline`

**翻转测试**：`Clone_ignores_pending_source_remove_in_same_buffer` 预期翻转（clone 从保留 Position 变为不含 Position），改名 + 改断言

## 知识页更新

- `kb-command-stream.md` 坑点段：Clone 语义变更（从"只读 archetype storage"到"虚拟状态快照"）
- `kb-design-rationale.md`：记录否决 A/B/C 的理由
- `kb-code-review-findings.md`：如有新 bug 发现按流程写入

## 风险

- virtual hierarchy traversal 防环必须严格（pending intent 可能形成 submit 时才被 World.AddChild 拒绝的环）
- materialized overlay scan 的性能需 perf 验证（全扫描 store）；若 Clone 是热点再考虑 lazy index
- ParallelCommandStream 语义弱化需明确文档
