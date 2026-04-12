# MiniArch Throughput Runner Implementation Plan

1. 文档与入口
   - 新增 throughput runner 设计文档
   - 在 `Program.cs` 增加 `throughput` CLI 分支

2. TDD：参数与汇总
   - 新增 `ThroughputRunnerTests`
   - 先写 `ThroughputOptions.TryParse` 默认值/覆盖测试
   - 先写 compare summary / smoke test

3. 实现 runner
   - 新增 `ThroughputRunner.cs`
   - 实现 options、engine/workload 枚举、result/summarizer
   - 实现固定时长 repeat 循环

4. 实现首版 workload
   - 复用 `BenchmarkWorldFactory`
   - 实现：
     - `query-with-all-entity`
     - `query-with-all-component-span`
   - 同时提供 `MiniArch` / `Arch` case

5. 脚本与知识库
   - 新增 `scripts/throughput.ps1`
   - 新增或更新知识页，记录 throughput workflow
   - 更新 `.knowledge/INDEX.md`

6. 验证
   - 跑新增 tests
   - 跑 `scripts/verify.ps1 -Configuration Release`
   - 跑一次实际 throughput 命令并记录输出
