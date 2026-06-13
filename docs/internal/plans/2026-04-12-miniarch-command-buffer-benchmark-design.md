# MiniArch Command Buffer Benchmark Design

## 目标

- 为 `MiniArch.Core.CommandBuffer` 增加 `MiniArch vs Arch` 的端到端 benchmark。
- 主口径比较 `record + play` 总成本，不拆 `playback/replay`。
- 覆盖双方公共合法命令：`Create / Add / Set / Remove / Destroy`。
- 产出时间与分配对比，并以 `MiniArch <= 1.5x Arch` 作为全部主场景的达标门禁。

## 非目标

- 不把 `Link / Unlink` 混入 `Arch` 对比主口径。
- 不把 setup/world 构建放进测量区。
- 不把 benchmark 当成功能正确性的唯一证明。

## 方案选择

### 方案 A：只做公共子集端到端对比

- 优点：
  - 结果最可解释。
  - 直接回答与 `Arch` 的真实差距。
- 缺点：
  - 无法覆盖 `MiniArch` 特有的 hierarchy 命令。

### 方案 B：公共子集主口径 + MiniArch-only 扩展口径

- 优点：
  - 对外对比干净。
  - 对内仍能压到 `Link / Unlink` 真实场景。
- 缺点：
  - benchmark 结构稍复杂。

### 方案 C：只做随机脚本

- 优点：
  - 覆盖广。
- 缺点：
  - 结果解释和热点定位都更差。

### 推荐

- 采用方案 B。
- 主达标门禁只看 `MiniArch vs Arch` 公共子集场景。
- `MiniArch-only` 扩展场景只用于内部回归，不参与与 `Arch` 的达标判定。

## 场景设计

### 主对比场景

- `dense-existing`
  - 预先创建大量 existing entity。
  - 每帧混合 `Add/Set/Remove`，穿插少量 `Destroy`。
  - 用来观察稳定帧结构变更成本。
- `create-heavy`
  - 每帧大量 `Create`，随后立即 `Add/Set`。
  - 穿插少量 `Destroy`。
  - 用来观察 reservation、created final-state 与 materialize 路径。
- `mixed-script`
  - 用固定种子生成确定性脚本。
  - 同时覆盖 existing entity 与 same-frame created entity。
  - 包含大部分公共合法命令组合。

### 扩展场景

- `miniarch-hierarchy-mixed`
  - 只跑 `MiniArch`。
  - 在 `mixed-script` 基础上加入 `Link / Unlink`。
  - 用于防止 `Play()` 在真实工作负载下回退。

## 档位

- `128 / 1000 / 10000`
- `128` 看固定成本与小规模分配。
- `1000` 看中档位对比稳定性。
- `10000` 看 steady-state 吞吐与规模放大效应。

## 同构输入策略

- 先定义中立脚本模型，再分别翻译成 `MiniArch` 与 `Arch` 的 API 调用。
- 只生成双方都支持的公共命令时，两个引擎共享同一份脚本。
- `MiniArch-only` 扩展场景单独维护，不与 `Arch` 共用结果。
- world 初始化、existing entity 预构建、脚本生成全部放在测量区外。

## 正确性验证

- 新增 parity tests：
  - 同一公共脚本在 `MiniArch` 与 `Arch` 上都能成功执行。
  - 执行后结构摘要一致：
    - live entity count
    - per-component presence
    - selected component values
- benchmark 本身不承担 correctness proof，只消费已经被 parity test 证明合法的场景。

## 达标门禁

- 对每个 `MiniArch vs Arch` 主场景、每个档位，都检查：
  - `Mean(MiniArch) <= 1.5 * Mean(Arch)`
  - `Allocated(MiniArch) <= 1.5 * Allocated(Arch)`
- 任意一项超过 `1.5x`：
  - 继续 profiling / 优化 / 复测
  - 直到全部主场景达标

## 预期改动

- `benchmarks/MiniArch.Benchmarks/CommandBufferBenchmarks.cs`
- `tests/MiniArch.Tests/Core/CommandBufferBenchmarkScenarioTests.cs`
- 可能新增 benchmark 共用脚本/场景 helper
- 如 benchmark 暴露热点，则继续修改：
  - `src/MiniArch/Core/CommandBuffer.cs`
  - `src/MiniArch/Core/World.cs`
  - 或相关 helper

## 验证

- `dotnet test tests/MiniArch.Tests/MiniArch.Tests.csproj --filter FullyQualifiedName~CommandBufferBenchmarkScenarioTests -v minimal`
- `dotnet run --project benchmarks/MiniArch.Benchmarks/MiniArch.Benchmarks.csproj -c Release -- --filter *CommandBuffer*`
- 必要时用 benchmark 结果继续做优化闭环
