# MiniArch

MiniArch 是一个聚焦于简单、完整 archetype ECS runtime 的 C# 项目。

## 先看这里

- 完整 API 手册：`src/MiniArch/README.md`
- 项目知识索引：`.knowledge/INDEX.md`
- 协作约束：`AGENTS.md`

## 仓库结构

- `src/MiniArch`：运行时代码与详细 API README
- `tests/MiniArch.Tests`：运行时行为测试
- `benchmarks/MiniArch.Benchmarks`：BenchmarkDotNet 基准
- `docs/plans`：设计与实现记录
- `.knowledge`：可复用项目知识
- `scripts`：build、test、benchmark、verify 脚本入口

## 常用命令

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\verify.ps1
```

## API 分层

- `MiniArch.Ecs`：普通游戏逻辑默认入口，心智负担更低，默认支持直接 `foreach` 查询。
- `MiniArch.Core`：面向 query 控制、chunk 级访问、command buffer、snapshot、profiling 等 advanced 用法。

完整 API 参考见 `src/MiniArch/README.md`。

## Agent 入口

1. 读取 `AGENTS.md`。
2. 读取 `.knowledge/INDEX.md`。
3. 打开最相关的 `kb-*.md`。
4. 在宣称完成前运行 `scripts\verify.ps1`。
