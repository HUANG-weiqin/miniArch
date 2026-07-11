using System.Diagnostics;
using MiniArch;
using MiniArch.Core;

// ── Attack benchmark: worst-case hierarchy unavailable lookup ──────────
//
// Scenario per iteration:
//   1. Create N pending entities   (PendingBatchCount = N)
//   2. For each: AddChild(pending[i], existing[i])   (H = N hierarchy entries)
//   3. Destroy all pending[i]      (BatchCanceled[i] = true for all N)
//   4. Snapshot()                   (exercises EmitHierarchyToDelta → IsDestroyedThisFrame)
//
// Measures BOTH record-time and snapshot-time separately, plus total.
// This is fair: improvement 1 moves work from snapshot to record.

internal static class Program
{
    private static void Main()
    {
        Warmup();

        int[] sizes = [0, 1, 10, 50, 100, 500, 1000];
        foreach (var n in sizes)
        {
            var iters = n >= 500 ? 2000 : 5000;
            RunAttack(n, iters);
        }
    }

    private static void Warmup()
    {
        var w = new World();
        var s = new CommandStream(w);
        var e = w.CreateEmpty();
        s.AddChild(e, s.Create());
        s.Destroy(s.Create());
        s.Snapshot();
    }

    private static void RunAttack(int n, int iterations)
    {
        var existing = new Entity[n];
        var world = new World();
        for (var i = 0; i < n; i++)
            existing[i] = world.CreateEmpty();

        var pending = new Entity[n];

        // Warmup (not timed)
        {
            var ws = new CommandStream(world);
            for (var i = 0; i < n; i++) pending[i] = ws.Create();
            for (var i = 0; i < n; i++) ws.AddChild(pending[i], existing[i]);
            for (var i = 0; i < n; i++) ws.Destroy(pending[i]);
            ws.Snapshot();
        }

        var recordSw = new Stopwatch();
        var snapshotSw = new Stopwatch();

        for (var iter = 0; iter < iterations; iter++)
        {
            var stream = new CommandStream(world);

            // Time: Record phase
            recordSw.Start();
            for (var i = 0; i < n; i++)
                pending[i] = stream.Create();
            for (var i = 0; i < n; i++)
                stream.AddChild(pending[i], existing[i]);
            for (var i = 0; i < n; i++)
                stream.Destroy(pending[i]);
            recordSw.Stop();

            // Time: Snapshot phase
            snapshotSw.Start();
            stream.Snapshot();
            snapshotSw.Stop();
        }

        var recUs = recordSw.Elapsed.TotalMicroseconds / iterations;
        var snapUs = snapshotSw.Elapsed.TotalMicroseconds / iterations;
        var totUs = recUs + snapUs;

        Console.WriteLine(
            $"N={n,5}  record={recUs,10:F2} µs  snapshot={snapUs,10:F2} µs  " +
            $"total={totUs,10:F2} µs");
    }
}
