# FrameReadModels ValueLab

## 结论先行

本实验探索 Frame Read Models：在 Snapshot 稳定后，从 `world.Query(desc).GetChunks()` 和组件 spans 构建帧内派生索引；Build 一次，随后执行大量只读按 key 查询。实验数据只用于判断这条能力是否值得产品化。

**这是一个 ValueLab，不是 public API。** 实验代码不进入 `src/MiniArch/`，修改 Core 需要单独的准备工作和同意。

## 约束

- **Release 构建**：AGENTS.md 性能基准铁律要求所有测量使用 `-c Release`。
- **Debug 构建**：打印错误并以非 0 退出，防止意外使用错误配置的测量结果。
- **禁止修改 Core**：不动 `src/MiniArch/**` 或 `tests/HeroPipeline.Tests/**`。
- **只读模式**：对 MiniArch 的查询 API 只读调用，不做 structural change 测量（structural change 在其他 bench 中）。

## 运行

```bash
# 默认：--quick placeholder
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab

# 正确性烟雾测试
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only

# 缩写基准集（骨架阶段为 placeholder）
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick

# 完整基准集（骨架阶段为 placeholder）
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full

# 帮助
dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --help
```

## 参数

| 参数 | 说明 |
|---|---|
| `--correctness-only` | 运行烟雾正确性测试（使用真实 World + Query + GetChunks） |
| `--quick` | 缩写基准集（骨架：占位打印） |
| `--full` | 完整基准集（骨架：占位打印） |
| `--help` | 显示帮助 |

## 正确性烟雾测试

`--correctness-only` 运行三个场景：

1. **SinglePosition**：创建 1 个带 Position 的实体，查询确认可读到。
2. **PositionVelocity**：创建 10 个带 Position+Velocity 的实体，查询确认全部可读到。
3. **MixedArchetypes**：创建两种 archetype（Position only + Position+Velocity），查询 Position 确认跨 archetype 正确聚合。

全部 PASS 则打印 `Correctness: PASS`。

## 组件模型

- `Position` (float X, Y)
- `Velocity` (float Dx, Dy)

仅用于布局实验，不是框架公共 API。

## 安全

- 不参与 lockstep、FrameDelta、Snapshot、Checksum、Replay。
- host-local / optional / non-deterministic / non-thread-safe。
