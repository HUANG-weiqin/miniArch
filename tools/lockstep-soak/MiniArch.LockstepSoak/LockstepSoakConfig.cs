namespace MiniArch.LockstepSoak;

sealed record LockstepSoakConfig
{
    public int TotalFrames { get; init; } = 100_000;
    public int Seed { get; init; } = 1234567;
    public int HostCount { get; init; } = 4;
    public int MaxOpsPerFrame { get; init; } = 8;
    public int EntityCap { get; init; } = 5000;
    public int EntityFloor { get; init; } = 200;
    public int DetailInterval { get; init; } = 1_000;
    public int ValidateInterval { get; init; } = 100;
    public int CheckpointInterval { get; init; } = 10_000;
    public bool PauseOnFail { get; init; }
    public int SweepCount { get; init; }
    public bool Determinism { get; init; }
    public bool Quiet { get; init; }

    public static LockstepSoakConfig FromArgs(string[] args)
    {
        var cfg = new LockstepSoakConfig();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--frames" when i + 1 < args.Length: cfg = cfg with { TotalFrames = int.Parse(args[++i]) }; break;
                case "--seed" when i + 1 < args.Length: cfg = cfg with { Seed = int.Parse(args[++i]) }; break;
                case "--hosts" when i + 1 < args.Length: cfg = cfg with { HostCount = int.Parse(args[++i]) }; break;
                case "--random-seed": cfg = cfg with { Seed = Environment.TickCount }; break;
                case "--ops-per-frame" when i + 1 < args.Length: cfg = cfg with { MaxOpsPerFrame = int.Parse(args[++i]) }; break;
                case "--entity-cap" when i + 1 < args.Length: cfg = cfg with { EntityCap = int.Parse(args[++i]) }; break;
                case "--entity-floor" when i + 1 < args.Length: cfg = cfg with { EntityFloor = int.Parse(args[++i]) }; break;
                case "--detail-interval" when i + 1 < args.Length: cfg = cfg with { DetailInterval = int.Parse(args[++i]) }; break;
                case "--validate-interval" when i + 1 < args.Length: cfg = cfg with { ValidateInterval = int.Parse(args[++i]) }; break;
                case "--checkpoint-interval" when i + 1 < args.Length: cfg = cfg with { CheckpointInterval = int.Parse(args[++i]) }; break;
                case "--pause-on-fail": cfg = cfg with { PauseOnFail = true }; break;
                case "--sweep" when i + 1 < args.Length: cfg = cfg with { SweepCount = int.Parse(args[++i]) }; break;
                case "--determinism": cfg = cfg with { Determinism = true }; break;
                case "--quiet": cfg = cfg with { Quiet = true }; break;
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
            MiniArch LockstepSoak — multi-host placeholder lockstep correctness proof.

            Usage: MiniArch.LockstepSoak [options]

            Options:
              --frames N             Total frames to simulate   (default: 100000)
              --seed N               Random seed                (default: 1234567)
              --hosts N              Number of hosts            (default: 4)
              --ops-per-frame N      Max operations per frame   (default: 8)
              --entity-cap N         Max alive entities         (default: 5000)
              --entity-floor N       Min alive entities         (default: 200)
              --detail-interval N    Detail line every N frames (default: 1000)
              --validate-interval N  Heavy validation every N frames (default: 100; 0=every frame)
              --checkpoint-interval N Checkpoint every N frames  (default: 10000)
              --pause-on-fail        Wait for keypress on fail  (default: off)
              --sweep N              Run N consecutive seeds (diversity sweep; quiet auto-enabled per seed)
              --determinism          Run same seed twice, compare final canonical checksum
              --quiet                Suppress per-frame detail output (auto-enabled in sweep)
              --help, -h             Show this help
            """);
    }
}
