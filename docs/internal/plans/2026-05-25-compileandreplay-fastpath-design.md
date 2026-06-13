# CompileAndReplay Fast Path 设计方案

## 目标

- 只优化 `CommandBuffer.CompileAndReplay()` 的 same-world 热路径。
- 保持 `Compile()` 返回可保留 `FrameDelta`、`World.Replay(FrameDelta)` 可跨 world replay 的现有语义不变。
- 优先提升 `DenseExisting` / `MixedScript` / `CreateHeavy` 三类 command-buffer 短跑吞吐。

## 当前问题

- `src/MiniArch/Core/CommandBuffer.cs` 的 `CompileAndReplay()` 先做 `CompileReusableBatch()`，再直接调用通用 `World.Replay(FrameDelta)`。
- 通用 replay 为 retained frame 和 cross-world replay 保留了额外安全检查与解析逻辑，但 same-world 直放路径并不总是需要这些成本。
- 目前最明显的重复工作有三类：
  - created entity 在 compile 阶段已经按 `ComponentType` 排序，但 replay 阶段还会再次构建 signature。
  - same-world replay 仍会逐条执行 `EnsureReplayReservation(...)`、`ResolveCompiledComponentType(...)`。
  - `CompileAndReplay()` 已经拿到 trusted compiled batch，但仍走通用 replay 分支。

## 设计边界

- 保留 `FrameDelta` 作为唯一公开 compiled IR。
- 不为未来功能预留额外抽象，只补 `CompileAndReplay()` 当前确实能用到的数据。
- 不改 command 归约规则，不碰 `Compile()` / `Replay(FrameDelta)` 的顺序语义。

## 方案

### 1. 给 created entity 挂最终 signature

- 修改 `src/MiniArch/Core/FrameDelta.cs` 的 `RawCreatedEntity`，让它同时保存：
  - `Entity`
  - `Signature`
  - `RawComponentValue[]`
- 修改 `src/MiniArch/Core/CommandBuffer.cs` 的 `CreatedEntityState.ToCompiledEntity()`：
  - 继续输出已排序的 `RawComponentValue[]`
  - 直接用排序后的 `ComponentType[]` 构造最终 `Signature`
- 这样 same-world replay 与 retained replay 都可以直接复用 compile 阶段的 final signature，不再重复 `BuildReplaySignature(...)`。

### 2. 给 same-world replay 单独开 trusted fast path

- 在 `src/MiniArch/Core/World.cs` 内部新增只供 `CommandBuffer.CompileAndReplay()` 使用的 fast path。
- fast path 只接受 owning-world trusted batch，避免污染公开 `Replay(FrameDelta)` 语义。
- fast path 里直接使用 compiled command 自带的：
  - reserved entity
  - final created signature
  - recorded `ComponentType`
  - typed `ColumnWriter`
- fast path 跳过：
  - `EnsureReplayReservation(...)` 的逐条重校验
  - created entity 的 `BuildReplaySignature(...)`
  - same-world 下不必要的 `ResolveCompiledComponentType(...)` 回退分支

### 3. 保持通用 replay 不变

- `World.Replay(FrameDelta)` 继续作为 retained / cross-world 的唯一公开入口。
- 它仍保留 reservation 同步检查与 component type 解析回退，保证现有测试和跨 world 语义稳定。

## 预期收益

- `CreateHeavy`：减少 created entity materialize 的重复 signature / type 解析成本。
- `DenseExisting` / `MixedScript`：减少 same-world replay 的 per-command 校验与查表。
- `Compile()` 路径的 smoke 分配不一定明显下降，因为这次重点不是 retained frame 物化，而是 `CompileAndReplay()` 吞吐。

## 风险与控制

- 风险：same-world fast path 和公开 `Replay(FrameDelta)` 语义漂移。
  - 控制：保留 `Play_matches_playback_and_replay_for_randomized_frames` 这类等价测试。
- 风险：created entity signature 改造影响 `FrameDelta.Merge` / `DeepCopyOwnedData()`。
  - 控制：补 focused tests，确保 retained frame 仍能 replay。
- 风险：误把 cross-world 检查删到公开 replay 里。
  - 控制：只在 internal fast path 里跳过检查，不改 public API 行为。

## 验证

- 行为：
  - `tests/MiniArch.Tests/Core/CommandBufferTests.cs`
  - 随机帧等价、buffer reuse、empty play、retained replay 等现有测试
- GC smoke：
  - `tests/MiniArch.Tests/Core/CommandBufferGcVerificationTests.cs`
- 吞吐：
  - `dotnet run --project .\benchmarks\MiniArch.Benchmarks\MiniArch.Benchmarks.csproj -c Release -- command-buffer --full --filter "*MiniArch_CommandBuffer_RecordPlay*"`
