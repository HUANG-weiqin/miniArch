using BenchmarkDotNet.Running;

namespace MiniArch.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromTypes(new[]
            {
                typeof(QueryBenchmarks),
                typeof(StructuralChangeBenchmarks),
                typeof(MixedStructuralChangeBenchmarks),
                typeof(SnapshotBenchmarks),
            })
            .Run(args, MiniArchBenchmarkConfig.Create());
    }
}
