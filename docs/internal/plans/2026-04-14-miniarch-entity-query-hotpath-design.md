# MiniArch Entity Query Hotpath Design

## 结论

- 本轮只优化 `Query` 的 warmed entity-only 热路径，不改任何公开 API。
- 当前 benchmark 已经走 `query.GetChunkSpan() -> chunk.GetEntities()` 的 span-first 路径，主要剩余固定成本来自 `Query.EnsureMatchingSnapshot()` 每次热调用都要读取并比较两套 world generation。
- 采用一个 world 侧统一的 query cache generation，替代 `Query` 热路径上的“双 generation 读取 + 双比较”。
- `Query` 的 snapshot 仍然保留同样的失效语义：只要 archetype 集合或 query layout 有变化，就重新 refresh；world 不变时，warmed path 只做一次 generation 命中判断并直接返回已缓存 chunk 数组。

## 目标

- 让 `benchmarks/MiniArch.Benchmarks/QueryBenchmarks.cs` 里的 `MiniArch_WithAll_Execute_Warmed` 在现有 benchmark 口径下更快。
- 保持 query 结果、refresh 语义和并发读行为不变。
- 把修改边界收敛在 query cache 失效判定，不顺手改 benchmark、公开 API 或 component query 路径。

## 非目标

- 不新增 public query/entity 遍历 API。
- 不重写 `BuildMatchingSnapshot()` / `Matches()` 的过滤逻辑。
- 不修改 benchmark world shape。
- 不处理 component row-wise/span benchmark 的额外优化。

## 当前事实

- `QueryBenchmarks.ExecuteMiniQuery()` 已直接遍历 `query.GetChunkSpan()` 和 `chunk.GetEntities()`，不是逐 row `GetEntity(row)`。
- warmed benchmark 的 setup 已经预热 query cache；steady-state 热路径主要是 `Query.GetChunkSpan()` 的命中分支与实体遍历本身。
- `Query.EnsureMatchingSnapshot()` 现在每次调用都会读取 world 的 `ArchetypeGeneration` 与 `QueryLayoutGeneration`，再与 snapshot 上两份 generation 比较。
- `Query` 的 refresh 条件本质上是“任何会影响匹配 archetype/chunk 集合的变化”，因此可以用单一 world generation 表达，而不需要在热路径保留两套比较。

## 方案

### 方案 A：统一 query generation（推荐）

- 在 `World` 内新增统一的 query cache generation。
- 每当已有逻辑会推进 `_archetypeGeneration` 或 `_queryLayoutGeneration` 时，同步推进统一 generation。
- `Query` 的 cached snapshot 只记录一份 generation。
- `GetChunkSpan()` / `MatchedChunks` / `MatchedArchetypes` 的热路径只比较这一个 generation；失配后仍走原来的 refresh/build 逻辑。

**优点**

- 完全不改公开 API。
- 改动集中，行为边界清楚。
- 直接命中 warmed entity-only benchmark 的固定开销。

**风险**

- 统一 generation 的推进点必须覆盖所有原本会让 query 失效的路径，否则会出现 stale snapshot。
- 并发读路径仍要保留当前的安全发布方式，不能把缓存数组拆成不一致的多次发布。

### 方案 B：重排 `Query` snapshot 对象布局

- 保留两份 generation，但减少 `MatchingSnapshot` 对象层级或访问次数。

**为什么不选**

- 收益不如直接合并判定条件明确。
- 依旧保留双读取/双比较，热路径改善有限。

## 设计细节

### World 侧

- 新增内部统一 generation 字段，例如 `_queryGeneration`。
- 新增内部只读入口供 `Query` 读取。
- 在以下位置推进统一 generation：
  - 新 archetype 发布后。
  - `TouchQueryLayout()` 立即生效时。
  - deferred layout update flush 生效时。

### Query 侧

- `MatchingSnapshot` 只记录一份 `Generation`。
- `EnsureMatchingSnapshot()` 读取 world 的统一 generation，命中则直接返回 snapshot。
- `RefreshSlow()` 和 `BuildMatchingSnapshot()` 只围绕这一个 generation 工作。
- `RefreshCount` 语义不变。

### 测试与验证

- 先补一个回归测试，锁定 unified generation 的存在与变化契约，确保：
  - world 初始 generation 可读取；
  - 创建新匹配 archetype / 触发 query layout 变化后 generation 前进；
  - world 不变时重复读取 query 不会额外 refresh。
- 然后跑 `QueryTests` 相关用例，确认现有 query cache 行为无回归。
- 最后跑过滤后的 warmed benchmark，给出修改前后对比。

## 验收标准

- `MiniArch_WithAll_Execute_Warmed` benchmark 对比当前基线有明确改善。
- 相关 query 测试通过。
- 不新增公开 API，不改变 benchmark 用法。

## 风险与回退

- 如果统一 generation 覆盖点不全，query 可能返回过期 chunk snapshot；因此实现后必须依赖现有 query refresh 测试和新增 generation 回归测试一起守护。
- 如果 benchmark 改善不明显，但测试全绿，下一步才考虑更激进的 snapshot 布局/遍历路径优化，而不是在本轮扩大范围。
