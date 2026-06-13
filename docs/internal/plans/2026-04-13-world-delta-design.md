# MiniArch Command Buffer World Delta Design

## 结论

- 新增双向 `WorldDelta`，以 entity 为粒度记录 `Before/After` 两端的完整公开状态快照。
- `WorldDelta` 的公开状态只覆盖：entity existence、全量组件值、parent 关系，以及这些状态派生出的 query 可见结果。
- `link/unlink` 不是附属命令，而是 entity 公开状态的一部分；delta 只记录最终 parent 状态，不记录中间命令历史。
- `destroy` 在 delta 中按最终公开效果建模：显式 destroy root 触发的 existing subtree 级联销毁，需要展开为最终消失的 existing entities；same-frame create 后又随帧内 destroy 一起消失的临时实体应折叠掉，不出现在 delta 里。
- `WorldDelta` 支持同一个 `World` 实例做 `ApplyForward/ApplyBackward`，也支持在不同但同步基线、实体身份对齐的 `World` 实例之间顺序 apply。
- 第一版不替代现有 `FrameCommands` / `ReverseFrameCommands` 提交链；`FrameCommands` 继续承担底层高效执行 IR，`WorldDelta` 作为更强语义的公开状态差异模型并存。

## 目标

- 让 `CommandBuffer` 能产出一个可保存、可跨实例顺序应用、也可反向回退的双向 `WorldDelta`。
- 让 `WorldDelta` 的 `ApplyForward` / `ApplyBackward` 都以公开状态同态为验收标准，而不是要求 free-list、version 历史、chunk 布局等内部状态完全一致。
- 覆盖 hierarchy、cascade destroy、same-frame transient entity 折叠、多帧顺序同步等关键案例。

## 非目标

- 不把 `WorldDelta` 设计成完整 world snapshot。
- 不要求任意不同 runtime、不同 entity 身份空间之间的映射同步。
- 第一版不把内部 free-list/version/chunk 状态纳入达标条件。
- 第一版不要求 `WorldDelta` 直接替换 `FrameCommands` 的热路径执行实现。

## 数据模型

建议新增：

```csharp
public readonly struct WorldDelta
{
    public IReadOnlyList<WorldDeltaEntry> Entries { get; }
}
```

```csharp
public readonly record struct WorldDeltaEntry(
    Entity Entity,
    WorldEntityPublicState? Before,
    WorldEntityPublicState? After);
```

```csharp
public readonly record struct WorldEntityPublicState(
    Entity Parent,
    IReadOnlyList<FrameComponentValue> Components);
```

语义：

- `Before == null && After != null`：entity 在本帧后公开出现。
- `Before != null && After == null`：entity 在本帧后从公开世界消失。
- `Before != null && After != null`：entity 公开状态发生变化。
- `Before == null && After == null`：非法；same-frame create+destroy 这类净变化为 `0` 的实体应在生成阶段直接折叠掉。

## 公开状态边界

`WorldDelta` 的一条 entry 只承诺这些公开可观察状态：

- `world.IsAlive(entity)`
- entity 的全量组件集合与组件值
- `world.TryGetParent(entity, out parent)` 的结果
- 基于上述状态得到的 query 枚举结果与 hierarchy 可见关系

不承诺这些内部状态：

- free-list 形状
- entity version 历史轨迹
- archetype/chunk 布局
- query cache 与其他中间缓存

## 生成语义

### 候选实体集

`CommandBuffer` 生成 delta 时，候选实体集来自：

- 所有 `CreatedEntities`
- `Add/Set/Remove` 触达的 entity
- `Link/Unlink` 的 child entity
- `DestroyedEntities` 对应的 existing subtree 闭包

destroy 闭包必须基于当前 world 的 hierarchy 读取 existing subtree；同帧通过 `link/unlink/relink` 引入或移出的实体，则需要在 shadow public state 推演阶段体现到最终 destroy 结果里。

### Before

- `Before` 必须从当前 world 的公开状态读取。
- 读取入口应复用 `TryGetLocation(...)`、archetype signature、chunk component 读取、`TryGetParent(...)` 等现有 runtime 能力。

### After

- `After` 不能直接照抄命令列表，而必须按当前 replay 固定顺序在内存中的 shadow public state 上推演。
- 推演顺序保持与现有 runtime 一致：`create -> link/unlink -> add -> set -> remove -> destroy`。
- destroy 阶段必须在 shadow hierarchy 上算最终消失闭包，而不是只删显式 destroy roots。

### 折叠规则

- same-frame `create + destroy` 的临时实体不进入 delta。
- same-frame `create -> link 到 doomed subtree -> 随 subtree 一起消失` 的临时实体不进入 delta。
- `Before` 与 `After` 深度相等的 entity 不进入 delta。

## Apply 语义

同一个 `WorldDelta` 需要支持两个方向：

- `ApplyForward(delta)`：把 world 推进到 `After`
- `ApplyBackward(delta)`：把 world 回退到 `Before`

两者只是读取目标态的方向不同，底层应用顺序保持一致：

1. materialize 所有目标态为 alive 且当前不存在的 entities
2. 让所有目标态为 alive 的 entities 拥有精确目标组件集
3. 应用这些存活 entities 的 parent 目标状态
4. 销毁所有目标态为 `null` 的 entities

这样可以同时覆盖：

- forward create / backward destroy
- forward destroy / backward restore
- forward / backward 的 link、unlink、relink

## 跨实例前提

`WorldDelta` 可以应用到不同 `World` 实例，但前提是：

1. 两个 world 运行在同类 runtime 语义下
2. 应用前的公开状态基线兼容
3. delta 中引用到的 entity handle 在目标 world 中代表同一公开身份
4. delta 引用到的外部 parent 等未变化实体在目标 world 中也对齐存在

第一版不解决 entity remap 问题，直接把 entity handle 对齐作为契约前提。

## 与现有 FrameCommands 的关系

- `FrameCommands` 继续保留为底层 forward IR。
- `WorldDelta` 作为更高层、可逆的公开状态差异模型，不要求马上替换 `Replay(frame)` 的底层实现。
- `CommandBuffer` 新增 `PlaybackDelta()` 之类的只读编译入口时，推荐内部流程为：
  - `Compile()`
  - `ToFrameCommands()`
  - `world.CaptureDelta(in frame)`
  - `Clear()`

这样可以在不公开改变 source world 状态的前提下，从 command buffer 产出 delta。

## 最小落地建议

第一版最小闭环建议是：

1. 新增 `WorldDelta` 数据结构。
2. `CommandBuffer` 增加 `PlaybackDelta()`。
3. `World` 增加 `ApplyDeltaForward()` / `ApplyDeltaBackward()`。
4. 生成 delta 时只依赖现有 public state 读取点和内存中的 shadow public state 推演，不引入整 world clone/snapshot。
5. 先把验收锁定为公开状态同态，再考虑后续是否用 `WorldDelta` 进一步统一 `FrameCommands` / `ReverseFrameCommands` 的实现层。

## 验证标准

- `PlaybackDelta()` 调用后 source world 的公开状态不变。
- 在同一个 world 上：`ApplyForward(delta)` 后达到目标公开状态，`ApplyBackward(delta)` 后恢复到原公开状态。
- 在不同但同步基线的 world 上：顺序应用多个 delta 后，最终公开状态与 source world 一致。
- hierarchy 与 destroy 相关测试必须覆盖：
  - `link`
  - `unlink`
  - `relink`
  - `destroy root -> cascade destroy`
  - `unlink` 后逃逸 destroy subtree
  - same-frame transient entity 折叠
