using System.Diagnostics;
using Friflo.Engine.ECS;
using MiniArch;
using MiniEntity = MiniArch.Entity;
using MiniQuery = MiniArch.Query;

namespace FrifloGameScenarios;

/// <summary>
/// Segmented timing profiler for S5-FullGameLoop.
/// Uses time-based warmup/measure (like main benchmark) to match conditions.
/// </summary>
public static class S5Profile
{
    const int WarmupSeconds = 10;
    const int MeasureSeconds = 10;

    public static void Run()
    {
        Console.WriteLine("=== S5-FullGameLoop Segmented Profile (10s warmup, 10s measure) ===");
        Console.WriteLine();
        ProfileMiniArch();
        Console.WriteLine();
        ProfileFriflo();
    }

    static (MiniQuery mq, MiniQuery cq, MiniQuery hq) SetupMini()
    {
        var w = new World(128, 20000);
        var r = new Random(42);
        for (int i = 0; i < 20000; i++)
            w.Create(new Position(i % 500, i / 500), new Velocity(r.Next(3) - 1, r.Next(3) - 1),
                new Health(50 + r.Next(50)), new Team(i % 4), new Damage(5 + r.Next(10)));
        var mq = w.Query(new QueryDescription().With<Position>().With<Velocity>());
        var cq = w.Query(new QueryDescription().With<Position>().With<Health>().With<Damage>().With<Team>());
        var hq = w.Query(new QueryDescription().With<Health>());
        return (mq, cq, hq);
    }

    static void ProfileMiniArch()
    {
        var (mq, cq, hq) = SetupMini();

        // Warmup: 10 seconds
        var sw = Stopwatch.StartNew();
        long warmupIters = 0;
        while (sw.Elapsed.TotalSeconds < WarmupSeconds) { MiniIter(mq, cq, hq); warmupIters++; }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        // Measure: 10 seconds, with per-query segmentation
        long measureIters = 0;
        long totalTicks = 0;
        long q1Ticks = 0, q2Ticks = 0, q3Ticks = 0;

        sw.Restart();
        while (sw.Elapsed.TotalSeconds < MeasureSeconds)
        {
            long s = 0;

            var t0 = Stopwatch.GetTimestamp();
            foreach (var c in mq.GetChunks()){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<sp.Length;i++)s+=sp[i].X+sv[i].VX;}
            q1Ticks += Stopwatch.GetTimestamp() - t0;

            t0 = Stopwatch.GetTimestamp();
            foreach (var c in cq.GetChunks()){var sp=c.GetSpan<Position>();var sh=c.GetSpan<Health>();var sd=c.GetSpan<Damage>();var st=c.GetSpan<Team>();for(int i=0;i<sp.Length;i++)s+=sp[i].X+sh[i].Value+sd[i].Value+st[i].Value;}
            q2Ticks += Stopwatch.GetTimestamp() - t0;

            t0 = Stopwatch.GetTimestamp();
            foreach (var c in hq.GetChunks()){var sh=c.GetSpan<Health>();for(int i=0;i<sh.Length;i++)s+=sh[i].Value;}
            q3Ticks += Stopwatch.GetTimestamp() - t0;

            measureIters++;
        }
        totalTicks = sw.ElapsedTicks;

        double freq = Stopwatch.Frequency;
        double totalUs = totalTicks / freq * 1e6 / measureIters;
        double q1Us = q1Ticks / freq * 1e6 / measureIters;
        double q2Us = q2Ticks / freq * 1e6 / measureIters;
        double q3Us = q3Ticks / freq * 1e6 / measureIters;

