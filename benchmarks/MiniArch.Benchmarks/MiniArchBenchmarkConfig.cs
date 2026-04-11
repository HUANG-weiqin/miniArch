using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Order;

namespace MiniArch.Benchmarks;

public sealed class MiniArchBenchmarkConfig : ManualConfig
{
    public MiniArchBenchmarkConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddExporter(MarkdownExporter.GitHub);
        AddLogger(ConsoleLogger.Default);
        AddJob(Job.ShortRun.WithRuntime(CoreRuntime.Core80).WithId("net8-short"));
        Orderer = new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest);
    }

    public static IConfig Create() => new MiniArchBenchmarkConfig();
}
