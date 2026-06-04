using System.Diagnostics;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using MiniArch;
using MiniArch.Core;

using MiniEntity = MiniArch.Entity;
using MiniQuery = MiniArch.Core.Query;
using FrifloEntity = Friflo.Engine.ECS.Entity;

namespace FrifloGameScenarios;

public static class Program
{
    const int WarmupSeconds = 3;
    const int MeasureSeconds = 10;

    public static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== MiniArch vs Friflo vs Arch: High-Pressure Game Scenario Benchmarks ===");
        Console.WriteLine($"Warmup: {WarmupSeconds}s, Measure: {MeasureSeconds}s per engine per scenario");
        Console.WriteLine();

        var all = new (string name, Func<IGameScenario> mini, Func<IGameScenario> friflo, Func<IGameScenario> arch)[]
        {
            ("S1-BulletHell",         ()=>new MiniBulletHell(),        ()=>new FrifloBulletHell(),        ()=>new ArchBulletHell()),
            ("S2-MMOZone",            ()=>new MiniMMOZone(),           ()=>new FrifloMMOZone(),           ()=>new ArchMMOZone()),
            ("S3-WaveSpawner",        ()=>new MiniWaveSpawner(),       ()=>new FrifloWaveSpawner(),       ()=>new ArchWaveSpawner()),
            ("S4-BuffSystem",         ()=>new MiniBuffSystem(),        ()=>new FrifloBuffSystem(),        ()=>new ArchBuffSystem()),
            ("S5-FullGameLoop",       ()=>new MiniFullGameLoop(),      ()=>new FrifloFullGameLoop(),      ()=>new ArchFullGameLoop()),
            ("S6-RPGStats",           ()=>new MiniRPGStats(),          ()=>new FrifloRPGStats(),          ()=>new ArchRPGStats()),
            ("S7-ConditionalEffects", ()=>new MiniConditionalEffects(),()=>new FrifloConditionalEffects(),()=>new ArchConditionalEffects()),
            ("S8-AIStateMachine",     ()=>new MiniAIStateMachine(),    ()=>new FrifloAIStateMachine(),    ()=>new ArchAIStateMachine()),
            ("S9-TeamAlternation",    ()=>new MiniTeamAlternation(),   ()=>new FrifloTeamAlternation(),   ()=>new ArchTeamAlternation()),
            ("S10-MixedLoad",         ()=>new MiniMixedLoad(),         ()=>new FrifloMixedLoad(),         ()=>new ArchMixedLoad()),
            ("S11-RandomEntityAccess",  ()=>new MiniRandomEntityAccess(), ()=>new FrifloRandomEntityAccess(), ()=>new ArchRandomEntityAccess()),
            ("S12-FollowTheLeader",     ()=>new MiniFollowTheLeader(),    ()=>new FrifloFollowTheLeader(),    ()=>new ArchFollowTheLeader()),
        };

        var scenarios = args.Length > 0
            ? all.Where(s => s.name.StartsWith(args[0], StringComparison.OrdinalIgnoreCase)).ToArray()
            : all;

        if (scenarios.Length == 0)
        {
            Console.WriteLine($"No scenario matches \"{args[0]}\". Available: {string.Join(", ", all.Select(s => s.name))}");
            return;
        }

        if (args.Length > 0)
        {
            Console.WriteLine($"Filter: \"{args[0]}\" -> {scenarios.Length} scenario(s) selected");
            Console.WriteLine();
        }

        Console.WriteLine($"{"Scenario",-22} | {"Engine",-10} | {"Ops/s",12} | {"Checksum",12} | {"GC Gen0/1/2",12}");
        Console.WriteLine(new string('-', 85));

        foreach (var (name, mini, friflo, arch) in scenarios)
        {
            using (var s = mini()) { var r = RunTimed(s); Console.WriteLine($"{name,-22} | {"MiniArch",-10} | {r.OpsPerSec,12:F1} | {r.Checksum,12} | {r.GcCollections,12}"); }
            GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
            using (var s = friflo()) { var r = RunTimed(s); Console.WriteLine($"{name,-22} | {"Friflo",-10} | {r.OpsPerSec,12:F1} | {r.Checksum,12} | {r.GcCollections,12}"); }
            GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
            using (var s = arch()) { var r = RunTimed(s); Console.WriteLine($"{name,-22} | {"Arch",-10} | {r.OpsPerSec,12:F1} | {r.Checksum,12} | {r.GcCollections,12}"); }
            GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
            Console.WriteLine();
        }
        Console.WriteLine("=== Benchmark Complete ===");
    }

    static ScenarioResult RunTimed(IGameScenario scenario)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < WarmupSeconds) scenario.RunIteration();
        GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        sw.Restart(); long iters = 0; long ck = 0;
        while (sw.Elapsed.TotalSeconds < MeasureSeconds) { ck += scenario.RunIteration(); iters++; }
        int d0 = GC.CollectionCount(0) - g0, d1 = GC.CollectionCount(1) - g1, d2 = GC.CollectionCount(2) - g2;
        return new ScenarioResult(iters / sw.Elapsed.TotalSeconds, ck, $"{d0}/{d1}/{d2}");
    }
}

