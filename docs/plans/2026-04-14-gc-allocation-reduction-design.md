# GC Allocation Reduction Design

## 结论

- 本轮目标是减少 MiniArch 中“明显且持续触发的无谓 GC”，不追求所有路径绝对 0 GC。
- 约束是只改内部实现，不改任何公开 API 或用户用法。
- 本轮优先处理两类高价值热点：
  - `World` / `HierarchyTable` 中 destroy closure 相关的临时集合分配。
  - `CommandBuffer` 编译期去重/归并阶段的临时集合分配。
- 验证采用双轨：新增/收紧分配断言测试守回归，再用 BenchmarkDotNet `MemoryDiagnoser` 观察目标路径分配下降。

## 目标

- 消除 1–3 个高价值、可稳定复现的 GC 热点。
- 让 destroy-heavy 路径和 command-buffer play 路径的热调用分配明显下降，能由测试和 benchmark 共同证明。
- 保持行为、结果顺序和公开 API 不变。

## 非目标

- 不重写 WorldDelta、snapshot、public state capture 的整体模型。
- 不调整 `World.Destroy`、`CommandBuffer`、`Query` 的公开接口。
- 不为了 GC 顺手引入大范围池化框架或跨模块抽象。

## 当前事实

- `World.Destroy(Entity)`、`CollectCurrentDestroyClosure(Entity, HashSet<Entity>)`、`CaptureReverseFrameCommands(...)` 都会为 destroy closure 新建 `List<Entity>` / `HashSet<Entity>`，而 `HierarchyTable.CollectDestroySubtree(...)` 还会新建 `Stack<(Entity,bool)>`。
- 这些集合只用于一次遍历，生命周期严格受调用边界控制，天然适合改为 world 内部 scratch 容器复用。
- `CommandBuffer.Compile()` 每次都会创建多组临时 `Dictionary` / `HashSet` / `List`，其中去重/归并阶段的中间集合只在单次 compile 内部使用，适合改为 buffer 级 scratch 复用。
- 现有测试已经有分配断言先例：`QueryTests` 与 `QueryComponentSetTests` 使用 `GC.GetAllocatedBytesForCurrentThread()`，`CommandBufferTests` 已有 play vs playback+replay 的分配比较。

## 方案

### 方案 A：局部 scratch 复用（推荐）

- 在 `World` 内新增 destroy traversal scratch，复用 `visited` / `destroyOrder` / traversal stack。
- 在 `CommandBuffer` 内新增 compile scratch，复用编译去重阶段的字典、哈希集与必要的中间列表。
- 保持最终输出结构和 replay 逻辑不变，只消除内部一次性中间集合分配。

**优点**

- 不改公开 API。
- 改动边界小，易验证、易回退。
- 直接命中当前最可疑的持续分配源。

**风险**

- scratch 生命周期必须严格受控，异常路径也要清理。
- 需要避免 destroy 流程内部嵌套调用导致 scratch 状态污染。

### 方案 B：统一对象池层

- 建立跨模块的通用 `List` / `Dictionary` / `HashSet` 对象池，然后让多个热点统一接入。

**为什么不选**

- 设计和验证成本偏高。
- 这轮目标是稳定拿收益，而不是先搭复杂基础设施。

## 设计细节

### Destroy closure 路径

- 在 `World` 内引入仅供内部使用的 destroy traversal scratch。
- `Destroy(...)`、`CollectCurrentDestroyClosure(...)`、`CaptureReverseFrameCommands(...)` 共享同一套 scratch 获取/归还逻辑。
- `HierarchyTable.CollectDestroySubtree(...)` 不再在方法内部 `new Stack<(Entity,bool)>`，改为接收可复用 stack 容器。
- scratch 在每次使用前后都显式 `Clear()`，并通过 `try/finally` 保证异常路径不泄漏状态。

### CommandBuffer compile 路径

- 在 `CommandBuffer` 内引入 compile scratch，承载：
  - created entity state 映射
  - add/set/remove 去重结构
  - hierarchy intent 映射
  - component type cache
  - destroyed entity set
- `Compile()` 在每次开始前清空 scratch，完成后只把最终需要交给 replay / frame 的结果保留到输出对象中。
- 若某些最终结果只在 `Play()` 内短暂存活，优先评估是否能继续收敛到 scratch 生命周期内。

## 测试与验证

- 先用 TDD 增加两个分配回归测试：
  - warmed destroy cascade 路径分配断言
  - warmed command buffer play 路径分配断言
- 然后实现最小优化让测试转绿。
- 最后运行 focused tests 与 command-buffer / query 相关 benchmark，给出 before/after 分配结果。

## 验收标准

- 至少 1–3 个明确热点的分配显著下降，其中 destroy closure 路径必须得到优化。
- 新增分配断言测试通过。
- focused benchmark 能看到目标路径分配下降或归零。
- 不改公开 API，不改变现有语义。

## 风险与回退

- 如果 scratch 复用引入状态污染或顺序问题，优先回退到更小粒度的局部复用，而不是扩大池化范围。
- 如果 command buffer 的最终分配仍明显存在，本轮先保留 destroy closure 收益，下一轮再继续压缩 `CompiledCommandBatch` 生命周期。
