# QueryPlan.ValueLab

这是**查询语言的价值验证实验，不是查询语言实现**。不包含 parser、binder、优化器或代码生成器，也不修改 MiniArch Core。

## 验证对象

在真实 `MiniArch.World` 上，对同一查询需求比较不同手写物理执行策略，确认：

1. 用户容易写出的方案是否有显著成本。
2. 合理手写方案能否解决，以及是否仍需要根据数据形状选择策略。
3. 受限查询优化层有哪些可自动化的机会，哪些场景没有额外优势。

候选执行器本身也是手写代码；本实验**不能证明尚未实现的语言能自动生成这些代码，也不能证明 SQL 文本比 C# DSL 更必要**。

## 运行

```bash
dotnet run -c Release --project tools/perf/QueryPlan.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/QueryPlan.ValueLab
```

Debug 构建拒绝运行。没有 MiniArch.Core/public API 变更，不属于 HeroComing 架构回归门禁；仍需验证实验自身。

## 查询需求与策略

| 需求 | 对比策略 |
|---|---|
| 筛选后返回 Entity、Score、Weight | Entity 列表后逐个 Get / Entity 融合 / Chunk 融合 |
| 一批 zone 的 Count、Sum(Weight)，不存在的 key 返回零 | Q 次 chunk scan / 可复用 hash 聚合 / 已知有界 key 的 dense 聚合 |
| 过滤后按 Score 降序、完整 Entity 升序取前 K 行 | Chunk 全量排序 / 有界 heap，反向写出 worst-first dequeue 的有序结果 |
| 根据唯一逻辑 target key 关联 Target.X，跳过不存在的 target | 每条查询目标 / 缓存 chunk 的嵌套遍历 / 投影 hash 索引 / 已知有界 key 的 dense 索引 |

各查询返回相同的类型化结果，写入复用的输出缓冲区；没有把结果生成成本藏在测量之外。未要求排序的投影与关联按实体身份规范化后验证，Top-K 则验证完整有序输出。

## 测量契约

- 每个操作代表一个**稳定读取阶段**；期间 World 无写入。
- **包含**：获取 Query chunks、扫描、每操作索引重建、scratch 清理、聚合/排序、结果写出。
- **不包含**：World/数据初始化、首次分配缓冲区、JIT 预热、解析/编译、调用方之后如何处理结果。
- 所有策略允许同样的跨阶段容量复用；不是拿每次 new Dictionary/List 的低质量方案作唯一对手。
- 300ms 预热；三个 350ms 测量窗口；每个窗口轮换策略顺序，取 median，输出 min/max。
- 每窗口 GC 在计时外；通过线程分配计数报告 B/op。
- 两种 archetype（有/无 Cold 组件）；Score 包含重复值；有不存在的 target/key。
- 数据集：N=10,000/targets=2/uniform，N=100,000/targets=64/uniform，以及后者 80% 集中在 key=0 的 hot 分布。
- 正确性独立用 fixture 原始记录作 oracle，覆盖 N=0/1/17/10,000/100,000、targets=2/8/64、uniform/hot、K=0/K>N，共 780 次完整结果比较。

## 已知限制

- 合成数据，只能证明这些数据形状的技术机会，不能证明外部项目的真实需求频率。
- Dense 策略依赖明确的有限 key 范围；任意值或稀疏超大 key 必须走其他策略。
- 逻辑 target key 在本实验中唯一；一对多 JOIN、重复 key、NULL、更新/事务语义不在范围内。
- 复用 scratch 内存与输出由实验控制；返回跨阶段持有的独立结果需要另算复制/分配成本。
- 未实现动态换计划、计划缓存、自动索引有效期管理或跨独立 Execute 调用的共享扫描。
- 候选策略集合不完整（例如没有 Quickselect/ radix sort）；“候选中最快”不等于全局最优。

结果与产品裁决见 `.knowledge/kb-query-language-value.md` 的报告链接。
