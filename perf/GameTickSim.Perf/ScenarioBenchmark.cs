using System.Diagnostics;
using System.Runtime.CompilerServices;
using DefaultEcs;
using Friflo.Engine.ECS;
using Position = MiniArchBenchmarks.GameTick.Position;
using MiniArch.Core;
using MiniArchBenchmarks.GameTick;

namespace GameTickSim;

using MiniWorld = MiniArch.World;

public static class ScenarioBenchmark
{
    public static void RunAll(string? onlyScenario = null)
    {
        var scenarios = new (string name, Action run)[]
        {
            ("A-PureIteration", RunPureIteration),
            ("B-WideSingleComponent", RunWideSingleComponent),
            ("F-MultiArchetypeIteration", RunMultiArchetypeIteration),
            ("G1-FragBaseline", RunFragBaseline),
            ("G2-FragAftermath", RunFragAftermath),
            ("H-MultiComponentJoin", RunMultiComponentJoin),
            ("I-SparseQuery", RunSparseQuery),
            ("C-StructuralAddRemove", RunStructuralAddRemove),
            ("J-EntityCreationBurst", RunEntityCreationBurst),
            ("D-MassCreateDestroy", RunMassCreateDestroy),
            ("E-MixedFullTick", RunMixedFullTick),
            ("K-BulletHell", RunBulletHell),
            ("L-BulletHellBuffs", RunBulletHellBuffs),
            ("M-BulletHellBuffsMultiArch", RunBulletHellBuffsMultiArch),
        };

        Console.WriteLine("=== Isolated Scenario Benchmark ===");
        Console.WriteLine($"Duration per scenario: {GameTickData.DurationSeconds}s");
        Console.WriteLine();

        if (!string.IsNullOrWhiteSpace(onlyScenario))
        {
            for (var i = 0; i < scenarios.Length; i++)
            {
                if (!string.Equals(scenarios[i].name, onlyScenario, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Console.WriteLine($"{'=',-5} {scenarios[i].name} {'=',-5}");
                scenarios[i].run();
                return;
            }

            throw new ArgumentException($"Unknown scenario '{onlyScenario}'.", nameof(onlyScenario));
        }

        foreach (var (name, run) in scenarios)
        {
            Console.WriteLine($"{'=',-5} {name} {'=',-5}");
            run();
            Console.WriteLine();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        PrintSummary();
    }

    private static readonly List<ScenarioResult> _results = [];

    record ScenarioResult(string Scenario, string Engine, double OpsPerSec, double AvgMs, double HeapDeltaKB, bool MemoryStable, int Gen0, int Gen1, int Gen2);

    static void RunPureIteration()
    {
        const int entityCount = 50_000;
        const int ticksToRun = 2000;

        // MiniArch
        using (var w = CreateMiniIterationWorld(entityCount))
        {
            var posType = Component<Position>.ComponentType;
            var velType = Component<Velocity>.ComponentType;
            var desc = new MiniArch.QueryDescription().With<Position>().With<Velocity>();

            int Tick()
            {
                var checksum = 0;
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var pos = chunk.GetComponentSpan<Position>(posType);
                    var vel = chunk.GetComponentSpan<Velocity>(velType);
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = CreateDefaultIterationWorld(entityCount);
            var set = w.GetEntities().With<Position>().With<Velocity>().AsSet();

            int Tick()
            {
                var checksum = 0;
                var entities = set.GetEntities();
                for (var i = 0; i < entities.Length; i++)
                {
                    ref var p = ref entities[i].Get<Position>();
                    ref var v = ref entities[i].Get<Velocity>();
                    p.X += v.X; p.Y += v.Y; p.Z += v.Z;
                    checksum += (int)(p.X + p.Y + p.Z);
                }
                return checksum;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = CreateArchIterationWorld(entityCount);
            var desc = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();

            int Tick()
            {
                var checksum = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    var pos = chunk.GetSpan<Position>();
                    var vel = chunk.GetSpan<Velocity>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = CreateFrifloIterationWorld(entityCount);
            var query = store.Query<FrifloPosition, FrifloVelocity>();

            int Tick()
            {
                var checksum = 0;
                foreach (var (posChunk, velChunk, _) in query.Chunks)
                {
                    var pos = posChunk.Span;
                    var vel = velChunk.Span;
                    for (var row = 0; row < pos.Length; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunWideSingleComponent()
    {
        const int entityCount = 50_000;
        const int ticksToRun = 2000;

        // MiniArch
        using (var w = CreateMiniHealthWorld(entityCount))
        {
            var healthType = Component<Health>.ComponentType;
            var desc = new MiniArch.QueryDescription().With<Health>();

            int Tick()
            {
                var checksum = 0;
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var health = chunk.GetComponentSpan<Health>(healthType);
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        health[row].Current -= 1;
                        if (health[row].Current < 0) health[row].Current = health[row].Max;
                        checksum += (int)health[row].Current;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = CreateDefaultHealthWorld(entityCount);
            var set = w.GetEntities().With<Health>().AsSet();

            int Tick()
            {
                var checksum = 0;
                var entities = set.GetEntities();
                for (var i = 0; i < entities.Length; i++)
                {
                    ref var h = ref entities[i].Get<Health>();
                    h.Current -= 1;
                    if (h.Current < 0) h.Current = h.Max;
                    checksum += (int)h.Current;
                }
                return checksum;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = CreateArchHealthWorld(entityCount);
            var desc = new Arch.Core.QueryDescription().WithAll<Health>();

            int Tick()
            {
                var checksum = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    var health = chunk.GetSpan<Health>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        health[row].Current -= 1;
                        if (health[row].Current < 0) health[row].Current = health[row].Max;
                        checksum += (int)health[row].Current;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = CreateFrifloHealthWorld(entityCount);
            var query = store.Query<FrifloHealth>();

            int Tick()
            {
                var checksum = 0;
                foreach (var (components, _) in query.Chunks)
                {
                    var health = components.Span;
                    for (var row = 0; row < health.Length; row++)
                    {
                        health[row].Current -= 1;
                        if (health[row].Current < 0) health[row].Current = health[row].Max;
                        checksum += (int)health[row].Current;
                    }
                }

                return checksum;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunStructuralAddRemove()
    {
        const int entityCount = 50_000;
        const int opsPerTick = 9_000;
        const int ticksToRun = 1000;
        const int seed = 42;

        // MiniArch
        using (var w = CreateMiniStructuralWorld(entityCount))
        {
            var pool = BuildMiniEntityPool(w, entityCount);
            var desc = new MiniArch.QueryDescription().With<Debuff>();
            var scratch = new MiniArch.Entity[opsPerTick * 2];
            var rng = new Random(seed);
            var debuffIdx = 0;

            int Tick()
            {
                // Remove all Debuff entities
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                var removeCount = 0;
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    for (var row = 0; row < chunk.Count; row++)
                        scratch[removeCount++] = chunk.GetEntity(row);
                }
                for (var i = 0; i < removeCount; i++)
                    w.Remove<Debuff>(scratch[i]);

                // Add Debuff to opsPerTick random entities
                for (var i = 0; i < opsPerTick; i++)
                {
                    var idx = (debuffIdx + i) % pool.Length;
                    w.Add(pool[idx], new Debuff { Timer = 1.0f });
                }
                debuffIdx = (debuffIdx + opsPerTick) % pool.Length;

                return removeCount + opsPerTick;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = CreateDefaultStructuralWorld(entityCount);
            var pool = BuildDefaultEntityPool(w, entityCount);
            var set = w.GetEntities().With<Debuff>().AsSet();
            var scratch = new DefaultEcs.Entity[opsPerTick * 2];
            var debuffIdx = 0;

            int Tick()
            {
                var entities = set.GetEntities();
                var count = entities.Length;
                for (var i = 0; i < count; i++)
                    scratch[i] = entities[i];
                for (var i = 0; i < count; i++)
                    scratch[i].Remove<Debuff>();

                for (var i = 0; i < opsPerTick; i++)
                {
                    var idx = (debuffIdx + i) % pool.Length;
                    pool[idx].Set(new Debuff { Timer = 1.0f });
                }
                debuffIdx = (debuffIdx + opsPerTick) % pool.Length;

                return count + opsPerTick;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = CreateArchStructuralWorld(entityCount);
            var pool = BuildArchEntityPool(w, entityCount);
            var desc = new Arch.Core.QueryDescription().WithAll<Debuff>();
            var scratch = new Arch.Core.Entity[opsPerTick * 2];
            var debuffIdx = 0;

            int Tick()
            {
                var total = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    for (var row = 0; row < chunk.Count; row++)
                        scratch[total++] = chunk.Entity(row);
                }
                for (var i = 0; i < total; i++)
                    w.Remove<Debuff>(scratch[i]);

                for (var i = 0; i < opsPerTick; i++)
                {
                    var idx = (debuffIdx + i) % pool.Length;
                    w.Add(pool[idx], new Debuff { Timer = 1.0f });
                }
                debuffIdx = (debuffIdx + opsPerTick) % pool.Length;

                return total + opsPerTick;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = CreateFrifloStructuralWorld(entityCount);
            var pool = BuildFrifloEntityPool(store);
            var debuffQuery = store.Query<FrifloDebuff>();
            var scratch = new Friflo.Engine.ECS.Entity[opsPerTick * 2];
            var debuffIdx = 0;

            int Tick()
            {
                var total = 0;
                foreach (var (compChunk, entitiesChunk) in debuffQuery.Chunks)
                {
                    for (var row = 0; row < compChunk.Span.Length; row++)
                        scratch[total++] = store.GetEntityById(entitiesChunk[row]);
                }
                for (var i = 0; i < total; i++)
                    scratch[i].RemoveComponent<FrifloDebuff>();

                for (var i = 0; i < opsPerTick; i++)
                {
                    var idx = (debuffIdx + i) % pool.Length;
                    pool[idx].AddComponent(new FrifloDebuff { Timer = 1.0f });
                }
                debuffIdx = (debuffIdx + opsPerTick) % pool.Length;

                return total + opsPerTick;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunMassCreateDestroy()
    {
        const int entityCount = 50_000;
        const int opsPerTick = 4_500;
        const int ticksToRun = 1000;
        const int seed = 42;

        // MiniArch
        using (var w = new MiniWorld())
        {
            for (var i = 0; i < entityCount; i++)
                w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });

            var rng = new Random(seed);
            var pool = new MiniArch.Entity[entityCount + opsPerTick * 2];
            var allDesc = new MiniArch.QueryDescription().With<Position>();
            var idx = 0;
            var q = w.Query(allDesc).Advanced;
            var chunks = q.GetChunkSpan();
            foreach (var chunk in chunks)
                for (var row = 0; row < chunk.Count; row++)
                    pool[idx++] = chunk.GetEntity(row);
            var count = idx;

            int Tick()
            {
                // Destroy opsPerTick random
                var toDestroy = Math.Min(opsPerTick, count);
                for (var i = 0; i < toDestroy; i++)
                {
                    var r = rng.Next(count);
                    var entity = pool[r];
                    count--;
                    pool[r] = pool[count];
                    w.Destroy(entity);
                }
                // Create opsPerTick new
                if (pool.Length < count + opsPerTick)
                    Array.Resize(ref pool, count + opsPerTick + 4096);
                for (var i = 0; i < opsPerTick; i++)
                {
                    var e = w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });
                    pool[count++] = e;
                }
                return opsPerTick * 2;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            var entities = new DefaultEcs.Entity[entityCount];
            for (var i = 0; i < entityCount; i++)
            {
                var e = w.CreateEntity();
                e.Set(new Position { X = i });
                e.Set(new Health { Current = 100, Max = 100 });
                entities[i] = e;
            }
            var rng = new Random(seed);
            var pool = new DefaultEcs.Entity[entityCount + opsPerTick * 2];
            Array.Copy(entities, pool, entityCount);
            var count = entityCount;

            int Tick()
            {
                var toDestroy = Math.Min(opsPerTick, count);
                for (var i = 0; i < toDestroy; i++)
                {
                    var r = rng.Next(count);
                    var entity = pool[r];
                    count--;
                    pool[r] = pool[count];
                    entity.Dispose();
                }
                if (pool.Length < count + opsPerTick)
                    Array.Resize(ref pool, count + opsPerTick + 4096);
                for (var i = 0; i < opsPerTick; i++)
                {
                    var e = w.CreateEntity();
                    e.Set(new Position { X = i });
                    e.Set(new Health { Current = 100, Max = 100 });
                    pool[count++] = e;
                }
                return opsPerTick * 2;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            for (var i = 0; i < entityCount; i++)
                w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });

            var rng = new Random(seed);
            var pool = new Arch.Core.Entity[entityCount + opsPerTick * 2];
            var allDesc = new Arch.Core.QueryDescription().WithAll<Position>();
            var idx = 0;
            foreach (var chunk in w.Query(in allDesc))
                for (var row = 0; row < chunk.Count; row++)
                    pool[idx++] = chunk.Entity(row);
            var count = idx;

            int Tick()
            {
                var toDestroy = Math.Min(opsPerTick, count);
                for (var i = 0; i < toDestroy; i++)
                {
                    var r = rng.Next(count);
                    var entity = pool[r];
                    count--;
                    pool[r] = pool[count];
                    w.Destroy(entity);
                }
                if (pool.Length < count + opsPerTick)
                    Array.Resize(ref pool, count + opsPerTick + 4096);
                for (var i = 0; i < opsPerTick; i++)
                {
                    var e = w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });
                    pool[count++] = e;
                }
                return opsPerTick * 2;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = new EntityStore();
            for (var i = 0; i < entityCount; i++)
                store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 });

            var rng = new Random(seed);
            var pool = BuildFrifloEntityPool(store);
            var count = pool.Length;
            var allQuery = store.Query<FrifloPosition>();

            int Tick()
            {
                var toDestroy = Math.Min(opsPerTick, count);
                for (var i = 0; i < toDestroy; i++)
                {
                    var r = rng.Next(count);
                    var entity = pool[r];
                    count--;
                    pool[r] = pool[count];
                    entity.DeleteEntity();
                }
                if (pool.Length < count + opsPerTick)
                    Array.Resize(ref pool, count + opsPerTick + 4096);
                for (var i = 0; i < opsPerTick; i++)
                {
                    var e = store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 });
                    pool[count++] = e;
                }
                return opsPerTick * 2;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunMixedFullTick()
    {
        const int ticksToRun = 1000;

        // MiniArch
        using (var w = MiniGameTickWorldFactory.CreateWorld())
        {
            MiniGameTickRunner.Initialize(w);
            _results.Add(Measure("MiniArch", () => MiniGameTickRunner.ExecuteTick(w), ticksToRun));
        }

        // DefaultEcs
        {
            using var w = DefaultGameTickWorldFactory.CreateWorld();
            DefaultGameTickRunner.Initialize(w);
            _results.Add(Measure("DefaultEcs", () => DefaultGameTickRunner.ExecuteTick(w), ticksToRun));
        }

        // Arch
        {
            using var w = ArchGameTickWorldFactory.CreateWorld();
            ArchGameTickRunner.Initialize(w);
            _results.Add(Measure("Arch", () => ArchGameTickRunner.ExecuteTick(w), ticksToRun));
        }

        // Friflo
        {
            var store = FrifloGameTickWorldFactory.CreateWorld();
            FrifloGameTickRunner.Initialize(store);
            _results.Add(Measure("Friflo", () => FrifloGameTickRunner.ExecuteTick(store), ticksToRun));
        }
    }

    static void RunBulletHell()
    {
        const int durationSeconds = 20;
        const int enemyBulletsPerTick = 500;
        const int playerBulletsPerTick = 200;
        const int particlesPerTick = 100;
        const float screenBound = 1000f;
        const int enrageInterval = 100;
        const float playerX = 0f, playerZ = 0f;
        const float bossX = 500f, bossZ = 500f;
        const float hitRadiusSqPlayer = 100f;
        const float hitRadiusSqBoss = 900f;
        const float bulletDirX = 0.7071f, bulletDirZ = 0.7071f;

        // MiniArch
        {
            using var w = new MiniWorld();


            var player = w.Create(new PlayerTag(), new Position(), new Velocity(), new Health { Current = 10000, Max = 10000 }, new Speed(0));
            var boss = w.Create(new BossTag(), new Position { X = bossX, Z = bossZ }, new Velocity(), new Health { Current = 100000, Max = 100000 }, new AiState());

            var moveDesc = new MiniArch.QueryDescription().With<Position>().With<Velocity>();
            var ebDesc = new MiniArch.QueryDescription().With<Position>().With<Damage>();
            var pbDesc = new MiniArch.QueryDescription().With<Position>().With<ProjectileTag>();
            var ptDesc = new MiniArch.QueryDescription().With<Position>().With<Lifetime>();
            var moveQuery = w.Query(moveDesc).Advanced;
            var ebQuery = w.Query(ebDesc).Advanced;
            var pbQuery = w.Query(pbDesc).Advanced;
            var ptQuery = w.Query(ptDesc).Advanced;
            var scratch = new MiniArch.Entity[8192];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll (iterate)
                t0 = Stopwatch.GetTimestamp();
                {
                    foreach (var row in moveQuery.EachSpan<Position, Velocity>())
                    {
                        row.Get0().X += row.Get1().X;
                        row.Get0().Y += row.Get1().Y;
                        row.Get0().Z += row.Get1().Z;
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create (spawn enemy + player bullets + particles)
                t0 = Stopwatch.GetTimestamp();
                // 2. Spawn enemy bullets from boss
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    w.Create(
                        new Position { X = bossX, Z = bossZ },
                        new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new Damage { Base = 1 + (i & 3) });
                }
                // 3. Spawn player bullets toward boss
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    w.Create(
                        new ProjectileTag(),
                        new Position(),
                        new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                // 7. Spawn particles
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    w.Create(
                        new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new Lifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy (scan + apply)
                // 4. Collide + cleanup enemy bullets
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanPositionEachSpan(ebQuery, scratch, playerX, playerZ, hitRadiusSqPlayer, screenBound);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                // 5. Collide + cleanup player bullets
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanPositionEachSpan(pbQuery, scratch, bossX, bossZ, hitRadiusSqBoss, screenBound);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                // 6. Process particles
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanLifetimeEachSpan(ptQuery, scratch);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !w.Has<Debuff>(boss))
                    w.Add(boss, new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("MiniArch", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs;
            var freq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [MiniArch phase breakdown] Iterate: {iterateNs / freq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / freq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / freq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / freq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%)");
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            var player = w.CreateEntity();
            player.Set(new PlayerTag());
            player.Set(new Position());
            player.Set(new Velocity());
            player.Set(new Health { Current = 10000, Max = 10000 });
            player.Set(new Speed(0));

            var boss = w.CreateEntity();
            boss.Set(new BossTag());
            boss.Set(new Position { X = bossX, Z = bossZ });
            boss.Set(new Velocity());
            boss.Set(new Health { Current = 100000, Max = 100000 });
            boss.Set(new AiState());

            var moveSet = w.GetEntities().With<Position>().With<Velocity>().AsSet();
            var ebSet = w.GetEntities().With<Position>().With<Damage>().AsSet();
            var pbSet = w.GetEntities().With<Position>().With<ProjectileTag>().AsSet();
            var ptSet = w.GetEntities().With<Position>().With<Lifetime>().AsSet();
            var scratch = new DefaultEcs.Entity[8192];
            var rng = new Random(42);
            var tickCount = 0;

            int Tick()
            {
                tickCount++;
                // 1. MoveAll
                {
                    var entities = moveSet.GetEntities();
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        ref var v = ref entities[i].Get<Velocity>();
                        p.X += v.X; p.Y += v.Y; p.Z += v.Z;
                    }
                }
                // 2. Spawn enemy bullets
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    var e = w.CreateEntity();
                    e.Set(new Position { X = bossX, Z = bossZ });
                    e.Set(new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed });
                    e.Set(new Damage { Base = 1 + (i & 3) });
                }
                // 3. Spawn player bullets
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    var e = w.CreateEntity();
                    e.Set(new ProjectileTag());
                    e.Set(new Position());
                    e.Set(new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                // 7. Spawn particles
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var e = w.CreateEntity();
                    e.Set(new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 });
                    e.Set(new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 });
                    e.Set(new Lifetime { Value = 20f });
                }
                // 4. Collide + cleanup enemy bullets
                {
                    var entities = ebSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        var dx = p.X - playerX;
                        var dz = p.Z - playerZ;
                        if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                            Math.Abs(p.X) > screenBound || Math.Abs(p.Z) > screenBound)
                        {
                            if (d < scratch.Length) scratch[d++] = entities[i];
                        }
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }
                // 5. Collide + cleanup player bullets
                {
                    var entities = pbSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        var dx = p.X - bossX;
                        var dz = p.Z - bossZ;
                        if (dx * dx + dz * dz < hitRadiusSqBoss ||
                            Math.Abs(p.X) > screenBound || Math.Abs(p.Z) > screenBound)
                        {
                            if (d < scratch.Length) scratch[d++] = entities[i];
                        }
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }
                // 6. Process particles
                {
                    var entities = ptSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var lt = ref entities[i].Get<Lifetime>();
                        lt.Value -= 1f;
                        if (lt.Value <= 0 && d < scratch.Length)
                            scratch[d++] = entities[i];
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }
                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !boss.Has<Debuff>())
                    boss.Set(new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("DefaultEcs", Tick, durationSeconds));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            var player = w.Create(new PlayerTag(), new Position(), new Velocity(), new Health { Current = 10000, Max = 10000 }, new Speed(0));
            var boss = w.Create(new BossTag(), new Position { X = bossX, Z = bossZ }, new Velocity(), new Health { Current = 100000, Max = 100000 }, new AiState());

            var moveDesc = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();
            var ebDesc = new Arch.Core.QueryDescription().WithAll<Position, Damage>();
            var pbDesc = new Arch.Core.QueryDescription().WithAll<Position, ProjectileTag>();
            var ptDesc = new Arch.Core.QueryDescription().WithAll<Position, Lifetime>();
            var scratch = new Arch.Core.Entity[8192];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll (iterate)
                t0 = Stopwatch.GetTimestamp();
                {
                    foreach (var chunk in w.Query(in moveDesc))
                    {
                        var pos = chunk.GetSpan<Position>();
                        var vel = chunk.GetSpan<Velocity>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            pos[row].X += vel[row].X;
                            pos[row].Y += vel[row].Y;
                            pos[row].Z += vel[row].Z;
                        }
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create (spawn enemy + player bullets + particles)
                t0 = Stopwatch.GetTimestamp();
                // 2. Spawn enemy bullets
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    w.Create(
                        new Position { X = bossX, Z = bossZ },
                        new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new Damage { Base = 1 + (i & 3) });
                }
                // 3. Spawn player bullets
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    w.Create(
                        new ProjectileTag(),
                        new Position(),
                        new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                // 7. Spawn particles
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    w.Create(
                        new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new Lifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy (scan + apply)
                // 4. Collide + cleanup enemy bullets
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in ebDesc))
                    {
                        var pos = chunk.GetSpan<Position>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            var dx = pos[row].X - playerX;
                            var dz = pos[row].Z - playerZ;
                            if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = chunk.Entity(row);
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                // 5. Collide + cleanup player bullets
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in pbDesc))
                    {
                        var pos = chunk.GetSpan<Position>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            var dx = pos[row].X - bossX;
                            var dz = pos[row].Z - bossZ;
                            if (dx * dx + dz * dz < hitRadiusSqBoss ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = chunk.Entity(row);
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                // 6. Process particles
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in ptDesc))
                    {
                        var lt = chunk.GetSpan<Lifetime>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            lt[row].Value -= 1f;
                            if (lt[row].Value <= 0 && d < scratch.Length)
                                scratch[d++] = chunk.Entity(row);
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !w.Has<Debuff>(boss))
                    w.Add(boss, new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("Arch", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs;
            var archFreq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [Arch phase breakdown] Iterate: {iterateNs / archFreq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / archFreq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / archFreq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / archFreq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%)");
        }

        // Friflo
        {
            var store = new EntityStore();
            var player = store.CreateEntity(new FrifloPlayerTag(), new FrifloPosition(), new FrifloVelocity(), new FrifloHealth { Current = 10000, Max = 10000 }, new FrifloSpeed(0));
            var boss = store.CreateEntity(new FrifloBossTag(), new FrifloPosition { X = bossX, Z = bossZ }, new FrifloVelocity(), new FrifloHealth { Current = 100000, Max = 100000 }, new FrifloAiState());

            var moveQuery = store.Query<FrifloPosition, FrifloVelocity>();
            var ebQuery = store.Query<FrifloPosition, FrifloDamage>();
            var pbQuery = store.Query<FrifloPosition, FrifloProjectileTag>();
            var ptQuery = store.Query<FrifloPosition, FrifloLifetime>();
            var scratch = new int[8192];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll
                t0 = Stopwatch.GetTimestamp();
                foreach (var (posChunk, velChunk, _) in moveQuery.Chunks)
                {
                    var pos = posChunk.Span;
                    var vel = velChunk.Span;
                    for (var row = 0; row < pos.Length; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create
                t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    store.CreateEntity(
                        new FrifloPosition { X = bossX, Z = bossZ },
                        new FrifloVelocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new FrifloDamage { Base = 1 + (i & 3) });
                }
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    store.CreateEntity(
                        new FrifloProjectileTag(),
                        new FrifloPosition(),
                        new FrifloVelocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    store.CreateEntity(
                        new FrifloPosition { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new FrifloVelocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new FrifloLifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy (scan + apply)
                // 4. Collide + cleanup enemy bullets
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, _, entitiesChunk) in ebQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (int row = 0; row < pos.Length; row++)
                        {
                            var dx = pos[row].X - playerX;
                            var dz = pos[row].Z - playerZ;
                            if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = entitiesChunk[row];
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                // 5. Collide + cleanup player bullets
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, _, entitiesChunk) in pbQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length; row++)
                        {
                            var dx = pos[row].X - bossX;
                            var dz = pos[row].Z - bossZ;
                            if (dx * dx + dz * dz < hitRadiusSqBoss ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = entitiesChunk[row];
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                // 6. Process particles
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, ltChunk, entitiesChunk) in ptQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        var lt = ltChunk.Span;
                        for (var row = 0; row < pos.Length; row++)
                        {
                            lt[row].Value -= 1f;
                            if (lt[row].Value <= 0 && d < scratch.Length)
                                scratch[d++] = entitiesChunk[row];
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;

                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !boss.HasComponent<FrifloDebuff>())
                    boss.AddComponent(new FrifloDebuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("Friflo", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs;
            var freq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [Friflo phase breakdown] Iterate: {iterateNs / freq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / freq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / freq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / freq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%)");
        }
    }

    static ScenarioResult Measure(string engine, Func<int> tickFunc, int ticksToRun)
    {
        // Warmup
        for (var i = 0; i < 20; i++)
            tickFunc();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var baselineHeap = GC.GetTotalMemory(true);
        var gen0Base = GC.CollectionCount(0);
        var gen1Base = GC.CollectionCount(1);
        var gen2Base = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ticksToRun; i++)
            tickFunc();
        sw.Stop();

        var totalMs = sw.Elapsed.TotalMilliseconds;
        var avgMs = totalMs / ticksToRun;
        var opsPerSec = ticksToRun / sw.Elapsed.TotalSeconds;

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var finalHeap = GC.GetTotalMemory(true);
        var heapDeltaKB = (finalHeap - baselineHeap) / 1024.0;
        var memoryStable = heapDeltaKB < 1024;

        var gen0 = GC.CollectionCount(0) - gen0Base;
        var gen1 = GC.CollectionCount(1) - gen1Base;
        var gen2 = GC.CollectionCount(2) - gen2Base;

        var result = new ScenarioResult("", engine, opsPerSec, avgMs, heapDeltaKB, memoryStable, gen0, gen1, gen2);
        Console.WriteLine($"  {engine,12}: {opsPerSec,10:F1} ops/s | {avgMs,8:F3}ms/op | heap {heapDeltaKB,8:F1}KB {(memoryStable ? "OK" : "WARN")} | GC {gen0}/{gen1}/{gen2}");
        return result;
    }

    static ScenarioResult MeasureTimed(string engine, Func<int> tickFunc, int durationSeconds)
    {
        // Warmup - 2 seconds
        var warmupSw = Stopwatch.StartNew();
        while (warmupSw.Elapsed.TotalSeconds < 2)
            tickFunc();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var baselineHeap = GC.GetTotalMemory(true);
        var gen0Base = GC.CollectionCount(0);
        var gen1Base = GC.CollectionCount(1);
        var gen2Base = GC.CollectionCount(2);

        var totalTicks = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < durationSeconds)
        {
            tickFunc();
            totalTicks++;
        }
        sw.Stop();

        var totalMs = sw.Elapsed.TotalMilliseconds;
        var avgMs = totalMs / totalTicks;
        var opsPerSec = totalTicks / sw.Elapsed.TotalSeconds;

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var finalHeap = GC.GetTotalMemory(true);
        var heapDeltaKB = (finalHeap - baselineHeap) / 1024.0;
        var memoryStable = heapDeltaKB < 1024;

        var gen0 = GC.CollectionCount(0) - gen0Base;
        var gen1 = GC.CollectionCount(1) - gen1Base;
        var gen2 = GC.CollectionCount(2) - gen2Base;

        var result = new ScenarioResult("", engine, opsPerSec, avgMs, heapDeltaKB, memoryStable, gen0, gen1, gen2);
        Console.WriteLine($"  {engine,12}: {opsPerSec,10:F1} ops/s | {avgMs,8:F3}ms/op | heap {heapDeltaKB,8:F1}KB {(memoryStable ? "OK" : "WARN")} | GC {gen0}/{gen1}/{gen2} | {totalTicks} ticks in {durationSeconds}s");
        return result;
    }

    // Keep span scans out of the large BulletHell Tick method. Inlining these loops
    // causes poor JIT codegen from register pressure around the row ref struct.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ScanPositionEachSpan(MiniArch.Core.Query query, MiniArch.Entity[] scratch, float targetX, float targetZ, float hitRadiusSq, float screenBound)
    {
        var d = 0;
        foreach (var row in query.EachSpan<Position>())
        {
            ref var pos = ref row.Get0();
            var dx = pos.X - targetX;
            var dz = pos.Z - targetZ;
            if (dx * dx + dz * dz < hitRadiusSq ||
                Math.Abs(pos.X) > screenBound || Math.Abs(pos.Z) > screenBound)
            {
                if (d < scratch.Length) scratch[d++] = row.Entity;
            }
        }

        return d;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ScanLifetimeEachSpan(MiniArch.Core.Query query, MiniArch.Entity[] scratch)
    {
        var d = 0;
        foreach (var row in query.EachSpan<Lifetime>())
        {
            ref var lt = ref row.Get0();
            lt.Value -= 1f;
            if (lt.Value <= 0 && d < scratch.Length)
            {
                scratch[d++] = row.Entity;
            }
        }

        return d;
    }

    static void PrintSummary()
    {
        Console.WriteLine("=== Isolated Scenario Benchmark Summary ===");
        Console.WriteLine();
        Console.WriteLine($"{"Scenario",28} | {"Engine",12} | {"ops/s",10} | {"ms/op",8} | {"HeapKB",8} | {"GC 0/1/2",10}");
        Console.WriteLine(new string('-', 95));

        // Assign scenario names
        var scenarioNames = new[] {
            "A-PureIteration", "B-WideSingleComponent",
            "F-MultiArchetypeIteration", "G1-FragBaseline", "G2-FragAftermath",
            "H-MultiComponentJoin", "I-SparseQuery",
            "C-StructuralAddRemove", "J-EntityCreationBurst",
            "D-MassCreateDestroy", "E-MixedFullTick", "K-BulletHell", "L-BulletHellBuffs"
        };
        var engines = new[] { "MiniArch", "DefaultEcs", "Arch", "Friflo" };
        const int engineCount = 4;
        for (var s = 0; s < scenarioNames.Length; s++)
        {
            for (var e = 0; e < engineCount; e++)
            {
                var r = _results[s * engineCount + e];
                var name = s == 0 ? scenarioNames[s] : "";
                Console.WriteLine($"{(e == 0 ? scenarioNames[s] : ""),28} | {r.Engine,12} | {r.OpsPerSec,10:F1} | {r.AvgMs,8:F3} | {r.HeapDeltaKB,8:F1} | {r.Gen0}/{r.Gen1}/{r.Gen2}");
            }
            if (s < scenarioNames.Length - 1)
            {
                // Speed comparison vs MiniArch
                var mini = _results[s * engineCount + 0];
                var fri = _results[s * engineCount + 3];
                var def = _results[s * engineCount + 1];
                var arch = _results[s * engineCount + 2];
                var best = Math.Max(Math.Max(mini.OpsPerSec, def.OpsPerSec), Math.Max(arch.OpsPerSec, fri.OpsPerSec));
                var winner = best == mini.OpsPerSec ? "MiniArch" : best == fri.OpsPerSec ? "Friflo" : best == arch.OpsPerSec ? "Arch" : "DefaultEcs";
                Console.WriteLine($"{new string(' ', 28)} | Winner: {winner} | Mini {mini.OpsPerSec / arch.OpsPerSec:F2}x Arch, {mini.OpsPerSec / fri.OpsPerSec:F2}x Friflo");
            }
        }

        // Per-scenario winners summary
        Console.WriteLine();
        Console.WriteLine("=== Per-Scenario Winners ===");
        for (var s = 0; s < scenarioNames.Length; s++)
        {
            var mini = _results[s * engineCount + 0];
            var def = _results[s * engineCount + 1];
            var arch = _results[s * engineCount + 2];
            var fri = _results[s * engineCount + 3];
            var best = Math.Max(Math.Max(mini.OpsPerSec, def.OpsPerSec), Math.Max(arch.OpsPerSec, fri.OpsPerSec));
            var winner = best == mini.OpsPerSec ? "MiniArch" : best == fri.OpsPerSec ? "Friflo" : best == arch.OpsPerSec ? "Arch" : "DefaultEcs";
            Console.WriteLine($"  {scenarioNames[s],-28} => {winner,-12} ({best:F1} ops/s)");
        }

        // Fragmentation impact: G1 vs G2
        Console.WriteLine();
        Console.WriteLine("=== Fragmentation Impact (G1 baseline vs G2 after fragmentation) ===");
        var g1Idx = Array.IndexOf(scenarioNames, "G1-FragBaseline");
        var g2Idx = Array.IndexOf(scenarioNames, "G2-FragAftermath");
        for (var e = 0; e < engineCount; e++)
        {
            var baseline = _results[g1Idx * engineCount + e];
            var fragmented = _results[g2Idx * engineCount + e];
            var degradation = (1.0 - fragmented.OpsPerSec / baseline.OpsPerSec) * 100;
            Console.WriteLine($"  {baseline.Engine,12}: {baseline.OpsPerSec,8:F0} -> {fragmented.OpsPerSec,8:F0} ops/s  ({degradation:+0.0;-0.0}% degradation)");
        }

        // Cross-archetype vs single-archetype: B vs F
        Console.WriteLine();
        Console.WriteLine("=== Multi-Archetype Overhead (B single-arch vs F 6-arch) ===");
        var bIdx = Array.IndexOf(scenarioNames, "B-WideSingleComponent");
        var fIdx = Array.IndexOf(scenarioNames, "F-MultiArchetypeIteration");
        for (var e = 0; e < engineCount; e++)
        {
            var single = _results[bIdx * engineCount + e];
            var multi = _results[fIdx * engineCount + e];
            var overhead = (1.0 - multi.OpsPerSec / single.OpsPerSec) * 100;
            Console.WriteLine($"  {single.Engine,12}: {single.OpsPerSec,8:F0} -> {multi.OpsPerSec,8:F0} ops/s  ({overhead:+0.0;-0.0}% overhead)");
        }
    }

    // World factories for isolated scenarios

    static MiniWorld CreateMiniIterationWorld(int count)
    {
        var w = new MiniWorld();
        for (var i = 0; i < count; i++)
        {
            w.Create(
                new Position { X = i, Y = i + 1, Z = i + 2 },
                new Velocity { X = 1, Y = 0.5f, Z = -0.5f },
                new Health { Current = 100, Max = 100 },
                new Mana { Current = 50, Max = 50 },
                new Scale { X = 1 });
        }
        return w;
    }

    static DefaultEcs.World CreateDefaultIterationWorld(int count)
    {
        var w = new DefaultEcs.World();
        for (var i = 0; i < count; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Position { X = i, Y = i + 1, Z = i + 2 });
            e.Set(new Velocity { X = 1, Y = 0.5f, Z = -0.5f });
            e.Set(new Health { Current = 100, Max = 100 });
            e.Set(new Mana { Current = 50, Max = 50 });
            e.Set(new Scale { X = 1 });
        }
        return w;
    }

    static Arch.Core.World CreateArchIterationWorld(int count)
    {
        var w = Arch.Core.World.Create();
        for (var i = 0; i < count; i++)
        {
            w.Create(
                new Position { X = i, Y = i + 1, Z = i + 2 },
                new Velocity { X = 1, Y = 0.5f, Z = -0.5f },
                new Health { Current = 100, Max = 100 },
                new Mana { Current = 50, Max = 50 },
                new Scale { X = 1 });
        }
        return w;
    }

    static MiniWorld CreateMiniHealthWorld(int count)
    {
        var w = new MiniWorld();
        for (var i = 0; i < count; i++)
        {
            w.Create(
                new Health { Current = 100, Max = 100 },
                new Position { X = i },
                new Velocity { X = 1 });
        }
        return w;
    }

    static DefaultEcs.World CreateDefaultHealthWorld(int count)
    {
        var w = new DefaultEcs.World();
        for (var i = 0; i < count; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Health { Current = 100, Max = 100 });
            e.Set(new Position { X = i });
            e.Set(new Velocity { X = 1 });
        }
        return w;
    }

    static Arch.Core.World CreateArchHealthWorld(int count)
    {
        var w = Arch.Core.World.Create();
        for (var i = 0; i < count; i++)
        {
            w.Create(
                new Health { Current = 100, Max = 100 },
                new Position { X = i },
                new Velocity { X = 1 });
        }
        return w;
    }

    static MiniWorld CreateMiniStructuralWorld(int count)
    {
        var w = new MiniWorld();
        for (var i = 0; i < count; i++)
        {
            w.Create(
                new Position { X = i },
                new Health { Current = 100, Max = 100 },
                new Velocity { X = 1 });
        }
        return w;
    }

    static DefaultEcs.World CreateDefaultStructuralWorld(int count)
    {
        var w = new DefaultEcs.World();
        for (var i = 0; i < count; i++)
        {
            var e = w.CreateEntity();
            e.Set(new Position { X = i });
            e.Set(new Health { Current = 100, Max = 100 });
            e.Set(new Velocity { X = 1 });
        }
        return w;
    }

    static Arch.Core.World CreateArchStructuralWorld(int count)
    {
        var w = Arch.Core.World.Create();
        for (var i = 0; i < count; i++)
        {
            w.Create(
                new Position { X = i },
                new Health { Current = 100, Max = 100 },
                new Velocity { X = 1 });
        }
        return w;
    }

    static MiniArch.Entity[] BuildMiniEntityPool(MiniWorld w, int count)
    {
        var pool = new MiniArch.Entity[count];
        var desc = new MiniArch.QueryDescription().With<Position>();
        var q = w.Query(desc).Advanced;
        var chunks = q.GetChunkSpan();
        var idx = 0;
        foreach (var chunk in chunks)
            for (var row = 0; row < chunk.Count; row++)
                pool[idx++] = chunk.GetEntity(row);
        return pool;
    }

    static DefaultEcs.Entity[] BuildDefaultEntityPool(DefaultEcs.World w, int count)
    {
        var span = w.GetEntities().With<Position>().AsSet().GetEntities();
        var arr = new DefaultEcs.Entity[span.Length];
        span.CopyTo(arr);
        return arr;
    }

    static Arch.Core.Entity[] BuildArchEntityPool(Arch.Core.World w, int count)
    {
        var pool = new Arch.Core.Entity[count];
        var desc = new Arch.Core.QueryDescription().WithAll<Position>();
        var idx = 0;
        foreach (var chunk in w.Query(in desc))
            for (var row = 0; row < chunk.Count; row++)
                pool[idx++] = chunk.Entity(row);
        return pool;
    }

    // Helper: Build Friflo entity pool from all entities with Position
    static Friflo.Engine.ECS.Entity[] BuildFrifloEntityPool(EntityStore store)
    {
        var q = store.Query<FrifloPosition>();
        var total = q.Count;
        var pool = new Friflo.Engine.ECS.Entity[total];
        var idx = 0;
        foreach (var (compChunk, entitiesChunk) in q.Chunks)
        {
            for (var row = 0; row < compChunk.Span.Length; row++)
                pool[idx++] = store.GetEntityById(entitiesChunk[row]);
        }
        return pool;
    }

    // Friflo world factory helpers for scenarios
    static EntityStore CreateFrifloIterationWorld(int count)
    {
        var store = new EntityStore();
        for (var i = 0; i < count; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i, Y = i + 1, Z = i + 2 },
                new FrifloVelocity { X = 1, Y = 0.5f, Z = -0.5f },
                new FrifloHealth { Current = 100, Max = 100 },
                new FrifloMana { Current = 50, Max = 50 },
                new FrifloScale { X = 1 });
        }
        return store;
    }

    static EntityStore CreateFrifloHealthWorld(int count)
    {
        var store = new EntityStore();
        for (var i = 0; i < count; i++)
        {
            store.CreateEntity(
                new FrifloHealth { Current = 100, Max = 100 },
                new FrifloPosition { X = i },
                new FrifloVelocity { X = 1 });
        }
        return store;
    }

    static EntityStore CreateFrifloStructuralWorld(int count)
    {
        var store = new EntityStore();
        for (var i = 0; i < count; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i },
                new FrifloHealth { Current = 100, Max = 100 },
                new FrifloVelocity { X = 1 });
        }
        return store;
    }

    static void CreateFrifloMultiArchetypeWorld(EntityStore store, int perType)
    {
        for (var i = 0; i < perType; i++)
            store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 });
        for (var i = 0; i < perType; i++)
            store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 }, new FrifloVelocity { X = 1 });
        for (var i = 0; i < perType; i++)
            store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 }, new FrifloVelocity { X = 1 }, new FrifloMana { Current = 50, Max = 50 });
        for (var i = 0; i < perType; i++)
            store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 }, new FrifloVelocity { X = 1 }, new FrifloMana { Current = 50, Max = 50 }, new FrifloStamina { Current = 100, Max = 100 });
        for (var i = 0; i < perType; i++)
            store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 }, new FrifloScale { X = 1 });
        for (var i = 0; i < perType; i++)
            store.CreateEntity(new FrifloPosition { X = i }, new FrifloHealth { Current = 100, Max = 100 }, new FrifloScale { X = 1 }, new FrifloColor(1, 1, 1, 1));
    }

    static EntityStore CreateFrifloMultiComponentWorld(int count)
    {
        var store = new EntityStore();
        for (var i = 0; i < count; i++)
        {
            store.CreateEntity(
                new FrifloPosition { X = i },
                new FrifloVelocity { X = 1 },
                new FrifloHealth { Current = 100, Max = 100 },
                new FrifloMana { Current = 50, Max = 50 },
                new FrifloStamina { Current = 100, Max = 100 });
        }
        return store;
    }

    // === Round 2 Scenarios ===

    static void RunMultiArchetypeIteration()
    {
        const int perType = 8_334;
        const int ticksToRun = 2000;

        // MiniArch: 6 archetypes, all with Health + varying extra components
        using (var w = new MiniWorld())
        {
            CreateMultiArchetypeMini(w, perType);
            var healthType = Component<Health>.ComponentType;
            var desc = new MiniArch.QueryDescription().With<Health>();

            int Tick()
            {
                var checksum = 0;
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var health = chunk.GetComponentSpan<Health>(healthType);
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        health[row].Current -= 1;
                        if (health[row].Current < 0) health[row].Current = health[row].Max;
                        checksum += (int)health[row].Current;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            CreateMultiArchetypeDefault(w, perType);
            var set = w.GetEntities().With<Health>().AsSet();

            int Tick()
            {
                var checksum = 0;
                var entities = set.GetEntities();
                for (var i = 0; i < entities.Length; i++)
                {
                    ref var h = ref entities[i].Get<Health>();
                    h.Current -= 1;
                    if (h.Current < 0) h.Current = h.Max;
                    checksum += (int)h.Current;
                }
                return checksum;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            CreateMultiArchetypeArch(w, perType);
            var desc = new Arch.Core.QueryDescription().WithAll<Health>();

            int Tick()
            {
                var checksum = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    var health = chunk.GetSpan<Health>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        health[row].Current -= 1;
                        if (health[row].Current < 0) health[row].Current = health[row].Max;
                        checksum += (int)health[row].Current;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = new EntityStore();
            CreateFrifloMultiArchetypeWorld(store, perType);
            var query = store.Query<FrifloHealth>();

            int Tick()
            {
                var checksum = 0;
                foreach (var (components, _) in query.Chunks)
                {
                    var health = components.Span;
                    for (var row = 0; row < health.Length; row++)
                    {
                        health[row].Current -= 1;
                        if (health[row].Current < 0) health[row].Current = health[row].Max;
                        checksum += (int)health[row].Current;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunFragBaseline()
    {
        const int entityCount = 50_000;
        const int ticksToRun = 2000;

        using (var w = CreateMiniIterationWorld(entityCount))
        {
            var posType = Component<Position>.ComponentType;
            var velType = Component<Velocity>.ComponentType;
            var desc = new MiniArch.QueryDescription().With<Position>().With<Velocity>();

            int Tick()
            {
                var checksum = 0;
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var pos = chunk.GetComponentSpan<Position>(posType);
                    var vel = chunk.GetComponentSpan<Velocity>(velType);
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X; pos[row].Y += vel[row].Y; pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        {
            using var w = CreateDefaultIterationWorld(entityCount);
            var set = w.GetEntities().With<Position>().With<Velocity>().AsSet();

            int Tick()
            {
                var checksum = 0;
                var entities = set.GetEntities();
                for (var i = 0; i < entities.Length; i++)
                {
                    ref var p = ref entities[i].Get<Position>();
                    ref var v = ref entities[i].Get<Velocity>();
                    p.X += v.X; p.Y += v.Y; p.Z += v.Z;
                    checksum += (int)(p.X + p.Y + p.Z);
                }
                return checksum;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        {
            using var w = CreateArchIterationWorld(entityCount);
            var desc = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();

            int Tick()
            {
                var checksum = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    var pos = chunk.GetSpan<Position>();
                    var vel = chunk.GetSpan<Velocity>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X; pos[row].Y += vel[row].Y; pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = CreateFrifloIterationWorld(entityCount);
            var query = store.Query<FrifloPosition, FrifloVelocity>();

            int Tick()
            {
                var checksum = 0;
                foreach (var (posChunk, velChunk, _) in query.Chunks)
                {
                    var pos = posChunk.Span;
                    var vel = velChunk.Span;
                    for (var row = 0; row < pos.Length; row++)
                    {
                        pos[row].X += vel[row].X; pos[row].Y += vel[row].Y; pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunFragAftermath()
    {
        const int entityCount = 50_000;
        const int ticksToRun = 2000;

        // MiniArch: create uniform, fragment into 5 archetypes, then measure
        using (var w = CreateMiniIterationWorld(entityCount))
        {
            var pool = BuildMiniEntityPool(w, entityCount);
            for (var i = 0; i < pool.Length; i++)
            {
                switch (i % 5)
                {
                    case 0: w.Add(pool[i], new PlayerTag()); break;
                    case 1: w.Add(pool[i], new EnemyTag()); break;
                    case 2: w.Add(pool[i], new NpcTag()); break;
                    case 3: w.Add(pool[i], new ProjectileTag()); break;
                    case 4: w.Add(pool[i], new DeadTag()); break;
                }
            }

            var posType = Component<Position>.ComponentType;
            var velType = Component<Velocity>.ComponentType;
            var desc = new MiniArch.QueryDescription().With<Position>().With<Velocity>();

            int Tick()
            {
                var checksum = 0;
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var pos = chunk.GetComponentSpan<Position>(posType);
                    var vel = chunk.GetComponentSpan<Velocity>(velType);
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X; pos[row].Y += vel[row].Y; pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = CreateDefaultIterationWorld(entityCount);
            var allEntities = w.GetEntities().With<Position>().AsSet().GetEntities();
            for (var i = 0; i < allEntities.Length; i++)
            {
                switch (i % 5)
                {
                    case 0: allEntities[i].Set(new PlayerTag()); break;
                    case 1: allEntities[i].Set(new EnemyTag()); break;
                    case 2: allEntities[i].Set(new NpcTag()); break;
                    case 3: allEntities[i].Set(new ProjectileTag()); break;
                    case 4: allEntities[i].Set(new DeadTag()); break;
                }
            }
            var set = w.GetEntities().With<Position>().With<Velocity>().AsSet();

            int Tick()
            {
                var checksum = 0;
                var entities = set.GetEntities();
                for (var i = 0; i < entities.Length; i++)
                {
                    ref var p = ref entities[i].Get<Position>();
                    ref var v = ref entities[i].Get<Velocity>();
                    p.X += v.X; p.Y += v.Y; p.Z += v.Z;
                    checksum += (int)(p.X + p.Y + p.Z);
                }
                return checksum;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = CreateArchIterationWorld(entityCount);
            var pool = BuildArchEntityPool(w, entityCount);
            for (var i = 0; i < pool.Length; i++)
            {
                switch (i % 5)
                {
                    case 0: w.Add(pool[i], new PlayerTag()); break;
                    case 1: w.Add(pool[i], new EnemyTag()); break;
                    case 2: w.Add(pool[i], new NpcTag()); break;
                    case 3: w.Add(pool[i], new ProjectileTag()); break;
                    case 4: w.Add(pool[i], new DeadTag()); break;
                }
            }
            var desc = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();

            int Tick()
            {
                var checksum = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    var pos = chunk.GetSpan<Position>();
                    var vel = chunk.GetSpan<Velocity>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X; pos[row].Y += vel[row].Y; pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = CreateFrifloIterationWorld(entityCount);
            var pool = BuildFrifloEntityPool(store);
            for (var i = 0; i < pool.Length; i++)
            {
                switch (i % 5)
                {
                    case 0: pool[i].AddComponent(new FrifloPlayerTag()); break;
                    case 1: pool[i].AddComponent(new FrifloEnemyTag()); break;
                    case 2: pool[i].AddComponent(new FrifloNpcTag()); break;
                    case 3: pool[i].AddComponent(new FrifloProjectileTag()); break;
                    case 4: pool[i].AddComponent(new FrifloDeadTag()); break;
                }
            }
            var query = store.Query<FrifloPosition, FrifloVelocity>();

            int Tick()
            {
                var checksum = 0;
                foreach (var (posChunk, velChunk, _) in query.Chunks)
                {
                    var pos = posChunk.Span;
                    var vel = velChunk.Span;
                    for (var row = 0; row < pos.Length; row++)
                    {
                        pos[row].X += vel[row].X; pos[row].Y += vel[row].Y; pos[row].Z += vel[row].Z;
                        checksum += (int)(pos[row].X + pos[row].Y + pos[row].Z);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunMultiComponentJoin()
    {
        const int entityCount = 50_000;
        const int ticksToRun = 2000;

        // MiniArch: 5-component query
        using (var w = new MiniWorld())
        {
            for (var i = 0; i < entityCount; i++)
                w.Create(
                    new Position { X = i },
                    new Velocity { X = 1 },
                    new Health { Current = 100, Max = 100 },
                    new Mana { Current = 50, Max = 50 },
                    new Stamina { Current = 100, Max = 100 });

            var posType = Component<Position>.ComponentType;
            var velType = Component<Velocity>.ComponentType;
            var healthType = Component<Health>.ComponentType;
            var manaType = Component<Mana>.ComponentType;
            var staminaType = Component<Stamina>.ComponentType;
            var desc = new MiniArch.QueryDescription()
                .With<Position>().With<Velocity>().With<Health>().With<Mana>().With<Stamina>();

            int Tick()
            {
                var checksum = 0;
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var pos = chunk.GetComponentSpan<Position>(posType);
                    var vel = chunk.GetComponentSpan<Velocity>(velType);
                    var health = chunk.GetComponentSpan<Health>(healthType);
                    var mana = chunk.GetComponentSpan<Mana>(manaType);
                    var stamina = chunk.GetComponentSpan<Stamina>(staminaType);
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X;
                        health[row].Current -= 1;
                        mana[row].Current -= 1;
                        stamina[row].Current -= 1;
                        checksum += (int)(pos[row].X + health[row].Current + mana[row].Current + stamina[row].Current);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            for (var i = 0; i < entityCount; i++)
            {
                var e = w.CreateEntity();
                e.Set(new Position { X = i });
                e.Set(new Velocity { X = 1 });
                e.Set(new Health { Current = 100, Max = 100 });
                e.Set(new Mana { Current = 50, Max = 50 });
                e.Set(new Stamina { Current = 100, Max = 100 });
            }
            var set = w.GetEntities().With<Position>().With<Velocity>().With<Health>().With<Mana>().With<Stamina>().AsSet();

            int Tick()
            {
                var checksum = 0;
                var entities = set.GetEntities();
                for (var i = 0; i < entities.Length; i++)
                {
                    ref var p = ref entities[i].Get<Position>();
                    ref var v = ref entities[i].Get<Velocity>();
                    ref var h = ref entities[i].Get<Health>();
                    ref var m = ref entities[i].Get<Mana>();
                    ref var s = ref entities[i].Get<Stamina>();
                    p.X += v.X;
                    h.Current -= 1;
                    m.Current -= 1;
                    s.Current -= 1;
                    checksum += (int)(p.X + h.Current + m.Current + s.Current);
                }
                return checksum;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            for (var i = 0; i < entityCount; i++)
                w.Create(
                    new Position { X = i },
                    new Velocity { X = 1 },
                    new Health { Current = 100, Max = 100 },
                    new Mana { Current = 50, Max = 50 },
                    new Stamina { Current = 100, Max = 100 });
            var desc = new Arch.Core.QueryDescription().WithAll<Position, Velocity, Health, Mana, Stamina>();

            int Tick()
            {
                var checksum = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    var pos = chunk.GetSpan<Position>();
                    var vel = chunk.GetSpan<Velocity>();
                    var health = chunk.GetSpan<Health>();
                    var mana = chunk.GetSpan<Mana>();
                    var stamina = chunk.GetSpan<Stamina>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X;
                        health[row].Current -= 1;
                        mana[row].Current -= 1;
                        stamina[row].Current -= 1;
                        checksum += (int)(pos[row].X + health[row].Current + mana[row].Current + stamina[row].Current);
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = CreateFrifloMultiComponentWorld(entityCount);
            var query = store.Query<FrifloPosition, FrifloVelocity, FrifloHealth, FrifloMana, FrifloStamina>();

            int Tick()
            {
                var checksum = 0;
                query.ForEachEntity((ref FrifloPosition pos, ref FrifloVelocity vel, ref FrifloHealth health, ref FrifloMana mana, ref FrifloStamina stamina, Friflo.Engine.ECS.Entity _) =>
                {
                    pos.X += vel.X;
                    health.Current -= 1;
                    mana.Current -= 1;
                    stamina.Current -= 1;
                    checksum += (int)(pos.X + health.Current + mana.Current + stamina.Current);
                });
                return checksum;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunSparseQuery()
    {
        const int entityCount = 50_000;
        const int sparseCount = 500;
        const int ticksToRun = 2000;

        // MiniArch: 500 of 50K have BuffRemaining
        using (var w = new MiniWorld())
        {
            var entities = new MiniArch.Entity[entityCount];
            for (var i = 0; i < entityCount; i++)
                entities[i] = w.Create(new Position { X = i });
            var rng = new Random(42);
            for (var i = 0; i < sparseCount; i++)
                w.Add(entities[rng.Next(entityCount)], new BuffRemaining { Value = 10.0f });

            var buffType = Component<BuffRemaining>.ComponentType;
            var desc = new MiniArch.QueryDescription().With<BuffRemaining>();

            int Tick()
            {
                var checksum = 0;
                var q = w.Query(desc).Advanced;
                var chunks = q.GetChunkSpan();
                for (var i = 0; i < chunks.Length; i++)
                {
                    var chunk = chunks[i];
                    var buff = chunk.GetComponentSpan<BuffRemaining>(buffType);
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        buff[row].Value -= 0.1f;
                        if (buff[row].Value < 0) buff[row].Value = 10.0f;
                        checksum += (int)buff[row].Value;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            var entities = new DefaultEcs.Entity[entityCount];
            for (var i = 0; i < entityCount; i++)
            {
                var e = w.CreateEntity();
                e.Set(new Position { X = i });
                entities[i] = e;
            }
            var rng = new Random(42);
            for (var i = 0; i < sparseCount; i++)
                entities[rng.Next(entityCount)].Set(new BuffRemaining { Value = 10.0f });
            var set = w.GetEntities().With<BuffRemaining>().AsSet();

            int Tick()
            {
                var checksum = 0;
                var ents = set.GetEntities();
                for (var i = 0; i < ents.Length; i++)
                {
                    ref var b = ref ents[i].Get<BuffRemaining>();
                    b.Value -= 0.1f;
                    if (b.Value < 0) b.Value = 10.0f;
                    checksum += (int)b.Value;
                }
                return checksum;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            var entityArr = new Arch.Core.Entity[entityCount];
            for (var i = 0; i < entityCount; i++)
                entityArr[i] = w.Create(new Position { X = i });
            var rng = new Random(42);
            for (var i = 0; i < sparseCount; i++)
                w.Add(entityArr[rng.Next(entityCount)], new BuffRemaining { Value = 10.0f });
            var desc = new Arch.Core.QueryDescription().WithAll<BuffRemaining>();

            int Tick()
            {
                var checksum = 0;
                foreach (var chunk in w.Query(in desc))
                {
                    var buff = chunk.GetSpan<BuffRemaining>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        buff[row].Value -= 0.1f;
                        if (buff[row].Value < 0) buff[row].Value = 10.0f;
                        checksum += (int)buff[row].Value;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = new EntityStore();
            var entityArr = new Friflo.Engine.ECS.Entity[entityCount];
            for (var i = 0; i < entityCount; i++)
                entityArr[i] = store.CreateEntity(new FrifloPosition { X = i });
            var rng = new Random(42);
            for (var i = 0; i < sparseCount; i++)
                entityArr[rng.Next(entityCount)].AddComponent(new FrifloBuffRemaining { Value = 10.0f });
            var query = store.Query<FrifloBuffRemaining>();

            int Tick()
            {
                var checksum = 0;
                foreach (var (components, _) in query.Chunks)
                {
                    var buff = components.Span;
                    for (var row = 0; row < buff.Length; row++)
                    {
                        buff[row].Value -= 0.1f;
                        if (buff[row].Value < 0) buff[row].Value = 10.0f;
                        checksum += (int)buff[row].Value;
                    }
                }
                return checksum;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    static void RunEntityCreationBurst()
    {
        const int burstSize = 10_000;
        const int ticksToRun = 1000;

        // MiniArch
        using (var w = new MiniWorld())
        {
            var scratch = new MiniArch.Entity[burstSize];

            int Tick()
            {
                for (var i = 0; i < burstSize; i++)
                    scratch[i] = w.Create(new DamageEvent { Amount = i });
                for (var i = 0; i < burstSize; i++)
                    w.Destroy(scratch[i]);
                return burstSize * 2;
            }

            _results.Add(Measure("MiniArch", Tick, ticksToRun));
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            var scratch = new DefaultEcs.Entity[burstSize];

            int Tick()
            {
                for (var i = 0; i < burstSize; i++)
                {
                    var e = w.CreateEntity();
                    e.Set(new DamageEvent { Amount = i });
                    scratch[i] = e;
                }
                for (var i = 0; i < burstSize; i++)
                    scratch[i].Dispose();
                return burstSize * 2;
            }

            _results.Add(Measure("DefaultEcs", Tick, ticksToRun));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            var scratch = new Arch.Core.Entity[burstSize];

            int Tick()
            {
                for (var i = 0; i < burstSize; i++)
                    scratch[i] = w.Create(new DamageEvent { Amount = i });
                for (var i = 0; i < burstSize; i++)
                    w.Destroy(scratch[i]);
                return burstSize * 2;
            }

            _results.Add(Measure("Arch", Tick, ticksToRun));
        }

        // Friflo
        {
            var store = new EntityStore();
            var scratch = new Friflo.Engine.ECS.Entity[burstSize];

            int Tick()
            {
                for (var i = 0; i < burstSize; i++)
                    scratch[i] = store.CreateEntity(new FrifloDamageEvent { Amount = i });
                for (var i = 0; i < burstSize; i++)
                    scratch[i].DeleteEntity();
                return burstSize * 2;
            }

            _results.Add(Measure("Friflo", Tick, ticksToRun));
        }
    }

    // Multi-archetype world helpers (6 archetypes, all with Health)
    static void CreateMultiArchetypeMini(MiniWorld w, int perType)
    {
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Velocity { X = 1 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Velocity { X = 1 }, new Mana { Current = 50, Max = 50 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Velocity { X = 1 }, new Mana { Current = 50, Max = 50 }, new Stamina { Current = 100, Max = 100 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Scale { X = 1 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Scale { X = 1 }, new Color(1, 1, 1, 1));
    }

    static void CreateMultiArchetypeDefault(DefaultEcs.World w, int perType)
    {
        for (var t = 0; t < 6; t++)
        {
            for (var i = 0; i < perType; i++)
            {
                var e = w.CreateEntity();
                e.Set(new Position { X = i });
                e.Set(new Health { Current = 100, Max = 100 });
                if (t >= 1 && t <= 3) e.Set(new Velocity { X = 1 });
                if (t == 2 || t == 3) e.Set(new Mana { Current = 50, Max = 50 });
                if (t == 3) e.Set(new Stamina { Current = 100, Max = 100 });
                if (t >= 4) e.Set(new Scale { X = 1 });
                if (t == 5) e.Set(new Color(1, 1, 1, 1));
            }
        }
    }

    static void CreateMultiArchetypeArch(Arch.Core.World w, int perType)
    {
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Velocity { X = 1 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Velocity { X = 1 }, new Mana { Current = 50, Max = 50 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Velocity { X = 1 }, new Mana { Current = 50, Max = 50 }, new Stamina { Current = 100, Max = 100 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Scale { X = 1 });
        for (var i = 0; i < perType; i++)
            w.Create(new Position { X = i }, new Health { Current = 100, Max = 100 }, new Scale { X = 1 }, new Color(1, 1, 1, 1));
    }

    static void RunBulletHellBuffs()
    {
        const int durationSeconds = 20;
        const int enemyBulletsPerTick = 300;
        const int playerBulletsPerTick = 150;
        const int particlesPerTick = 100;
        const int buffApplyCount = 200;
        const float screenBound = 1000f;
        const float playerX = 0f, playerZ = 0f;
        const float bossX = 500f, bossZ = 500f;
        const float hitRadiusSqPlayer = 100f;
        const float hitRadiusSqBoss = 900f;
        const float bulletDirX = 0.7071f, bulletDirZ = 0.7071f;
        const int enrageInterval = 100;
        const float statusDuration = 3f;

        // MiniArch
        {
            using var w = new MiniWorld();

            var player = w.Create(new PlayerTag(), new Position(), new Velocity(), new Health { Current = 10000, Max = 10000 }, new Speed(0));
            var boss = w.Create(new BossTag(), new Position { X = bossX, Z = bossZ }, new Velocity(), new Health { Current = 100000, Max = 100000 }, new AiState());

            var moveDesc = new MiniArch.QueryDescription().With<Position>().With<Velocity>();
            var ebDesc = new MiniArch.QueryDescription().With<Position>().With<Damage>();
            var pbDesc = new MiniArch.QueryDescription().With<Position>().With<ProjectileTag>();
            var ptDesc = new MiniArch.QueryDescription().With<Position>().With<Lifetime>();
            var buffDesc = new MiniArch.QueryDescription().With<BuffEffect>();
            var ebBuffTargetDesc = new MiniArch.QueryDescription().With<Position>().With<Damage>().Without<BuffEffect>();
            var pbBuffTargetDesc = new MiniArch.QueryDescription().With<Position>().With<ProjectileTag>().Without<BuffEffect>();
            var moveQuery = w.Query(moveDesc).Advanced;
            var ebQuery = w.Query(ebDesc).Advanced;
            var pbQuery = w.Query(pbDesc).Advanced;
            var ptQuery = w.Query(ptDesc).Advanced;
            var buffQuery = w.Query(buffDesc).Advanced;
            var ebBuffTargetQuery = w.Query(ebBuffTargetDesc).Advanced;
            var pbBuffTargetQuery = w.Query(pbBuffTargetDesc).Advanced;
            var scratch = new MiniArch.Entity[8192];
            var buffCandidates = new MiniArch.Entity[buffApplyCount];
            var poisonCandidates = new MiniArch.Entity[buffApplyCount / 2];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0, buffNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll
                t0 = Stopwatch.GetTimestamp();
                foreach (var row in moveQuery.EachSpan<Position, Velocity>())
                {
                    row.Get0().X += row.Get1().X;
                    row.Get0().Y += row.Get1().Y;
                    row.Get0().Z += row.Get1().Z;
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create
                t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    w.Create(
                        new Position { X = bossX, Z = bossZ },
                        new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new Damage { Base = 1 + (i & 3) });
                }
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    w.Create(
                        new ProjectileTag(),
                        new Position(),
                        new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    w.Create(
                        new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new Lifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy (scan + apply)
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanPositionEachSpan(ebQuery, scratch, playerX, playerZ, hitRadiusSqPlayer, screenBound);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanPositionEachSpan(pbQuery, scratch, bossX, bossZ, hitRadiusSqBoss, screenBound);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanLifetimeEachSpan(ptQuery, scratch);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 9. Buff system: collect -> apply (outside query loop)
                t0 = Stopwatch.GetTimestamp();
                {
                    // Collect burning candidates (only entities WITHOUT BuffEffect)
                    var burnCount = 0;
                    foreach (var chunk in ebBuffTargetQuery.GetChunkSpan())
                    {
                        for (var row = 0; row < chunk.Count && burnCount < buffApplyCount; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                                buffCandidates[burnCount++] = chunk.GetEntity(row);
                        }
                    }
                    // Apply burning outside query loop
                    for (var i = 0; i < burnCount; i++)
                        w.Add(buffCandidates[i], new BuffEffect(0, statusDuration));

                    // Collect poison candidates
                    var poisonCount = 0;
                    foreach (var chunk in pbBuffTargetQuery.GetChunkSpan())
                    {
                        for (var row = 0; row < chunk.Count && poisonCount < buffApplyCount / 2; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                                poisonCandidates[poisonCount++] = chunk.GetEntity(row);
                        }
                    }
                    // Apply poison outside query loop
                    for (var i = 0; i < poisonCount; i++)
                        w.Add(poisonCandidates[i], new BuffEffect(1, statusDuration));

                    // Tick down buff timers, remove expired
                    var removeCount = 0;
                    foreach (var chunk in buffQuery.GetChunkSpan())
                    {
                        var buffs = chunk.GetComponentSpan<BuffEffect>(Component<BuffEffect>.ComponentType);
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            buffs[row].Remaining -= 1f;
                            if (buffs[row].Remaining <= 0 && removeCount < scratch.Length)
                                scratch[removeCount++] = chunk.GetEntity(row);
                        }
                    }
                    for (var i = 0; i < removeCount; i++)
                        w.Remove<BuffEffect>(scratch[i]);
                }
                t1 = Stopwatch.GetTimestamp();
                buffNs += t1 - t0;

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !w.Has<Debuff>(boss))
                    w.Add(boss, new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("MiniArch", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs + buffNs;
            var freq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [MiniArch phase breakdown] Iterate: {iterateNs / freq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / freq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / freq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / freq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%) | Buff: {buffNs / freq / 1_000_000:F1}ms ({100.0 * buffNs / totalNs:F1}%)");
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            var player = w.CreateEntity();
            player.Set(new PlayerTag());
            player.Set(new Position());
            player.Set(new Velocity());
            player.Set(new Health { Current = 10000, Max = 10000 });
            player.Set(new Speed(0));

            var boss = w.CreateEntity();
            boss.Set(new BossTag());
            boss.Set(new Position { X = bossX, Z = bossZ });
            boss.Set(new Velocity());
            boss.Set(new Health { Current = 100000, Max = 100000 });
            boss.Set(new AiState());

            var moveSet = w.GetEntities().With<Position>().With<Velocity>().AsSet();
            var ebSet = w.GetEntities().With<Position>().With<Damage>().AsSet();
            var pbSet = w.GetEntities().With<Position>().With<ProjectileTag>().AsSet();
            var ptSet = w.GetEntities().With<Position>().With<Lifetime>().AsSet();
            var buffSet = w.GetEntities().With<BuffEffect>().AsSet();
            var scratch = new DefaultEcs.Entity[8192];
            var rng = new Random(42);
            var tickCount = 0;

            int Tick()
            {
                tickCount++;
                // 1. MoveAll
                {
                    var entities = moveSet.GetEntities();
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        ref var v = ref entities[i].Get<Velocity>();
                        p.X += v.X; p.Y += v.Y; p.Z += v.Z;
                    }
                }
                // 2. Spawn enemy bullets
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    var e = w.CreateEntity();
                    e.Set(new Position { X = bossX, Z = bossZ });
                    e.Set(new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed });
                    e.Set(new Damage { Base = 1 + (i & 3) });
                }
                // 3. Spawn player bullets
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    var e = w.CreateEntity();
                    e.Set(new ProjectileTag());
                    e.Set(new Position());
                    e.Set(new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                // 7. Spawn particles
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var e = w.CreateEntity();
                    e.Set(new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 });
                    e.Set(new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 });
                    e.Set(new Lifetime { Value = 20f });
                }
                // 4. Collide + cleanup enemy bullets
                {
                    var entities = ebSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        var dx = p.X - playerX;
                        var dz = p.Z - playerZ;
                        if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                            Math.Abs(p.X) > screenBound || Math.Abs(p.Z) > screenBound)
                        {
                            if (d < scratch.Length) scratch[d++] = entities[i];
                        }
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }
                // 5. Collide + cleanup player bullets
                {
                    var entities = pbSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        var dx = p.X - bossX;
                        var dz = p.Z - bossZ;
                        if (dx * dx + dz * dz < hitRadiusSqBoss ||
                            Math.Abs(p.X) > screenBound || Math.Abs(p.Z) > screenBound)
                        {
                            if (d < scratch.Length) scratch[d++] = entities[i];
                        }
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }
                // 6. Process particles
                {
                    var entities = ptSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var lt = ref entities[i].Get<Lifetime>();
                        lt.Value -= 1f;
                        if (lt.Value <= 0 && d < scratch.Length)
                            scratch[d++] = entities[i];
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }

                // 9. Buff system
                {
                    // Apply burning to random enemy bullets
                    var ebEntities = ebSet.GetEntities();
                    var applied = 0;
                    for (var i = 0; i < ebEntities.Length && applied < buffApplyCount; i++)
                    {
                        if (rng.NextSingle() < 0.5f && !ebEntities[i].Has<BuffEffect>())
                        {
                            ebEntities[i].Set(new BuffEffect(0, statusDuration)); // type 0 = Burning
                            applied++;
                        }
                    }
                    // Apply poison to random player bullets
                    var pbEntities = pbSet.GetEntities();
                    applied = 0;
                    for (var i = 0; i < pbEntities.Length && applied < buffApplyCount / 2; i++)
                    {
                        if (rng.NextSingle() < 0.5f && !pbEntities[i].Has<BuffEffect>())
                        {
                            pbEntities[i].Set(new BuffEffect(1, statusDuration)); // type 1 = Poisoned
                            applied++;
                        }
                    }

                    // Tick down buffs, remove expired
                    var buffEntities = buffSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < buffEntities.Length; i++)
                    {
                        ref var buff = ref buffEntities[i].Get<BuffEffect>();
                        buff.Remaining -= 1f;
                        if (buff.Remaining <= 0 && d < scratch.Length)
                            scratch[d++] = buffEntities[i];
                    }
                    for (var i = 0; i < d; i++)
                        scratch[i].Remove<BuffEffect>();
                }

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !boss.Has<Debuff>())
                    boss.Set(new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("DefaultEcs", Tick, durationSeconds));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            var player = w.Create(new PlayerTag(), new Position(), new Velocity(), new Health { Current = 10000, Max = 10000 }, new Speed(0));
            var boss = w.Create(new BossTag(), new Position { X = bossX, Z = bossZ }, new Velocity(), new Health { Current = 100000, Max = 100000 }, new AiState());

            var moveDesc = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();
            var ebDesc = new Arch.Core.QueryDescription().WithAll<Position, Damage>();
            var pbDesc = new Arch.Core.QueryDescription().WithAll<Position, ProjectileTag>();
            var ptDesc = new Arch.Core.QueryDescription().WithAll<Position, Lifetime>();
            var buffDesc = new Arch.Core.QueryDescription().WithAll<BuffEffect>();
            var scratch = new Arch.Core.Entity[8192];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0, buffNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll
                t0 = Stopwatch.GetTimestamp();
                foreach (var chunk in w.Query(in moveDesc))
                {
                    var pos = chunk.GetSpan<Position>();
                    var vel = chunk.GetSpan<Velocity>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create
                t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    w.Create(
                        new Position { X = bossX, Z = bossZ },
                        new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new Damage { Base = 1 + (i & 3) });
                }
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    w.Create(
                        new ProjectileTag(),
                        new Position(),
                        new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    w.Create(
                        new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new Lifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in ebDesc))
                    {
                        var pos = chunk.GetSpan<Position>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            var dx = pos[row].X - playerX;
                            var dz = pos[row].Z - playerZ;
                            if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = chunk.Entity(row);
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in pbDesc))
                    {
                        var pos = chunk.GetSpan<Position>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            var dx = pos[row].X - bossX;
                            var dz = pos[row].Z - bossZ;
                            if (dx * dx + dz * dz < hitRadiusSqBoss ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = chunk.Entity(row);
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in ptDesc))
                    {
                        var lt = chunk.GetSpan<Lifetime>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            lt[row].Value -= 1f;
                            if (lt[row].Value <= 0 && d < scratch.Length)
                                scratch[d++] = chunk.Entity(row);
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 9. Buff system
                t0 = Stopwatch.GetTimestamp();
                {
                    // Apply burning to random enemy bullets
                    var applied = 0;
                    foreach (var chunk in w.Query(in ebDesc))
                    {
                        for (var row = 0; row < chunk.Count && applied < buffApplyCount; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = chunk.Entity(row);
                                if (!w.Has<BuffEffect>(e))
                                {
                                    w.Add(e, new BuffEffect(0, statusDuration)); // type 0 = Burning
                                    applied++;
                                }
                            }
                        }
                    }
                    // Apply poison to random player bullets
                    applied = 0;
                    foreach (var chunk in w.Query(in pbDesc))
                    {
                        for (var row = 0; row < chunk.Count && applied < buffApplyCount / 2; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = chunk.Entity(row);
                                if (!w.Has<BuffEffect>(e))
                                {
                                    w.Add(e, new BuffEffect(1, statusDuration)); // type 1 = Poisoned
                                    applied++;
                                }
                            }
                        }
                    }

                    // Tick down buffs, remove expired
                    var removeCount = 0;
                    foreach (var chunk in w.Query(in buffDesc))
                    {
                        var buffs = chunk.GetSpan<BuffEffect>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            buffs[row].Remaining -= 1f;
                            if (buffs[row].Remaining <= 0 && removeCount < scratch.Length)
                                scratch[removeCount++] = chunk.Entity(row);
                        }
                    }
                    for (var i = 0; i < removeCount; i++)
                        w.Remove<BuffEffect>(scratch[i]);
                }
                t1 = Stopwatch.GetTimestamp();
                buffNs += t1 - t0;

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !w.Has<Debuff>(boss))
                    w.Add(boss, new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("Arch", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs + buffNs;
            var archFreq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [Arch phase breakdown] Iterate: {iterateNs / archFreq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / archFreq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / archFreq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / archFreq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%) | Buff: {buffNs / archFreq / 1_000_000:F1}ms ({100.0 * buffNs / totalNs:F1}%)");
        }

        // Friflo
        {
            var store = new EntityStore();
            var player = store.CreateEntity(new FrifloPlayerTag(), new FrifloPosition(), new FrifloVelocity(), new FrifloHealth { Current = 10000, Max = 10000 }, new FrifloSpeed(0));
            var boss = store.CreateEntity(new FrifloBossTag(), new FrifloPosition { X = bossX, Z = bossZ }, new FrifloVelocity(), new FrifloHealth { Current = 100000, Max = 100000 }, new FrifloAiState());

            var moveQuery = store.Query<FrifloPosition, FrifloVelocity>();
            var ebQuery = store.Query<FrifloPosition, FrifloDamage>();
            var pbQuery = store.Query<FrifloPosition, FrifloProjectileTag>();
            var ptQuery = store.Query<FrifloPosition, FrifloLifetime>();
            var buffQuery = store.Query<FrifloBuffEffect>();
            var scratch = new int[8192];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0, buffNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll
                t0 = Stopwatch.GetTimestamp();
                foreach (var (posChunk, velChunk, _) in moveQuery.Chunks)
                {
                    var pos = posChunk.Span;
                    var vel = velChunk.Span;
                    for (var row = 0; row < pos.Length; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create
                t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    store.CreateEntity(
                        new FrifloPosition { X = bossX, Z = bossZ },
                        new FrifloVelocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new FrifloDamage { Base = 1 + (i & 3) });
                }
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    store.CreateEntity(
                        new FrifloProjectileTag(),
                        new FrifloPosition(),
                        new FrifloVelocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    store.CreateEntity(
                        new FrifloPosition { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new FrifloVelocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new FrifloLifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy (scan + apply)
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, _, entitiesChunk) in ebQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length; row++)
                        {
                            var dx = pos[row].X - playerX;
                            var dz = pos[row].Z - playerZ;
                            if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = entitiesChunk[row];
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, _, entitiesChunk) in pbQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length; row++)
                        {
                            var dx = pos[row].X - bossX;
                            var dz = pos[row].Z - bossZ;
                            if (dx * dx + dz * dz < hitRadiusSqBoss ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = entitiesChunk[row];
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, ltChunk, entitiesChunk) in ptQuery.Chunks)
                    {
                        var lt = ltChunk.Span;
                        for (var row = 0; row < lt.Length; row++)
                        {
                            lt[row].Value -= 1f;
                            if (lt[row].Value <= 0 && d < scratch.Length)
                                scratch[d++] = entitiesChunk[row];
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 9. Buff system
                t0 = Stopwatch.GetTimestamp();
                {
                    // Apply burning to random enemy bullets (collect first, apply outside query)
                    var applyCount = 0;
                    var toBuff = new int[buffApplyCount];
                    foreach (var (posChunk, _, entitiesChunk) in ebQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length && applyCount < buffApplyCount; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = store.GetEntityById(entitiesChunk[row]);
                                if (!e.HasComponent<FrifloBuffEffect>())
                                    toBuff[applyCount++] = entitiesChunk[row];
                            }
                        }
                    }
                    for (var bi = 0; bi < applyCount; bi++)
                    {
                        var e = store.GetEntityById(toBuff[bi]);
                        e.AddComponent(new FrifloBuffEffect { Type = 0, Remaining = statusDuration }); // type 0 = Burning
                    }
                    // Apply poison to random player bullets (collect first, apply outside query)
                    var toPoison = new int[buffApplyCount / 2];
                    applyCount = 0;
                    foreach (var (posChunk, _, entitiesChunk) in pbQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length && applyCount < buffApplyCount / 2; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = store.GetEntityById(entitiesChunk[row]);
                                if (!e.HasComponent<FrifloBuffEffect>())
                                    toPoison[applyCount++] = entitiesChunk[row];
                            }
                        }
                    }
                    for (var bi = 0; bi < applyCount; bi++)
                    {
                        var e = store.GetEntityById(toPoison[bi]);
                        e.AddComponent(new FrifloBuffEffect { Type = 1, Remaining = statusDuration }); // type 1 = Poisoned
                    }

                    // Tick down buffs, remove expired
                    var removeCount = 0;
                    foreach (var (buffChunk, entitiesChunk) in buffQuery.Chunks)
                    {
                        var buffs = buffChunk.Span;
                        for (var row = 0; row < buffs.Length; row++)
                        {
                            buffs[row].Remaining -= 1f;
                            if (buffs[row].Remaining <= 0 && removeCount < scratch.Length)
                                scratch[removeCount++] = entitiesChunk[row];
                        }
                    }
                    for (var i = 0; i < removeCount; i++)
                    {
                        var e = store.GetEntityById(scratch[i]);
                        e.RemoveComponent<FrifloBuffEffect>();
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                buffNs += t1 - t0;

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !boss.HasComponent<FrifloDebuff>())
                    boss.AddComponent(new FrifloDebuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("Friflo", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs + buffNs;
            var freq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [Friflo phase breakdown] Iterate: {iterateNs / freq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / freq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / freq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / freq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%) | Buff: {buffNs / freq / 1_000_000:F1}ms ({100.0 * buffNs / totalNs:F1}%)");
        }
    }

    // M-BulletHellBuffsMultiArch: same as L-BulletHellBuffs but with random Faction(0-7)
    // splitting each archetype into 8 sub-archetypes (3 x 8 = 24 archetypes for main queries)
    static void RunBulletHellBuffsMultiArch()
    {
        const int durationSeconds = 20;
        const int enemyBulletsPerTick = 300;
        const int playerBulletsPerTick = 150;
        const int particlesPerTick = 100;
        const int buffApplyCount = 200;
        const float screenBound = 1000f;
        const float playerX = 0f, playerZ = 0f;
        const float bossX = 500f, bossZ = 500f;
        const float hitRadiusSqPlayer = 100f;
        const float hitRadiusSqBoss = 900f;
        const float bulletDirX = 0.7071f, bulletDirZ = 0.7071f;
        const int enrageInterval = 100;
        const float statusDuration = 3f;
        const int factionCount = 8;

        // MiniArch
        {
            using var w = new MiniWorld();

            var player = w.Create(new PlayerTag(), new Position(), new Velocity(), new Health { Current = 10000, Max = 10000 }, new Speed(0));
            var boss = w.Create(new BossTag(), new Position { X = bossX, Z = bossZ }, new Velocity(), new Health { Current = 100000, Max = 100000 }, new AiState());

            var moveDesc = new MiniArch.QueryDescription().With<Position>().With<Velocity>();
            var ebDesc = new MiniArch.QueryDescription().With<Position>().With<Damage>();
            var pbDesc = new MiniArch.QueryDescription().With<Position>().With<ProjectileTag>();
            var ptDesc = new MiniArch.QueryDescription().With<Position>().With<Lifetime>();
            var buffDesc = new MiniArch.QueryDescription().With<BuffEffect>();
            var ebBuffTargetDesc = new MiniArch.QueryDescription().With<Position>().With<Damage>().Without<BuffEffect>();
            var pbBuffTargetDesc = new MiniArch.QueryDescription().With<Position>().With<ProjectileTag>().Without<BuffEffect>();
            var moveQuery = w.Query(moveDesc).Advanced;
            var ebQuery = w.Query(ebDesc).Advanced;
            var pbQuery = w.Query(pbDesc).Advanced;
            var ptQuery = w.Query(ptDesc).Advanced;
            var buffQuery = w.Query(buffDesc).Advanced;
            var ebBuffTargetQuery = w.Query(ebBuffTargetDesc).Advanced;
            var pbBuffTargetQuery = w.Query(pbBuffTargetDesc).Advanced;
            var scratch = new MiniArch.Entity[8192];
            var buffCandidates = new MiniArch.Entity[buffApplyCount];
            var poisonCandidates = new MiniArch.Entity[buffApplyCount / 2];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0, buffNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll
                t0 = Stopwatch.GetTimestamp();
                foreach (var row in moveQuery.EachSpan<Position, Velocity>())
                {
                    row.Get0().X += row.Get1().X;
                    row.Get0().Y += row.Get1().Y;
                    row.Get0().Z += row.Get1().Z;
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create (with random Faction to split archetypes)
                t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    w.Create(
                        new Faction(rng.Next(factionCount)),
                        new Position { X = bossX, Z = bossZ },
                        new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new Damage { Base = 1 + (i & 3) });
                }
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    w.Create(
                        new Faction(rng.Next(factionCount)),
                        new ProjectileTag(),
                        new Position(),
                        new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    w.Create(
                        new Faction(rng.Next(factionCount)),
                        new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new Lifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy (scan + apply)
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanPositionEachSpan(ebQuery, scratch, playerX, playerZ, hitRadiusSqPlayer, screenBound);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanPositionEachSpan(pbQuery, scratch, bossX, bossZ, hitRadiusSqBoss, screenBound);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = ScanLifetimeEachSpan(ptQuery, scratch);
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 9. Buff system: collect -> apply (outside query loop)
                t0 = Stopwatch.GetTimestamp();
                {
                    // Collect burning candidates (only entities WITHOUT BuffEffect)
                    var burnCount = 0;
                    foreach (var chunk in ebBuffTargetQuery.GetChunkSpan())
                    {
                        for (var row = 0; row < chunk.Count && burnCount < buffApplyCount; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                                buffCandidates[burnCount++] = chunk.GetEntity(row);
                        }
                    }
                    // Apply burning outside query loop
                    for (var i = 0; i < burnCount; i++)
                        w.Add(buffCandidates[i], new BuffEffect(0, statusDuration));

                    // Collect poison candidates
                    var poisonCount = 0;
                    foreach (var chunk in pbBuffTargetQuery.GetChunkSpan())
                    {
                        for (var row = 0; row < chunk.Count && poisonCount < buffApplyCount / 2; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                                poisonCandidates[poisonCount++] = chunk.GetEntity(row);
                        }
                    }
                    // Apply poison outside query loop
                    for (var i = 0; i < poisonCount; i++)
                        w.Add(poisonCandidates[i], new BuffEffect(1, statusDuration));

                    // Tick down buff timers, remove expired
                    var removeCount = 0;
                    foreach (var chunk in buffQuery.GetChunkSpan())
                    {
                        var buffs = chunk.GetComponentSpan<BuffEffect>(Component<BuffEffect>.ComponentType);
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            buffs[row].Remaining -= 1f;
                            if (buffs[row].Remaining <= 0 && removeCount < scratch.Length)
                                scratch[removeCount++] = chunk.GetEntity(row);
                        }
                    }
                    for (var i = 0; i < removeCount; i++)
                        w.Remove<BuffEffect>(scratch[i]);
                }
                t1 = Stopwatch.GetTimestamp();
                buffNs += t1 - t0;

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !w.Has<Debuff>(boss))
                    w.Add(boss, new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("MiniArch", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs + buffNs;
            var freq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [MiniArch phase breakdown] Iterate: {iterateNs / freq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / freq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / freq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / freq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%) | Buff: {buffNs / freq / 1_000_000:F1}ms ({100.0 * buffNs / totalNs:F1}%)");
        }

        // DefaultEcs
        {
            using var w = new DefaultEcs.World();
            var player = w.CreateEntity();
            player.Set(new PlayerTag());
            player.Set(new Position());
            player.Set(new Velocity());
            player.Set(new Health { Current = 10000, Max = 10000 });
            player.Set(new Speed(0));

            var boss = w.CreateEntity();
            boss.Set(new BossTag());
            boss.Set(new Position { X = bossX, Z = bossZ });
            boss.Set(new Velocity());
            boss.Set(new Health { Current = 100000, Max = 100000 });
            boss.Set(new AiState());

            var moveSet = w.GetEntities().With<Position>().With<Velocity>().AsSet();
            var ebSet = w.GetEntities().With<Position>().With<Damage>().AsSet();
            var pbSet = w.GetEntities().With<Position>().With<ProjectileTag>().AsSet();
            var ptSet = w.GetEntities().With<Position>().With<Lifetime>().AsSet();
            var buffSet = w.GetEntities().With<BuffEffect>().AsSet();
            var scratch = new DefaultEcs.Entity[8192];
            var rng = new Random(42);
            var tickCount = 0;

            int Tick()
            {
                tickCount++;
                // 1. MoveAll
                {
                    var entities = moveSet.GetEntities();
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        ref var v = ref entities[i].Get<Velocity>();
                        p.X += v.X; p.Y += v.Y; p.Z += v.Z;
                    }
                }
                // 2. Spawn enemy bullets (with Faction)
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    var e = w.CreateEntity();
                    e.Set(new Faction(rng.Next(factionCount)));
                    e.Set(new Position { X = bossX, Z = bossZ });
                    e.Set(new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed });
                    e.Set(new Damage { Base = 1 + (i & 3) });
                }
                // 3. Spawn player bullets (with Faction)
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    var e = w.CreateEntity();
                    e.Set(new Faction(rng.Next(factionCount)));
                    e.Set(new ProjectileTag());
                    e.Set(new Position());
                    e.Set(new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                // 7. Spawn particles (with Faction)
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var e = w.CreateEntity();
                    e.Set(new Faction(rng.Next(factionCount)));
                    e.Set(new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 });
                    e.Set(new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 });
                    e.Set(new Lifetime { Value = 20f });
                }
                // 4. Collide + cleanup enemy bullets
                {
                    var entities = ebSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        var dx = p.X - playerX;
                        var dz = p.Z - playerZ;
                        if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                            Math.Abs(p.X) > screenBound || Math.Abs(p.Z) > screenBound)
                        {
                            if (d < scratch.Length) scratch[d++] = entities[i];
                        }
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }
                // 5. Collide + cleanup player bullets
                {
                    var entities = pbSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var p = ref entities[i].Get<Position>();
                        var dx = p.X - bossX;
                        var dz = p.Z - bossZ;
                        if (dx * dx + dz * dz < hitRadiusSqBoss ||
                            Math.Abs(p.X) > screenBound || Math.Abs(p.Z) > screenBound)
                        {
                            if (d < scratch.Length) scratch[d++] = entities[i];
                        }
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }
                // 6. Process particles
                {
                    var entities = ptSet.GetEntities();
                    var d = 0;
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var lt = ref entities[i].Get<Lifetime>();
                        lt.Value -= 1f;
                        if (lt.Value <= 0 && d < scratch.Length)
                            scratch[d++] = entities[i];
                    }
                    for (var i = 0; i < d; i++) scratch[i].Dispose();
                }

                // 9. Buff system
                {
                    // Apply burning
                    var entities = ebSet.GetEntities();
                    var applied = 0;
                    for (var i = 0; i < entities.Length && applied < buffApplyCount; i++)
                    {
                        if (rng.NextSingle() < 0.5f && !entities[i].Has<BuffEffect>())
                        {
                            entities[i].Set(new BuffEffect(0, statusDuration));
                            applied++;
                        }
                    }
                    // Apply poison
                    entities = pbSet.GetEntities();
                    applied = 0;
                    for (var i = 0; i < entities.Length && applied < buffApplyCount / 2; i++)
                    {
                        if (rng.NextSingle() < 0.5f && !entities[i].Has<BuffEffect>())
                        {
                            entities[i].Set(new BuffEffect(1, statusDuration));
                            applied++;
                        }
                    }
                    // Tick down buffs, remove expired
                    entities = buffSet.GetEntities();
                    for (var i = 0; i < entities.Length; i++)
                    {
                        ref var buff = ref entities[i].Get<BuffEffect>();
                        buff.Remaining -= 1f;
                        if (buff.Remaining <= 0)
                        {
                            entities[i].Remove<BuffEffect>();
                        }
                    }
                }

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !boss.Has<Debuff>())
                    boss.Set(new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("DefaultEcs", Tick, durationSeconds));
        }

        // Arch
        {
            using var w = Arch.Core.World.Create();
            var player = w.Create(new PlayerTag(), new Position(), new Velocity(), new Health { Current = 10000, Max = 10000 }, new Speed(0));
            var boss = w.Create(new BossTag(), new Position { X = bossX, Z = bossZ }, new Velocity(), new Health { Current = 100000, Max = 100000 }, new AiState());

            var moveDesc = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();
            var ebDesc = new Arch.Core.QueryDescription().WithAll<Position, Damage>();
            var pbDesc = new Arch.Core.QueryDescription().WithAll<Position, ProjectileTag>();
            var ptDesc = new Arch.Core.QueryDescription().WithAll<Position, Lifetime>();
            var buffDesc = new Arch.Core.QueryDescription().WithAll<BuffEffect>();
            var scratch = new Arch.Core.Entity[8192];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0, buffNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll
                t0 = Stopwatch.GetTimestamp();
                foreach (var chunk in w.Query(in moveDesc))
                {
                    var pos = chunk.GetSpan<Position>();
                    var vel = chunk.GetSpan<Velocity>();
                    for (var row = 0; row < chunk.Count; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create (with Faction)
                t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    w.Create(new Faction(rng.Next(factionCount)),
                        new Position { X = bossX, Z = bossZ },
                        new Velocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new Damage { Base = 1 + (i & 3) });
                }
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    w.Create(new Faction(rng.Next(factionCount)),
                        new ProjectileTag(),
                        new Position(),
                        new Velocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    w.Create(new Faction(rng.Next(factionCount)),
                        new Position { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new Velocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new Lifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in ebDesc))
                    {
                        var pos = chunk.GetSpan<Position>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            var dx = pos[row].X - playerX;
                            var dz = pos[row].Z - playerZ;
                            if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = chunk.Entity(row);
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in pbDesc))
                    {
                        var pos = chunk.GetSpan<Position>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            var dx = pos[row].X - bossX;
                            var dz = pos[row].Z - bossZ;
                            if (dx * dx + dz * dz < hitRadiusSqBoss ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = chunk.Entity(row);
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var chunk in w.Query(in ptDesc))
                    {
                        var lt = chunk.GetSpan<Lifetime>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            lt[row].Value -= 1f;
                            if (lt[row].Value <= 0 && d < scratch.Length)
                                scratch[d++] = chunk.Entity(row);
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) w.Destroy(scratch[i]);
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 9. Buff system
                t0 = Stopwatch.GetTimestamp();
                {
                    // Apply burning to random enemy bullets (collect first, apply outside query)
                    var applied = 0;
                    foreach (var chunk in w.Query(in ebDesc))
                    {
                        for (var row = 0; row < chunk.Count && applied < buffApplyCount; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = chunk.Entity(row);
                                if (!w.Has<BuffEffect>(e))
                                {
                                    w.Add(e, new BuffEffect(0, statusDuration));
                                    applied++;
                                }
                            }
                        }
                    }
                    // Apply poison to random player bullets (collect first, apply outside query)
                    applied = 0;
                    foreach (var chunk in w.Query(in pbDesc))
                    {
                        for (var row = 0; row < chunk.Count && applied < buffApplyCount / 2; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = chunk.Entity(row);
                                if (!w.Has<BuffEffect>(e))
                                {
                                    w.Add(e, new BuffEffect(1, statusDuration));
                                    applied++;
                                }
                            }
                        }
                    }

                    // Tick down buffs, remove expired
                    var removeCount = 0;
                    foreach (var chunk in w.Query(in buffDesc))
                    {
                        var buffs = chunk.GetSpan<BuffEffect>();
                        for (var row = 0; row < chunk.Count; row++)
                        {
                            buffs[row].Remaining -= 1f;
                            if (buffs[row].Remaining <= 0 && removeCount < scratch.Length)
                                scratch[removeCount++] = chunk.Entity(row);
                        }
                    }
                    for (var i = 0; i < removeCount; i++)
                        w.Remove<BuffEffect>(scratch[i]);
                }
                t1 = Stopwatch.GetTimestamp();
                buffNs += t1 - t0;

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !w.Has<Debuff>(boss))
                    w.Add(boss, new Debuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("Arch", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs + buffNs;
            var freq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [Arch phase breakdown] Iterate: {iterateNs / freq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / freq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / freq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / freq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%) | Buff: {buffNs / freq / 1_000_000:F1}ms ({100.0 * buffNs / totalNs:F1}%)");
        }

        // Friflo
        {
            var store = new EntityStore();
            var player = store.CreateEntity(new FrifloPlayerTag(), new FrifloPosition(), new FrifloVelocity(), new FrifloHealth { Current = 10000, Max = 10000 }, new FrifloSpeed(0));
            var boss = store.CreateEntity(new FrifloBossTag(), new FrifloPosition { X = bossX, Z = bossZ }, new FrifloVelocity(), new FrifloHealth { Current = 100000, Max = 100000 }, new FrifloAiState());

            var moveQuery = store.Query<FrifloPosition, FrifloVelocity>();
            var ebQuery = store.Query<FrifloPosition, FrifloDamage>();
            var pbQuery = store.Query<FrifloPosition, FrifloProjectileTag>();
            var ptQuery = store.Query<FrifloPosition, FrifloLifetime>();
            var buffQuery = store.Query<FrifloBuffEffect>();
            var scratch = new int[8192];
            var rng = new Random(42);
            var tickCount = 0;
            long iterateNs = 0, createNs = 0, destroyScanNs = 0, destroyApplyNs = 0, buffNs = 0;

            int Tick()
            {
                tickCount++;
                long t0, t1;

                // 1. MoveAll
                t0 = Stopwatch.GetTimestamp();
                foreach (var (posChunk, velChunk, _) in moveQuery.Chunks)
                {
                    var pos = posChunk.Span;
                    var vel = velChunk.Span;
                    for (var row = 0; row < pos.Length; row++)
                    {
                        pos[row].X += vel[row].X;
                        pos[row].Y += vel[row].Y;
                        pos[row].Z += vel[row].Z;
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                iterateNs += t1 - t0;

                // 2+3+7. Create (with random Faction to split archetypes)
                t0 = Stopwatch.GetTimestamp();
                for (var i = 0; i < enemyBulletsPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    var speed = 30f + rng.NextSingle() * 20f;
                    store.CreateEntity(
                        new FrifloFaction(rng.Next(factionCount)),
                        new FrifloPosition { X = bossX, Z = bossZ },
                        new FrifloVelocity { X = MathF.Cos(angle) * speed, Z = MathF.Sin(angle) * speed },
                        new FrifloDamage { Base = 1 + (i & 3) });
                }
                for (var i = 0; i < playerBulletsPerTick; i++)
                {
                    var spread = (rng.NextSingle() - 0.5f) * 0.6f;
                    store.CreateEntity(
                        new FrifloFaction(rng.Next(factionCount)),
                        new FrifloProjectileTag(),
                        new FrifloPosition(),
                        new FrifloVelocity { X = (bulletDirX + spread) * 40f, Z = (bulletDirZ + spread) * 40f });
                }
                for (var i = 0; i < particlesPerTick; i++)
                {
                    var angle = rng.NextSingle() * 6.2832f;
                    store.CreateEntity(
                        new FrifloFaction(rng.Next(factionCount)),
                        new FrifloPosition { X = rng.NextSingle() * 10 - 5, Z = rng.NextSingle() * 10 - 5 },
                        new FrifloVelocity { X = MathF.Cos(angle) * 5, Z = MathF.Sin(angle) * 5 },
                        new FrifloLifetime { Value = 20f });
                }
                t1 = Stopwatch.GetTimestamp();
                createNs += t1 - t0;

                // 4+5+6. Destroy (scan + apply)
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, _, entitiesChunk) in ebQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length; row++)
                        {
                            var dx = pos[row].X - playerX;
                            var dz = pos[row].Z - playerZ;
                            if (dx * dx + dz * dz < hitRadiusSqPlayer ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = entitiesChunk[row];
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, _, entitiesChunk) in pbQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length; row++)
                        {
                            var dx = pos[row].X - bossX;
                            var dz = pos[row].Z - bossZ;
                            if (dx * dx + dz * dz < hitRadiusSqBoss ||
                                Math.Abs(pos[row].X) > screenBound || Math.Abs(pos[row].Z) > screenBound)
                            {
                                if (d < scratch.Length) scratch[d++] = entitiesChunk[row];
                            }
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }
                {
                    t0 = Stopwatch.GetTimestamp();
                    var d = 0;
                    foreach (var (posChunk, ltChunk, entitiesChunk) in ptQuery.Chunks)
                    {
                        var lt = ltChunk.Span;
                        for (var row = 0; row < lt.Length; row++)
                        {
                            lt[row].Value -= 1f;
                            if (lt[row].Value <= 0 && d < scratch.Length)
                                scratch[d++] = entitiesChunk[row];
                        }
                    }
                    t1 = Stopwatch.GetTimestamp();
                    destroyScanNs += t1 - t0;
                    t0 = Stopwatch.GetTimestamp();
                    for (var i = 0; i < d; i++) store.GetEntityById(scratch[i]).DeleteEntity();
                    t1 = Stopwatch.GetTimestamp();
                    destroyApplyNs += t1 - t0;
                }

                // 9. Buff system
                t0 = Stopwatch.GetTimestamp();
                {
                    // Apply burning to random enemy bullets (collect first, apply outside query)
                    var applyCount = 0;
                    var toBuff = new int[buffApplyCount];
                    foreach (var (posChunk, _, entitiesChunk) in ebQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length && applyCount < buffApplyCount; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = store.GetEntityById(entitiesChunk[row]);
                                if (!e.HasComponent<FrifloBuffEffect>())
                                    toBuff[applyCount++] = entitiesChunk[row];
                            }
                        }
                    }
                    for (var bi = 0; bi < applyCount; bi++)
                    {
                        var e = store.GetEntityById(toBuff[bi]);
                        e.AddComponent(new FrifloBuffEffect { Type = 0, Remaining = statusDuration });
                    }
                    // Apply poison to random player bullets (collect first, apply outside query)
                    var toPoison = new int[buffApplyCount / 2];
                    applyCount = 0;
                    foreach (var (posChunk, _, entitiesChunk) in pbQuery.Chunks)
                    {
                        var pos = posChunk.Span;
                        for (var row = 0; row < pos.Length && applyCount < buffApplyCount / 2; row++)
                        {
                            if (rng.NextSingle() < 0.5f)
                            {
                                var e = store.GetEntityById(entitiesChunk[row]);
                                if (!e.HasComponent<FrifloBuffEffect>())
                                    toPoison[applyCount++] = entitiesChunk[row];
                            }
                        }
                    }
                    for (var bi = 0; bi < applyCount; bi++)
                    {
                        var e = store.GetEntityById(toPoison[bi]);
                        e.AddComponent(new FrifloBuffEffect { Type = 1, Remaining = statusDuration });
                    }

                    // Tick down buffs, remove expired
                    var removeCount = 0;
                    foreach (var (buffChunk, entitiesChunk) in buffQuery.Chunks)
                    {
                        var buffs = buffChunk.Span;
                        for (var row = 0; row < buffs.Length; row++)
                        {
                            buffs[row].Remaining -= 1f;
                            if (buffs[row].Remaining <= 0 && removeCount < scratch.Length)
                                scratch[removeCount++] = entitiesChunk[row];
                        }
                    }
                    for (var i = 0; i < removeCount; i++)
                    {
                        var e = store.GetEntityById(scratch[i]);
                        e.RemoveComponent<FrifloBuffEffect>();
                    }
                }
                t1 = Stopwatch.GetTimestamp();
                buffNs += t1 - t0;

                // 8. BossEnrage
                if (tickCount % enrageInterval == 0 && !boss.HasComponent<FrifloDebuff>())
                    boss.AddComponent(new FrifloDebuff { Timer = 5f });

                return tickCount;
            }

            _results.Add(MeasureTimed("Friflo", Tick, durationSeconds));

            var totalNs = iterateNs + createNs + destroyScanNs + destroyApplyNs + buffNs;
            var freq = (double)Stopwatch.Frequency / 1_000_000_000;
            Console.WriteLine($"  [Friflo phase breakdown] Iterate: {iterateNs / freq / 1_000_000:F1}ms ({100.0 * iterateNs / totalNs:F1}%) | Create: {createNs / freq / 1_000_000:F1}ms ({100.0 * createNs / totalNs:F1}%) | DestroyScan: {destroyScanNs / freq / 1_000_000:F1}ms ({100.0 * destroyScanNs / totalNs:F1}%) | DestroyApply: {destroyApplyNs / freq / 1_000_000:F1}ms ({100.0 * destroyApplyNs / totalNs:F1}%) | Buff: {buffNs / freq / 1_000_000:F1}ms ({100.0 * buffNs / totalNs:F1}%)");
        }
    }
}
