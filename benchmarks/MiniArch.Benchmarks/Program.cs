using BenchmarkDotNet.Running;

namespace MiniArch.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "profile-query", StringComparison.OrdinalIgnoreCase))
        {
            var exitCode = QueryProfilingRunner.RunFromCommandLine(
                args[1..],
                Console.Out,
                Console.Error,
                CancellationToken.None);
            Environment.ExitCode = exitCode;
            return;
        }

        BenchmarkSwitcher.FromTypes(
                [
                    typeof(QueryBenchmarks),
                    typeof(StructuralChangeBenchmarks),
                    typeof(MixedStructuralChangeBenchmarks),
                    typeof(SnapshotBenchmarks),
                ])
            .Run(args, MiniArchBenchmarkConfig.Create());
    }
}
