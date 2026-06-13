# Command Buffer Replay/Rewind Benchmark Design

## 结论

- 推荐新增一组独立的 `MiniArch-only` command buffer replay/rewind benchmark，而不是把 rewind 直接塞进现有 `MiniArch vs Arch` benchmark。
- 新 benchmark 的主口径至少包含：`Playback only`、`ReplayWithReverse`、`Rewind only`、`ReplayWithReverse+Rewind`。
- 场景至少保留三类：
  - `existing-heavy`：existing entity 为主，压旧值捕获与 restore。
  - `create-or-mixed-heavy`：大量 create，或 existing/create 混合，压 reservation、created final-state 与 rewind 后再 replay。
  - `hierarchy`：`MiniArch-only` 扩展场景，用 `Link/Unlink` 与 subtree destroy 压 hierarchy rewind 顺序。
- 不与 `Arch` 比 rewind。`Arch` 当前没有等价的 `ReplayWithReverse(...)` / `PlayWithReverse()` / `Rewind(...)` 契约；硬做对比只会把 snapshot/undo 模拟成本混进结果，失去可解释性。
- 现有 `MiniArch vs Arch` benchmark 继续回答共享命令子集上的 `record + play` 差距；新 benchmark 负责回答 `MiniArch` 自己在 replay/reverse/rewind 各阶段的时间与分配结构。

## 现状缺口

- 当前仓库已经有 command buffer benchmark，但主口径仍是共享结构命令子集上的 `record + play`。
- 当前 benchmark 缺少 replay/rewind 的拆分信号：
  - 没有 `replay-only` / `ReplayWithReverse` 基准。
  - 没有 `rewind-only` 基准。
  - 没有“一次 forward + reverse 周期”基准。
- 当前 worktree 已经落了 `ReplayWithReverse(...)`、`PlayWithReverse()`、`Rewind(...)` 与对应测试，但 benchmark 还不能回答：
  - reverse capture 本身占多少。
  - rewind 本身占多少。
  - existing-heavy 与 create-heavy/mixed-heavy 下，forward/reverse 哪一段是主热点。
  - hierarchy 场景的回退成本是否明显高于纯结构命令场景。

## 目标

- 为 command buffer 新增 replay/rewind benchmark，分离 `Playback`、forward apply、reverse capture、rewind 的成本。
- 让 benchmark 结果能直接支持后续优化判断：是该优化 compile、forward replay、reverse capture，还是 rewind 执行。
- 保持 setup、world 构建、脚本生成、预热都在测量区外。
- 同时输出时间和分配，不只看均值耗时。

## 非目标

- 不改动现有 `MiniArch vs Arch` `record + play` 主 benchmark 的达标定义。
- 不把 rewind benchmark 设计成 `Arch` 对照基准。
- 不把 benchmark 当成功能正确性的唯一证明；正确性仍由测试先锁定。
- 不在本次 benchmark 里引入 snapshot rollback、throughput runner 或采样 profiler 新入口。

## 方案对比

### 方案 A：继续扩展现有 `MiniArch vs Arch` benchmark，把 rewind 也做成跨引擎对比

- 做法：给 `Arch` 额外补一层模拟 undo/snapshot，再和 `MiniArch` 的 rewind 一起比较。
- 优点：表面上能得到跨引擎数字。
- 缺点：
  - `Arch` 没有等价 rewind API，比较对象已经不是“同一语义下的不同实现”。
  - 很容易把 snapshot、外部 undo log 或额外脚本转换成本混进结果。
  - 无法公平对齐 reserved entity 轨迹、same-frame create+destroy rollback、hierarchy rewind 这些 `MiniArch` 特有语义。

### 方案 B：新增独立 `MiniArch-only` replay/rewind benchmark

- 做法：保留现有 `MiniArch vs Arch` `record + play` benchmark 不动，另加一组只测 `MiniArch` 的 `Playback only`、`ReplayWithReverse`、`Rewind only`、`ReplayWithReverse+Rewind`。
- 优点：
  - 结果最干净，直接回答 `MiniArch` 自身 replay/reverse/rewind 的成本结构。
  - 与当前已经落地的 `ReplayWithReverse(...)` / `Rewind(...)` 语义完全对齐。
  - 易于在 existing-heavy、create/mixed-heavy、hierarchy 三类场景上做分档分析。
