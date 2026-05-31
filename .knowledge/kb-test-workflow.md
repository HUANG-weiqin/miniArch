---
title: Test Workflow
module: MiniArch.Tests
description: How the test suite, query profiling, snapshot benchmarks, and structural-change benchmarks are organized and how to run them
updated: 2026-05-31
---
# Test Workflow

## 这个模块是干什么的

- 这个模块负责：
  - 验证 ECS core 的行为
  - 覆盖实体生命周期、chunk 存储、结构迁移和 query
  - 作为 typed-column / direct-index 重构后的行为回归网
  - 提供 `Create / CreateMany / Add / Set / Remove / Destroy` 的 benchmark 口径
  - 为 `CreateMany` 单独区分 append-only、recycled ids、mixed ids 三类场景
  - 单独保留 query 相关的性能对比口径
  - 提供 query 采样 profiling 的独立入口，便于外部 CPU sampler 定位热点函数
  - 提供固定时长 throughput runner，便于对比 `MiniArch vs Arch` 的真实 `ops/s`
  - 用复杂 archetype 分布覆盖 query filter + traversal 的热路径
  - 提供 snapshot save/load、snapshot bytes 和 `Bytes/entity` 的 benchmark 口径
  - 为 future agent 提供回归判断
- 这个模块不负责：
  - 业务特性设计
  - 核心运行时代码实现

## 架构

- 核心组成：单元测试锁定局部行为，集成测试验证迁移链路，benchmark 对比 Arch / MiniArch 热路径。
- 数据流 / 控制流：
  - 单元测试先锁定局部行为
  - 集成测试再验证迁移链路
  - mixed structural-change benchmark 用固定种子生成同一批 `Create / Add / Set / Remove / Destroy` 操作脚本
  - snapshot benchmark 预先构造"每 entity 10 个 unmanaged 组件"的 world，只测 `WorldSnapshot.Save` / `WorldSnapshot.Load` 本体，并单独导出 `SnapshotBytes`
- benchmark 只比较同构输入下的 Arch / MiniArch 热路径，不承担正确性证明
- setup、world 构建和脚本生成都放在测量区外
- command buffer benchmark 应优先区分 `record`、`playback only`、`replay only`、`play only`、`end-to-end`，避免把 setup/录制/目标阶段混在同一个测量区
- `MiniArch vs Arch` command buffer 主对比现已落成，当前主口径是共享结构命令子集上的 `record + play`
- 共享子集只覆盖 `Create / Add / Set / Remove / Destroy`；`Link / Unlink` 只放在 `MiniArch-only` 扩展 benchmark
- 共享 command buffer 场景先通过 parity tests 锁定最终结构摘要一致，再进入 benchmark；不能跳过这一步直接比较均值
- complex query benchmark 用固定 archetype 配比生成同一类 world 布局，并在测量区内执行 query + 命中 entity 遍历
  - 当前 `EntityCount` 档位覆盖 `128 / 256 / 512 / 1024 / 2048 / 10k / 50k / 100k`，用来同时观察小规模固定成本和大规模 steady-state 吞吐
  - query profiling 复用同一套 complex query world，但在 BenchmarkDotNet 之外跑固定时长循环，给 PerfView/Visual Studio CPU Usage 这类采样器一个干净窗口
- `scripts\verify.ps1` 统一跑 build + test
  - 不直接测试私有实现细节

## 决策