public interface IGameScenario : IDisposable { void Warmup(int n); long RunIteration(); }
public readonly record struct ScenarioResult(double OpsPerSec, long Checksum, string GcCollections);

// ============================================================================
// S1: BulletHell — 100K entities, pure iteration throughput
// All three engines use equivalent chunk-span for-loop patterns.
// ============================================================================
public sealed class MiniBulletHell : IGameScenario
{
    readonly World _w; readonly MiniQuery _q;
    public MiniBulletHell(){_w=new World(128,100000);for(int i=0;i<100000;i++)_w.Create(new Position(i,i),new Velocity(1,1));_q=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<Velocity>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var c in _q.ChunksOf<Position,Velocity>()){int n=c.Count;var sp=c.Span0;var sv=c.Span1;for(int i=0;i<n;i++)s+=sp[i].X+sv[i].VY;}return s;}
}
public sealed class FrifloBulletHell : IGameScenario
{
    readonly EntityStore _s; readonly ArchetypeQuery<Position,Velocity> _q;
    public FrifloBulletHell(){_s=new EntityStore();for(int i=0;i<100000;i++)_s.CreateEntity(new Position(i,i),new Velocity(1,1));_q=_s.Query<Position,Velocity>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var(p,v,e)in _q.Chunks){var sp=p.Span;var sv=v.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sv[n].VY;}return s;}
}

// ============================================================================
// S2: MMOZone — 30K entities across 8 archetypes
// ============================================================================
public sealed class MiniMMOZone : IGameScenario
{
    readonly World _w; readonly MiniQuery _q;
    public MiniMMOZone(){_w=new World(128,30000);int per=30000/8,idx=0;for(int a=0;a<8&&idx<30000;a++)for(int i=0;i<per&&idx<30000;i++,idx++){var e=_w.Create(new Position(idx,idx),new Health(100+idx%50),new Team(idx%4));switch(a){case 0:_w.Add(e,new Armor(10));break;case 1:_w.Add(e,new Mana(50));break;case 2:_w.Add(e,new Damage(5));_w.Add(e,new Shield(20));break;case 3:_w.Add(e,new Cooldown(0));_w.Add(e,new Stamina(30));break;case 4:_w.Add(e,new Velocity(0,0));break;case 5:_w.Add(e,new Velocity(0,0));_w.Add(e,new Armor(15));break;case 6:_w.Add(e,new Mana(30));_w.Add(e,new Damage(8));break;case 7:_w.Add(e,new Shield(10));_w.Add(e,new Stamina(20));_w.Add(e,new XP(0));break;}}_q=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<Health>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var c in _q.ChunksOf<Position,Health>()){var sp=c.Span0;var sh=c.Span1;for(int i=0;i<c.Count;i++)s+=sp[i].X+sh[i].Value;}return s;}
}
public sealed class FrifloMMOZone : IGameScenario
{
    readonly EntityStore _s; readonly ArchetypeQuery<Position,Health> _q;
    public FrifloMMOZone(){_s=new EntityStore();int per=30000/8,idx=0;for(int a=0;a<8&&idx<30000;a++)for(int i=0;i<per&&idx<30000;i++,idx++){var e=_s.CreateEntity(new Position(idx,idx),new Health(100+idx%50),new Team(idx%4));switch(a){case 0:e.AddComponent(new Armor(10));break;case 1:e.AddComponent(new Mana(50));break;case 2:e.AddComponent(new Damage(5));e.AddComponent(new Shield(20));break;case 3:e.AddComponent(new Cooldown(0));e.AddComponent(new Stamina(30));break;case 4:e.AddComponent(new Velocity(0,0));break;case 5:e.AddComponent(new Velocity(0,0));e.AddComponent(new Armor(15));break;case 6:e.AddComponent(new Mana(30));e.AddComponent(new Damage(8));break;case 7:e.AddComponent(new Shield(10));e.AddComponent(new Stamina(20));e.AddComponent(new XP(0));break;}}_q=_s.Query<Position,Health>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var(p,h,e)in _q.Chunks){var sp=p.Span;var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sh[n].Value;}return s;}
}

