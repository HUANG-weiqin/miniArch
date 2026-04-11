# Complex Query Benchmark Report

Date: 2026-04-11

## 结论

- 这轮复杂 query benchmark 已落地并可复跑，入口是 `scripts/benchmark.ps1 -Filter "*QueryBenchmarks*"`.
- 场景使用 5 类 archetype 混合分布，entity 档位为 `10_000 / 50_000 / 100_000`，每个 entity 至少 8 个 component。
- 这轮测的是 query 过滤 + 命中 entity 遍历；遍历阶段只累加 `entity.Id`，尽量避免把额外业务计算混入结果。
- 在 `10_000` 和多数 `50_000` 档位，MiniArch 大致落在 Arch 的 `1.2x ~ 1.6x`。
- 到 `100_000` 档位时，MiniArch 开始明显拉开，`WithAll` 场景达到约 `2.5x`，说明大规模高命中遍历还有优化空间。
- MiniArch 分配略高于 Arch：MiniArch 基本在 `1.55 KB ~ 1.60 KB`，Arch 约为 `1.04 KB ~ 1.32 KB`。

## 场景说明

- archetype 分布：
  - `40%`：命中 `WithAll`，并命中 `AnyTagA`
  - `20%`：命中 `WithAll`，但带 `ExcludedTag`
  - `15%`：命中 `WithAll`，并命中 `AnyTagB`
  - `15%`：命中 `WithAll`，不命中 `Any`
  - `10%`：缺少 `Health`，用于制造过滤失败分支
- query 三类：
  - `WithAll<Position, Velocity, Health, Team>`
  - `WithAll + Without<ExcludedTag>`
  - `WithAll + Any<AnyTagA, AnyTagB>`
- 命中规模：
  - `WithAll` 命中约 `90%`
  - `WithAll + Without` 命中约 `70%`
  - `WithAll + Any` 命中约 `55%`

## 结果摘要

| EntityCount | Query | Arch Mean | MiniArch Mean | MiniArch / Arch |
| --- | --- | ---: | ---: | ---: |
| 10,000 | WithAll | 33.70 us | 41.43 us | 1.23x |
| 10,000 | WithAll + Without | 29.20 us | 37.37 us | 1.28x |
| 10,000 | WithAll + Any | 26.20 us | 35.72 us | 1.36x |
| 50,000 | WithAll | 50.43 us | 65.27 us | 1.29x |
| 50,000 | WithAll + Without | 36.83 us | 84.47 us | 2.29x |
| 50,000 | WithAll + Any | 31.08 us | 49.90 us | 1.61x |
| 100,000 | WithAll | 70.27 us | 177.23 us | 2.52x |
| 100,000 | WithAll + Without | 69.53 us | 111.20 us | 1.60x |
| 100,000 | WithAll + Any | 58.13 us | 102.13 us | 1.76x |

## 简短解读

- MiniArch 在小到中等规模下没有被 Arch 完全甩开，说明当前 query archetype 过滤与 chunk/row 遍历路径已经具备基本可比性。
- `WithAll` 在 `100_000` 档位退化最明显，这更像是大规模高命中遍历的成本被放大，而不是 `Without` / `Any` 过滤的独有问题。
- `WithAll + Without` 的 `50_000` 档位波动较大；当前 `ShortRun` 只有 3 次正式采样，这类值更适合作为方向信号，而不是精确结论。
- 从分配看，MiniArch 还保留了一点固定开销，但量级不大，目前主要矛盾仍然是大档位时间成本。

## 复跑命令

```powershell
powershell -ExecutionPolicy Bypass -File scripts/benchmark.ps1 -Filter "*QueryBenchmarks*"
```

原始 BenchmarkDotNet 汇总文件：

- `BenchmarkDotNet.Artifacts/results/MiniArch.Benchmarks.QueryBenchmarks-report-github.md`
