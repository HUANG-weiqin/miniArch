var cfg = MiniArch.Soak.SoakConfig.FromArgs(args);
var runner = new MiniArch.Soak.SoakRunner(cfg);
var passed = runner.Run();
Environment.Exit(passed ? 0 : 1);
