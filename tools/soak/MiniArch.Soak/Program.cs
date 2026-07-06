var cfg = MiniArch.Soak.SoakConfig.FromArgs(args);

if (cfg.Determinism)
{
    // Run same config twice (same seed), compare final CanonicalChecksum
    var runner1 = new MiniArch.Soak.SoakRunner(cfg);
    var ok1 = runner1.Run();
    var runner2 = new MiniArch.Soak.SoakRunner(cfg);
    var ok2 = runner2.Run();

    if (!ok1 || !ok2)
        Environment.Exit(1);

    Console.WriteLine();
    Console.WriteLine($"  Checksum run 1: {Convert.ToHexString(runner1.FinalChecksum!)}");
    Console.WriteLine($"  Checksum run 2: {Convert.ToHexString(runner2.FinalChecksum!)}");

    if (runner1.FinalChecksum!.AsSpan().SequenceEqual(runner2.FinalChecksum!))
    {
        Console.WriteLine("  DETERMINISM: PASS (checksums match)");
        Environment.Exit(0);
    }
    else
    {
        Console.WriteLine("  DETERMINISM: FAIL (checksums differ)");
        Environment.Exit(1);
    }
}
else if (cfg.SweepCount > 0)
{
    // Diversity sweep: run N consecutive seeds
    var passed = 0;
    for (var i = 0; i < cfg.SweepCount; i++)
    {
        var seedCfg = cfg with { Seed = cfg.Seed + i, Quiet = true };
        var runner = new MiniArch.Soak.SoakRunner(seedCfg);
        var ok = runner.Run();
        if (ok)
        {
            Console.WriteLine(runner.GetSweepSummaryLine());
            passed++;
        }
        else
        {
            Console.WriteLine($"  sweep FAIL at seed {cfg.Seed + i}");
            Environment.Exit(1);
        }
    }
    Console.WriteLine($"  sweep PASS {passed}/{cfg.SweepCount}");
    Environment.Exit(0);
}
else
{
    // Single run (original behavior)
    var runner = new MiniArch.Soak.SoakRunner(cfg);
    var passed = runner.Run();
    Environment.Exit(passed ? 0 : 1);
}
