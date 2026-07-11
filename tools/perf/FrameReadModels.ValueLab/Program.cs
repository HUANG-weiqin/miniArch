// FrameReadModels ValueLab — experiments in frame-level chunk read models.
// Uses real MiniArch.World, QueryDescription, and world.Query().GetChunks().
// Runs in Release config only (per AGENTS.md). Debug builds print an error and exit non-zero.
//
// Run:
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000
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
const string NArg = "--n";
const string HelpArg = "--help";

var correctnessOnly = false;
var quick = false;
var full = false;
var help = false;
var nValue = 1000; // default test size
var hasN = false;

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

    if (string.Equals(arg, NArg, StringComparison.OrdinalIgnoreCase))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine("ERROR: --n requires an integer argument.");
            PrintUsage();
            Environment.ExitCode = 2;
            return;
        }
        if (!int.TryParse(args[i + 1], out nValue) || nValue <= 0)
        {
            Console.Error.WriteLine($"ERROR: invalid --n value: {args[i + 1]}");
            Environment.ExitCode = 2;
            return;
        }
        i++; // skip the value
        hasN = true;
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
    if (hasN)
    {
        Console.WriteLine($"Using --n {nValue} for correctness tests");
        FrameReadModelCorrectness.SetSize(nValue);
    }
    RunCorrectnessOnly();
    return;
}

if (quick)
{
    Console.WriteLine("Mode: --quick (not yet implemented — runs correctness only)");
    Console.WriteLine();
    FrameReadModelCorrectness.SetSize(1000);
    RunCorrectnessOnly();
    return;
}

if (full)
{
    Console.WriteLine("Mode: --full (not yet implemented — runs correctness with 1M smoke)");
    Console.WriteLine();
    FrameReadModelCorrectness.SetSize(1000000);
    RunCorrectnessOnly();
    return;
}

// Default: run correctness with quick size
Console.WriteLine("Mode: default (runs correctness, quick size)");
Console.WriteLine();
FrameReadModelCorrectness.SetSize(1000);
RunCorrectnessOnly();

// ────────────────────────────────────────────────────────────────
static void RunCorrectnessOnly()
{
    Console.WriteLine("Running correctness matrix...");
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
    Console.WriteLine("  --correctness-only   Run correctness matrix (default: quick size = 1000).");
    Console.WriteLine("  --n <int>            Set test entity count (default: 1000, smoke: 1000000).");
    Console.WriteLine("  --quick              Run abbreviated benchmark set (placeholder).");
    Console.WriteLine("  --full               Run comprehensive benchmark set (placeholder).");
    Console.WriteLine("  --help               Show this help.");
    Console.WriteLine();
    Console.WriteLine("This is a ValueLab: not public API, Release-only, read-only on MiniArch Core.");
}
