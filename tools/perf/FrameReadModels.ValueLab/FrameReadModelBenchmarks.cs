// Benchmark placeholders for FrameReadModel ValueLab.
// Concrete benchmark implementations will be added in later tasks.

using MiniArch;

namespace FrameReadModels.ValueLab;

/// <summary>
/// Placeholder benchmark runner for FrameReadModel scenarios.
/// Currently prints a description and returns a zero result.
/// Full benchmarks will be implemented later per the plan.
/// </summary>
internal static class FrameReadModelBenchmarks
{
    /// <summary>
    /// Runs the "quick" benchmark set (abbreviated).
    /// Returns the number of completed benchmark iterations (for smoke verification)
    /// or -1 if the mode is not yet implemented.
    /// </summary>
    public static int RunQuick()
    {
        Console.WriteLine("[FrameReadModelBenchmarks] Quick bench set — not yet implemented (placeholder).");
        Console.WriteLine("[FrameReadModelBenchmarks] Would run small-scale frame read model benchmarks.");
        return -1;
    }

    /// <summary>
    /// Runs the "full" benchmark set (comprehensive).
    /// Returns the number of completed benchmark iterations (for smoke verification)
    /// or -1 if the mode is not yet implemented.
    /// </summary>
    public static int RunFull()
    {
        Console.WriteLine("[FrameReadModelBenchmarks] Full bench set — not yet implemented (placeholder).");
        Console.WriteLine("[FrameReadModelBenchmarks] Would run comprehensive frame read model benchmarks across all layouts.");
        return -1;
    }
}