// ============================================================================
// S3: WaveSpawner — spawn/despawn churn
// ============================================================================
public sealed class MiniWaveSpawner : IGameScenario
{
    readonly World _w; readonly Queue<MiniEntity> _a=new(); readonly MiniQuery _q; readonly Random _r=new(42);
    public MiniWaveSpawner(){_w=new World(128,50000);for(int i=0;i<30000;i++){var e=_w.Create(new Position(i,i),new Velocity(i%5,i%5),new Lifetime(_r.Next(60,180)));_a.Enqueue(e);}_q=MiniQuery.Create(_w,new QueryDescription().With<Lifetime>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;int sc=_r.Next(500,1500);for(int i=0;i<sc;i++){var e=_w.Create(new Position(_r.Next(1000),_r.Next(1000)),new Velocity(_r.Next(5),_r.Next(5)),new Lifetime(_r.Next(30,120)));_a.Enqueue(e);s+=e.Id;}foreach(var c in _q.ChunksOf<Lifetime>()){var sl=c.Span0;for(int i=0;i<c.Count;i++)s+=sl[i].Ticks;}int d=Math.Min(_a.Count-20000,2000);for(int i=0;i<d&&_a.Count>0;i++){var e=_a.Dequeue();_w.Destroy(e);s+=e.Id;}return s;}
}
public sealed class FrifloWaveSpawner : IGameScenario
{
    readonly EntityStore _s; readonly Queue<FrifloEntity> _a=new(); readonly ArchetypeQuery<Lifetime> _q; readonly Random _r=new(42);
    public FrifloWaveSpawner(){_s=new EntityStore();for(int i=0;i<30000;i++){var e=_s.CreateEntity(new Position(i,i),new Velocity(i%5,i%5),new Lifetime(_r.Next(60,180)));_a.Enqueue(e);}_q=_s.Query<Lifetime>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;int sc=_r.Next(500,1500);for(int i=0;i<sc;i++){var e=_s.CreateEntity(new Position(_r.Next(1000),_r.Next(1000)),new Velocity(_r.Next(5),_r.Next(5)),new Lifetime(_r.Next(30,120)));_a.Enqueue(e);s+=e.Id;}foreach(var(lt,e)in _q.Chunks){var sl=lt.Span;for(int n=0;n<e.Length;n++)s+=sl[n].Ticks;}int d=Math.Min(_a.Count-20000,2000);for(int i=0;i<d&&_a.Count>0;i++){var e=_a.Dequeue();e.DeleteEntity();s+=e.Id;}return s;}
}

// ============================================================================
// S4: BuffSystem — component add/remove stress
// ============================================================================
public sealed class MiniBuffSystem : IGameScenario
{
    readonly World _w; readonly MiniEntity[] _es; readonly MiniQuery _bq,_fq; readonly Random _r=new(42);
    public MiniBuffSystem(){_w=new World(128,30000);_es=new MiniEntity[30000];for(int i=0;i<_es.Length;i++){_es[i]=_w.Create(new Position(i,i),new Health(100));if(_r.Next(2)==0)_w.Add(_es[i],new Burning(0));if(_r.Next(3)==0)_w.Add(_es[i],new Frozen(0));}_bq=MiniQuery.Create(_w,new QueryDescription().With<Burning>().With<Health>());_fq=MiniQuery.Create(_w,new QueryDescription().With<Frozen>().With<Position>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;for(int i=0;i<1500;i++){var e=_es[_r.Next(_es.Length)];if(_r.Next(2)==0){if(_w.Has<Burning>(e))_w.Remove<Burning>(e);else _w.Add(e,new Burning(0));}else{if(_w.Has<Frozen>(e))_w.Remove<Frozen>(e);else _w.Add(e,new Frozen(0));}s+=e.Id;}foreach(var c in _bq.ChunksOf<Health>()){var sh=c.Span0;for(int i=0;i<c.Count;i++)s+=sh[i].Value;}foreach(var c in _fq.ChunksOf<Position>()){var sp=c.Span0;for(int i=0;i<c.Count;i++)s+=sp[i].X;}return s;}
}
public sealed class FrifloBuffSystem : IGameScenario
{
    readonly EntityStore _s; readonly FrifloEntity[] _es; readonly ArchetypeQuery<Burning,Health> _bq; readonly ArchetypeQuery<Frozen,Position> _fq; readonly Random _r=new(42);
    public FrifloBuffSystem(){_s=new EntityStore();_es=new FrifloEntity[30000];for(int i=0;i<_es.Length;i++){_es[i]=_s.CreateEntity(new Position(i,i),new Health(100));if(_r.Next(2)==0)_es[i].AddComponent(new Burning(0));if(_r.Next(3)==0)_es[i].AddComponent(new Frozen(0));}_bq=_s.Query<Burning,Health>();_fq=_s.Query<Frozen,Position>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;for(int i=0;i<1500;i++){var e=_es[_r.Next(_es.Length)];if(_r.Next(2)==0){if(e.HasComponent<Burning>())e.RemoveComponent<Burning>();else e.AddComponent(new Burning(0));}else{if(e.HasComponent<Frozen>())e.RemoveComponent<Frozen>();else e.AddComponent(new Frozen(0));}s+=e.Id;}foreach(var(_,h,e)in _bq.Chunks){var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sh[n].Value;}foreach(var(_,p,e)in _fq.Chunks){var sp=p.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X;}return s;}
}

// ============================================================================
// S5: FullGameLoop — 4-system pipeline
// ============================================================================
public sealed class MiniFullGameLoop : IGameScenario
{
    readonly World _w; readonly MiniQuery _mq,_cq,_hq; readonly Random _r=new(42);
    public MiniFullGameLoop(){_w=new World(128,20000);for(int i=0;i<20000;i++)_w.Create(new Position(i%500,i/500),new Velocity(_r.Next(3)-1,_r.Next(3)-1),new Health(50+_r.Next(50)),new Team(i%4),new Damage(5+_r.Next(10)));_mq=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<Velocity>());_cq=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<Health>().With<Damage>().With<Team>());_hq=MiniQuery.Create(_w,new QueryDescription().With<Health>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;
        foreach(var c in _mq.ChunksOf<Position,Velocity>()){int n=c.Count;var sp=c.Span0;var sv=c.Span1;for(int i=0;i<n;i++)s+=sp[i].X+sv[i].VX;}
        foreach(var c in _cq.ChunksOf<Position,Health,Damage,Team>()){int n=c.Count;var sp=c.Span0;var sh=c.Span1;var sd=c.Span2;var st=c.Span3;for(int i=0;i<n;i++)s+=sp[i].X+sh[i].Value+sd[i].Value+st[i].Value;}
        foreach(var c in _hq.ChunksOf<Health>()){int n=c.Count;var sh=c.Span0;for(int i=0;i<n;i++)s+=sh[i].Value;}return s;}
}
public sealed class FrifloFullGameLoop : IGameScenario
{
    readonly EntityStore _s; readonly ArchetypeQuery<Position,Velocity> _mq; readonly ArchetypeQuery<Position,Health,Damage,Team> _cq; readonly ArchetypeQuery<Health> _hq; readonly Random _r=new(42);
    public FrifloFullGameLoop(){_s=new EntityStore();for(int i=0;i<20000;i++)_s.CreateEntity(new Position(i%500,i/500),new Velocity(_r.Next(3)-1,_r.Next(3)-1),new Health(50+_r.Next(50)),new Team(i%4),new Damage(5+_r.Next(10)));_mq=_s.Query<Position,Velocity>();_cq=_s.Query<Position,Health,Damage,Team>();_hq=_s.Query<Health>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var(p,v,e)in _mq.Chunks){var sp=p.Span;var sv=v.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sv[n].VX;}foreach(var(p,h,d,t,e)in _cq.Chunks){var sp=p.Span;var sh=h.Span;var sd=d.Span;var st=t.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sh[n].Value+sd[n].Value+st[n].Value;}foreach(var(h,e)in _hq.Chunks){var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sh[n].Value;}return s;}
}

