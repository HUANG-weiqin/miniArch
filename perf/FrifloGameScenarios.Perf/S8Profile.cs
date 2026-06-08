using System.Diagnostics;
using Friflo.Engine.ECS;
using MiniArch;
using FrifloEntity = Friflo.Engine.ECS.Entity;
using MiniEntity = MiniArch.Entity;

namespace FrifloGameScenarios;

/// <summary>
/// Segmented timing profiler for S8-AIStateMachine.
/// Compares MiniArch vs Friflo phase-by-phase.
/// </summary>
public static class S8Profile
{
    public static void Run()
    {
        Console.WriteLine("=== S8-AIStateMachine Segmented Profile ===");
        ProfileMiniArch();
        Console.WriteLine();
        ProfileFriflo();
    }

    static void ProfileMiniArch()
    {
        const int WarmupIters = 500;
        const int MeasureIters = 5000;

        var w = new World(128, 30000);
        var es = new MiniEntity[30000];
        for (int i = 0; i < es.Length; i++)
            es[i] = w.Create(new Position(i, i), new StateIdle(0));

        var iq = w.Query(new QueryDescription().With<StateIdle>().With<Position>());
        var mq = w.Query(new QueryDescription().With<StateMove>().With<Position>());
        var aq = w.Query(new QueryDescription().With<StateAttack>().With<Position>());

        var r = new Random(42);

        for (int iter = 0; iter < WarmupIters; iter++)
            RunMiniIteration(es, w, iq, mq, aq, r);

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        long totalHasTicks = 0, totalRemoveTicks = 0, totalAddTicks = 0, totalQueryTicks = 0;
        long totalTicks = 0;
        var sw = new Stopwatch();

        for (int iter = 0; iter < MeasureIters; iter++)
        {
            sw.Restart();
            long hasTicks = 0, removeTicks = 0, addTicks = 0;
            int opCount = es.Length / 5;

            for (int i = 0; i < opCount; i++)
            {
                var e = es[r.Next(es.Length)];

                var t1 = Stopwatch.GetTimestamp();
                bool hIdle = w.Has<StateIdle>(e);
                bool hMove = w.Has<StateMove>(e);
                bool hAtk = w.Has<StateAttack>(e);
                bool hDead = w.Has<StateDead>(e);
                hasTicks += Stopwatch.GetTimestamp() - t1;

                var t2 = Stopwatch.GetTimestamp();
                if (hIdle) w.Remove<StateIdle>(e);
                if (hMove) w.Remove<StateMove>(e);
                if (hAtk) w.Remove<StateAttack>(e);
                if (hDead) w.Remove<StateDead>(e);
                removeTicks += Stopwatch.GetTimestamp() - t2;

                var t3 = Stopwatch.GetTimestamp();
                switch (r.Next(4))
                {
                    case 0: w.Add(e, new StateIdle(0)); break;
                    case 1: w.Add(e, new StateMove(0)); break;
                    case 2: w.Add(e, new StateAttack(0)); break;
                    case 3: w.Add(e, new StateDead(0)); break;
                }
                addTicks += Stopwatch.GetTimestamp() - t3;
            }

            var t4 = Stopwatch.GetTimestamp();
            long s = 0;
            foreach (var c in iq.GetChunks()){var sp=c.GetSpan<Position>();for(int i=0;i<sp.Length;i++)s+=sp[i].X;}
            foreach (var c in mq.GetChunks()){var sp=c.GetSpan<Position>();for(int i=0;i<sp.Length;i++)s+=sp[i].X;}
            foreach (var c in aq.GetChunks()){var sp=c.GetSpan<Position>();for(int i=0;i<sp.Length;i++)s+=sp[i].X;}
            long queryTicks = Stopwatch.GetTimestamp() - t4;

            totalTicks += sw.ElapsedTicks;
            totalHasTicks += hasTicks;
            totalRemoveTicks += removeTicks;
            totalAddTicks += addTicks;
            totalQueryTicks += queryTicks;
        }

        double freq = Stopwatch.Frequency;
        double iterUs = totalTicks / freq * 1e6 / MeasureIters;
        double hasUs = totalHasTicks / freq * 1e6 / MeasureIters;
        double removeUs = totalRemoveTicks / freq * 1e6 / MeasureIters;
        double addUs = totalAddTicks / freq * 1e6 / MeasureIters;
        double queryUs = totalQueryTicks / freq * 1e6 / MeasureIters;

        Console.WriteLine("  [MiniArch]");
        Console.WriteLine($"    Iteration    : {iterUs,8:F2} us  ({MeasureIters / (totalTicks / freq),8:F1} ops/s)");
        Console.WriteLine($"    Has<T>       : {hasUs,8:F2} us  ({hasUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    Remove<T>    : {removeUs,8:F2} us  ({removeUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    Add<T>       : {addUs,8:F2} us  ({addUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    Query        : {queryUs,8:F2} us  ({queryUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    Structural   : {hasUs + removeUs + addUs,8:F2} us  ({(hasUs + removeUs + addUs) / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    ~{(es.Length / 5 * 2)} Remove+Add ops/iter");
    }

