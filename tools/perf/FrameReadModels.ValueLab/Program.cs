// FrameReadModels ValueLab — experiments in frame-level chunk read models.
// Uses real MiniArch.World, QueryDescription, and world.Query().GetChunks().
// Runs in Release config only (per AGENTS.md). Debug builds print an error and exit non-zero.
//
// Run:
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full

using FrameReadModels.ValueLab;

#if DEBUG
Console.Error.WriteLine("ERROR: FrameReadModels ValueLab must be built in Release config.");
Console.Error.WriteLine("  dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab");
Environment.ExitCode = 1;
return;
#endif

const string CorrectnessOnlyArg = "--correctness-only";
const string QuickArg = "--quick";
const string FullArg = "--full";
const string HelpArg = "--help";

var correctnessOnly = false;
var quick = false;
var full = false;
var help = false;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (string.Equals(arg, CorrectnessOnlyArg, StringComparison.OrdinalIgnoreCase))
    {
        correctnessOnly = true;
        continue;
    }

    if (string.Equals(arg, QuickArg, StringComparison.OrdinalIgnoreCase))
    {
        quick = true;
        continue;
    }

    if (string.Equals(arg, FullArg, StringComparison.OrdinalIgnoreCase))
    {
        full = true;
        continue;
    }

    if (string.Equals(arg, HelpArg, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase))
    {
        help = true;
        continue;
    }

    Console.Error.WriteLine($"Unknown argument: {arg}");
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

if (help)
{
    PrintUsage();
    return;
}

Console.WriteLine("=== FrameReadModels ValueLab ===");
Console.WriteLine();

if (correctnessOnly)
{
    RunCorrectnessOnly();
    return;
}

if (quick)
{
    Console.WriteLine("Mode: --quick (placeholder)");
    Console.WriteLine();
    FrameReadModelBenchmarks.RunQuick();
    return;
}

if (full)
{
    Console.WriteLine("Mode: --full (placeholder)");
    Console.WriteLine();
    FrameReadModelBenchmarks.RunFull();
    return;
}

// Default: run quick placeholder
Console.WriteLine("Mode: default (runs --quick placeholder)");
Console.WriteLine();
FrameReadModelBenchmarks.RunQuick();

// ────────────────────────────────────────────────────────────────
static void RunCorrectnessOnly()
{
    Console.WriteLine("Running correctness-only smoke tests...");
    Console.WriteLine();

    var pass = FrameReadModelCorrectness.RunAll();

    Console.WriteLine();

    if (pass)
    {
        Console.WriteLine("Correctness: PASS");
    }
    else
    {
        Console.Error.WriteLine("Correctness: FAIL");
        Environment.ExitCode = 1;
    }
}

static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --correctness-only   Run smoke correctness tests (no performance measurement).");
    Console.WriteLine("  --quick              Run abbreviated benchmark set (placeholder in skeleton).");
    Console.WriteLine("  --full               Run comprehensive benchmark set (placeholder in skeleton).");
    Console.WriteLine("  --help               Show this help.");
    Console.WriteLine();
    Console.WriteLine("This is a ValueLab: not public API, Release-only, read-only on MiniArch Core.");
}