- 每个核心概念一个测试文件，便于按模块定位问题。
- 集成测试只保留一条完整迁移路径，避免重复覆盖。
- 验证脚本和测试项目分离，方便 agent 在需要时只跑局部测试。
- 结构变化相关测试必须保留 `Set` 的 in-place 语义断言，因为这是 typed-column / direct-index 重构的核心安全网。
- command buffer 需要单独锁定 `Compile()` 不改 world、跨 world `Replay()`、created final archetype 和 free-list/version 语义；这些不能靠立即生效 API 测试替代。
- command buffer 还要单独锁定"recording 不提前发布 world layout 变化"：录制 `Add/Set/Remove/Link` 时，不应先把 archetype / query generation 这类内部布局状态改掉。
- command buffer 的 `CompileAndReplay()` 需要锁定与 `Compile()+Replay()` 的结果等价，并用 allocation smoke test 确认 direct replay 少于 retained frame 路径。
- 如果目标是优化 command buffer 的 GC，还应补 allocation smoke test，确认 `CompileAndReplay()` 不为可保留 `FrameDelta` 做额外物化。
- `ArchetypeTests` 需要覆盖"复用前面空掉的 chunk"这一行为，并用结构性测试锁定 `Archetype` 有显式 non-full chunk tracking；否则 `Remove` benchmark 的分配回退和 remove-heavy 后插入扫描大量满 chunk 的退化很难在功能测试里暴露出来。
- `WorldLifecycleTests` 需要覆盖 `EnsureCapacity` 和 `CreateMany`，否则 `Create` 的分配优化和批量语义很容易在重构时被回退。
- `WorldLifecycleTests` 还要覆盖 `CreateMany` 的跨 chunk 顺序和二次批量追加语义，否则批量 reservation 很容易只保住"能跑"而丢掉位置正确性。
- `WorldLifecycleTests` 还要覆盖 `CreateMany` 的 recycled/mixed id 语义，否则 fresh-path 优化很容易掩盖 free-list 路径的行为回退。
- `EntityTests` 和 `WorldLifecycleTests` 需要锁定 entity 句柄契约：`default(Entity)` 非法、fresh entity 从 `Version = 1` 起步、recycled entity 再次创建后版本继续递增。
- `WorldLifecycleTests` 还要覆盖带组件的 `Create<T...>` 直接进入最终 archetype，并锁定当前高性能重载上限 `16`；否则实现很容易退回 `Create + Add` 链路，或者在扩重载时静默漏掉目标 arity。
- `WorldLifecycleTests` 还要覆盖带组件 `Create<T...>` 的 warmed allocation；测试前需 `EnsureCapacity`，否则会把 entity metadata / destroy scratch 增长误判成 archetype lookup 分配。
- `WorldLifecycleTests` 还要锁定默认 world 的 dense archetype 会放大 chunk，而显式 `chunkCapacity` 仍保持固定边界；否则 query 吞吐优化很容易在后续重构中被悄悄回退，或者反过来破坏依赖小 chunk 的测试。
- `ArchetypeEdges` 的 direct-index 化是性能目标本身，可以用一条小范围的结构测试锁定，避免静默退回字典实现。
- `ChunkTests` 需要同时覆盖"引用类型列会清尾槽位"和"含引用字段的 struct 也会清尾槽位"；否则删除路径很容易被错误地简化成 `IsValueType` 判断。
- `ChunkTests` 需要锁定 component lookup 读路径不保存 `_lastComponentId/_lastColumnIndex` 这类共享可变 last-lookup cache；这类优化会破坏同一 chunk 的并发只读契约。
- `UserQueryTests` 需要覆盖 `OrderBy(Comparison<Entity>)`、`OrderBy(IComparer<Entity>)` 和同一 ordered query 的并发枚举；排序 comparer 可捕获 world 做只读组件访问，但不能写 world。
- `WorldLifecycleTests` / `CommandBufferTests` 需要锁定泛型 component-type cache 不再以 registry + id 两个 static 字段表达，避免跨 world 或并发首次缓存时形成错配。
- mixed structural-change benchmark 默认使用 `20/20/20/20/20` 的均衡分布，并用固定种子生成同一条随机脚本。
- `CreateMany` benchmark 不能只测 fresh append-only；必须把 recycled ids 和 mixed ids 分开跑，否则无法判断优化是否只对 `_freeIds.Count == 0` 的快路径有效。
- benchmark 必须同时看时间和分配，不能只看平均耗时。
- command buffer benchmark 至少要覆盖一个小档位和一个大档位，否则很难区分固定分配与规模放大效应。
- command buffer `MiniArch vs Arch` 的验收门禁是：所有共享场景、所有档位上，`MiniArch` 的时间和分配都不能慢于 `Arch` 超过 `1.5x`
- snapshot benchmark 的大小指标必须和时间分开导出，不能靠日志打印混进计时结果。
- mixed `CreateMany` benchmark 看到巨大离群值时，要先排除 metadata 扩容边界；必要时结合单次调用诊断看 `Capacity` 变化、分配字节和 GC 代际计数，再判断是不是 free-list 热点本身。
- `QueryTests` 需要覆盖"热缓存后的同一 query 并发枚举"和"冷缓存首次并发 materialize"两类只读场景；否则 query 的 copy-on-write 发布容易退回共享可变缓存。
- `QueryDescription` 新增后，`QueryTests` / `QueryFilterTests` 还要覆盖 description 与 generic/builder 查询等价、同一 description 跨 world 复用、description 冷路径并发 materialize，以及公开类型视图不会暴露可变内部数组；否则"可复用描述 + 缓存 key"这层契约很容易被悄悄打破。
- `CommandBufferTests` 需要同时覆盖 existing entity replay、created entity final-state、same-frame create+destroy 消除和并发 recording；否则很难看出 replay 顺序和 entity reservation 是否被悄悄改坏。
- `CommandBufferTests` 里的 `CompileAndReplay()` 不应只跑手写 happy-path，还要覆盖复杂/随机脚本，避免短路径在 hierarchy 或 created/destroyed 混合帧上回退。
- 如果目标是优化 command buffer 的 GC，除了行为测试，还要加 warmed allocation smoke test，至少锁定 `World.Destroy(...)`、`World.Replay(FrameDelta)` 和 `CommandBuffer.CompileAndReplay()` 的 steady-state 分配回退。
- benchmark 入口如果默认会跑完整 `command-buffer` 矩阵，容易在本地验证时卡死；更稳妥的是默认 smoke、完整 BenchmarkDotNet 用显式 `--full`。
- 对会直接 mutate world 的 `record + play` 基准，必须避免让同一个 iteration 内的多次 workload 复用同一份 world state；当前仓库通过 `command-buffer` 专用 benchmark 子命令隔离这组口径
- complex query benchmark world 应该优先用 direct-create 先落 `Position/Velocity/Team` 这类 query 核心组件，再补剩余组件；否则 `Create + Add + Add + ...` 会留下过多历史空 archetype，污染 query benchmark。
- complex query benchmark 不能只测 query builder 创建；必须测实际 query 执行。
- complex query benchmark 的命中组件要放在最终 archetype 的后半段构建，避免 `MiniArch` 的迁移中间态空 archetype 混入结果。
- query benchmark 需要同时保留 mixed 口径和 warmed 口径：mixed 反映 public query API 的整体成本，warmed 反映 steady-state traversal；两者不能互相替代。
- 如果目标是看 entity-only 遍历热路径，benchmark 应优先用 chunk 的实体 span 视图，而不是在热循环里重复调用 `GetEntity(row)`；否则结果会掺入 accessor 固定成本。
- 如果目标是看 component-consuming query，benchmark 应同时保留 `row-wise` 和 `span` 两条口径；对 typed chunk，`GetComponentSpan<T>()` 往往比逐 row `GetComponent<T>()` 快一个量级，适合先做 A/B 再决定是否推广到系统层遍历。
- 如果 query benchmark 把 builder 和执行放在同一个测量区，就要额外准备一条 steady-state benchmark：预热 query cache，只测 refresh 后的 chunk/entity 遍历。否则固定 build 分配会掩盖真正的读路径差距。
- 如果 warmed query benchmark 的 short job 方差过大，尤其是 `50k/100k` 档位，不要只看一份 BenchmarkDotNet 均值；再用 `profile-query --temperature hot` 跑固定时长热循环，比较 `Completed iterations` 作为 traversal-only 的二次验证。
- 如果目标是回答"MiniArch 和 Arch 的真实吞吐差多少"，优先补一条 throughput runner，而不是继续放大 short job 次数；`ops/s` 对这类问题更直接。
- query 采样入口默认应走独立 runner，而不是直接采样 BenchmarkDotNet 子进程；前者更容易把样本集中在 `Query` 热路径上。
- query 采样需要区分 `hot` 和 `cold`：`hot` 看 chunk traversal，`cold` 通过 fresh query 实例把 `RefreshIfNeeded / BuildMatchingArchetypes / Matches` 拉回样本。

