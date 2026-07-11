# FrameLookup Chunk-Run Consumer Experiment Design

## 结论

本实验只验证一个问题：**把逐 row callback 改成按 chunk-run 批量 callback，能不能消掉 DirectForEach 的 per-row 调用成本，同时保留 component row 读取优势。**

不进入 `src/MiniArch/`，只改 `tools/perf/FrameReadModels.ValueLab/**` 和报告/知识库。

## 背景

M4 gate 已证明：

- compact CSR row-ref lookup 本体成立。
- `CopyRowRefs + manual consume` 快。
- 逐 row `ForEach(key, consumer)` 正确但太慢：realistic 约 13.41x、full-1m 约 1.98x 慢于 CopyRowRefs。

失败原因不是索引，而是消费粒度太细：每个 row 调一次 consumer。

## 候选方案

### A. On-the-fly contiguous run callback（本次推荐）

对同一个 key 的 row refs 顺序扫描，遇到相同 `ChunkIndex` 且 `RowIndex` 连续的区间时合成一个 run：

```text
RowRef(c0,r10), RowRef(c0,r11), RowRef(c0,r12), RowRef(c2,r5)
=> Run(c0,start=10,length=3), Run(c2,start=5,length=1)
```

然后每个 run 调一次 consumer：

```csharp
consumer.Accept(entities.Slice(start, length), healths.Slice(start, length));
```

优点：不新增存储，不改变 build，风险最低。缺点：如果同 key rows 在 CSR 中跨 chunk 很碎，callback 次数仍可能多。

### B. Build-time run table

build 时额外把每个 key 的 row refs 压缩成 run table。

优点：query 时最省。缺点：build 变复杂、需要额外 arrays、NoGrow 语义和 0B 高水位更难保证。

### C. Expose CopyRowRefs as terminal

直接承认 caller-provided span 是最快形态，把它产品化。

优点：当前证据最好。缺点：生命周期/名字/容量处理暴露给用户，API 更低层。

## 决策

先做 A：On-the-fly contiguous run callback。

理由：它是最小实验。若 A 都不能接近 CopyRowRefs，则 build-time run table 更不该直接进 production；若 A 接近或超过 CopyRowRefs，再考虑是否值得做 B。

## ValueLab API 形状

新增 lab-only consumer interface：

```csharp
internal interface IFrameRunConsumer<T1>
    where T1 : unmanaged
{
    void Accept(ReadOnlySpan<Entity> entities, ReadOnlySpan<T1> c1);
}
```

同样提供 2/3 component arity。

新增 `CompactRowLookup<TKey>` 方法：

```csharp
public int ForEachRun<T1,TConsumer>(TKey key, ReadOnlySpan<ChunkView> chunks, ref TConsumer consumer)
    where T1 : unmanaged
    where TConsumer : struct, IFrameRunConsumer<T1>;
```

返回 processed row count；缺失 key 返回 0。

## 控制流

```text
Find key bucket
for each RowRef in bucket:
    start new run
    extend while next RowRef has same chunk and adjacent row
    get chunk spans once
    callback once with sliced spans
```

## 正确性要求

- run consumer sum 与 `CopyRowRefs + manual span read` 一致。
- 覆盖：single run、multi run、multi chunk、missing key、default key、chunked storage、hot bucket。
- Rebuild 后只读新结果。
- 不改变 existing correctness matrix 结果。

## 性能验收

新增 benchmark variant：`CompactRowRunForEach`。

必须比较：

1. `CompactRowDsl`：CopyRowRefs + manual consume。
2. `CompactRowDirectForEach`：逐 row callback（已知失败）。
3. `CompactRowRunForEach`：chunk-run callback。
4. Dictionary / EntityArray / DenseInt 作为边界参考。

Go 条件：

- `realistic` 和 `full-1m` 的 row component consume 不慢于 CopyRowRefs 10%。
- build 仍然高水位 0B。
- correctness-only 和 1M smoke 通过。

Hold 条件：

- 比逐 row DirectForEach 明显改善，但仍慢于 CopyRowRefs。
- 或只在 hot bucket 改善，不改善 uniform/high-cardinality。

No-Go 条件：

- correctness 不能稳定证明。
- 分配或复杂度破坏 ValueLab 边界。
- 性能仍接近逐 row DirectForEach。
