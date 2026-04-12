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
.\scripts\pack.ps1
```

## 本地包源

- 本仓库的 NuGet 本地源配置在 `NuGet.config`。
- `./scripts/pack.ps1` 会把 `MiniArch` 打成带 XML 文档的包，并输出到 `artifacts/nuget`。
- 之后可以直接让其他项目引用这个本地源来消费最新包。

## API 分层

- `MiniArch`：唯一的默认用户入口，公开 `World`、`Entity`、`QueryDescription` 和 description-based `foreach` 查询。
- `MiniArch.Core`：advanced 类型集合，保留 `Query`、`Chunk`、`CommandBuffer`、`WorldSnapshot`、profiling 等底层能力。

完整 API 参考见 `src/MiniArch/README.md`。

## Agent 入口

1. 读取 `AGENTS.md`。
2. 读取 `.knowledge/INDEX.md`。
3. 打开最相关的 `kb-*.md`。
4. 在宣称完成前运行 `scripts\verify.ps1`。
