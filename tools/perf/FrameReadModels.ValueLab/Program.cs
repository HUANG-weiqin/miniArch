// FrameReadModels ValueLab — experiments in frame-level chunk read models.
// Uses real MiniArch.World, QueryDescription, and world.Query().GetChunks().
// Runs in Release config only (per AGENTS.md). Debug builds print an error and exit non-zero.
//
// Run:
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab              (default: correctness + quick bench)
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --correctness-only --n 1000000
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --quick   (quick bench only)
//   dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab -- --full    (full bench only)

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
var nValue = 1000;
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

        hasN = true;
        i++;
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
    if (hasN)
        Console.WriteLine($"Using --n {nValue} for correctness tests");

    FrameReadModelCorrectness.SetSize(nValue);
    Console.WriteLine("Running correctness matrix...");
    Console.WriteLine();
    var correctnessPass = FrameReadModelCorrectness.RunAll();
    Console.WriteLine();
    Console.WriteLine(correctnessPass ? "Correctness: PASS" : "Correctness: FAIL");
    if (!correctnessPass)
        Environment.ExitCode = 1;
    return;
}

if (quick)
{
    Console.WriteLine("Mode: --quick");
    Console.WriteLine();
    FrameReadModelBenchmarks.RunQuick();
    return;
}

if (full)
{
    Console.WriteLine("Mode: --full");
    Console.WriteLine();
    FrameReadModelBenchmarks.RunFull();
    return;
}

// Default: correctness (quick size) then quick bench
Console.WriteLine("Mode: default (correctness + quick benchmarks)");
Console.WriteLine();

// Correctness first
Console.WriteLine("Running correctness matrix (quick size)...");
Console.WriteLine();
FrameReadModelCorrectness.SetSize(1000);
var pass = FrameReadModelCorrectness.RunAll();
Console.WriteLine();
Console.WriteLine(pass ? "Correctness: PASS" : "Correctness: FAIL");
Console.WriteLine();

if (!pass)
{
    Console.Error.WriteLine("Correctness failed — aborting benchmarks.");
    Environment.ExitCode = 1;
    return;
}

// Then quick benchmarks
Console.WriteLine("Running quick benchmarks...");
Console.WriteLine();
FrameReadModelBenchmarks.RunQuick();

// ────────────────────────────────────────────────────────────────
static void PrintUsage()
{
    Console.WriteLine("Usage: dotnet run -c Release --project tools/perf/FrameReadModels.ValueLab [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --quick              Run abbreviated benchmark set (3 scenarios).");
    Console.WriteLine("  --full               Run comprehensive benchmark set (4 scenarios).");
    Console.WriteLine("  --correctness-only   Run correctness matrix only.");
    Console.WriteLine("  --n <int>            Entity count for correctness smoke (e.g. 1000000).");
    Console.WriteLine("  --help               Show this help.");
    Console.WriteLine();
    Console.WriteLine("Default: correctness (quick size) + quick benchmarks.");
}
