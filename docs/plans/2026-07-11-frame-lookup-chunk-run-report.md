# FrameLookup Chunk-Run Consumer Report

## Verdict

**No-Go for generic v1。**

On-the-fly chunk-run / batched consumer 正确性通过，但没有解决发布 API 的核心性能问题。它只在 hot bucket 场景改善逐 row DirectForEach；在真正想产品化的 uniform / high-cardinality 场景中反而更慢。

## Plain-English Summary

chunk-run 的前提是“同一个 key 命中的 rows 在同一个 chunk 内经常连续”。实测 workload 不满足这个前提。

uniform/high-cardinality 下，同 key rows 分散在 chunk 里，几乎每个命中都变成一个长度 1 的 run。于是 chunk-run 还是接近“每 row 回调一次”，还额外多了 run 合并判断和 span slice。

## What Was Measured

命令：

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000
```

新增 variant：

- `CompactRowRunForEach`：`CompactRowLookup<TKey>.ForEachRun<T>`，按 key 扫 CSR row refs，on-the-fly 合并 same-chunk adjacent rows，再给 run consumer sliced spans。

## Correctness Evidence

新增 correctness case：`RunForEachConsistency`。

覆盖：

- run consumer sum 与 `CopyRowRefs + manual span read` 一致。
- correctness matrix 全布局通过。
- 1M smoke 通过。

结果：`--correctness-only` 与 `--correctness-only --n 1000000` 均 `Correctness: PASS`。

## Performance Evidence

Full run 摘要：

| Scenario | CopyRowRefs rowComp | DirectForEach rowComp | RunForEach rowComp | Run / Copy | Run / Direct | Runs |
|---|---:|---:|---:|---:|---:|---:|
| small-q | 0.01ms | 0.01ms | 0.02ms | 2.25x | 1.31x | 94 |
| realistic | 0.51ms | 15.31ms | 17.31ms | 34.15x | 1.13x | 122,065 |
| hot | 60.42ms | 88.89ms | 62.95ms | 1.04x | 0.71x | 5,556,465 |
| full-1m | 68.45ms | 136.48ms | 133.11ms | 1.94x | 0.98x | 12,204,355 |

## Why It Failed

核心因果：

```text
run count ≈ row count  =>  callback count 仍接近 per-row
callback count 高      =>  consumer 调用成本仍在
额外 run 合并判断      =>  比 DirectForEach 更慢或接近
```

在 `realistic` 中，50K rows / 4096 keys / random uniform 让同 key rows 几乎不连续。10K queries 总共产生 122,065 runs，run callback 没有真正 batch 起来。

在 `hot` 中，大量 key=0 的 rows 更容易相邻，所以 RunForEach 从 DirectForEach 88.89ms 改善到 62.95ms，接近 CopyRowRefs 60.42ms。但 hot 本来就是禁用区间，而且 entity-only baseline 在 hot 更强。

## API Decision

- 不把 on-the-fly chunk-run 作为通用 v1。
- 不做 build-time run table：当前证据显示真正问题是 row physical order 与 key 不聚簇；预存 runs 不能让 uniform key 变连续。
- 继续保持 production blocked。
- 下一步如果还要找 public terminal，优先研究 caller-provided `CopyRowRefs` / row-ref span terminal，而不是继续包装 callback。

## Updated Boundary

当前可成立的最小事实：

- **最快通用 row consume 仍是 caller-provided row-ref buffer + manual span read。**
- row callback 和 run callback 都不是发布级通用 terminal。
- hot bucket 可被 run batching 改善，但 hot bucket 不代表目标场景。

## Commands Run

```bash
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
git diff --check
```