## 当前已验证的 benchmark 结论

- `StructuralChangeBenchmarks` 里，`MiniArch` 在 `Create / CreateMany / Set / Destroy / Remove` 上整体优于 `Arch`；`Add Position` 这一项当前仍偏慢，且波动较大，需要单独盯住
- `StructuralChangeBenchmarks` 里的 `Create(Position, Velocity)` 现在已经补齐 Arch 同构基准，最新短跑结果里 `MiniArch` 约 `33.74 us`，`Arch` 约 `39.81 us`，MiniArch 仍领先
- `StructuralChangeBenchmarks` 现在包含 `MiniArch_Create_PositionVelocity`，用于单独观察 typed `Create<T1,T2>` 的时间和分配回归
- `MixedStructuralChangeBenchmarks` 里，`MiniArch` 在 `100 / 1000 / 10000` 三个档位都稳定快于 `Arch`，大致领先 `42%~48%`
- `QueryBenchmarks` 里，`MiniArch` 在 `WithAll+Without`、`WithAll+Any` 这类过滤更重的 warmed 查询上领先，但在 `100000` 档位的纯 warmed entity/span traversal 上落后 `Arch`
- `QueryBenchmarks` 的 `row-wise` 口径明显慢于 `span` 口径，说明对 typed chunk，span 读取仍然是更优的系统层遍历方式

