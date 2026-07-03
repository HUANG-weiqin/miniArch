using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Friflo.Engine.ECS;
using MiniArch;

using MiniEntity = MiniArch.Entity;
using FrifloEntity = Friflo.Engine.ECS.Entity;

namespace S10Simd;

// --- Components ---
public struct Position : IComponent { public int X; public int Y; public Position(int x, int y) { X = x; Y = y; } }
public struct Health   : IComponent { public int Value; public Health(int v) => Value = v; }
public struct Damage   : IComponent { public int Value; public Damage(int v) => Value = v; }
public struct Team     : IComponent { public int Value; public Team(int v) => Value = v; }
public struct Burning  : IComponent { public int Dummy; public Burning(int d) => Dummy = d; }
// --- Padding to create 16 archetypes ---
public struct PadA : IComponent { public byte _; public PadA(byte v) => _ = v; }
public struct PadB : IComponent { public byte _; public PadB(byte v) => _ = v; }
public struct PadC : IComponent { public byte _; public PadC(byte v) => _ = v; }
public struct PadD : IComponent { public byte _; public PadD(byte v) => _ = v; }
public struct PadE : IComponent { public byte _; public PadE(byte v) => _ = v; }
public struct PadF : IComponent { public byte _; public PadF(byte v) => _ = v; }
public struct PadG : IComponent { public byte _; public PadG(byte v) => _ = v; }
public struct PadH : IComponent { public byte _; public PadH(byte v) => _ = v; }
public struct PadI : IComponent { public byte _; public PadI(byte v) => _ = v; }
public struct PadJ : IComponent { public byte _; public PadJ(byte v) => _ = v; }
public struct PadK : IComponent { public byte _; public PadK(byte v) => _ = v; }
public struct PadL : IComponent { public byte _; public PadL(byte v) => _ = v; }
public struct PadM : IComponent { public byte _; public PadM(byte v) => _ = v; }
public struct PadN : IComponent { public byte _; public PadN(byte v) => _ = v; }
public struct PadO : IComponent { public byte _; public PadO(byte v) => _ = v; }
public struct PadP : IComponent { public byte _; public PadP(byte v) => _ = v; }

public interface IScenario : IDisposable { long RunIteration(); }

[MemoryDiagnoser]
public class MixedLoadBench
{
    [Params(1, 4, 8, 16)]
    public int Archetypes { get; set; }

    private MiniMixedLoad _mini = null!;
    private MiniMixedLoadSimd _miniSimd = null!;
    private FrifloMixedLoad _friflo = null!;
    private FrifloMixedLoadSimd _frifloSimd = null!;
    private ArchMixedLoad _arch = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mini = new MiniMixedLoad(Archetypes);
        _miniSimd = new MiniMixedLoadSimd(Archetypes);
        _friflo = new FrifloMixedLoad(Archetypes);
        _frifloSimd = new FrifloMixedLoadSimd(Archetypes);
        _arch = new ArchMixedLoad(Archetypes);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _mini.Dispose(); _miniSimd.Dispose(); _friflo.Dispose(); _frifloSimd.Dispose(); _arch.Dispose();
    }

    [Benchmark] public long MiniArch_Scalar() => _mini.RunIteration();
    [Benchmark] public long MiniArch_SIMD()   => _miniSimd.RunIteration();
    [Benchmark] public long Friflo_Scalar()   => _friflo.RunIteration();
    [Benchmark] public long Friflo_SIMD()     => _frifloSimd.RunIteration();
    [Benchmark] public long Arch_Scalar()     => _arch.RunIteration();
}

public static class Program
{
    public static void Main() => BenchmarkRunner.Run<MixedLoadBench>();
}

// ============================================================================
// MiniArch — scalar
// ============================================================================
public sealed class MiniMixedLoad : IScenario
{
    readonly MiniArch.World _w = new();
    readonly Queue<MiniEntity> _q = new();
    readonly MiniEntity[] _es = new MiniEntity[15000];
    readonly MiniArch.Query _hq, _pq;
    readonly Random _r = new(42);
    int _n = 15000;
    readonly int _mask;

