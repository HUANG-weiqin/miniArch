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

        if (args.Length > 0 && string.Equals(args[0], "throughput", StringComparison.OrdinalIgnoreCase))
        {
            var exitCode = ThroughputRunner.RunFromCommandLine(
                args[1..],
                Console.Out,
                Console.Error,
                CancellationToken.None);
            Environment.ExitCode = exitCode;
            return;
        }

        if (args.Length > 0 && string.Equals(args[0], "command-buffer", StringComparison.OrdinalIgnoreCase))
        {
            BenchmarkSwitcher.FromTypes(
            [
                typeof(CommandBufferBenchmarks),
                typeof(CommandBufferHierarchyBenchmarks),
            ]).Run(args[1..], MiniArchBenchmarkConfig.CreateCommandBufferConfig());
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, MiniArchBenchmarkConfig.Create());
    }
}