        Console.WriteLine("  [MiniArch]");
        Console.WriteLine($"    Warmup iters    : {warmupIters,8}");
        Console.WriteLine($"    Measure iters   : {measureIters,8}");
        Console.WriteLine($"    Total           : {totalUs,8:F2} us  ({measureIters / (totalTicks / freq),8:F1} ops/s)");
        Console.WriteLine($"    Q1 (Pos+Vel)    : {q1Us,8:F2} us  ({q1Us / totalUs * 100,5:F1}%)");
        Console.WriteLine($"    Q2 (Pos+H+D+T)  : {q2Us,8:F2} us  ({q2Us / totalUs * 100,5:F1}%)");
        Console.WriteLine($"    Q3 (Health)     : {q3Us,8:F2} us  ({q3Us / totalUs * 100,5:F1}%)");
    }

    static (Friflo.Engine.ECS.ArchetypeQuery<Position, Velocity> mq,
            Friflo.Engine.ECS.ArchetypeQuery<Position, Health, Damage, Team> cq,
            Friflo.Engine.ECS.ArchetypeQuery<Health> hq) SetupFriflo()
    {
        var store = new EntityStore();
        var r = new Random(42);
        for (int i = 0; i < 20000; i++)
            store.CreateEntity(new Position(i % 500, i / 500), new Velocity(r.Next(3) - 1, r.Next(3) - 1),
                new Health(50 + r.Next(50)), new Team(i % 4), new Damage(5 + r.Next(10)));
        var mq = store.Query<Position, Velocity>();
        var cq = store.Query<Position, Health, Damage, Team>();
        var hq = store.Query<Health>();
        return (mq, cq, hq);
    }

    static void ProfileFriflo()
    {
        var (mq, cq, hq) = SetupFriflo();

        var sw = Stopwatch.StartNew();
        long warmupIters = 0;
        while (sw.Elapsed.TotalSeconds < WarmupSeconds) { FrifloIter(mq, cq, hq); warmupIters++; }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        long measureIters = 0;
        long totalTicks = 0;
        long q1Ticks = 0, q2Ticks = 0, q3Ticks = 0;

        sw.Restart();
        while (sw.Elapsed.TotalSeconds < MeasureSeconds)
        {
            long s = 0;

            var t0 = Stopwatch.GetTimestamp();
            foreach (var (p, v, e) in mq.Chunks) { var sp = p.Span; var sv = v.Span; for (int n = 0; n < e.Length; n++) s += sp[n].X + sv[n].VX; }
            q1Ticks += Stopwatch.GetTimestamp() - t0;

            t0 = Stopwatch.GetTimestamp();
            foreach (var (p, h, d, t, e) in cq.Chunks) { var sp = p.Span; var sh = h.Span; var sd = d.Span; var st = t.Span; for (int n = 0; n < e.Length; n++) s += sp[n].X + sh[n].Value + sd[n].Value + st[n].Value; }
            q2Ticks += Stopwatch.GetTimestamp() - t0;

            t0 = Stopwatch.GetTimestamp();
            foreach (var (h, e) in hq.Chunks) { var sh = h.Span; for (int n = 0; n < e.Length; n++) s += sh[n].Value; }
            q3Ticks += Stopwatch.GetTimestamp() - t0;

            measureIters++;
        }
        totalTicks = sw.ElapsedTicks;

        double freq = Stopwatch.Frequency;
        double totalUs = totalTicks / freq * 1e6 / measureIters;
        double q1Us = q1Ticks / freq * 1e6 / measureIters;
        double q2Us = q2Ticks / freq * 1e6 / measureIters;
        double q3Us = q3Ticks / freq * 1e6 / measureIters;

        Console.WriteLine("  [Friflo]");
        Console.WriteLine($"    Warmup iters    : {warmupIters,8}");
        Console.WriteLine($"    Measure iters   : {measureIters,8}");
        Console.WriteLine($"    Total           : {totalUs,8:F2} us  ({measureIters / (totalTicks / freq),8:F1} ops/s)");
        Console.WriteLine($"    Q1 (Pos+Vel)    : {q1Us,8:F2} us  ({q1Us / totalUs * 100,5:F1}%)");
        Console.WriteLine($"    Q2 (Pos+H+D+T)  : {q2Us,8:F2} us  ({q2Us / totalUs * 100,5:F1}%)");
        Console.WriteLine($"    Q3 (Health)     : {q3Us,8:F2} us  ({q3Us / totalUs * 100,5:F1}%)");
    }

    static long MiniIter(MiniQuery mq, MiniQuery cq, MiniQuery hq)
    {
        long s = 0;
        foreach (var c in mq.GetChunks()){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<sp.Length;i++)s+=sp[i].X+sv[i].VX;}
        foreach (var c in cq.GetChunks()){var sp=c.GetSpan<Position>();var sh=c.GetSpan<Health>();var sd=c.GetSpan<Damage>();var st=c.GetSpan<Team>();for(int i=0;i<sp.Length;i++)s+=sp[i].X+sh[i].Value+sd[i].Value+st[i].Value;}
        foreach (var c in hq.GetChunks()){var sh=c.GetSpan<Health>();for(int i=0;i<sh.Length;i++)s+=sh[i].Value;}
        return s;
    }

    static long FrifloIter(
        Friflo.Engine.ECS.ArchetypeQuery<Position, Velocity> mq,
        Friflo.Engine.ECS.ArchetypeQuery<Position, Health, Damage, Team> cq,
        Friflo.Engine.ECS.ArchetypeQuery<Health> hq)
    {
        long s = 0;
        foreach (var (p, v, e) in mq.Chunks) { var sp = p.Span; var sv = v.Span; for (int n = 0; n < e.Length; n++) s += sp[n].X + sv[n].VX; }
        foreach (var (p, h, d, t, e) in cq.Chunks) { var sp = p.Span; var sh = h.Span; var sd = d.Span; var st = t.Span; for (int n = 0; n < e.Length; n++) s += sp[n].X + sh[n].Value + sd[n].Value + st[n].Value; }
        foreach (var (h, e) in hq.Chunks) { var sh = h.Span; for (int n = 0; n < e.Length; n++) s += sh[n].Value; }
        return s;
    }
}