    public MiniMixedLoad(int archetypeCount)
    {
        _mask = archetypeCount - 1;
        for (int i = 0; i < 15000; i++)
        {
            var e = _w.Create(new Position(i, i), new Health(100), new Damage(5), new Team(i % 4));
            _es[i] = e;
            switch (i & _mask) { case 0: _w.Add(e, new PadA(0)); break; case 1: _w.Add(e, new PadB(0)); break; case 2: _w.Add(e, new PadC(0)); break; case 3: _w.Add(e, new PadD(0)); break; case 4: _w.Add(e, new PadE(0)); break; case 5: _w.Add(e, new PadF(0)); break; case 6: _w.Add(e, new PadG(0)); break; case 7: _w.Add(e, new PadH(0)); break; case 8: _w.Add(e, new PadI(0)); break; case 9: _w.Add(e, new PadJ(0)); break; case 10: _w.Add(e, new PadK(0)); break; case 11: _w.Add(e, new PadL(0)); break; case 12: _w.Add(e, new PadM(0)); break; case 13: _w.Add(e, new PadN(0)); break; case 14: _w.Add(e, new PadO(0)); break; default: _w.Add(e, new PadP(0)); break; }
        }
        _hq = _w.Query(new QueryDescription().With<Health>().With<Team>());
        _pq = _w.Query(new QueryDescription().With<Position>());
    }
    public void Dispose() => _w.Dispose();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunIteration()
    {
        long s = 0;
        for (int i = 0; i < 200; i++)
        {
            var e = _w.Create(new Position(_n, _n), new Health(50 + _r.Next(50)), new Damage(3 + _r.Next(7)), new Team(_r.Next(4)));
            switch (_n & _mask) { case 0: _w.Add(e, new PadA(0)); break; case 1: _w.Add(e, new PadB(0)); break; case 2: _w.Add(e, new PadC(0)); break; case 3: _w.Add(e, new PadD(0)); break; case 4: _w.Add(e, new PadE(0)); break; case 5: _w.Add(e, new PadF(0)); break; case 6: _w.Add(e, new PadG(0)); break; case 7: _w.Add(e, new PadH(0)); break; case 8: _w.Add(e, new PadI(0)); break; case 9: _w.Add(e, new PadJ(0)); break; case 10: _w.Add(e, new PadK(0)); break; case 11: _w.Add(e, new PadL(0)); break; case 12: _w.Add(e, new PadM(0)); break; case 13: _w.Add(e, new PadN(0)); break; case 14: _w.Add(e, new PadO(0)); break; default: _w.Add(e, new PadP(0)); break; }
            _q.Enqueue(e); _n++; s += e.Id;
        }
        for (int i = 0; i < 20; i++) { var e = _es[_r.Next(_es.Length)]; if (_w.Has<Burning>(e)) _w.Remove<Burning>(e); else _w.Add(e, new Burning(0)); s += e.Id; }
        foreach (var c in _hq.GetChunks()) { var sh = c.GetSpan<Health>(); var st = c.GetSpan<Team>(); for (int i = 0; i < sh.Length; i++) s += sh[i].Value + st[i].Value; }
        foreach (var c in _pq.GetChunks()) { var sp = c.GetSpan<Position>(); for (int i = 0; i < sp.Length; i++) s += sp[i].X; }
        int dc = Math.Min(200, _q.Count - 10000);
        for (int i = 0; i < dc && _q.Count > 0; i++) { var e = _q.Dequeue(); _w.Destroy(e); s += e.Id; }
        return s;
    }
}

// ============================================================================
// MiniArch — SIMD
// ============================================================================
public sealed class MiniMixedLoadSimd : IScenario
{
    readonly MiniArch.World _w = new();
    readonly Queue<MiniEntity> _q = new();
    readonly MiniEntity[] _es = new MiniEntity[15000];
    readonly MiniArch.Query _hq, _pq;
    readonly Random _r = new(42);
    int _n = 15000;
    static readonly Vector256<int> PosMask = Vector256.Create(-1, 0, -1, 0, -1, 0, -1, 0);
    readonly int _mask;

