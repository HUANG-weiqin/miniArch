using System.Diagnostics;
using ArchWorld = Arch.Core.World;
using ArchQueryDescription = Arch.Core.QueryDescription;
using MiniArch;
using MiniArchBenchmarks;
using MiniQuery = MiniArch.Query;

const int entityCount = 100_000;
const int warmup = 3;
const int measureMs = 10000;

// ===== Narrow read (2 component) =====

static int MiniNarrowEach(MiniQuery q)
{
    var s = 0;
    foreach (var chunk in q.GetChunks())
    {
        var c0 = chunk.GetSpan<Position>();
        var c1 = chunk.GetSpan<Velocity>();
        for (int i = 0; i < chunk.Count; i++)
            s += c0[i].X + c1[i].Y;
    }
    return s;
}

static int ArchNarrowRead(ArchWorld w, ArchQueryDescription d)
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
    foreach (var chunk in q.GetChunks())
    {
        var c0 = chunk.GetSpan<Position>();
        var c1 = chunk.GetSpan<Velocity>();
        var c2 = chunk.GetSpan<Health>();
        var c3 = chunk.GetSpan<Team>();
        var c4 = chunk.GetSpan<Acceleration>();
        var c5 = chunk.GetSpan<Mana>();
        for (int i = 0; i < chunk.Count; i++)
            s += c0[i].X + c1[i].Y + c2[i].Value + c3[i].Value + c4[i].X + c5[i].Value;
    }
    return s;
}

static int ArchWideRead(ArchWorld w, ArchQueryDescription d)
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
    foreach (var chunk in q.GetChunks())
    {
        var pos = chunk.GetSpan<Position>();
        var vel = chunk.GetSpan<Velocity>();
        var hp = chunk.GetSpan<Health>();
        var mana = chunk.GetSpan<Mana>();
        for (int i = 0; i < chunk.Count; i++)
        {
            pos[i].X += vel[i].Y;
            hp[i].Value -= 1;
            mana[i].Value += 2;
            s += pos[i].X + hp[i].Value + mana[i].Value;
        }
    }
    return s;
}

static int MiniWideWriteManual(MiniQuery q)
{
    var s = 0;
    foreach (var chunk in q.GetChunks())
    {
        var pos = chunk.GetSpan<Position>();
        var vel = chunk.GetSpan<Velocity>();
        var hp = chunk.GetSpan<Health>();
        var mana = chunk.GetSpan<Mana>();
        for (int i = 0; i < chunk.Count; i++)
        {
            pos[i].X += vel[i].Y;
            hp[i].Value -= 1;
            mana[i].Value += 2;
            s += pos[i].X + hp[i].Value + mana[i].Value;
        }
    }
    return s;
}

static int ArchWideWrite(ArchWorld w, ArchQueryDescription d)
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
var nmQ = nm.World.Query(new QueryDescription().With<Position>().With<Velocity>());
Run("  MiniArch EachSpan",  () => MiniNarrowEach(nmQ));
Run("  Arch GetSpan",       () => ArchNarrowRead(na.World, na.WithAllDescription));

Console.WriteLine();
Console.WriteLine("--- Wide read (Position+Velocity+Health+Team+Acceleration+Mana) ---");
Console.WriteLine();
var wm = BenchmarkWorldFactory.CreateMiniWideQueryWorld(entityCount);
var wa = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
var wmQ = wm.World.Query(new QueryDescription().With<Position>().With<Velocity>().With<Health>().With<Team>().With<Acceleration>().With<Mana>());
Run("  MiniArch EachSpan read",  () => MiniWideEach(wmQ));
Run("  Arch GetSpan read",       () => ArchWideRead(wa.World, wa.WideDescription));

Console.WriteLine();
Console.WriteLine("--- Wide write (Position.X += Vel.Y, Health -= 1, Mana += 2) ---");
Console.WriteLine();
var wm2 = BenchmarkWorldFactory.CreateMiniWideQueryWorld(entityCount);
var wa2 = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
var wm2Q = wm2.World.Query(new QueryDescription().With<Position>().With<Velocity>().With<Health>().With<Team>().With<Acceleration>().With<Mana>());
Run("  MiniArch EachSpan write", () => MiniWideWrite(wm2Q));
Run("  MiniArch manual chunk write", () => MiniWideWriteManual(wm2Q));
Run("  Arch GetSpan write",      () => ArchWideWrite(wa2.World, wa2.WideDescription));

na.Dispose();
wa.Dispose();
wa2.Dispose();
Console.WriteLine("\nDone.");
