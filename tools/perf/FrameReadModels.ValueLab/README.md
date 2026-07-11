# FrameReadModels ValueLab

## 结论先行

这是 Frame Read Models 的独立 ValueLab：在 Snapshot 稳定后，从 `world.Query(desc).GetChunks()` 和组件 spans 构建帧内派生索引；Build 一次，随后执行大量只读按 key 查询。

**当前结论：Conditional Go。** uniform / 高基数 / 组件读取场景中，compact CSR row-ref layout 明显优于重复扫描和 per-key scan；hot bucket 与纯 entity-only 场景不能只靠 row-ref CSR 硬吃，必须单独处理或标为禁用区间。

**这不是 public API。** 实验代码不进入 `src/MiniArch/`，不修改 Core。

## 约束

- 必须 `-c Release`。
- Debug 构建运行会报错并返回非 0。
- 禁止修改 `src/MiniArch/**` 或 `tests/HeroPipeline.Tests/**`。
- 只读 Snapshot 模型：Build 后查询阶段不发生 World 修改。
- Clear/Rebuild/Grow 后旧 BucketView、span、row-ref view、enumerator 全部失效。

## 运行

```bash
# 默认：correctness quick size + quick perf
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab

# 正确性矩阵
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000

# 性能矩阵
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full
```

## 覆盖范围

- 正确性：empty、single/multi/default/missing key、hot bucket、hash collision、multi archetype、chunked、Where 0/partial/100、scan order、entity vs row consistency、AutoGrow、Clear rebuild、NoGrow failure、1M smoke。
- 性能布局：`EntityArrayLookup`、`LinkedRowLookup`、`CompactRowLookup`、`DenseIntCompactLookup`。
- 基线：`RawRepeatedScan`、`ComponentBucketQuery<Cell>`、`Dictionary<int,List<Entity>>`、`RawSameCompact`。

## 最新实测摘要

见 `docs/plans/2026-07-11-frame-read-models-report.md`。

关键结论：

- `CompactRowDsl` 在 `realistic`（50K / Q=10K / distinct=4096）总 row 成本约 2ms；重复扫描约 1.3s，ComponentBucketQuery 约 0.7s。
- `CompactRowDsl` 在 `full-1m`（1M / Q=50K / distinct=4096）build + row consume 约 193ms，稳态 build 分配 0B。
- chunk-row component read 比 `World.Get<Health>` 随机定位稳定快，full-1m 中约 0.25x。
- `LinkedRow` 不值得产品化：读取跳跃明显。
- hot bucket 场景 row-ref lookup 会被大桶重复遍历拖垮；不能作为通用胜利证据。
