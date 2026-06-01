using System.Diagnostics;
using Arch.Core;
using MiniArch.Core;
using MiniArchBenchmarks;
using MiniQuery = MiniArch.Core.Query;
using MiniComponentType = MiniArch.Core.ComponentType;

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

// ===== Narrow write-back (Position++, Velocity++) =====

static int MiniNarrowWrite(MiniQuery q, MiniComponentType pt, MiniComponentType vt)
{
    var s = 0;
    var ch = q.GetChunkSpan();
    for (var ci = 0; ci < ch.Length; ci++)
    {
        var c = ch[ci];
        var pos = c.GetWritableComponentSpan<Position>(pt);
        var vel = c.GetWritableComponentSpan<Velocity>(vt);
        for (var ri = 0; ri < c.Count; ri++)
        {
            pos[ri] = new Position(pos[ri].X + 1, pos[ri].Y + 1);
            vel[ri] = new Velocity(vel[ri].X + 1, vel[ri].Y + 1);
            s += pos[ri].X + vel[ri].Y;
        }
    }
    return s;
}

static int ArchNarrowWrite(World w, QueryDescription d)
{
    var s = 0;
    var q = w.Query(in d);
    foreach (var c in q)
    {
        var pos = c.GetSpan<Position>();
        var vel = c.GetSpan<Velocity>();
        for (var ri = 0; ri < c.Count; ri++)
        {
            pos[ri] = new Position(pos[ri].X + 1, pos[ri].Y + 1);
            vel[ri] = new Velocity(vel[ri].X + 1, vel[ri].Y + 1);
            s += pos[ri].X + vel[ri].Y;
        }
    }
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

// ===== Wide write-back (6 components, increment all fields) =====

static int MiniWideWrite(MiniQuery q, MiniComponentType pt, MiniComponentType vt, MiniComponentType ht, MiniComponentType tt, MiniComponentType at, MiniComponentType mt)
{
    var s = 0;
    var ch = q.GetChunkSpan();
    for (var ci = 0; ci < ch.Length; ci++)
    {
        var c = ch[ci];
        var pos = c.GetWritableComponentSpan<Position>(pt);
        var vel = c.GetWritableComponentSpan<Velocity>(vt);
        var hp = c.GetWritableComponentSpan<Health>(ht);
        var tm = c.GetWritableComponentSpan<Team>(tt);
        var acc = c.GetWritableComponentSpan<Acceleration>(at);
        var mana = c.GetWritableComponentSpan<Mana>(mt);
        for (var ri = 0; ri < c.Count; ri++)
        {
            pos[ri] = new Position(pos[ri].X + 1, pos[ri].Y + 1);
            vel[ri] = new Velocity(vel[ri].X + 1, vel[ri].Y + 1);
            hp[ri] = new Health(hp[ri].Value + 1);
            tm[ri] = new Team(tm[ri].Value + 1);
            acc[ri] = new Acceleration(acc[ri].X + 1, acc[ri].Y + 1);
            mana[ri] = new Mana(mana[ri].Value + 1);
            s += pos[ri].X + vel[ri].Y + hp[ri].Value + tm[ri].Value + acc[ri].X + mana[ri].Value;
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
            pos[ri] = new Position(pos[ri].X + 1, pos[ri].Y + 1);
            vel[ri] = new Velocity(vel[ri].X + 1, vel[ri].Y + 1);
            hp[ri] = new Health(hp[ri].Value + 1);
            tm[ri] = new Team(tm[ri].Value + 1);
            acc[ri] = new Acceleration(acc[ri].X + 1, acc[ri].Y + 1);
            mana[ri] = new Mana(mana[ri].Value + 1);
            s += pos[ri].X + vel[ri].Y + hp[ri].Value + tm[ri].Value + acc[ri].X + mana[ri].Value;
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

Console.WriteLine($"Read vs Write comparison: {entityCount:N0} entities, {measureMs}ms");
Console.WriteLine(new string('-', 80));

Console.WriteLine("\n--- Narrow (Position + Velocity) ---");
Console.WriteLine();
var nm = BenchmarkWorldFactory.CreateMiniComplexQueryWorld(entityCount);
var na = BenchmarkWorldFactory.CreateArchComplexQueryWorld(entityCount);
Console.WriteLine("  Read:");
Run("  MiniArch EachSpan",  () => MiniNarrowEach(nm.WithAllQuery));
Run("  Arch GetSpan",       () => ArchNarrowRead(na.World, na.WithAllDescription));
Console.WriteLine("  Write-back (both components, ++all fields):");
Run("  MiniArch span write",() => MiniNarrowWrite(nm.WithAllQuery, nm.PositionType, nm.VelocityType));
Run("  Arch span write",    () => ArchNarrowWrite(na.World, na.WithAllDescription));

Console.WriteLine();
Console.WriteLine("--- Wide (Position+Velocity+Health+Team+Acceleration+Mana) ---");
Console.WriteLine();
var wm = BenchmarkWorldFactory.CreateMiniWideQueryWorld(entityCount);
var wa = BenchmarkWorldFactory.CreateArchWideQueryWorld(entityCount);
Console.WriteLine("  Read:");
Run("  MiniArch EachSpan",  () => MiniWideEach(wm.WideQuery));
Run("  Arch GetSpan",       () => ArchWideRead(wa.World, wa.WideDescription));
Console.WriteLine("  Write-back (6 components, ++all fields):");
Run("  MiniArch span write",() => MiniWideWrite(wm.WideQuery, wm.PositionType, wm.VelocityType, wm.HealthType, wm.TeamType, wm.AccelerationType, wm.ManaType));
Run("  Arch span write",    () => ArchWideWrite(wa.World, wa.WideDescription));

na.Dispose();
wa.Dispose();
Console.WriteLine("\nDone.");
