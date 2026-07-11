# FrameLookup Public API Gate Report

## Verdict

**Conditional Hold。暂不进入 `src/MiniArch/`。**

Compact CSR row-ref lookup 的 ValueLab 价值仍成立：uniform / 高基数 / 组件读取场景里，`Snapshot -> Build once -> key -> row refs -> chunk span read` 继续明显优于重复扫描和 per-key scan，也能在 1M 场景保持 0B build allocation。

但 M1 候选发布形态 `ForEach(key, ref rowConsumer)` 没过性能门槛。Direct ForEach 正确性成立，却因为每 row 调用 struct consumer，稳定慢于 `CopyRowRefs + manual consume`。因此不能把当前 API 形状产品化。

## Plain-English Summary

索引本身值得继续：把“每次问都扫全表”变成“每帧先整理一次电话簿”。

问题出在“怎么把查到的 rows 交给用户”：逐行回调看起来干净，但每行都过一层 consumer 调用，省下的 row-ref copy 被调用成本吃掉了。现在进库会发布一个慢的漂亮 API；正确动作是 Hold，先重设计 consume 形态。

## Evidence Commands

已运行：

```bash
dotnet build -c Release miniArch.sln
dotnet test -c Release miniArch.sln
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000
```

M4 结束前会再跑一次 `--full` 和 `git diff --check` 作为本报告 verification。

## Direct ForEach Evidence

Full run 摘要：

| Scenario | CopyRowRefs rowComp | DirectForEach rowComp | Direct / Copy | Result |
|---|---:|---:|---:|---|
| small-q | 0.01ms | 0.01ms | 1.68x | 噪声区，Q 太小 |
| realistic | 0.51ms | 6.87ms | 13.41x | ❌ 失败 |
| hot | 51.63ms | 87.36ms | 1.69x | ❌ 失败 |
| full-1m | 67.81ms | 134.51ms | 1.98x | ❌ 失败 |

解释：

- `CopyRowRefs + manual consume` 的内层循环是直接数组/span 读取。
- DirectForEach 的每一行都调用 `consumer.Accept(...)`；即使 consumer 是 struct generic，当前 JIT 形态仍产生可测 per-row 调用成本。
- 它没有降低 hot bucket 问题；hot 仍是禁用区间。

## Correctness Evidence

M3 后 correctness matrix 覆盖 20 cases：

- empty world
- single/multi/missing/default key
- hot bucket
- hash collision
- multi archetype
- chunked storage
- Where 0/partial/100
- scan order
- entity vs row consistency
- DirectForEach consistency
- AutoGrow multiple resize
- Clear rebuild
- Rebuild publishes new result
- NoGrow early/late fail
- 1M smoke

结果：`--correctness-only` 与 `--correctness-only --n 1000000` 均 `Correctness: PASS`。

## Baseline Comparison

代表性 full run：

| Scenario | CompactRowDsl | DictionaryList | ComponentBucket / Scan |
|---|---:|---:|---:|
| realistic | build 0.65ms；totalRow 1.99ms；0B | build 1.15ms；total entity+component 6.86ms；~1.38MB alloc | ComponentBucket total ≈519ms；Raw scan totalRow ≈1504ms |
| full-1m | build 12.81ms；totalRow 149.48ms；0B | build 20.58ms；total entity+component 177.67ms；~21MB alloc | scan/bucket skipped（N×Q 过大） |
| hot | totalRow 218.85ms | entity-only total ≈99.54ms | hot 继续不是 row-ref 通用胜利区 |

结论：compact CSR 本体继续成立；失败的是发布级 Direct ForEach 消费形态。

## API Decision

M1 的候选 API 需要修订：

- 保留：`FrameLookup<TKey>` = key -> component rows。
- 保留：Snapshot 内 build once，不做 live cache / freshness tracking。
- 保留：compact CSR row-ref 作为主物理布局。
- Hold：逐 row `ForEach(key, consumer)` 不进 v1。
- 禁止：为绕过该问题引入 Core mutation hook、自动刷新、hot bucket hybrid。

下一轮最小研究方向：

1. **chunk-run/batched consumer**：每次回调一段同 chunk contiguous rows，减少 per-row call。
2. **caller-provided row-ref span**：把 `CopyRowRefs` 明确成低层 public terminal，但需要重新审查生命周期与名字。
3. **specialized generated consume**：只在证据显示 JIT 能内联并打平后考虑。

## M5-M8 Entry Conditions

当前不满足 M5 入口条件。重新进入 production TDD 前必须全部满足：

1. 新 consume 形态在 `realistic` 和 `full-1m` 中不慢于 `CompactRowDsl + CopyRowRefs + manual consume`。
2. 仍然保持 build/query 热路径高水位 0B。
3. API 文档能一句话解释，且不会把 entity-only/hot/low-Q 包装成已解决。
4. correctness matrix 覆盖新 consume 形态。
5. 更新本设计文档后再进入 `src/MiniArch/`。

## Final Gate

本次 Gate 的执行结论：

- **不进入 production。**
- M5/M6/M7/M8 按路线跳过。
- M9 交付报告应把本分支定位为“证据分支”，不是发布实现分支。