    static void ProfileFriflo()
    {
        const int WarmupIters = 500;
        const int MeasureIters = 5000;

        var store = new Friflo.Engine.ECS.EntityStore();
        var es = new FrifloEntity[30000];
        for (int i = 0; i < es.Length; i++)
            es[i] = store.CreateEntity(new Position(i, i), new StateIdle(0));

        var iq = store.Query<StateIdle, Position>();
        var mq = store.Query<StateMove, Position>();
        var aq = store.Query<StateAttack, Position>();

        var r = new Random(42);

        for (int iter = 0; iter < WarmupIters; iter++)
            RunFrifloIteration(es, iq, mq, aq, r);

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        long totalHasTicks = 0, totalRemoveTicks = 0, totalAddTicks = 0, totalQueryTicks = 0;
        long totalTicks = 0;

        for (int iter = 0; iter < MeasureIters; iter++)
        {
            var sw = Stopwatch.StartNew();
            long hasTicks = 0, removeTicks = 0, addTicks = 0;
            int opCount = es.Length / 5;

            for (int i = 0; i < opCount; i++)
            {
                var e = es[r.Next(es.Length)];

                var t1 = Stopwatch.GetTimestamp();
                bool hIdle = e.HasComponent<StateIdle>();
                bool hMove = e.HasComponent<StateMove>();
                bool hAtk = e.HasComponent<StateAttack>();
                bool hDead = e.HasComponent<StateDead>();
                hasTicks += Stopwatch.GetTimestamp() - t1;

                var t2 = Stopwatch.GetTimestamp();
                if (hIdle) e.RemoveComponent<StateIdle>();
                if (hMove) e.RemoveComponent<StateMove>();
                if (hAtk) e.RemoveComponent<StateAttack>();
                if (hDead) e.RemoveComponent<StateDead>();
                removeTicks += Stopwatch.GetTimestamp() - t2;

                var t3 = Stopwatch.GetTimestamp();
                switch (r.Next(4))
                {
                    case 0: e.AddComponent(new StateIdle(0)); break;
                    case 1: e.AddComponent(new StateMove(0)); break;
                    case 2: e.AddComponent(new StateAttack(0)); break;
                    case 3: e.AddComponent(new StateDead(0)); break;
                }
                addTicks += Stopwatch.GetTimestamp() - t3;
            }

            var t4 = Stopwatch.GetTimestamp();
            long s = 0;
            foreach (var (_, p, _) in iq.Chunks) { var sp = p.Span; for (int i = 0; i < sp.Length; i++) s += sp[i].X; }
            foreach (var (_, p, _) in mq.Chunks) { var sp = p.Span; for (int i = 0; i < sp.Length; i++) s += sp[i].X; }
            foreach (var (_, p, _) in aq.Chunks) { var sp = p.Span; for (int i = 0; i < sp.Length; i++) s += sp[i].X; }
            long queryTicks = Stopwatch.GetTimestamp() - t4;

            totalTicks += sw.ElapsedTicks;
            totalHasTicks += hasTicks;
            totalRemoveTicks += removeTicks;
            totalAddTicks += addTicks;
            totalQueryTicks += queryTicks;
        }

        double freq = Stopwatch.Frequency;
        double iterUs = totalTicks / freq * 1e6 / MeasureIters;
        double hasUs = totalHasTicks / freq * 1e6 / MeasureIters;
        double removeUs = totalRemoveTicks / freq * 1e6 / MeasureIters;
        double addUs = totalAddTicks / freq * 1e6 / MeasureIters;
        double queryUs = totalQueryTicks / freq * 1e6 / MeasureIters;

        Console.WriteLine("  [Friflo]");
        Console.WriteLine($"    Iteration    : {iterUs,8:F2} us  ({MeasureIters / (totalTicks / freq),8:F1} ops/s)");
        Console.WriteLine($"    HasComponent : {hasUs,8:F2} us  ({hasUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    RemoveComp   : {removeUs,8:F2} us  ({removeUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    AddComponent : {addUs,8:F2} us  ({addUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    Query        : {queryUs,8:F2} us  ({queryUs / iterUs * 100,5:F1}%)");
        Console.WriteLine($"    Structural   : {hasUs + removeUs + addUs,8:F2} us  ({(hasUs + removeUs + addUs) / iterUs * 100,5:F1}%)");
    }