// ============================================================================
// S6: RPGStats — wide component access (5 comps)
// ============================================================================
public sealed class MiniRPGStats : IGameScenario
{
    readonly World _w; readonly MiniQuery _q;
    public MiniRPGStats(){_w=new World(128,40000);for(int i=0;i<40000;i++)_w.Create(new Position(i,i+1),new Velocity(i%5,i%5),new Health(100+i%50),new Mana(50+i%30),new Armor(10+i%20));_q=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<Velocity>().With<Health>().With<Mana>().With<Armor>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var c in _q.Chunks){var sp=c.GetComponentSpan<Position>(Component<Position>.ComponentType);var sv=c.GetComponentSpan<Velocity>(Component<Velocity>.ComponentType);var sh=c.GetComponentSpan<Health>(Component<Health>.ComponentType);var sm=c.GetComponentSpan<Mana>(Component<Mana>.ComponentType);var sa=c.GetComponentSpan<Armor>(Component<Armor>.ComponentType);for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VY+sh[i].Value+sm[i].Value+sa[i].Value;}return s;}
}
public sealed class FrifloRPGStats : IGameScenario
{
    readonly EntityStore _s; readonly ArchetypeQuery<Position,Velocity,Health,Mana,Armor> _q;
    public FrifloRPGStats(){_s=new EntityStore();for(int i=0;i<40000;i++)_s.CreateEntity(new Position(i,i+1),new Velocity(i%5,i%5),new Health(100+i%50),new Mana(50+i%30),new Armor(10+i%20));_q=_s.Query<Position,Velocity,Health,Mana,Armor>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var(p,v,h,m,a,e)in _q.Chunks){var sp=p.Span;var sv=v.Span;var sh=h.Span;var sm=m.Span;var sa=a.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sv[n].VY+sh[n].Value+sm[n].Value+sa[n].Value;}return s;}
}

