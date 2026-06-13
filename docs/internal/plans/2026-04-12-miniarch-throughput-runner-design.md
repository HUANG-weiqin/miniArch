# MiniArch Throughput Runner Design

## 目标

- 在仓库内新增一个可复用的吞吐量对比入口。
- 让 `MiniArch` 和 `Arch` 在相同 workload、相同预热、相同时长下输出稳定的 `ops/s` 对比。
- 首版只覆盖 query workload，但 runner 结构必须允许后续挂接 `CreateMany`、`Remove`、`Destroy` 等热点。

## 非目标

- 不替代 BenchmarkDotNet。
- 不在首版里把所有 hotspot 都接入 throughput runner。
- 不为了吞吐量测试重写现有 benchmark world factory。

## 方案选择

### 方案 A：只做 query 专用 throughput runner

- 优点：
  - 最快落地。
  - 改动最少。
- 缺点：
  - 后续其他热点无法复用。
  - 会和现有 `QueryProfilingRunner` 一样继续按场景复制逻辑。

### 方案 B：做通用 throughput runner，首版只接 query workload

- 优点：
  - 满足复用目标。
  - 后续新增 workload 只需要扩展 factory 和 execute 逻辑。
  - 输出格式可以统一。
- 缺点：
  - 首版结构比方案 A 多一层抽象。

### 推荐

- 采用方案 B。

## 设计

### CLI 入口

- 在 `benchmarks/MiniArch.Benchmarks/Program.cs` 增加：
  - `throughput`
- 命令示例：
  - `dotnet ... throughput --workload query-with-all-entity --engine both --entity-count 100000 --duration 10 --warmup 3 --repeat 5`

### 参数模型

- `workload`
  - `query-with-all-entity`
  - `query-with-all-component-span`
- `engine`
  - `miniarch`
  - `arch`
  - `both`
- `entity-count`
- `duration`
  - 单轮固定时长，单位秒
- `warmup`
  - 每轮测试前预热迭代数
- `repeat`
  - 重复轮数

### 核心抽象

- `ThroughputOptions`
  - 负责 CLI 参数解析
- `ThroughputWorkload`
  - 枚举 workload 名称
- `ThroughputEngine`
  - 枚举 engine 名称
- `IThroughputCase`
  - `WarmUp(int count, CancellationToken)`
  - `long RunIteration()`
  - `string DisplayName`
- `ThroughputCaseFactory`
  - 根据 `workload + engine + entityCount` 创建具体 case
- `ThroughputRunner`
  - 控制 repeat、固定时长循环、汇总和输出

### 输出

- 每轮输出：
  - engine
  - workload
  - repeat index
  - iterations
  - elapsed
  - ops/s
  - checksum
- 汇总输出：
  - avg ops/s
  - median ops/s
  - best ops/s
- 如果包含 `both`：
  - 额外输出 `MiniArch vs Arch` 的平均吞吐差距百分比

## 测试策略

- 为 `ThroughputOptions.TryParse` 写默认值和显式覆盖测试。
- 为 `ThroughputRunner.Run` 写一个小规模、短时长的 smoke test：
  - `query-with-all-entity`
  - `engine=both`
  - `entity-count=1000`
  - `duration=20ms~50ms`
  - 断言每个 engine 都有正吞吐结果。
- 为 compare summary 写格式/数值测试，锁定差距计算。

## 验证

- `dotnet test` 跑新增 tests。
- `scripts/verify.ps1 -Configuration Release`
- 手工运行一次：
  - `scripts/throughput.ps1`