    static long RunMiniIteration(MiniEntity[] es, World w, MiniArch.Query iq, MiniArch.Query mq, MiniArch.Query aq, Random r)
    {
        for (int i = 0; i < es.Length / 5; i++)
        {
            var e = es[r.Next(es.Length)];
            if (w.Has<StateIdle>(e)) w.Remove<StateIdle>(e);
            if (w.Has<StateMove>(e)) w.Remove<StateMove>(e);
            if (w.Has<StateAttack>(e)) w.Remove<StateAttack>(e);
            if (w.Has<StateDead>(e)) w.Remove<StateDead>(e);
            switch (r.Next(4))
            {
                case 0: w.Add(e, new StateIdle(0)); break;
                case 1: w.Add(e, new StateMove(0)); break;
                case 2: w.Add(e, new StateAttack(0)); break;
                case 3: w.Add(e, new StateDead(0)); break;
            }
        }
        long s = 0;
        foreach (var c in iq.GetChunks()){var sp=c.GetSpan<Position>();for(int i=0;i<sp.Length;i++)s+=sp[i].X;}
        foreach (var c in mq.GetChunks()){var sp=c.GetSpan<Position>();for(int i=0;i<sp.Length;i++)s+=sp[i].X;}
        foreach (var c in aq.GetChunks()){var sp=c.GetSpan<Position>();for(int i=0;i<sp.Length;i++)s+=sp[i].X;}
        return s;
    }

    static long RunFrifloIteration(FrifloEntity[] es,
        Friflo.Engine.ECS.ArchetypeQuery<StateIdle, Position> iq,
        Friflo.Engine.ECS.ArchetypeQuery<StateMove, Position> mq,
        Friflo.Engine.ECS.ArchetypeQuery<StateAttack, Position> aq, Random r)
    {
        for (int i = 0; i < es.Length / 5; i++)
        {
            var e = es[r.Next(es.Length)];
            if (e.HasComponent<StateIdle>()) e.RemoveComponent<StateIdle>();
            if (e.HasComponent<StateMove>()) e.RemoveComponent<StateMove>();
            if (e.HasComponent<StateAttack>()) e.RemoveComponent<StateAttack>();
            if (e.HasComponent<StateDead>()) e.RemoveComponent<StateDead>();
            switch (r.Next(4))
            {
                case 0: e.AddComponent(new StateIdle(0)); break;
                case 1: e.AddComponent(new StateMove(0)); break;
                case 2: e.AddComponent(new StateAttack(0)); break;
                case 3: e.AddComponent(new StateDead(0)); break;
            }
        }
        long s = 0;
        foreach (var (_, p, _) in iq.Chunks) { var sp = p.Span; for (int i = 0; i < sp.Length; i++) s += sp[i].X; }
        foreach (var (_, p, _) in mq.Chunks) { var sp = p.Span; for (int i = 0; i < sp.Length; i++) s += sp[i].X; }
        foreach (var (_, p, _) in aq.Chunks) { var sp = p.Span; for (int i = 0; i < sp.Length; i++) s += sp[i].X; }
        return s;
    }
}