// ============================================================================
// S7: ConditionalEffects — filtered With/Without
// ============================================================================
public sealed class MiniConditionalEffects : IGameScenario
{
    readonly World _w; readonly MiniQuery _pq,_bq;
    public MiniConditionalEffects(){_w=new World(128,50000);var r=new Random(42);for(int i=0;i<50000;i++){var e=_w.Create(new Position(i,i),new Health(100));if(r.Next(100)<40)_w.Add(e,new Poisoned(0));if(r.Next(100)<30)_w.Add(e,new Burning(0));if(r.Next(100)<5)_w.Add(e,new ImmuneToPoison(0));if(r.Next(100)<5)_w.Add(e,new FireResist(0));}_pq=MiniQuery.Create(_w,new QueryDescription().With<Poisoned>().With<Health>().Without<ImmuneToPoison>());_bq=MiniQuery.Create(_w,new QueryDescription().With<Burning>().With<Health>().Without<FireResist>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var c in _pq.ChunksOf<Health>()){var sh=c.Span0;for(int i=0;i<c.Count;i++)s+=sh[i].Value;}foreach(var c in _bq.ChunksOf<Health>()){var sh=c.Span0;for(int i=0;i<c.Count;i++)s+=sh[i].Value;}return s;}
}
public sealed class FrifloConditionalEffects : IGameScenario
{
    readonly EntityStore _s; readonly ArchetypeQuery<Poisoned,Health> _pq; readonly ArchetypeQuery<Burning,Health> _bq;
    public FrifloConditionalEffects(){_s=new EntityStore();var r=new Random(42);for(int i=0;i<50000;i++){var e=_s.CreateEntity(new Position(i,i),new Health(100));if(r.Next(100)<40)e.AddComponent(new Poisoned(0));if(r.Next(100)<30)e.AddComponent(new Burning(0));if(r.Next(100)<5)e.AddComponent(new ImmuneToPoison(0));if(r.Next(100)<5)e.AddComponent(new FireResist(0));}_pq=_s.Query<Poisoned,Health>().WithoutAllComponents(ComponentTypes.Get<ImmuneToPoison>());_bq=_s.Query<Burning,Health>().WithoutAllComponents(ComponentTypes.Get<FireResist>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;foreach(var(_,h,e)in _pq.Chunks){var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sh[n].Value;}foreach(var(_,h,e)in _bq.Chunks){var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sh[n].Value;}return s;}
}

// ============================================================================
// S8: AIStateMachine — rapid archetype switching
// ============================================================================
public sealed class MiniAIStateMachine : IGameScenario
{
    readonly World _w; readonly MiniEntity[] _es; readonly MiniQuery _iq,_mq,_aq; readonly Random _r=new(42);
    public MiniAIStateMachine(){_w=new World(128,30000);_es=new MiniEntity[30000];for(int i=0;i<_es.Length;i++)_es[i]=_w.Create(new Position(i,i),new StateIdle(0));_iq=MiniQuery.Create(_w,new QueryDescription().With<StateIdle>().With<Position>());_mq=MiniQuery.Create(_w,new QueryDescription().With<StateMove>().With<Position>());_aq=MiniQuery.Create(_w,new QueryDescription().With<StateAttack>().With<Position>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;for(int i=0;i<_es.Length/5;i++){var e=_es[_r.Next(_es.Length)];if(_w.Has<StateIdle>(e))_w.Remove<StateIdle>(e);if(_w.Has<StateMove>(e))_w.Remove<StateMove>(e);if(_w.Has<StateAttack>(e))_w.Remove<StateAttack>(e);if(_w.Has<StateDead>(e))_w.Remove<StateDead>(e);switch(_r.Next(4)){case 0:_w.Add(e,new StateIdle(0));break;case 1:_w.Add(e,new StateMove(0));break;case 2:_w.Add(e,new StateAttack(0));break;case 3:_w.Add(e,new StateDead(0));break;}s+=e.Id;}foreach(var c in _iq.ChunksOf<Position>()){var sp=c.Span0;for(int i=0;i<c.Count;i++)s+=sp[i].X;}foreach(var c in _mq.ChunksOf<Position>()){var sp=c.Span0;for(int i=0;i<c.Count;i++)s+=sp[i].X;}foreach(var c in _aq.ChunksOf<Position>()){var sp=c.Span0;for(int i=0;i<c.Count;i++)s+=sp[i].X;}return s;}
}
public sealed class FrifloAIStateMachine : IGameScenario
{
    readonly EntityStore _s; readonly FrifloEntity[] _es; readonly ArchetypeQuery<StateIdle,Position> _iq; readonly ArchetypeQuery<StateMove,Position> _mq; readonly ArchetypeQuery<StateAttack,Position> _aq; readonly Random _r=new(42);
    public FrifloAIStateMachine(){_s=new EntityStore();_es=new FrifloEntity[30000];for(int i=0;i<_es.Length;i++)_es[i]=_s.CreateEntity(new Position(i,i),new StateIdle(0));_iq=_s.Query<StateIdle,Position>();_mq=_s.Query<StateMove,Position>();_aq=_s.Query<StateAttack,Position>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;for(int i=0;i<_es.Length/5;i++){var e=_es[_r.Next(_es.Length)];if(e.HasComponent<StateIdle>())e.RemoveComponent<StateIdle>();if(e.HasComponent<StateMove>())e.RemoveComponent<StateMove>();if(e.HasComponent<StateAttack>())e.RemoveComponent<StateAttack>();if(e.HasComponent<StateDead>())e.RemoveComponent<StateDead>();switch(_r.Next(4)){case 0:e.AddComponent(new StateIdle(0));break;case 1:e.AddComponent(new StateMove(0));break;case 2:e.AddComponent(new StateAttack(0));break;case 3:e.AddComponent(new StateDead(0));break;}s+=e.Id;}foreach(var(_,p,e)in _iq.Chunks){var sp=p.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X;}foreach(var(_,p,e)in _mq.Chunks){var sp=p.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X;}foreach(var(_,p,e)in _aq.Chunks){var sp=p.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X;}return s;}
}

// ============================================================================
// S9: TeamAlternation — alternating query access
// ============================================================================
public sealed class MiniTeamAlternation : IGameScenario
{
    readonly World _w; readonly MiniQuery _aq,_bq; int _t;
    public MiniTeamAlternation(){_w=new World(128,60000);for(int i=0;i<60000;i++){var e=_w.Create(new Position(i,i),new Health(100));if(i%2==0)_w.Add(e,new TagTeamA(0));else _w.Add(e,new TagTeamB(0));}_aq=MiniQuery.Create(_w,new QueryDescription().With<TagTeamA>().With<Position>().With<Health>());_bq=MiniQuery.Create(_w,new QueryDescription().With<TagTeamB>().With<Position>().With<Health>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){_t++;long s=0;var q=_t%2==1?_aq:_bq;foreach(var c in q.ChunksOf<Position,Health>()){var sp=c.Span0;var sh=c.Span1;for(int i=0;i<c.Count;i++)s+=sp[i].X+sh[i].Value;}return s;}
}
public sealed class FrifloTeamAlternation : IGameScenario
{
    readonly EntityStore _s; readonly ArchetypeQuery<TagTeamA,Position,Health> _aq; readonly ArchetypeQuery<TagTeamB,Position,Health> _bq; int _t;
    public FrifloTeamAlternation(){_s=new EntityStore();for(int i=0;i<60000;i++){var e=_s.CreateEntity(new Position(i,i),new Health(100));if(i%2==0)e.AddComponent(new TagTeamA(0));else e.AddComponent(new TagTeamB(0));}_aq=_s.Query<TagTeamA,Position,Health>();_bq=_s.Query<TagTeamB,Position,Health>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){_t++;long s=0;if(_t%2==1){foreach(var(_,p,h,e)in _aq.Chunks){var sp=p.Span;var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sh[n].Value;}}else{foreach(var(_,p,h,e)in _bq.Chunks){var sp=p.Span;var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sh[n].Value;}}return s;}
}

// ============================================================================
// S10: MixedLoad — create + modify + destroy combined
// ============================================================================
public sealed class MiniMixedLoad : IGameScenario
{
    readonly World _w; readonly List<MiniEntity> _a=new(); readonly MiniQuery _hq,_pq; readonly Random _r=new(42); int _n=15000;
    public MiniMixedLoad(){_w=new World(128,20000);for(int i=0;i<15000;i++)_a.Add(_w.Create(new Position(i,i),new Health(100),new Damage(5),new Team(i%4)));_hq=MiniQuery.Create(_w,new QueryDescription().With<Health>().With<Team>());_pq=MiniQuery.Create(_w,new QueryDescription().With<Position>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;for(int i=0;i<200;i++){var e=_w.Create(new Position(_n,_n),new Health(50+_r.Next(50)),new Damage(3+_r.Next(7)),new Team(_r.Next(4)));_a.Add(e);_n++;s+=e.Id;}int mc=Math.Min(5000,_a.Count);for(int i=0;i<mc;i++)s+=_a[_r.Next(_a.Count)].Id;foreach(var c in _hq.ChunksOf<Health,Team>()){var sh=c.Span0;var st=c.Span1;for(int i=0;i<c.Count;i++)s+=sh[i].Value+st[i].Value;}foreach(var c in _pq.ChunksOf<Position>()){var sp=c.Span0;for(int i=0;i<c.Count;i++)s+=sp[i].X;}int dc=Math.Min(200,_a.Count-10000);for(int i=0;i<dc&&_a.Count>0;i++){var e=_a[0];_a.RemoveAt(0);_w.Destroy(e);s+=e.Id;}return s;}
}
public sealed class FrifloMixedLoad : IGameScenario
{
    readonly EntityStore _s; readonly List<FrifloEntity> _a=new(); readonly ArchetypeQuery<Health,Team> _hq; readonly ArchetypeQuery<Position> _pq; readonly Random _r=new(42); int _n=15000;
    public FrifloMixedLoad(){_s=new EntityStore();for(int i=0;i<15000;i++)_a.Add(_s.CreateEntity(new Position(i,i),new Health(100),new Damage(5),new Team(i%4)));_hq=_s.Query<Health,Team>();_pq=_s.Query<Position>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;for(int i=0;i<200;i++){var e=_s.CreateEntity(new Position(_n,_n),new Health(50+_r.Next(50)),new Damage(3+_r.Next(7)),new Team(_r.Next(4)));_a.Add(e);_n++;s+=e.Id;}int mc=Math.Min(5000,_a.Count);for(int i=0;i<mc;i++)s+=_a[_r.Next(_a.Count)].Id;foreach(var(h,t,e)in _hq.Chunks){var sh=h.Span;var st=t.Span;for(int n=0;n<e.Length;n++)s+=sh[n].Value+st[n].Value;}foreach(var(p,e)in _pq.Chunks){var sp=p.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X;}int dc=Math.Min(200,_a.Count-10000);for(int i=0;i<dc&&_a.Count>0;i++){var e=_a[0];_a.RemoveAt(0);e.DeleteEntity();s+=e.Id;}return s;}
}

// ============================================================================
// S11: RandomEntityAccess — entity-level get/set stress (no chunk iteration)
// Tests: per-entity random access throughput. This is the slow path in every ECS
// but unavoidable in gameplay code (damage events, pickups, trigger zones).
// ============================================================================
public sealed class MiniRandomEntityAccess : IGameScenario
{
    readonly World _w; readonly MiniEntity[] _es; readonly MiniQuery _q; readonly Random _r=new(42);
    public MiniRandomEntityAccess(){_w=new World(128,30000);_es=new MiniEntity[30000];for(int i=0;i<30000;i++)_es[i]=_w.Create(new Position(i,i),new Health(100+i%50));_q=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<Health>());}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;
        for(int i=0;i<2000;i++){ref var e=ref _es[_r.Next(30000)];ref var h=ref _w.GetRef<Health>(e);s+=h.Value;h.Value--;ref var p=ref _w.GetRef<Position>(e);s+=p.X+p.Y;p.X++;p.Y++;};
        foreach(var c in _q.ChunksOf<Position,Health>()){int n=c.Count;var sp=c.Span0;var sh=c.Span1;for(int i=0;i<n;i++)s+=sp[i].X+sh[i].Value;}return s;}
}
public sealed class FrifloRandomEntityAccess : IGameScenario
{
    readonly EntityStore _s; readonly FrifloEntity[] _es; readonly ArchetypeQuery<Position,Health> _q; readonly Random _r=new(42);
    public FrifloRandomEntityAccess(){_s=new EntityStore();_es=new FrifloEntity[30000];for(int i=0;i<30000;i++)_es[i]=_s.CreateEntity(new Position(i,i),new Health(100+i%50));_q=_s.Query<Position,Health>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;
        for(int i=0;i<2000;i++){var e=_es[_r.Next(30000)];var h=e.GetComponent<Health>();s+=h.Value;h.Value-=1;e.AddComponent(h);var p=e.GetComponent<Position>();s+=p.X+p.Y;p.X+=1;p.Y+=1;e.AddComponent(p);}
        foreach(var(p,h,e)in _q.Chunks){var sp=p.Span;var sh=h.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sh[n].Value;}return s;}
}

// ============================================================================
// S12: FollowTheLeader — cross-entity lookup during chunk iteration
// Tests: entity-level random reads inside a query loop. Followers read their
// leader's Position via entity reference each frame — a common formation/tracking pattern.
// ============================================================================
public sealed class MiniFollowTheLeader : IGameScenario
{
    readonly World _w; readonly MiniEntity[] _ld; readonly MiniQuery _fq,_lq; readonly ComponentLookup<Position> _posLookup;
    public MiniFollowTheLeader(){_w=new World(128,20100);_ld=new MiniEntity[100];
        for(int i=0;i<100;i++)_ld[i]=_w.Create(new Position(i*10,i*10),new Velocity(1,1));
        for(int i=0;i<20000;i++)_w.Create(new Position(i,i+1),new LeaderIdx(i%100));
        _fq=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<LeaderIdx>());
        _lq=MiniQuery.Create(_w,new QueryDescription().With<Position>().With<Velocity>());_posLookup=_w.Lookup<Position>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;
        foreach(var c in _fq.ChunksOf<Position,LeaderIdx>()){int n=c.Count;var sp=c.Span0;var sl=c.Span1;for(int i=0;i<n;i++){ref var lp=ref _posLookup.GetRef(_ld[sl[i].Value]);s+=sp[i].X+lp.X+sp[i].Y+lp.Y;}}
        foreach(var c in _lq.ChunksOf<Position,Velocity>()){int n=c.Count;var sp=c.Span0;var sv=c.Span1;for(int i=0;i<n;i++)s+=sp[i].X+sv[i].VX;}return s;}
}
public sealed class FrifloFollowTheLeader : IGameScenario
{
    readonly EntityStore _s; readonly FrifloEntity[] _ld; readonly ArchetypeQuery<Position,LeaderIdx> _fq; readonly ArchetypeQuery<Position,Velocity> _lq;
    public FrifloFollowTheLeader(){_s=new EntityStore();_ld=new FrifloEntity[100];
        for(int i=0;i<100;i++)_ld[i]=_s.CreateEntity(new Position(i*10,i*10),new Velocity(1,1));
        for(int i=0;i<20000;i++)_s.CreateEntity(new Position(i,i+1),new LeaderIdx(i%100));
        _fq=_s.Query<Position,LeaderIdx>();_lq=_s.Query<Position,Velocity>();}
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();}public void Dispose(){}
    [MethodImpl(MethodImplOptions.NoInlining)]public long RunIteration(){long s=0;
        foreach(var(p,l,e)in _fq.Chunks){var sp=p.Span;var sl=l.Span;for(int n=0;n<e.Length;n++){var lp=_ld[sl[n].Value].GetComponent<Position>();s+=sp[n].X+lp.X+sp[n].Y+lp.Y;}}
        foreach(var(p,v,e)in _lq.Chunks){var sp=p.Span;var sv=v.Span;for(int n=0;n<e.Length;n++)s+=sp[n].X+sv[n].VX;}return s;}
}
