using System.Diagnostics;
using Arch.Core;
using MiniArch.Core;
using MiniArchBenchmarks;
using MiniQuery = MiniArch.Core.Query;
using MiniComponentType = MiniArch.Core.ComponentType;

const int entityCount = 100_000;
const int warmup = 3;
const int measureMs = 10000;

// ===== MiniArch =====

static int MiniNarrowManual(MiniQuery q, MiniComponentType pt, MiniComponentType vt)
{
    var s = 0;
    var ch = q.GetChunkSpan();
    for (var ci = 0; ci < ch.Length; ci++)
    {
        var c = ch[ci];
        var ps = c.GetComponentSpan<Position>(pt);
        var vs = c.GetComponentSpan<Velocity>(vt);
        for (var ri = 0; ri < c.Count; ri++) s += ps[ri].X + vs[ri].Y;
    }
    return s;
}

static int MiniNarrowEach(MiniQuery q)
{
    var s = 0;
    foreach (var r in q.EachSpan<Position, Velocity>())
        s += r.Get0().X + r.Get1().Y;
    return s;
}

static int MiniWideManual(MiniQuery q, MiniComponentType pt, MiniComponentType vt, MiniComponentType ht, MiniComponentType tt, MiniComponentType at, MiniComponentType mt)
{
    var s = 0;
    var ch = q.GetChunkSpan();
    for (var ci = 0; ci < ch.Length; ci++)
    {
        var c = ch[ci];
        var ps = c.GetComponentSpan<Position>(pt);
        var vs = c.GetComponentSpan<Velocity>(vt);
        var hs = c.GetComponentSpan<Health>(ht);
        var ts = c.GetComponentSpan<Team>(tt);
        var ac = c.GetComponentSpan<Acceleration>(at);
        var ms = c.GetComponentSpan<Mana>(mt);
        for (var ri = 0; ri < c.Count; ri++)
            s += ps[ri].X + vs[ri].Y + hs[ri].Value + ts[ri].Value + ac[ri].X + ms[ri].Value;
    }
    return s;
}

static int MiniWideEach(MiniQuery q)
{
    var s = 0;
    foreach (var r in q.EachSpan<Position, Velocity, Health, Team, Acceleration, Mana>())
        s += r.Get0().X + r.Get1().Y + r.Get2().Value + r.Get3().Value + r.Get4().X + r.Get5().Value;
    return s;
}

// ===== Arch =====

static int ArchNarrowManual(World w, QueryDescription d)
{
    var s = 0;
    var q = w.Query(in d);
    foreach (var c in q)
    {
        c.GetSpan<Position, Velocity>(out var pos, out var vel);
        for (var ri = 0; ri < c.Count; ri++) s += pos[ri].X + vel[ri].Y;
    }
    return s;
}

static int ArchWideManual(World w, QueryDescription d)
{
    var s = 0;
    var q = w.Query(in d);
    foreach (var c in q)
    {
        c.GetSpan<Position, Velocity, Health, Team, Acceleration, Mana>(
            out var pos, out var vel, out var hp, out var tm, out var acc, out var mana);
        for (var ri = 0; ri < c.Count; ri++)
            s += pos[ri].X + vel[ri].Y + hp[ri].Value + tm[ri].Value + acc[ri].X + mana[ri].Value;
    }
    return s;
}

// ===== Runner =====

void Run(string label, Func<long> fn)
{
    for (var w = 0; w < warmup; w++) fn();
    var sw = Stopwatch.StartNew();
    long iters = 0;
    while (sw.ElapsedMilliseconds < measureMs) { fn(); iters++; }
    sw.Stop();
    Console.WriteLine($"{label,-50} {iters / sw.Elapsed.TotalSeconds,10:F1} ops/s");
}

Console.WriteLine($"EachSpan comparison: {entityCount:N0} entities, {measureMs}ms");
Console.WriteLine(new string('-', 80));

Console.WriteLine("\n--- Narrow (Position + Velocity) ---");
Console.WriteLine();
Console.WriteLine("  MiniArch:");
var nm = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(entityCount);
Run("  Manual (chunk span)", () => MiniNarrowManual(nm.WithAllQuery, nm.PositionType, nm.VelocityType));
Run("  EachSpan",            () => MiniNarrowEach(nm.WithAllQuery));

Console.WriteLine();
Console.WriteLine("  Arch:");
var na = BenchmarkWorldFactory.CreateArchComplexQueryWorld(entityCount);
Run("  Manual (GetSpan)", () => ArchNarrowManual(na.World, na.WithAllDescription));

Console.WriteLine();
Console.WriteLine("--- Wide (Position+Velocity+Health+Team+Acceleration+Mana) ---");
Console.WriteLine();
Console.WriteLine("  MiniArch:");
var wm = BenchmarkWorldFactory.CreateMiniWideQueryWorld(entityCount);
Run("  Manual (chunk span)", () => MiniWideManual(wm.WideQuery,
    wm.PositionType, wm.VelocityType, wm.HealthType,
    wm.TeamType, wm.AccelerationType, wm.ManaType));
Run("  EachSpan",            () => MiniWideEach(wm.WideQuery));

Console.WriteLine();
Console.WriteLine("  Arch:");
var wa = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
Run("  Manual (GetSpan)", () => ArchWideManual(wa.World, wa.WideDescription));

na.Dispose();
wa.Dispose();

Console.WriteLine("\nDone.");