    public MiniMixedLoadSimd(int archetypeCount)
    {
        _mask = archetypeCount - 1;
        for (int i = 0; i < 15000; i++)
        {
            var e = _w.Create(new Position(i, i), new Health(100), new Damage(5), new Team(i % 4));
            _es[i] = e;
            switch (i & _mask) { case 0: _w.Add(e, new PadA(0)); break; case 1: _w.Add(e, new PadB(0)); break; case 2: _w.Add(e, new PadC(0)); break; case 3: _w.Add(e, new PadD(0)); break; case 4: _w.Add(e, new PadE(0)); break; case 5: _w.Add(e, new PadF(0)); break; case 6: _w.Add(e, new PadG(0)); break; case 7: _w.Add(e, new PadH(0)); break; case 8: _w.Add(e, new PadI(0)); break; case 9: _w.Add(e, new PadJ(0)); break; case 10: _w.Add(e, new PadK(0)); break; case 11: _w.Add(e, new PadL(0)); break; case 12: _w.Add(e, new PadM(0)); break; case 13: _w.Add(e, new PadN(0)); break; case 14: _w.Add(e, new PadO(0)); break; default: _w.Add(e, new PadP(0)); break; }
        }
        _hq = _w.Query(new QueryDescription().With<Health>().With<Team>());
        _pq = _w.Query(new QueryDescription().With<Position>());
    }
    public void Dispose() => _w.Dispose();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunIteration()
    {
        long s = 0;
        for (int i = 0; i < 200; i++)
        {
            var e = _w.Create(new Position(_n, _n), new Health(50 + _r.Next(50)), new Damage(3 + _r.Next(7)), new Team(_r.Next(4)));
            switch (_n & _mask) { case 0: _w.Add(e, new PadA(0)); break; case 1: _w.Add(e, new PadB(0)); break; case 2: _w.Add(e, new PadC(0)); break; case 3: _w.Add(e, new PadD(0)); break; case 4: _w.Add(e, new PadE(0)); break; case 5: _w.Add(e, new PadF(0)); break; case 6: _w.Add(e, new PadG(0)); break; case 7: _w.Add(e, new PadH(0)); break; case 8: _w.Add(e, new PadI(0)); break; case 9: _w.Add(e, new PadJ(0)); break; case 10: _w.Add(e, new PadK(0)); break; case 11: _w.Add(e, new PadL(0)); break; case 12: _w.Add(e, new PadM(0)); break; case 13: _w.Add(e, new PadN(0)); break; case 14: _w.Add(e, new PadO(0)); break; default: _w.Add(e, new PadP(0)); break; }
            _q.Enqueue(e); _n++; s += e.Id;
        }
        for (int i = 0; i < 20; i++) { var e = _es[_r.Next(_es.Length)]; if (_w.Has<Burning>(e)) _w.Remove<Burning>(e); else _w.Add(e, new Burning(0)); s += e.Id; }
        foreach (var c in _hq.GetChunks())
        {
            var hi = MemoryMarshal.Cast<Health, int>(c.GetSpan<Health>());
            var ti = MemoryMarshal.Cast<Team, int>(c.GetSpan<Team>());
            int len = hi.Length, i = 0; long local = 0;
            if (len >= 8) { var vs = Vector256<int>.Zero; var lim = len - (len & 7); for (; i < lim; i += 8) vs += Unsafe.As<int, Vector256<int>>(ref hi[i]) + Unsafe.As<int, Vector256<int>>(ref ti[i]); local += Vector256.Sum(vs); }
            for (; i < len; i++) local += hi[i] + ti[i];
            s += local;
        }
        foreach (var c in _pq.GetChunks())
        {
            var ints = MemoryMarshal.Cast<Position, int>(c.GetSpan<Position>());
            int len = ints.Length, i = 0; long local = 0;
            if (len >= 8) { var lim = len - (len & 7); for (; i < lim; i += 8) { var v = Vector256.BitwiseAnd(Unsafe.As<int, Vector256<int>>(ref ints[i]), PosMask); local += (long)v.GetElement(0) + (long)v.GetElement(2) + (long)v.GetElement(4) + (long)v.GetElement(6); } }
            for (; i < len; i += 2) local += ints[i];
            s += local;
        }
        int dc = Math.Min(200, _q.Count - 10000);
        for (int i = 0; i < dc && _q.Count > 0; i++) { var e = _q.Dequeue(); _w.Destroy(e); s += e.Id; }
        return s;
    }
}

// ============================================================================
// Friflo — scalar
// ============================================================================
public sealed class FrifloMixedLoad : IScenario
{
    readonly EntityStore _s = new();
    readonly Queue<FrifloEntity> _q = new();
    readonly FrifloEntity[] _es = new FrifloEntity[15000];
    readonly ArchetypeQuery<Health, Team> _hq;
    readonly ArchetypeQuery<Position> _pq;
    readonly Random _r = new(42);
    int _n = 15000;
    readonly int _mask;