- 缺点：
  - 不会产出新的跨引擎 rewind 数字。

### 方案 C：只加 `ReplayWithReverse+Rewind` 端到端 benchmark，不拆分阶段

- 做法：只测一次 forward+reverse 周期。
- 优点：实现最省。
- 缺点：
  - 无法定位成本到底来自 `Playback`、reverse capture，还是 `Rewind`。
  - 一旦结果回退，后续 profiling/优化方向不清晰。

## 推荐方案

- 采用方案 B。
- 原因：当前缺的不是“再多一条总分”，而是 replay/reverse/rewind 的拆分信号。
- 具体策略：
  - 继续保留现有 `MiniArch vs Arch` `record + play` benchmark，作为共享命令子集上的对外比较口径。
  - 新增独立 `MiniArch-only` benchmark，专门覆盖 replay/reverse/rewind。
  - hierarchy 只进 `MiniArch-only` 口径，不进入跨引擎 pass/fail。

## 为何不与 Arch 比 Rewind

- `Arch` 当前没有公开等价的 `ReplayWithReverse(...)`、`PlayWithReverse()`、`Rewind(...)`。
- `MiniArch` rewind 依赖的不是普通“撤销命令”，还包含：
  - reverse capture 时读取 existing entity 旧组件值。
  - destroy subtree 前捕获旧 hierarchy。
  - reserved entity 轨迹恢复，保证 `ReplayWithReverse -> Rewind -> ReplayWithReverse` 仍可重放。
- 若为 `Arch` 额外造一层 snapshot 或自定义 undo log，比较结果就变成：
  - `MiniArch` 原生 rewind 实现
  - 对比 `Arch` 外挂模拟方案
- 这类结果既不能回答共享命令热路径差距，也不能回答 `MiniArch` rewind 自身是否高效，解释价值很低。

## 口径设计

### 必选口径

- `Playback only`
  - 只测 `CommandBuffer.Playback()`。
  - 目的：观察 compile / frame materialization 成本。
- `ReplayWithReverse`
  - 输入为预先构造好的 `FrameCommands`，只测 `world.ReplayWithReverse(in frame)`。
  - 目的：观察 forward apply + reverse capture 的合并成本。
- `Rewind only`
  - setup 先执行一次 `ReplayWithReverse(...)` 并保留 `ReverseFrameCommands`；测量区只执行 `world.Rewind(in reverse)`。
  - 目的：隔离纯回退成本。
- `ReplayWithReverse+Rewind`
  - 一次完整 forward+reverse 周期。
  - 目的：回答一帧应用后立刻回退的总成本。

### 可选补充口径

- `PlayWithReverse`
  - 用来对照 `Playback()+ReplayWithReverse(...)` 是否仍保有分配优势。
- `PlayWithReverse+Rewind`
  - 用来回答 owning-world 短路径在“应用后立即回退”时是否仍值得保留。

## 场景选择

### 1. `existing-heavy`

- world 预先构造大量 existing entity。
- 每轮主要执行 `Set/Add/Remove`，穿插少量 `Destroy`。
- 不强调 `Create`，重点压：
  - existing entity 旧值采集。
  - old archetype 恢复。
  - destroy existing entity 后的 restore。
- 这是 replay/reverse 的基础场景，必须保留。

### 2. `mixed-heavy`

- existing entity 与 newly-created entity 混合。
- 包含：
  - created survivor。
  - same-frame `create + destroy` transient entity。
  - existing entity 的 `Set/Remove/Destroy`。
- 重点压：
  - created final-state。
  - reservation 轨迹恢复。
  - rewind 后再次 replay 的稳定性。
- 如果实现上更容易先落 `create-heavy`，也可以先以 `create-heavy` 起步，但最终推荐保留 `mixed-heavy`，因为它更接近真实 frame。

### 3. `hierarchy`（推荐作为扩展场景）

