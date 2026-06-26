using System.Runtime.CompilerServices;
using System.Threading;
using Arch.Core;

namespace FrifloGameScenarios;

// ============================================================================
// S1: BulletHell — 100K entities, pure iteration throughput
// ============================================================================
public sealed class ArchBulletHell : IGameScenario
{
    readonly World _w; Query _q;
    public ArchBulletHell() { _w=World.Create(); for(int i=0;i<100_000;i++)_w.Create<Position,Velocity>(new Position(i,i),new Velocity(1,1)); _q=_w.Query(new QueryDescription().WithAll<Position,Velocity>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0; foreach(var c in _q){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VY;} return s;}
}

// ============================================================================
// S2: MMOZone — 30K entities across 8 archetypes
// ============================================================================
public sealed class ArchMMOZone : IGameScenario
{
    readonly World _w; Query _q;
    public ArchMMOZone() { _w=World.Create(); int per=30000/8,idx=0;
        for(int a=0;a<8&&idx<30000;a++) for(int i=0;i<per&&idx<30000;i++,idx++){ var e=_w.Create<Position,Health,Team>(new Position(idx,idx),new Health(100+idx%50),new Team(idx%4));
            switch(a){ case 0:_w.Add<Armor>(e,new Armor(10));break; case 1:_w.Add<Mana>(e,new Mana(50));break; case 2:_w.Add<Damage>(e,new Damage(5));_w.Add<Shield>(e,new Shield(20));break;
            case 3:_w.Add<Cooldown>(e,new Cooldown(0));_w.Add<Stamina>(e,new Stamina(30));break; case 4:_w.Add<Velocity>(e,new Velocity(0,0));break;
            case 5:_w.Add<Velocity>(e,new Velocity(0,0));_w.Add<Armor>(e,new Armor(15));break; case 6:_w.Add<Mana>(e,new Mana(30));_w.Add<Damage>(e,new Damage(8));break;
            case 7:_w.Add<Shield>(e,new Shield(10));_w.Add<Stamina>(e,new Stamina(20));_w.Add<XP>(e,new XP(0));break; } }
        _q=_w.Query(new QueryDescription().WithAll<Position,Health>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0; foreach(var c in _q){var sp=c.GetSpan<Position>();var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sh[i].Value;} return s;}
}

// ============================================================================
// S3: WaveSpawner — spawn/despawn churn
// ============================================================================
public sealed class ArchWaveSpawner : IGameScenario
{
    readonly World _w; readonly Queue<Entity> _a=new(); Query _q; readonly Random _r=new(42);
    public ArchWaveSpawner() { _w=World.Create(); for(int i=0;i<30000;i++){var e=_w.Create<Position,Velocity,Lifetime>(new Position(i,i),new Velocity(i%5,i%5),new Lifetime(_r.Next(60,180)));_a.Enqueue(e);} _q=_w.Query(new QueryDescription().WithAll<Lifetime>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0; int sc=_r.Next(500,1500);
        for(int i=0;i<sc;i++){var e=_w.Create<Position,Velocity,Lifetime>(new Position(_r.Next(1000),_r.Next(1000)),new Velocity(_r.Next(5),_r.Next(5)),new Lifetime(_r.Next(30,120)));_a.Enqueue(e);s+=e.Id;}
        foreach(var c in _q){var sl=c.GetSpan<Lifetime>();for(int i=0;i<c.Count;i++)s+=sl[i].Ticks;}
        int d=Math.Min(_a.Count-20000,2000); for(int i=0;i<d&&_a.Count>0;i++){var e=_a.Dequeue();_w.Destroy(e);s+=e.Id;} return s;}
}

// ============================================================================
// S4: BuffSystem — component add/remove stress
// ============================================================================
public sealed class ArchBuffSystem : IGameScenario
{
    readonly World _w; readonly Entity[] _es; Query _bq,_fq; readonly Random _r=new(42);
    public ArchBuffSystem() { _w=World.Create(); _es=new Entity[30000]; for(int i=0;i<_es.Length;i++){_es[i]=_w.Create<Position,Health>(new Position(i,i),new Health(100)); if(_r.Next(2)==0)_w.Add<Burning>(_es[i],new Burning(0)); if(_r.Next(3)==0)_w.Add<Frozen>(_es[i],new Frozen(0));} _bq=_w.Query(new QueryDescription().WithAll<Burning,Health>()); _fq=_w.Query(new QueryDescription().WithAll<Frozen,Position>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        for(int i=0;i<1500;i++){var e=_es[_r.Next(_es.Length)]; if(_r.Next(2)==0){if(_w.Has<Burning>(e))_w.Remove<Burning>(e);else _w.Add<Burning>(e,new Burning(0));}else{if(_w.Has<Frozen>(e))_w.Remove<Frozen>(e);else _w.Add<Frozen>(e,new Frozen(0));} s+=e.Id;}
        {foreach(var c in _bq){var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sh[i].Value;}}
        {foreach(var c in _fq){var sp=c.GetSpan<Position>();for(int i=0;i<c.Count;i++)s+=sp[i].X;}} return s;}
}

// ============================================================================
// S5: FullGameLoop — 4-system pipeline
// ============================================================================
public sealed class ArchFullGameLoop : IGameScenario
{
    readonly World _w; Query _mq,_cq,_hq; readonly Random _r=new(42);
    public ArchFullGameLoop() { _w=World.Create(); for(int i=0;i<20000;i++)_w.Create<Position,Velocity,Health,Team,Damage>(new Position(i%500,i/500),new Velocity(_r.Next(3)-1,_r.Next(3)-1),new Health(50+_r.Next(50)),new Team(i%4),new Damage(5+_r.Next(10))); _mq=_w.Query(new QueryDescription().WithAll<Position,Velocity>()); _cq=_w.Query(new QueryDescription().WithAll<Position,Health,Damage,Team>()); _hq=_w.Query(new QueryDescription().WithAll<Health>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        {foreach(var c in _mq){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VX;}}
        {foreach(var c in _cq){var sp=c.GetSpan<Position>();var sh=c.GetSpan<Health>();var sd=c.GetSpan<Damage>();var st=c.GetSpan<Team>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sh[i].Value+sd[i].Value+st[i].Value;}}
        {foreach(var c in _hq){var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sh[i].Value;}} return s;}
}

// ============================================================================
// S6: RPGStats — wide component access (5 comps)
// ============================================================================
public sealed class ArchRPGStats : IGameScenario
{
    readonly World _w; Query _q;
    public ArchRPGStats() { _w=World.Create(); for(int i=0;i<40000;i++)_w.Create<Position,Velocity,Health,Mana,Armor>(new Position(i,i+1),new Velocity(i%5,i%5),new Health(100+i%50),new Mana(50+i%30),new Armor(10+i%20)); _q=_w.Query(new QueryDescription().WithAll<Position,Velocity,Health,Mana,Armor>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0; foreach(var c in _q){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();var sh=c.GetSpan<Health>();var sm=c.GetSpan<Mana>();var sa=c.GetSpan<Armor>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VY+sh[i].Value+sm[i].Value+sa[i].Value;} return s;}
}

// ============================================================================
// S7: ConditionalEffects — filtered With/Without
// ============================================================================
public sealed class ArchConditionalEffects : IGameScenario
{
    readonly World _w; Query _pq,_bq;
    public ArchConditionalEffects() { _w=World.Create(); var r=new Random(42);
        for(int i=0;i<50000;i++){var e=_w.Create<Position,Health>(new Position(i,i),new Health(100)); if(r.Next(100)<40)_w.Add<Poisoned>(e,new Poisoned(0)); if(r.Next(100)<30)_w.Add<Burning>(e,new Burning(0)); if(r.Next(100)<5)_w.Add<ImmuneToPoison>(e,new ImmuneToPoison(0)); if(r.Next(100)<5)_w.Add<FireResist>(e,new FireResist(0));}
        _pq=_w.Query(new QueryDescription().WithAll<Poisoned,Health>().WithNone<ImmuneToPoison>()); _bq=_w.Query(new QueryDescription().WithAll<Burning,Health>().WithNone<FireResist>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        {foreach(var c in _pq){var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sh[i].Value;}}
        {foreach(var c in _bq){var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sh[i].Value;}} return s;}
}

// ============================================================================
// S8: AIStateMachine — rapid archetype switching
// ============================================================================
public sealed class ArchAIStateMachine : IGameScenario
{
    readonly World _w; readonly Entity[] _es; Query _iq,_mq,_aq; readonly Random _r=new(42);
    public ArchAIStateMachine() { _w=World.Create(); _es=new Entity[30000]; for(int i=0;i<_es.Length;i++)_es[i]=_w.Create<Position,StateIdle>(new Position(i,i),new StateIdle(0)); _iq=_w.Query(new QueryDescription().WithAll<StateIdle,Position>()); _mq=_w.Query(new QueryDescription().WithAll<StateMove,Position>()); _aq=_w.Query(new QueryDescription().WithAll<StateAttack,Position>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        for(int i=0;i<_es.Length/5;i++){var e=_es[_r.Next(_es.Length)]; if(_w.Has<StateIdle>(e))_w.Remove<StateIdle>(e); if(_w.Has<StateMove>(e))_w.Remove<StateMove>(e); if(_w.Has<StateAttack>(e))_w.Remove<StateAttack>(e); if(_w.Has<StateDead>(e))_w.Remove<StateDead>(e);
        switch(_r.Next(4)){case 0:_w.Add<StateIdle>(e,new StateIdle(0));break;case 1:_w.Add<StateMove>(e,new StateMove(0));break;case 2:_w.Add<StateAttack>(e,new StateAttack(0));break;case 3:_w.Add<StateDead>(e,new StateDead(0));break;} s+=e.Id;}
        {foreach(var c in _iq){var sp=c.GetSpan<Position>();for(int i=0;i<c.Count;i++)s+=sp[i].X;}}
        {foreach(var c in _mq){var sp=c.GetSpan<Position>();for(int i=0;i<c.Count;i++)s+=sp[i].X;}}
        {foreach(var c in _aq){var sp=c.GetSpan<Position>();for(int i=0;i<c.Count;i++)s+=sp[i].X;}} return s;}
}

// ============================================================================
// S9: TeamAlternation — alternating query access
// ============================================================================
public sealed class ArchTeamAlternation : IGameScenario
{
    readonly World _w; Query _aq,_bq; int _t;
    public ArchTeamAlternation() { _w=World.Create(); for(int i=0;i<60000;i++){var e=_w.Create<Position,Health>(new Position(i,i),new Health(100)); if(i%2==0)_w.Add<TagTeamA>(e,new TagTeamA(0));else _w.Add<TagTeamB>(e,new TagTeamB(0));} _aq=_w.Query(new QueryDescription().WithAll<TagTeamA,Position,Health>()); _bq=_w.Query(new QueryDescription().WithAll<TagTeamB,Position,Health>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){_t++;long s=0; if(_t%2==1){foreach(var c in _aq){var sp=c.GetSpan<Position>();var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sh[i].Value;}}else{foreach(var c in _bq){var sp=c.GetSpan<Position>();var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sh[i].Value;}} return s;}
}

// ============================================================================
// S10: MixedLoad — create + query + destroy combined
// ============================================================================
public sealed class ArchMixedLoad : IGameScenario
{
    readonly World _w; readonly Queue<Entity> _q=new(); Query _hq,_pq; readonly Random _r=new(42); int _n=15000;
    public ArchMixedLoad() { _w=World.Create(); for(int i=0;i<15000;i++)_q.Enqueue(_w.Create<Position,Health,Damage,Team>(new Position(i,i),new Health(100),new Damage(5),new Team(i%4))); _hq=_w.Query(new QueryDescription().WithAll<Health,Team>()); _pq=_w.Query(new QueryDescription().WithAll<Position>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        for(int i=0;i<200;i++){var e=_w.Create<Position,Health,Damage,Team>(new Position(_n,_n),new Health(50+_r.Next(50)),new Damage(3+_r.Next(7)),new Team(_r.Next(4)));_q.Enqueue(e);_n++;s+=e.Id;}
        {foreach(var c in _hq){var sh=c.GetSpan<Health>();var st=c.GetSpan<Team>();for(int i=0;i<c.Count;i++)s+=sh[i].Value+st[i].Value;}}
        {foreach(var c in _pq){var sp=c.GetSpan<Position>();for(int i=0;i<c.Count;i++)s+=sp[i].X;}}
        int dc=Math.Min(200,_q.Count-10000); for(int i=0;i<dc&&_q.Count>0;i++){var e=_q.Dequeue();_w.Destroy(e);s+=e.Id;} return s;}
}

// ============================================================================
// S11: RandomEntityAccess — entity-level get/set stress (no chunk iteration)
// ============================================================================
public sealed class ArchRandomEntityAccess : IGameScenario
{
    readonly World _w; readonly Entity[] _es; Query _q; readonly Random _r=new(42);
    public ArchRandomEntityAccess() { _w=World.Create(); _es=new Entity[30000]; for(int i=0;i<30000;i++)_es[i]=_w.Create<Position,Health>(new Position(i,i),new Health(100+i%50)); _q=_w.Query(new QueryDescription().WithAll<Position,Health>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        for(int i=0;i<2000;i++){var idx=_r.Next(30000);ref var h=ref _w.Get<Health>(_es[idx]);s+=h.Value;h.Value--;ref var p=ref _w.Get<Position>(_es[idx]);s+=p.X+p.Y;p.X++;p.Y++;};
        {foreach(var c in _q){var sp=c.GetSpan<Position>();var sh=c.GetSpan<Health>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sh[i].Value;}} return s;}
}

// ============================================================================
// S14: ParallelBulletHell — 100K entities, parallel chunk query (read-only hash)
// ============================================================================
public sealed class ArchParallelBulletHell : IGameScenario
{
    readonly World _w; Chunk[] _chunks; Action<int> _body; long _sum;
    public ArchParallelBulletHell() { _w=World.Create(); for(int i=0;i<100_000;i++)_w.Create<Position,Velocity>(new Position(i,i),new Velocity(1,1)); var q=_w.Query(new QueryDescription().WithAll<Position,Velocity>()); var l=new List<Chunk>(); foreach(var c in q) l.Add(c); _chunks=l.ToArray(); _body=i=>{var c=_chunks[i];long local=0;var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int j=0;j<c.Count;j++)local+=sp[j].X*17+sp[j].Y*13+sv[j].VX*31+sv[j].VY*7;Interlocked.Add(ref _sum,local);}; }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){_sum=0; Parallel.For(0,_chunks.Length,_body); return _sum;}
}

// ============================================================================
// S13: FragmentedQueries — 30K entities across ~70 archetypes (random 4/8 tags)
// ============================================================================
public sealed class ArchFragmentedQueries : IGameScenario
{
    readonly World _w; Query _qAll,_qMid,_qNarrow;
    public ArchFragmentedQueries() { _w=World.Create(); var r=new Random(42);
        for(int i=0;i<30000;i++){ var pool=new int[]{0,1,2,3,4,5,6,7}; var sz=8; var chosen=new int[4];
            for(int j=0;j<4;j++){ var pick=r.Next(sz); chosen[j]=pool[pick]; pool[pick]=pool[--sz]; }
            var e=_w.Create<Position,Velocity>(new Position(i,i),new Velocity(1,1));
            for(int j=0;j<4;j++){ switch(chosen[j]){ case 0:_w.Add<FragTag0>(e,new(0));break;case 1:_w.Add<FragTag1>(e,new(0));break;case 2:_w.Add<FragTag2>(e,new(0));break;case 3:_w.Add<FragTag3>(e,new(0));break;case 4:_w.Add<FragTag4>(e,new(0));break;case 5:_w.Add<FragTag5>(e,new(0));break;case 6:_w.Add<FragTag6>(e,new(0));break;case 7:_w.Add<FragTag7>(e,new(0));break;} } }
        _qAll=_w.Query(new QueryDescription().WithAll<Position,Velocity>());
        _qMid=_w.Query(new QueryDescription().WithAll<FragTag0,Position,Velocity>());
        _qNarrow=_w.Query(new QueryDescription().WithAll<FragTag0,FragTag1,Position,Velocity>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        {foreach(var c in _qAll){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VY;}}
        {foreach(var c in _qMid){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VY;}}
        {foreach(var c in _qNarrow){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VY;}} return s;}
}

// ============================================================================
// S12: FollowTheLeader — cross-entity lookup during chunk iteration
// ============================================================================
public sealed class ArchFollowTheLeader : IGameScenario
{
    readonly World _w; readonly Entity[] _ld; Query _fq,_lq;
    public ArchFollowTheLeader() { _w=World.Create(); _ld=new Entity[100];
        for(int i=0;i<100;i++)_ld[i]=_w.Create<Position,Velocity>(new Position(i*10,i*10),new Velocity(1,1));
        for(int i=0;i<20000;i++)_w.Create<Position,LeaderIdx>(new Position(i,i+1),new LeaderIdx(i%100));
        _fq=_w.Query(new QueryDescription().WithAll<Position,LeaderIdx>()); _lq=_w.Query(new QueryDescription().WithAll<Position,Velocity>()); }
    public void Warmup(int n){for(int i=0;i<n;i++)RunIteration();} public void Dispose()=>_w.Dispose();
    [MethodImpl(MethodImplOptions.NoInlining)] public long RunIteration(){long s=0;
        {foreach(var c in _fq){var sp=c.GetSpan<Position>();var sl=c.GetSpan<LeaderIdx>();for(int i=0;i<c.Count;i++){ref var lp=ref _w.Get<Position>(_ld[sl[i].Value]);s+=sp[i].X+lp.X+sp[i].Y+lp.Y;}}}
        {foreach(var c in _lq){var sp=c.GetSpan<Position>();var sv=c.GetSpan<Velocity>();for(int i=0;i<c.Count;i++)s+=sp[i].X+sv[i].VX;}} return s;}
}