    public FrifloMixedLoad(int archetypeCount)
    {
        _mask = archetypeCount - 1;
        for (int i = 0; i < 15000; i++)
        {
            var e = _s.CreateEntity(new Position(i, i), new Health(100), new Damage(5), new Team(i % 4));
            _es[i] = e;
            switch (i & _mask) { case 0: e.AddComponent(new PadA(0)); break; case 1: e.AddComponent(new PadB(0)); break; case 2: e.AddComponent(new PadC(0)); break; case 3: e.AddComponent(new PadD(0)); break; case 4: e.AddComponent(new PadE(0)); break; case 5: e.AddComponent(new PadF(0)); break; case 6: e.AddComponent(new PadG(0)); break; case 7: e.AddComponent(new PadH(0)); break; case 8: e.AddComponent(new PadI(0)); break; case 9: e.AddComponent(new PadJ(0)); break; case 10: e.AddComponent(new PadK(0)); break; case 11: e.AddComponent(new PadL(0)); break; case 12: e.AddComponent(new PadM(0)); break; case 13: e.AddComponent(new PadN(0)); break; case 14: e.AddComponent(new PadO(0)); break; default: e.AddComponent(new PadP(0)); break; }
        }
        _hq = _s.Query<Health, Team>();
        _pq = _s.Query<Position>();
    }
    public void Dispose() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunIteration()
    {
        long s = 0;
        for (int i = 0; i < 200; i++)
        {
            var e = _s.CreateEntity(new Position(_n, _n), new Health(50 + _r.Next(50)), new Damage(3 + _r.Next(7)), new Team(_r.Next(4)));
            switch (_n & _mask) { case 0: e.AddComponent(new PadA(0)); break; case 1: e.AddComponent(new PadB(0)); break; case 2: e.AddComponent(new PadC(0)); break; case 3: e.AddComponent(new PadD(0)); break; case 4: e.AddComponent(new PadE(0)); break; case 5: e.AddComponent(new PadF(0)); break; case 6: e.AddComponent(new PadG(0)); break; case 7: e.AddComponent(new PadH(0)); break; case 8: e.AddComponent(new PadI(0)); break; case 9: e.AddComponent(new PadJ(0)); break; case 10: e.AddComponent(new PadK(0)); break; case 11: e.AddComponent(new PadL(0)); break; case 12: e.AddComponent(new PadM(0)); break; case 13: e.AddComponent(new PadN(0)); break; case 14: e.AddComponent(new PadO(0)); break; default: e.AddComponent(new PadP(0)); break; }
            _q.Enqueue(e); _n++; s += e.Id;
        }
        for (int i = 0; i < 20; i++) { var e = _es[_r.Next(_es.Length)]; if (e.HasComponent<Burning>()) e.RemoveComponent<Burning>(); else e.AddComponent(new Burning(0)); s += e.Id; }
        foreach (var (h, t, e) in _hq.Chunks) { var sh = h.Span; var st = t.Span; for (int n = 0; n < e.Length; n++) s += sh[n].Value + st[n].Value; }
        foreach (var (p, e) in _pq.Chunks) { var sp = p.Span; for (int n = 0; n < e.Length; n++) s += sp[n].X; }
        int dc = Math.Min(200, _q.Count - 10000);
        for (int i = 0; i < dc && _q.Count > 0; i++) { var e = _q.Dequeue(); e.DeleteEntity(); s += e.Id; }
        return s;
    }
}

// ============================================================================
// Friflo — SIMD
// ============================================================================
public sealed class FrifloMixedLoadSimd : IScenario
{
    readonly EntityStore _s = new();
    readonly Queue<FrifloEntity> _q = new();
    readonly FrifloEntity[] _es = new FrifloEntity[15000];
    readonly ArchetypeQuery<Health, Team> _hq;
    readonly ArchetypeQuery<Position> _pq;
    readonly Random _r = new(42);
    int _n = 15000;
    static readonly Vector256<int> PosMask = Vector256.Create(-1, 0, -1, 0, -1, 0, -1, 0);
    readonly int _mask;

