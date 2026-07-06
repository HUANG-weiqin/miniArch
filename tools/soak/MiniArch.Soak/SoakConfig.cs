namespace MiniArch.Soak;

sealed record SoakConfig
{
    public int TotalFrames { get; init; } = 100_000;
    public int Seed { get; init; } = 1234567;
    public int MaxOpsPerFrame { get; init; } = 8;
    public int EntityCap { get; init; } = 5000;
    public int EntityFloor { get; init; } = 200;
    public int DetailInterval { get; init; } = 1_000;   // second-tier line
    public int CheckpointInterval { get; init; } = 10_000; // third-tier line
    public bool PauseOnFail { get; init; }

    public static SoakConfig FromArgs(string[] args)
    {
        var cfg = new SoakConfig();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--frames" when i + 1 < args.Length: cfg = cfg with { TotalFrames = int.Parse(args[++i]) }; break;
                case "--seed" when i + 1 < args.Length: cfg = cfg with { Seed = int.Parse(args[++i]) }; break;
                case "--random-seed": cfg = cfg with { Seed = Environment.TickCount }; break;
                case "--ops-per-frame" when i + 1 < args.Length: cfg = cfg with { MaxOpsPerFrame = int.Parse(args[++i]) }; break;
                case "--entity-cap" when i + 1 < args.Length: cfg = cfg with { EntityCap = int.Parse(args[++i]) }; break;
                case "--entity-floor" when i + 1 < args.Length: cfg = cfg with { EntityFloor = int.Parse(args[++i]) }; break;
                case "--detail-interval" when i + 1 < args.Length: cfg = cfg with { DetailInterval = int.Parse(args[++i]) }; break;
                case "--checkpoint-interval" when i + 1 < args.Length: cfg = cfg with { CheckpointInterval = int.Parse(args[++i]) }; break;
                case "--pause-on-fail": cfg = cfg with { PauseOnFail = true }; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
        return cfg;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MiniArch Soak Test — long-running ECS correctness validator.

            Usage: MiniArch.Soak [options]

            Options:
              --frames N             Total frames to simulate  (default: 100000)
              --seed N               Random seed               (default: 42)
              --ops-per-frame N      Max operations per frame  (default: 8)
              --entity-cap N         Max alive entities        (default: 5000)
              --entity-floor N       Min alive entities        (default: 200)
              --detail-interval N    Detail line every N frames(default: 1000)
              --checkpoint-interval N Checkpoint every N frames(default: 10000)
              --pause-on-fail        Wait for keypress on fail (default: off)
              --help, -h             Show this help
            """);
    }
}
