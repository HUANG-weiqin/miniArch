using System.Diagnostics;
using Arch.Core;
using MiniArch.Core;
using MiniArchBenchmarks;
using MiniQuery = MiniArch.Core.Query;

const int entityCount = 100_000;
const int warmup = 3;
const int measureMs = 10000;

// ===== Narrow read (2 component) =====

static int MiniNarrowEach(MiniQuery q)
{
    var s = 0;
    foreach (var r in q.EachSpan<Position, Velocity>()) s += r.Get0().X + r.Get1().Y;
    return s;
}

static int ArchNarrowRead(World w, QueryDescription d)
{
    var s = 0;
    var q = w.Query(in d);
    foreach (var c in q) { c.GetSpan<Position, Velocity>(out var pos, out var vel); for (var ri = 0; ri < c.Count; ri++) s += pos[ri].X + vel[ri].Y; }
    return s;
}

// ===== Wide read (6 component) =====

static int MiniWideEach(MiniQuery q)
{
    var s = 0;
    foreach (var r in q.EachSpan<Position, Velocity, Health, Team, Acceleration, Mana>())
        s += r.Get0().X + r.Get1().Y + r.Get2().Value + r.Get3().Value + r.Get4().X + r.Get5().Value;
    return s;
}

static int ArchWideRead(World w, QueryDescription d)
{
    var s = 0;
    var q = w.Query(in d);
    foreach (var c in q)
    {
        c.GetSpan<Position, Velocity, Health, Team, Acceleration, Mana>(out var pos, out var vel, out var hp, out var tm, out var acc, out var mana);
        for (var ri = 0; ri < c.Count; ri++) s += pos[ri].X + vel[ri].Y + hp[ri].Value + tm[ri].Value + acc[ri].X + mana[ri].Value;
    }
    return s;
}

// ===== Wide write (read 6, write Position+Health+Mana) =====

static int MiniWideWrite(MiniQuery q)
{
    var s = 0;
    foreach (var r in q.EachSpan<Position, Velocity, Health, Team, Acceleration, Mana>())
    {
        ref var pos = ref r.Get0();
        ref var hp = ref r.Get2();
        ref var mana = ref r.Get5();
        pos.X += r.Get1().Y;
        hp.Value -= 1;
        mana.Value += 2;
        s += pos.X + hp.Value + mana.Value;
    }
    return s;
}

static int MiniWideWriteManual(MiniQuery q,
    MiniArch.Core.ComponentType pt, MiniArch.Core.ComponentType vt,
    MiniArch.Core.ComponentType ht, MiniArch.Core.ComponentType tt,
    MiniArch.Core.ComponentType at, MiniArch.Core.ComponentType mt)
{
    var s = 0;
    var chunks = q.GetChunkSpan();
    for (var ci = 0; ci < chunks.Length; ci++)
    {
        var c = chunks[ci];
        var pos = c.GetComponentSpan<Position>(pt);
        var vel = c.GetComponentSpan<Velocity>(vt);
        var hp = c.GetComponentSpan<Health>(ht);
        var tm = c.GetComponentSpan<Team>(tt);
        var acc = c.GetComponentSpan<Acceleration>(at);
        var mana = c.GetComponentSpan<Mana>(mt);
        for (var ri = 0; ri < c.Count; ri++)
        {
            pos[ri].X += vel[ri].Y;
            hp[ri].Value -= 1;
            mana[ri].Value += 2;
            s += pos[ri].X + hp[ri].Value + mana[ri].Value;
        }
    }
    return s;
}

static int ArchWideWrite(World w, QueryDescription d)
{
    var s = 0;
    var q = w.Query(in d);
    foreach (var c in q)
    {
        c.GetSpan<Position, Velocity, Health, Team, Acceleration, Mana>(out var pos, out var vel, out var hp, out var tm, out var acc, out var mana);
        for (var ri = 0; ri < c.Count; ri++)
        {
            pos[ri].X += vel[ri].Y;
            hp[ri].Value -= 1;
            mana[ri].Value += 2;
            s += pos[ri].X + hp[ri].Value + mana[ri].Value;
        }
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
    Console.WriteLine($"{label,-55} {iters / sw.Elapsed.TotalSeconds,10:F1} ops/s");
}

Console.WriteLine($"EachSpan read/write comparison: {entityCount:N0} entities, {measureMs}ms");
Console.WriteLine(new string('-', 80));

Console.WriteLine("\n--- Narrow read (Position + Velocity) ---");
Console.WriteLine();
var nm = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(entityCount);
var na = BenchmarkWorldFactory.CreateArchComplexQueryWorld(entityCount);
Run("  MiniArch EachSpan",  () => MiniNarrowEach(nm.WithAllQuery));
Run("  Arch GetSpan",       () => ArchNarrowRead(na.World, na.WithAllDescription));

Console.WriteLine();
Console.WriteLine("--- Wide read (Position+Velocity+Health+Team+Acceleration+Mana) ---");
Console.WriteLine();
var wm = BenchmarkWorldFactory.CreateMiniWideQueryWorld(entityCount);
var wa = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
Run("  MiniArch EachSpan read",  () => MiniWideEach(wm.WideQuery));
Run("  Arch GetSpan read",       () => ArchWideRead(wa.World, wa.WideDescription));

Console.WriteLine();
Console.WriteLine("--- Wide write (Position.X += Vel.Y, Health -= 1, Mana += 2) ---");
Console.WriteLine();
var wm2 = BenchmarkWorldFactory.CreateMiniWideQueryWorld(entityCount);
var wa2 = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
Run("  MiniArch EachSpan write", () => MiniWideWrite(wm2.WideQuery));
Run("  MiniArch manual chunk write", () => MiniWideWriteManual(wm2.WideQuery,
    MiniArch.Core.Component<Position>.ComponentType, MiniArch.Core.Component<Velocity>.ComponentType,
    MiniArch.Core.Component<Health>.ComponentType, MiniArch.Core.Component<Team>.ComponentType,
    MiniArch.Core.Component<Acceleration>.ComponentType, MiniArch.Core.Component<Mana>.ComponentType));
Run("  Arch GetSpan write",      () => ArchWideWrite(wa2.World, wa2.WideDescription));

na.Dispose();
wa.Dispose();
wa2.Dispose();
Console.WriteLine("\nDone.");