    public FrifloMixedLoadSimd(int archetypeCount)
    {
        _mask = archetypeCount - 1;
        for (int i = 0; i < 15000; i++)
        {
            var e = _s.CreateEntity(new Position(i, i), new Health(100), new Damage(5), new Team(i % 4));
            _es[i] = e;
            switch (i & _mask) { case 0: e.AddComponent(new PadA(0)); break; case 1: e.AddComponent(new PadB(0)); break; case 2: e.AddComponent(new PadC(0)); break; case 3: e.AddComponent(new PadD(0)); break; case 4: e.AddComponent(new PadE(0)); break; case 5: e.AddComponent(new PadF(0)); break; case 6: e.AddComponent(new PadG(0)); break; case 7: e.AddComponent(new PadH(0)); break; case 8: e.AddComponent(new PadI(0)); break; case 9: e.AddComponent(new PadJ(0)); break; case 10: e.AddComponent(new PadK(0)); break; case 11: e.AddComponent(new PadL(0)); break; case 12: e.AddComponent(new PadM(0)); break; case 13: e.AddComponent(new PadN(0)); break; case 14: e.AddComponent(new PadO(0)); break; default: e.AddComponent(new PadP(0)); break; }
        }
        _hq = _s.Query<Health, Team>();
        _pq = _s.Query<Position>();
    }
    public void Dispose() { }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunIteration()
    {
        long s = 0;
        for (int i = 0; i < 200; i++)
        {
            var e = _s.CreateEntity(new Position(_n, _n), new Health(50 + _r.Next(50)), new Damage(3 + _r.Next(7)), new Team(_r.Next(4)));
            switch (_n & _mask) { case 0: e.AddComponent(new PadA(0)); break; case 1: e.AddComponent(new PadB(0)); break; case 2: e.AddComponent(new PadC(0)); break; case 3: e.AddComponent(new PadD(0)); break; case 4: e.AddComponent(new PadE(0)); break; case 5: e.AddComponent(new PadF(0)); break; case 6: e.AddComponent(new PadG(0)); break; case 7: e.AddComponent(new PadH(0)); break; case 8: e.AddComponent(new PadI(0)); break; case 9: e.AddComponent(new PadJ(0)); break; case 10: e.AddComponent(new PadK(0)); break; case 11: e.AddComponent(new PadL(0)); break; case 12: e.AddComponent(new PadM(0)); break; case 13: e.AddComponent(new PadN(0)); break; case 14: e.AddComponent(new PadO(0)); break; default: e.AddComponent(new PadP(0)); break; }
            _q.Enqueue(e); _n++; s += e.Id;
        }
        for (int i = 0; i < 20; i++) { var e = _es[_r.Next(_es.Length)]; if (e.HasComponent<Burning>()) e.RemoveComponent<Burning>(); else e.AddComponent(new Burning(0)); s += e.Id; }
        foreach (var (h, t, e) in _hq.Chunks)
        {
            var hi = MemoryMarshal.Cast<Health, int>(h.Span);
            var ti = MemoryMarshal.Cast<Team, int>(t.Span);
            int len = hi.Length, i = 0; long local = 0;
            if (len >= 8) { var vs = Vector256<int>.Zero; var lim = len - (len & 7); for (; i < lim; i += 8) vs += Unsafe.As<int, Vector256<int>>(ref hi[i]) + Unsafe.As<int, Vector256<int>>(ref ti[i]); local += Vector256.Sum(vs); }
            for (; i < len; i++) local += hi[i] + ti[i];
            s += local;
        }
        foreach (var (p, e) in _pq.Chunks)
        {
            var ints = MemoryMarshal.Cast<Position, int>(p.Span);
            int len = ints.Length, i = 0; long local = 0;
            if (len >= 8) { var lim = len - (len & 7); for (; i < lim; i += 8) { var v = Vector256.BitwiseAnd(Unsafe.As<int, Vector256<int>>(ref ints[i]), PosMask); local += (long)v.GetElement(0) + (long)v.GetElement(2) + (long)v.GetElement(4) + (long)v.GetElement(6); } }
            for (; i < len; i += 2) local += ints[i];
            s += local;
        }
        int dc = Math.Min(200, _q.Count - 10000);
        for (int i = 0; i < dc && _q.Count > 0; i++) { var e = _q.Dequeue(); e.DeleteEntity(); s += e.Id; }
        return s;
    }
}

