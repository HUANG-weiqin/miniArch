---
title: ComponentBucketQuery MVP 验证报告
module: MiniArch（用户 API 分层）
description: ComponentBucketQuery MVP 最终报告——0 core intrusion、确定性 per-key scan、正确性模型、性能矩阵
updated: 2026-07-10
---

# ComponentBucketQuery MVP 验证报告

## 核心结论

1. **API 定型：确定性 per-key scan。** `ComponentBucketQuery<TComponent>` 每次 public read 从真实 World 对请求 key 做 deterministic scan，不使用 hash/fingerprint/count fast-path/dirty mode/AutoFreshness。正确性在调用时刻是确定性的，无概率 false negative。
2. **零 core 入侵。** 完全在 MiniArch.Core 之上构建，只使用 World 公开 API，不修改 `src/MiniArch/Core/` 任何代码。
3. **API 精简到最低必要面。** 公开类型 `ComponentBucketQuery<TComponent>`，方法：构造函数、`Get`、`TryGet`、`ContainsKey`、`Count`。`Refresh()` 已删除——每次操作自验证，无需刷新入口。`IDisposable` 不必要——无内部状态需要释放。
4. **无内部 buffer 架构。** `Get/TryGet` 不再维护内部 `Entity[]` 缓存，改为直接写入调用者提供的 `Span<Entity>`。无 Bucket class，无 Dictionary，无 `_buffer`/`_count` 字段。调用者控制内存生命周期，消除 span 过期风险。`Count/ContainsKey` 直接扫描不写入 span。无 `Clear`、无 `Dispose`。
5. **Correctness 全通过：** 16 个专项测试 + 900 MiniArch 回归 + 5 HeroPipeline 全部通过。确定性正确性模型已在测试中验证。
6. **性能定位：**
    - CountOnly：BucketQuery 远快于 ManualExpanded（~8×~10×，因 `Count(key)` 直接 per-key scan，不全量建桶；ManualExpanded 每轮全量建桶）。
    - Read2/Write3：BucketQuery 优于 ManualExpanded（~1.1×~3.7×，因确定性 scan 只取请求 key，ManualExpanded 每轮全量建桶；P 越大优势越明显）。
    - AutoFreshness（高频变更场景）：BucketQuery 优于 ManualExpanded（~3.9×~4.4×，因确定性 scan 无校验开销）。
   - 相比 ScanOneKey：CountOnly/AutoFreshness 场景 BucketQuery 可接近或超过 ScanOneKey（部分因 scope 预构造/测量差异）；Read2/Write3 场景 ScanOneKey 仍强（~2×~7×），因直接 chunk span 融合业务操作，避免 entity materialization + GetRef。
7. **产品判断：API 正确性确定，性能可预测，可在单线程 game-loop 约束下使用。** 使用者需注意：提供足够大的 `Span<Entity>` 以容纳查询结果。
8. **稳态 GC 结论：** BucketQuery 在 CountOnly/Read2/Write3 三类 hot path 为 0.0 B/round、0 GC；AutoFreshness 约 393.6KB/round 来自 benchmark 自身 `new List<(Entity, CardZone)>`，与 ScanOneKey 同量级，不是 query 分配。

## 这个模块是干什么的

- 这个模块负责：
  - 按组件的当前值对实体进行动态分桶，提供 `ComponentBucketQuery<TComponent>` 类型。
  - 用户通过 `new ComponentBucketQuery<TComponent>(world)` 或 `new ComponentBucketQuery<TComponent>(world, scope)` 构造。
  - 每次 `Get/TryGet` 只扫描请求 key，将匹配实体写入调用者提供的 span。
  - `Count/ContainsKey` 直接扫描不写入 span。
  - 零 core 侵入：完全在 MiniArch.Core 之上构建，只使用 World 公开 API。
- 这个模块不负责：
  - 替代 MiniArch.Core 的 Query。它提供按组件值分桶的便利抽象，不是 Query 的性能替代。
  - 保证跨线程安全。为单线程 game-loop 设计。
  - 自动跟踪组件变化。不提供手动 Add/Set/Remove 通知 API，每次读操作从 World 重新扫描请求 key。

## 架构

- 核心组成：
  - `ComponentBucketQuery<TComponent>`：泛型主类型，内部无 buffer，无 Bucket class 或 Dictionary。每次 `Get/TryGet` 直接写入调用者提供的 `Span<Entity>`。
  - 构造函数 `ComponentBucketQuery<TComponent>(World world)` 和 `(World world, QueryDescription scope)`：必须传入 World；可选 scope 限定查询范围。构造函数使用 `scope.GetRequiredTypes()` 代替 `RequiredTypes.ToArray()` 避免冷路径分配。
  - 无内部缓存，无 `_buffer` 字段。