## 认知模型

- 理解这个模块时，应该把它看成：
  - 运行时行为的安全网
- 这个模块里最重要的抽象是：
  - 行为断言
  - 回归信号
- 常见误解：
  - 认为测试只要能编译就够了
  - 认为集成测试可以代替对关键路径的单元测试

## 入口

- 第一次读或加功能，先看：
  - `IntegrationTests.cs`：最完整的端到端例子
  - `CommandBufferTests.cs`：command buffer 专属语义、跨 world replay、并发 recording
  - `WorldStructuralChangeTests.cs`：结构迁移的关键行为
  - `QueryTests.cs`：query 行为约束与可见性
  - `QueryFilterTests.cs`：链式 filter 和 builder 契约
  - `ChunkTests.cs`：存储密度约束
  - `ArchetypeTests.cs`：chunk 复用和可写 chunk 选择策略
  - `StructuralChangeBenchmarks.cs`：`Create / CreateMany / Add / Set / Remove / Destroy` 与 Arch 的时间和分配对照
  - `CommandBufferBenchmarks.cs`：`record + play`、hierarchy 性能入口
  - `CommandBufferSharedScenarios.cs`：共享脚本模型、MiniArch/Arch parity 执行器和结构摘要 helper
  - `QueryBenchmarks.cs`：复杂 query 场景下的 Arch / MiniArch 执行对照
  - `SnapshotBenchmarks.cs`：`WorldSnapshot.Save` / `Load` 的时间、分配、`SnapshotBytes` 和 `Bytes/entity`
  - `ComplexQueryBenchmarkScenarioTests.cs`：benchmark world shape、命中比例和 `>= 8 component` 契约
  - `scripts\profile-query.ps1`：query 采样入口，适合定位热点函数，支持 `--workload entity|component-row-wise|component-span`
- 修 bug，先看：
  - 对应功能的测试文件
  - `scripts\test.ps1`
  - `scripts\benchmark.ps1`：benchmark 入口，必要时配合 `--filter`
  - 也可以直接运行：`dotnet run --project .\benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj -c Release -- command-buffer`
  - `scripts\profile-query.ps1`：如果问题是 query 热点分布，而不是均值回归，支持 `--workload entity|component-row-wise|component-span`

## 坑点