// ============================================================================
// Arch
// ============================================================================
public sealed class ArchMixedLoad : IScenario
{
    readonly Arch.Core.World _w = Arch.Core.World.Create();
    readonly Queue<Arch.Core.Entity> _q = new();
    readonly Arch.Core.Entity[] _es = new Arch.Core.Entity[15000];
    readonly Arch.Core.Query _hq, _pq;
    readonly Random _r = new(42);
    int _n = 15000;
    readonly int _mask;

    public ArchMixedLoad(int archetypeCount)
    {
        _mask = archetypeCount - 1;
        for (int i = 0; i < 15000; i++)
        {
            var e = _w.Create<Position, Health, Damage, Team>(new Position(i, i), new Health(100), new Damage(5), new Team(i % 4));
            _es[i] = e;
            switch (i & _mask) { case 0: _w.Add<PadA>(e, new PadA(0)); break; case 1: _w.Add<PadB>(e, new PadB(0)); break; case 2: _w.Add<PadC>(e, new PadC(0)); break; case 3: _w.Add<PadD>(e, new PadD(0)); break; case 4: _w.Add<PadE>(e, new PadE(0)); break; case 5: _w.Add<PadF>(e, new PadF(0)); break; case 6: _w.Add<PadG>(e, new PadG(0)); break; case 7: _w.Add<PadH>(e, new PadH(0)); break; case 8: _w.Add<PadI>(e, new PadI(0)); break; case 9: _w.Add<PadJ>(e, new PadJ(0)); break; case 10: _w.Add<PadK>(e, new PadK(0)); break; case 11: _w.Add<PadL>(e, new PadL(0)); break; case 12: _w.Add<PadM>(e, new PadM(0)); break; case 13: _w.Add<PadN>(e, new PadN(0)); break; case 14: _w.Add<PadO>(e, new PadO(0)); break; default: _w.Add<PadP>(e, new PadP(0)); break; }
        }
        _hq = _w.Query(new Arch.Core.QueryDescription().WithAll<Health, Team>());
        _pq = _w.Query(new Arch.Core.QueryDescription().WithAll<Position>());
    }
    public void Dispose() => _w.Dispose();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public long RunIteration()
    {
        long s = 0;
        for (int i = 0; i < 200; i++)
        {
            var e = _w.Create<Position, Health, Damage, Team>(new Position(_n, _n), new Health(50 + _r.Next(50)), new Damage(3 + _r.Next(7)), new Team(_r.Next(4)));
            switch (_n & _mask) { case 0: _w.Add<PadA>(e, new PadA(0)); break; case 1: _w.Add<PadB>(e, new PadB(0)); break; case 2: _w.Add<PadC>(e, new PadC(0)); break; case 3: _w.Add<PadD>(e, new PadD(0)); break; case 4: _w.Add<PadE>(e, new PadE(0)); break; case 5: _w.Add<PadF>(e, new PadF(0)); break; case 6: _w.Add<PadG>(e, new PadG(0)); break; case 7: _w.Add<PadH>(e, new PadH(0)); break; case 8: _w.Add<PadI>(e, new PadI(0)); break; case 9: _w.Add<PadJ>(e, new PadJ(0)); break; case 10: _w.Add<PadK>(e, new PadK(0)); break; case 11: _w.Add<PadL>(e, new PadL(0)); break; case 12: _w.Add<PadM>(e, new PadM(0)); break; case 13: _w.Add<PadN>(e, new PadN(0)); break; case 14: _w.Add<PadO>(e, new PadO(0)); break; default: _w.Add<PadP>(e, new PadP(0)); break; }
            _q.Enqueue(e); _n++; s += e.Id;
        }
        for (int i = 0; i < 20; i++) { var e = _es[_r.Next(_es.Length)]; if (_w.Has<Burning>(e)) _w.Remove<Burning>(e); else _w.Add<Burning>(e, new Burning(0)); s += e.Id; }
        foreach (var c in _hq) { var sh = c.GetSpan<Health>(); var st = c.GetSpan<Team>(); for (int i = 0; i < c.Count; i++) s += sh[i].Value + st[i].Value; }
        foreach (var c in _pq) { var sp = c.GetSpan<Position>(); for (int i = 0; i < c.Count; i++) s += sp[i].X; }
        int dc = Math.Min(200, _q.Count - 10000);
        for (int i = 0; i < dc && _q.Count > 0; i++) { var e = _q.Dequeue(); _w.Destroy(e); s += e.Id; }
        return s;
    }
}