- 数据流：
  - `Get(key, destination)` → `_world.Query(_scope)` → 遍历 chunk → 比较 `TComponent` 值 → 写入 `destination` → 返回写入总数。
  - `Count(key)` / `ContainsKey(key)` → 直接 `_world.Query(_scope)` → 遍历 chunk 计数/判断，不写入任何 span。
- 事实源策略：分桶依据始终来自 `World.Query` 返回的真实组件值。不做假设、不做缓存推测。

## 决策

- **确定性正确性优于概率性能。** 早期原型尝试过 fingerprint/adaptive dirty mode/AutoFreshness，发现概率正确性不可接受（false negative 风险）。最终选择：每次 read 从 World 确定性扫描请求 key，消除所有概率判断。
- **`Refresh()` 不必要。** 因为每次 public read 都已自验证，无需独立的刷新入口。删除 `Refresh()` 减少 API surface。
- **调用者提供 span 替代内部 buffer。** 不再维护内部 `Entity[]` 缓存。调用者通过 `Span<Entity>` 提供输出缓冲区，控制内存生命周期。消除 span 过期风险，简化内部状态管理。
- **零 core 入侵优先。** `ComponentBucketQuery` 不修改任何 `src/MiniArch/Core/` 代码，保持 core 的纯净和稳定性。
- **构造函数冷路径优化。** 使用 `scope.GetRequiredTypes()` 代替 `RequiredTypes.ToArray()`，避免冷启动时不必要的数组分配。
- **极简 API。** 只暴露一个泛型类型、两个构造重载（无 scope / 带 scope）、四个读方法（Get/TryGet/ContainsKey/Count）。不实现 IDisposable——无内部资源需要释放。降低学习和使用成本。

## 认知模型

- 把 `ComponentBucketQuery` 理解为一个**按需扫描的桶查询器**：它不对 freshness 做任何假设，每次读调用都从 World 重新扫描请求 key。`Get/TryGet` 将匹配实体写入调用者提供的 `Span<Entity>`，返回写入计数。调用者负责提供足够大的缓冲区。
- 常见误解：
  - 比手动扫表快？→ CountOnly 场景因 `Count(key)` 直接 per-key scan，不全量建桶，远快于 ManualExpanded。Read2/Write3 在同一量级。`Get/TryGet` 无内部 buffer 复用，每次重新写入调用者提供的 span。性能可预测。
  - 替代 Query？→ 不，它构建在 Query 之上，为按值分桶场景提供便利。
  - 有自动 freshness 跟踪？→ 没有。每次从 World 扫描请求 key。没有 freshness 校验开销，没有 false negative 风险。

## 正确性

### 正确性模型

- **确定性正确性：** 每次 public read 在调用时刻从真实 World 扫描请求 key，结果直接反映当前 world 中该 key 的全部实体。无缓存推测，无概率判断，无 false negative。
- **约束条件：**
  1. 单线程 game-loop 使用。
  2. 提供足够大的 `Span<Entity>` 以容纳查询结果。
- 这些约束与 MiniArch.ECS 整体使用模式一致，不是额外负担。

### 专项测试（16 项通过）

覆盖：identity、add、remove、query isolation、multi-bucket、确定性正确性验证、边界条件。

### 回归测试（900 + 5 通过）

- 900 个 MiniArch.Tests 通过：核心库零回归。
- 5 个 HeroPipeline.Tests 通过：上层管线兼容。

### HeroComing.Perf 噪音记录

| 运行 | Movement | Attack | 结果 |
|:---:|:--------:|:------:|:----:|
| 最终验证 | 1681.4 | 1182.4 | ✅ 通过（内存无增长） |

**判断：** Movement 1681.4（阈值 1642）、Attack 1182.4（阈值 997）均通过，内存无异常增长，确认确定性 scan 实现无性能回归。

## 性能测量

配置：`N=100000`，`measure=3000ms`，`warmup=8`，uniform 分布。

### 三种对比策略

- **ScanOneKey**：单 key 直接扫表下界——用 Query 按固定值过滤组件，相当于最佳手写。
- **ManualExpanded**：同语义手动展开——每轮扫 query 并全量建桶，然后对桶作读/写/计数。
- **BucketQuery**：`ComponentBucketQuery` 确定性 scan——每次 `Get/TryGet` 只扫描请求 key，写入调用者提供的 buffer。

### P=4

| 场景 | ScanOneKey | ManualExpanded | BucketQuery |
|:----|:----------:|:--------------:|:-----------:|
| CountOnly | 19,770 | 1,370 | 14,058 |
| Read2 | 12,145 | 1,134 | 1,820 |
| Write3 | 10,064 | 986 | 1,129 |
| AutoFreshness | 3,477 | 1,108 | 4,821 |

### P=16