- 只跑 `MiniArch`。
- 在 `mixed-heavy` 基础上加入 `Link/Unlink`、destroy subtree。
- 重点压：
  - reverse hierarchy capture。
  - restore old parent/children。
  - rewind 安全顺序是否带来额外固定成本。
- 这个场景不参与任何 `Arch` 对比门禁，只服务于 `MiniArch` 自身回归。

## 档位

- 推荐沿用 `128 / 1000 / 10000`。
- `128` 观察固定成本和小规模分配。
- `1000` 观察中档稳定性。
- `10000` 观察规模放大后的 steady-state 成本。

## 数据与测量边界

- setup 放在测量区外：
  - world 初始化。
  - existing entity 预构建。
  - benchmark 脚本生成。
  - `Playback only` 之外所需的预编译 `FrameCommands`。
  - `Rewind only` 所需的已应用 world 与 `ReverseFrameCommands`。
- 测量区内只做目标动作，避免把 world 构建或脚本生成混进去。
- 对会 mutate world 的 benchmark，要继续按 iteration 重建 state，避免同一份 world 被重复消费后失真。

## 验证方法

### 功能验证

- 先用测试锁定 benchmark 场景本身，而不是直接跑 benchmark 看均值。
- 推荐新增 `CommandBufferBenchmarkScenarioTests.cs`，至少验证：
  - `existing-heavy` / `mixed-heavy` / `hierarchy` 场景可成功执行。
  - `ReplayWithReverse -> Rewind` 后公开可观察状态恢复。
  - `mixed-heavy` 在 rewind 后可再次 replay，并得到同一结果。
  - hierarchy 场景下 old parent/children 与 subtree restore 正确。

### benchmark 验证

- BenchmarkDotNet 结果至少检查：
  - 每个口径都有时间与分配输出。
  - 各档位结果稳定，不缺项。
  - `ReplayWithReverse+Rewind` 大致可由 `ReplayWithReverse` 与 `Rewind only` 解释，若明显异常再查额外固定成本。
- 这组 benchmark 不设 `Arch <= 1.5x` 类跨引擎门禁；主要验收是口径齐全、场景可解释、结果可复现。

## 运行命令

### 测试

```powershell
dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter "FullyQualifiedName~CommandBufferTests|FullyQualifiedName~WorldStructuralChangeTests|FullyQualifiedName~QueryTests|FullyQualifiedName~CommandBufferBenchmarkScenarioTests" -v minimal
```

### benchmark

```powershell
pwsh ./scripts/benchmark.ps1 -Filter "*CommandBuffer*"
```

如果后续把 replay/rewind benchmark 独立成单独类型，推荐进一步收窄：

```powershell
dotnet run --project benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release -- --filter "*CommandBuffer*Rewind*"
```

## 风险

- `Rewind only` 很容易误把 setup 里的首次 apply 或 reverse capture 混进测量区，必须严格隔离。
- `mixed-heavy` 若脚本过随机，结果解释会变差；应保持固定种子与固定比例。
- hierarchy 场景如果把“旧子树恢复”和“本帧新建后又级联销毁的节点”混在一起，会让结果难解释。
- `Playback only` 与 `ReplayWithReverse` 使用的脚本模型若不一致，分段结果就无法拼回总成本。
- 如果仅看时间不看分配，可能漏掉 reverse capture 或 rewind restore 的 GC 回退。

## 落地建议

- benchmark 结构上建议新增一组独立类型，例如 `CommandBufferReplayRewindBenchmarks`，不要把所有新口径继续堆进现有 `record + play` 类型里。
- 场景工厂应复用现有 command buffer shared scenario 思路，但 rewind benchmark 的状态对象应显式区分：
  - `PlaybackState`
  - `ReplayWithReverseState`
  - `RewindState`
  - `ReplayRewindCycleState`
- 文档和知识页中要明确：
  - `MiniArch vs Arch` 仍主看共享命令 `record + play`。
  - replay/rewind benchmark 是 `MiniArch-only` 的内部性能信号，不承担跨引擎胜负判断。
