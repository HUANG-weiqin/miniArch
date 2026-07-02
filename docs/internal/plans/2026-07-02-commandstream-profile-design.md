# CommandStream Profile Design

## Goal

建立一套只针对 `CommandStream` 的 profiling 入口，用数据判断是否还存在值得做的优化空间，而不是继续凭直觉扫代码。

## Non-goals

- 不改变 `src/MiniArch/` 运行时行为。
- 不把 profiling counter 常驻进热路径。
- 不替代 `HeroComing.Perf --check-baseline` 回归门禁。
- 不做 MiniArch/Friflo/Arch 对比；三方对比仍由 `CommandBufferGame.Perf` / `GameTickSim.Perf` 负责。

## Approach

采用两阶段方案：

1. **独立 runner + 外部 CPU sampling**：新增 `tools/perf/CommandStream.Profile`，构造可重复的 CommandStream-only workload，长时间循环并输出 PID，方便 `dotnet-trace` 附加采样。
2. **最小内置阶段计时**：runner 自己拆分 record / submit / snapshot / clear 等外层阶段；不在 `CommandStream` runtime 内插入计时探针。只有当 sampling 证明需要更细粒度时，才考虑后续新增条件编译 counter。

## Workloads

首版保留最小但覆盖热点差异的 workload：

- `existing-set`：existing entity 上只执行 `Set<T>`，定位 `ComponentStore<T>.ApplyToWorld`。
- `existing-add-remove`：existing entity 上执行结构 add/remove，定位 structural apply。
- `create-small4`：每 tick 创建实体并添加 4 个小 component id，定位 pending materialize mask path。
- `create-duplicates`：同一 pending entity 多次写同 component，定位 last-wins / dedup path。
- `create-destroy`：同帧 create 后 destroy，定位 reserve/release/cancel path。
- `snapshot-only`：只 record + snapshot + clear，不 submit，定位 `EmitToDelta` / `FrameDelta` append。

## Output

Runner 输出：

- workload 名称
- process id
- warmup / measure 秒数
- ticks/s、ms/tick
- record%、submit%、snapshot%、clear%
- live entity count
- heap delta 与 GC count

脚本 `tools/scripts/profile-commandstream.ps1` 负责：

- 构建 Release runner。
- 启动指定 workload。
- 可选用 `dotnet-trace collect --profile cpu-sampling` 附加采样并生成 `.nettrace`。
- 打印后续 `dotnet-trace report ... topN` 命令。

## Stop Rules

继续优化 CommandStream 的必要条件：

- `CommandStream.*` 在 sampling 中仍有明显 inclusive 占比；且
- 至少一个 CommandStream leaf function 有可观察 exclusive 成本；且
- 热点不主要落在 `World` / `Archetype` / query / GC。

如果 CommandStream 总占比低于约 10-15%，或热点已经转移到存储层，则停止 CommandStream 内部微优化。

## Verification

- `dotnet build -c Release miniArch.sln`
- `dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario create-small4 --warmup 1 --measure 2`
- `dotnet run -c Release --project tools/perf/CommandStream.Profile -- --scenario snapshot-only --warmup 1 --measure 2`
- `dotnet run -c Release --project tools/perf/HeroComing.Perf --check-baseline`（若改动触及 `src/MiniArch/`；本方案首版不触及，可跳过）
