using BenchmarkDotNet.Running;
using System.Diagnostics;

namespace MiniArchBenchmarks;

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

        if (args.Length > 0 && string.Equals(args[0], "throughput-cb", StringComparison.OrdinalIgnoreCase))
        {
            var exitCode = CommandBufferThroughputRunner.RunFromCommandLine(
                args[1..],
                Console.Out,
                Console.Error,
                CancellationToken.None);
            Environment.ExitCode = exitCode;
            return;
        }

        if (args.Length > 0 && string.Equals(args[0], "command-buffer", StringComparison.OrdinalIgnoreCase))
        {
            RunCommandBufferBenchmarks(args[1..]);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, MiniArchBenchmarkConfig.CreateEmpty());
    }

    private static void RunCommandBufferBenchmarks(string[] args)
    {
        var fullMode = HasSwitch(args, "--full");
        var filteredArgs = RemoveSwitch(args, "--full");

        if (fullMode)
        {
            BenchmarkSwitcher.FromTypes(
            [
                typeof(CommandBufferBenchmarks),
                typeof(CommandBufferHierarchyBenchmarks),
            ]).Run(filteredArgs, MiniArchBenchmarkConfig.CreateCommandBufferConfig());
            return;
        }

        RunCommandBufferSmoke();
    }

    private static void RunCommandBufferSmoke()
    {
        var world = new MiniArch.World();
        var existing = new MiniArch.Entity[128];
        for (var i = 0; i < existing.Length; i++)
        {
            existing[i] = world.Create(new BenchmarkPosition(i, i + 1), new BenchmarkVelocity(i + 2, i + 3), new BenchmarkHealth(100 + i));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var stopwatch = Stopwatch.StartNew();
        var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();

        var buffer = new MiniArch.Core.CommandStream(world);
        for (var i = 0; i < existing.Length; i++)
        {
            buffer.Set(existing[i], new BenchmarkPosition(i + 10, i + 11));
            buffer.Set(existing[i], new BenchmarkVelocity(i + 12, i + 13));
            if ((i & 1) == 0) buffer.Remove<BenchmarkHealth>(existing[i]);
            if ((i & 7) == 0) buffer.Destroy(existing[i]);
        }
        buffer.Submit();

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;

        Console.WriteLine($"Command-buffer smoke: existing-heavy (128 entities)");
        Console.WriteLine($"Elapsed: {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"Allocated: {allocated} bytes");
    }

    private static bool HasSwitch(IEnumerable<string> args, string switchName)
    {
        return args.Any(arg => string.Equals(arg, switchName, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] RemoveSwitch(IEnumerable<string> args, string switchName)
    {
        return args.Where(arg => !string.Equals(arg, switchName, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