- 历史上容易出问题的地方：
  - 只跑局部测试，没看整体迁移是否破坏
  - 只跑 `CommandBufferTests`，却没回归 lifecycle / structural-change / query，容易漏掉对 world 基础契约的破坏
  - 断言太宽泛，漏掉 chunk 级行为
  - 只看运行时，不看分配和 GC
  - `Remove` 只看时间变快，却没发现 archetype 没复用已有空 chunk，导致分配被隐藏放大
  - `Create` 只看时间，不看 entity metadata 扩容带来的分配回退
  - 加了 `CreateMany` 却没把它纳入 benchmark，导致 bulk path 长期失真
  - `CreateMany` 只看分配下降，却没确认是否还在逐实体落位，导致 bulk time 仍明显慢于 Arch
  - `CreateMany` 只测 append-only，误把 fresh-path 成绩当成所有 free-list 场景的结论
  - 带组件 `Create<T...>` 只断言组件值正确，却没检查 query 看到的 archetype 集合，容易漏掉"功能正确但留下空中间 archetype"的回退
  - 混合 benchmark 没有固定种子，导致 MiniArch 和 Arch 的输入不一致
  - snapshot benchmark 把 world 构建或 byte[] 预生成算进了 save/load 时间，导致结果失真
  - query 并发测试只覆盖"缓存已热"的读取，而没压到"第一次并发建 query / 刷新快照"的冷路径
  - chunk 读路径上普通字段组成的 cache pair 即使每个字段都是 `int`，也不能视为并发安全；两个字段之间没有原子一致性，另一个线程可能看到新 component id 和旧 column index
  - 泛型静态 cache 如果用两个字段共同表达"这个 world/registry 下的 component id"，也会有同类错配风险；测试应锁定单 entry 发布形态
  - complex query benchmark 如果从空实体一路逐组件 `Add` 到终态，最终 world 会残留大量空 archetype，导致 query benchmark 测到的不是"最终 world 上的 query"，而是"历史迁移残留 + query"的混合成本
  - query benchmark 只测 builder 创建，误把 API 组装成本当成 query 热路径
  - query benchmark 在 MiniArch 里把命中组件加得太早，导致中间态空 archetype 也被扫进结果
  - query benchmark 只保留 builder+execute 的混合口径时，很难分清"固定分配来自 fluent builder"还是"规模退化来自 chunk/row 遍历"；要至少保留一条 warmed-query 口径。
  - **零分配测试的 warmup 必须循环多次（≥10），不能只调一次**。.NET 8 JIT tiered compilation 会在 warmup 后的首次调用时把方法从 Tier 0 升级到 Tier 1，这个升级过程会分配 JIT 内部数据结构。单次 warmup 不足以触发升级，导致测量循环的第一次调用出现 264 bytes 的假分配。经验值：warmup 循环 10 次后再 GC.Collect，可确保 JIT 完成升级。
  - 如果 warmed query state 没有在 setup 阶段先 materialize 匹配 archetype，benchmark 名字虽然写着 warmed，实际测到的仍会掺入冷 refresh 成本。
  - 直接采样 BenchmarkDotNet 子进程时，样本里会混入 harness、warmup 和 fork 开销；定位 query 热点时优先跑 `scripts\profile-query.ps1`
  - 如果想看 archetype 匹配热点，不要只跑 `hot` 模式；热缓存会把刷新成本藏掉
  - 如果 throughput/profiling 显示 query steady-state 慢，但 matched chunk 数又远高于 Arch，优先怀疑 archetype chunk 粒度，而不是继续在 query matching 上做微调。
- 容易误判的地方：
  - 认为 query 结果对了，chunk 顺序就一定对了
  - 认为 entity 还活着，version 也一定没错
  - 以为 `dotnet test --filter` 能绕过测试工程里的编译错误；实际上测试项目先能编译，filter 才有意义
- 改这里时要特别小心：
  - 测试名要稳定，方便 agent 用 `--filter` 定位
  - 集成测试不要过度依赖实现细节
  - `Set` 相关测试要先确认核心是否已经切到 typed-column / direct-index；如果没有，先保留适配点，不要伪造新行为
  - benchmark 输出要和 Arch 在相同 entity 布局、相同操作脚本下对齐，否则对比没有意义
  - 运行 BenchmarkDotNet 时尽量从 `Release` 入口触发；虽然 BenchmarkDotNet 会单独编译 benchmark 可执行文件，但 debug host 警告会污染阅读
  - 当前仓库里如果 benchmark 场景测试本身编译失败，必须先把它和本次功能验证分开说明，不要把 query 回归结果和现有编译阻塞混为一谈
  - `FrameDelta` 当前可以保留后重放，但跨 world replay 仍要求双方从同一初始态按相同 frame 顺序推进；benchmark 不应把"偏离同步前提后的失败"误判成功能回归

