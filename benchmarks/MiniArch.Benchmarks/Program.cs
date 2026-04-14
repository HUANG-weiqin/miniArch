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

        if (args.Length > 0 && string.Equals(args[0], "command-buffer", StringComparison.OrdinalIgnoreCase))
        {
            RunCommandBufferBenchmarks(args[1..]);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, MiniArchBenchmarkConfig.Create());
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
                typeof(CommandBufferReplayRewindBenchmarks),
                typeof(CommandBufferWorldDeltaBenchmarks),
            ]).Run(filteredArgs, MiniArchBenchmarkConfig.CreateCommandBufferConfig());
            return;
        }

        RunCommandBufferSmoke();
    }

    private static void RunCommandBufferSmoke()
    {
        var scenario = new CommandBufferReplayScenarioDefinition(
            "existing-heavy-smoke",
            CommandBufferReplayScenarioKind.ExistingHeavy,
            128);

        using var playback = CommandBufferReplayScenarios.PreparePlayback(scenario);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var frame = playback.Buffer.Playback();
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;

        Console.WriteLine($"Command-buffer smoke: {scenario.Name}");
        Console.WriteLine($"Elapsed: {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"Allocated: {allocated} bytes");
        Console.WriteLine($"Frame commands: {frame.CreatedEntities.Count} created, {frame.AddCommands.Count} adds, {frame.SetCommands.Count} sets, {frame.RemoveCommands.Count} removes, {frame.DestroyedEntities.Count} destroys");
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
