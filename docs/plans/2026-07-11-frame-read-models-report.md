# Frame Read Models ValueLab Report

## Verdict

**Conditional Go。**

值得继续产品化一个很窄的 MVP：**Snapshot 内 Build 一次的 compact/CSR row-ref lookup**，服务 uniform / 高基数 / 组件读取占比高的场景。不要发布完整关系代数 API，不要把 hot bucket / 纯 entity-only 场景包装成已解决。

## Plain-English Summary

董事长版一句话：这东西像“每帧先把电话簿按小区排好”，之后问 5 万次“某小区有哪些人”就不用每次扫全城。对分散的小区很值；如果所有人都挤在一个小区，问一次就要搬一大车人，索引也救不了。

## What Was Measured

命令：

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
```

Full matrix：

| Scenario | N | Q | distinct | distribution |
|---|---:|---:|---:|---|
| small-q | 50K | 8 | 4096 | uniform |
| realistic | 50K | 10K | 4096 | uniform |
| hot | 100K | 10K | 64 | 80% key 0 |
| full-1m | 1M | 50K | 4096 | uniform |

Variants：`RawRepeatedScan`、`ComponentBucketQuery<Cell>`、`Dictionary<int,List<Entity>>`、`RawSameCompact`、`EntityArrayDsl`、`LinkedRowDsl`、`CompactRowDsl`、`DenseIntDsl`。

## Correctness Evidence

`--correctness-only --n 1000000` 通过：4 layouts × 18 cases + 1M smoke。

覆盖：empty、single/multi/default/missing key、hot bucket、hash collision、multi archetype、chunked storage、Where 0/partial/100、scan order、Entity 与 row 读取一致、AutoGrow、Clear rebuild、NoGrow failure。

## Performance Evidence

摘取最后一轮 `--full` 数据：

| Scenario | Key result |
|---|---|
| realistic | `CompactRowDsl`: build 0.61ms，totalRow 2.01ms；`RawRepeatedScan`: totalRow 1312ms；`ComponentBucket`: totalEntityComponent 683ms；`DictionaryList`: build + totalEntityComponent ≈ 8.9ms |
| full-1m | `CompactRowDsl`: build 16.24ms，totalRow 176.55ms，0B build alloc；`DictionaryList`: build + totalEntityComponent ≈ 367ms，build alloc ≈ 21MB |
| hot | `CompactRowDsl` row component 88ms vs entity component 167ms，但总 row 成本 269ms；`EntityArrayDsl`/`DictionaryList` 对纯 entity path 更适合 |

### Go gates

| Gate | Result |
|---|---|
| 稳态 build 0 B/op | ✅ A/B/C/Dense layout measured build alloc 0B after warm capacity |
| DSL tax ≤3% | ✅ 代表性场景约 -0.1% 到 +1.8%；single run 有噪声，但没有系统性 DSL 税 |
| 相对最佳适用基线快 ≥15% | ✅ uniform/component 场景成立；hot/entity-only 不成立 |
| chunk-row 快于 `World.Get` 随机定位 | ✅ realistic 约 0.22x，full-1m 约 0.25x |
| 1M 无崩溃/异常内存增长 | ✅ full-1m 通过 |
| 读取顺序和两种路径一致 | ✅ correctness matrix 通过 |

## Layout Decision

- **C Compact CSR：Conditional Go。** uniform / 高基数 / 组件读取场景最值得继续。
- **B Linked rows：No-Go。** Build 快，但读取跳跃；没有胜出区间。
- **DenseInt：保留为后续特化候选。** 有界 int key 有价值，但当前 consume 没稳定胜过 Compact。
- **EntityArray：只作为 entity-only/hot baseline。** 它不保存 row refs，不能满足组件 span 读取目标；但数据说明纯 Entity 消费需要单独 API 或禁用说明。

## Applicable Range

- Build 一次后 Q 达到上千到几万。
- distinct key 较多，单 key K 通常几十到几百。
- 组件读取占比高，需要避免 `World.Get<T>` 随机定位。
- Snapshot 查询阶段无修改。

## Do-Not-Use Range

- Q 很小且只读 entity。
- 单个 hot key 占绝大多数结果，并被重复查询。
- 需要 key 枚举稳定顺序。
- 需要跨帧 freshness 或自动同步。

## Productization Recommendation

下一阶段只做：

```text
Rows<T1/T2/T3>
→ optional Where(struct predicate)
→ KeyBy(struct selector)
→ Into(FrameLookup<TKey>)
```

只产品化 compact/CSR row-ref lookup；不要做 GroupBy、Join、TopK、parallel build、key enumeration。产品化必须重新 TDD：每个行为先写测试，再写最小实现。

## Commands Run

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab
git diff --check
```