| 场景 | ScanOneKey | ManualExpanded | BucketQuery |
|:----|:----------:|:--------------:|:-----------:|
| CountOnly | 18,444 | 1,541 | 12,679 |
| Read2 | 12,025 | 1,437 | 3,764 |
| Write3 | 15,844 | 1,329 | 2,815 |
| AutoFreshness | 3,523 | 1,228 | 4,920 |

### P=64

| 场景 | ScanOneKey | ManualExpanded | BucketQuery |
|:----|:----------:|:--------------:|:-----------:|
| CountOnly | 16,696 | 1,417 | 11,315 |
| Read2 | 11,336 | 1,401 | 5,229 |
| Write3 | 15,430 | 1,392 | 4,617 |
| AutoFreshness | 3,538 | 1,177 | 4,619 |

### 关键观察

- **CountOnly（纯计数）**：BucketQuery 远快于 ManualExpanded（~8×~10×）。原因：`Count(key)` 直接 per-key scan，不全量建桶；而 ManualExpanded 每轮全量建桶。
- **Read2/Write3（静态读）**：BucketQuery 优于 ManualExpanded（~1.1×~3.7×，P 越大优势越显著）。因为确定性 scan 只遍历所有 chunk 但只取一个 key；而 ManualExpanded 每轮全量建桶所有 key，然后在桶上操作。CountOnly/AutoFreshness 场景 BucketQuery 可接近或超过 ScanOneKey（部分因 scope 预构造/测量差异）。Read2/Write3 场景 ScanOneKey 仍强（~2×~7×），因为直接 chunk span 融合业务操作，避免 entity materialization + GetRef。
- **AutoFreshness（高频变更场景）**：BucketQuery 反而优于 ManualExpanded（~3.9×~4.4×）。因为确定性 scan 本身就是扫描，没有额外的"校验 vs 重建"选择开销；而 ManualExpanded 必须每轮全量建桶。
- **整体定位**：BucketQuery 是正确性优先的便利抽象。性能在可接受范围内且有竞争力（尤其 CountOnly 和 AutoFreshness 场景），但不适合替代裸 Query 的热路径。

## 产品判断

| 评估项 | 结论 |
|:-------|:-----|
| API 设计 | ✅ 正确性确定——确定性 per-key scan，零概率 false negative |
| API 精简度 | ✅ 只暴露必要的 public 方法，Refresh() 已删除 |
| 零 core 侵入 | ✅ 完全在 World 公开 API 之上构建 |
| 正确性 | ✅ 单线程 game-loop 约束下完全确定 |
| CountOnly | ✅ 远优于 ManualExpanded（~8×~10×） |
| 读/写场景 | ✅ 优于 ManualExpanded（~1.1×~3.7×，P 越大优势越显著） |
| 高频变更场景 | ✅ 优于 ManualExpanded（~3.9×~4.4×） |
| 当前建议 | ✅ **可用于单线程 game-loop 场景**。使用者需提供足够大的 `Span<Entity>` |

## 坑点

- **调用者需确保 span 容量足够**：`Get/TryGet` 在 destination 耗尽时继续计数但不写入超出部分。调用者应使用 `Count` 预查或提供最大容量 span（如 `stackalloc Entity[maxCount]`）。
- **不是零开销抽象**：每次 `Get/TryGet` 遍历 scope 匹配的所有 chunk。在 entity 总数大量但请求 key 很少的场景下，遍历所有 chunk 仍有一定成本。
- **没有自动 freshness tracking**：每次调用都从 World 重新扫描。这是使用约束，不是 bug。
- **`Count/ContainsKey` 不写入 span**：每次调用都执行全扫描。如果调用者后续还要遍历同一 key 的实体，建议直接 `Get(key, span)` 并用返回值获取数量，避免 `Count` 后再 `Get` 双扫描。
- **线程不安全**：设计为单线程 game-loop 使用。

## 设计演化

早期原型尝试过：
1. **Fingerprint + freshness cache**：每次读前校验 fingerprint 决定是否需要重建。结果：AutoFreshness 性能仅 ManualExpanded 的 50-58%，且概率正确性存在 false negative 风险。
2. **Adaptive dirty mode**：根据历史模式动态跳过 fingerprint 校验。结果：复杂度过高，正确性难以保证，收益有限。
3. **内部 buffer（Entity[] _buffer + _count）**：最终演化步骤。`Get/TryGet` 返回内部 buffer 的 `ReadOnlySpan<Entity>`，生命周期受限。进一步简化为无内部 buffer、调用者提供 span 方案。

最终确定性 per-key scan + caller-provided span 方案同时解决了正确性、生命周期和性能问题：
- 正确性：100% 确定，无概率路径。
- 生命周期：span 由调用者管理，再无过期风险。
- 性能：AutoFreshness 场景反而优于 ManualExpanded（去掉了校验开销）。
- 复杂度：单一直线 scan，无状态机/adaptive 逻辑。
