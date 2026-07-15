using System.Diagnostics;
using System.Reflection;
using MiniArch;
using MiniArch.Core;
using MiniArch.Diagnostics;

namespace MiniArch.LockstepSoak;

enum SoakPhase { WarmUp, StableMutate, MigrationStorm, HierarchyStress, AllocatorChurn, Cooldown }

// Component types matching soak's test component set
readonly record struct CompA(int Value);
readonly record struct CompB(long Value);
readonly record struct CompC(float X, float Y);
readonly record struct CompD(int V1, int V2, int V3, int V4, int V5, int V6, int V7, int V8);

/// <summary>
/// OwnerTag stamps each entity with its creating host at creation time.
/// This is a stable ownership identifier — it does not change on ID recycle,
/// unlike modulo-by-ID partitioning which drifts as IDs are freed and reused.
/// </summary>
readonly record struct OwnerTag(int HostId);

/// <summary>
/// Multi-host placeholder lockstep correctness verifier.
///
/// N independent hosts each record random ops into their own CommandStream
/// (DeferredEntities=true), Snapshot() a placeholder delta, Clear(), then
/// ALL hosts replay ALL N deltas in fixed hostId order. Cross-host
/// CanonicalChecksum byte-level convergence is verified at the configured
/// interval and always on the final frame.
///
/// Ownership is tracked via the OwnerTag component: each host operates only
/// on entities it created (OwnerTag.HostId == host.HostId). This guarantees
/// zero cross-host structural conflicts, so every delta is legal — no
/// exceptions are swallowed during replay.
///
/// This is a focused tool. Known simplifications vs the full soak:
///   - The CompA/CompB mutation model is tracked only on host[0] and checks the
///     full tracked set every 100 frames. Direct creates and clone roots are
///     modeled from generated intent; recursive clone descendants are baselined
///     when first observed, then checked against subsequent generated mutations.
///   - Same-frame Create→Set/Add/AddChild chaining is limited: OpAddChild
///     creates a child and links it in one call; other ops (Set/Add) target
///     only real entities from previous frames' replays.
/// </summary>
sealed class LockstepSoakRunner
{
    readonly LockstepSoakConfig _cfg;
    readonly Stopwatch _sw = new();

    // ── Per-host state ────────────────────────────────────────────
    sealed class HostState
    {
        public World World;
        public CommandStream Stream;
        public Random Rng;
        /// <summary>Real entities owned by this host (OwnerTag matches), from previous frames' replays.</summary>
        public List<Entity> Alive = [];
        /// <summary>Placeholders created this frame (OwnerTag already recorded, not yet materialized).</summary>
        public List<Entity> CreatedThisFrame = [];
        public int HostId;

        // Op counters
        public long Creates, Destroys, Adds, Sets, Removes, Clones, AddHier, RemoveHier;
        public long Migrations;
        public int PeakEntityCount;

        // Per-frame tracking
        /// <summary>
        /// Final component presence after this frame's queued Add/Remove intents.
        /// World.Has only sees the frame-start state, so generation must consult
        /// this overlay before emitting another strict component command.
        /// </summary>
        public readonly Dictionary<(Entity Entity, int ComponentKind), bool> PendingComponentPresence = [];
        public readonly HashSet<Entity> DestroyedThisFrame = [];
        public readonly HashSet<Entity> PendingHierarchyChildren = [];
        public readonly HashSet<Entity> PendingHierarchyParents = [];
        public readonly List<string> OpLog = new(64);

        public HostState(int hostId, LockstepSoakConfig cfg)
        {
            HostId = hostId;
            World = new World();
            Stream = new CommandStream(World) { DeferredEntities = true };
            Rng = new Random(cfg.Seed * 1000003 + hostId);
        }
    }

    readonly HostState[] _hosts;
    int _peakEntityCount;

    // Frame-level accounting
    long _lastDetailFrame;
    double _prevDetailElapsed;
    int _prevDetailEntCount;
    long _prevDetailG0, _prevDetailG1, _prevDetailG2;
    long _prevDetailAllocBytes, _prevDetailDeltaBytes;
    long _frameDeltaBytes;

    // Per-run baseline
    long _runStartAlloc;
    int _runStartG0, _runStartG1, _runStartG2;

    // Reference model — tracked only on host[0]. EntitySlot keeps deferred
    // placeholder keys resolvable after the source host replays its own delta.
    sealed record RefState(EntitySlot Slot, int? A, long? B);
    readonly Dictionary<Entity, RefState> _refModel = [];
    long _refChecks;

    const int MaxOpLog = 64;

    // Reflection accessor for FreeList (internal on World)
    static readonly PropertyInfo? FreeListProperty =
        typeof(World).GetProperty("FreeList", BindingFlags.Instance | BindingFlags.NonPublic);

    // RecycledEntity is an internal struct in World.EntityLifecycle.cs
    // We access its fields via reflection or by reading the FreeList through
    // the WorldDigest API. For detailed per-slot dumps we use WorldDigest
    // occupancy hashes plus a best-effort reflection-based free list read.

    public byte[]? FinalChecksum { get; private set; }

    public LockstepSoakRunner(LockstepSoakConfig cfg)
    {
        _cfg = cfg;
        _hosts = new HostState[cfg.HostCount];
        for (var i = 0; i < cfg.HostCount; i++)
            _hosts[i] = new HostState(i, cfg);
    }

    // ── Public entry ─────────────────────────────────────────────

    public bool Run()
    {
        Console.WriteLine($"  LockstepSoak  seed={_cfg.Seed}  hosts={_cfg.HostCount}  frames={_cfg.TotalFrames}" +
            $"  cap={_cfg.EntityCap}  floor={_cfg.EntityFloor}  ops/f={_cfg.MaxOpsPerFrame}");
        Console.WriteLine($"  {new string('\u2500', 60)}");

        _runStartAlloc = GC.GetAllocatedBytesForCurrentThread();
        _runStartG0 = GC.CollectionCount(0);
        _runStartG1 = GC.CollectionCount(1);
        _runStartG2 = GC.CollectionCount(2);
        _prevDetailG0 = _runStartG0;
        _prevDetailG1 = _runStartG1;
        _prevDetailG2 = _runStartG2;
        _prevDetailAllocBytes = _runStartAlloc;

        _sw.Start();

        var result = RunCore();
        if (result)
            FinalChecksum = _hosts[0].World.CanonicalChecksum();
        PrintFinal(result);
        return result;
    }

    // ── Core frame loop ──────────────────────────────────────────

    bool RunCore()
    {
        for (var frame = 1; frame <= _cfg.TotalFrames; frame++)
        {
            var phase = GetPhase(frame);

            // Phase 1: Per-host op generation
            for (var h = 0; h < _cfg.HostCount; h++)
            {
                var host = _hosts[h];

                // Rebuild realOwned (Alive) from world query: only entities with
                // OwnerTag matching this host. No modulo — stable per-entity tag
                // that doesn't wander on ID recycle.
                RebuildAlive(host);

                // Use total entity count (same on all hosts) for cap/floor regulation
                var totalCount = _hosts[0].World.EntityCount;
                AdjustOpWeights(phase, totalCount, out var createW, out var destroyW, out var addW, out var setW,
                    out var removeW, out var cloneW, out var addHierW, out var removeHierW);

                host.PendingComponentPresence.Clear();
                host.PendingHierarchyChildren.Clear();
                host.PendingHierarchyParents.Clear();
                host.DestroyedThisFrame.Clear();
                host.OpLog.Clear();
                host.CreatedThisFrame.Clear();

                var opsThisFrame = host.Rng.Next(1, _cfg.MaxOpsPerFrame + 1);
                for (var op = 0; op < opsThisFrame; op++)
                {
                    try
                    {
                        RandomOp(host, createW, destroyW, addW, setW, removeW, cloneW, addHierW, removeHierW);
                    }
                    catch (Exception ex)
                    {
                        FailWithException(frame, phase, "Phase 1 (Record)", ex);
                        return false;
                    }
                }

                // Peak = real owned + placeholders created this frame
                host.PeakEntityCount = Math.Max(host.PeakEntityCount, host.Alive.Count + host.CreatedThisFrame.Count);
            }

            // Phase 2: Snapshot + Clear (relay mode — no Submit)
            var deltas = new FrameDelta[_cfg.HostCount];
            for (var h = 0; h < _cfg.HostCount; h++)
            {
                deltas[h] = _hosts[h].Stream.Snapshot();
                _hosts[h].Stream.Clear();
            }

            // Phase 3: Per-host replay all deltas in fixed hostId order.
            // Every delta is guaranteed legal because each host only operates
            // on entities it owns (OwnerTag matches creating host). No cross-host
            // structural conflict is possible. Any exception here is a real bug
            // — never swallow.
            try
            {
                for (var h = 0; h < _cfg.HostCount; h++)
                    for (var d = 0; d < _cfg.HostCount; d++)
                        _hosts[h].Stream.Replay(deltas[d], resolveSlots: h == d);
            }
            catch (Exception ex)
            {
                FailWithException(frame, phase, "Phase 3 (Replay)", ex);
                return false;
            }

            // Track delta bytes (from host[0]'s delta as representative)
            if (deltas[0] is not null)
                _frameDeltaBytes += deltas[0].AsSpan().Length;

            ResolveRefModelEntities();

            // Phase 4: Verification
            if (!Verify(frame, phase)) return false;

            // Detail output
            if (!_cfg.Quiet)
            {
                if (frame % _cfg.DetailInterval == 0)
                {
                    PrintDetail(frame, phase);
                }
                else if (frame % _cfg.CheckpointInterval == 0)
                {
                    if (!RunCheckpoint(frame)) return false;
                    Console.WriteLine($"\r[{frame,8}] checkpoint passed");
                }
                else if (frame % 500 == 0)
                {
                    Console.Write($"\r  [{frame,7}]  ent={_hosts[0].Alive.Count,5}\r");
                }
            }
        }
        if (_refChecks == 0)
        {
            Fail(_cfg.TotalFrames, SoakPhase.Cooldown,
                "RefModel executed zero checks; the mutation oracle is inactive.");
            return false;
        }
        return true;
    }

    // ── Phase helpers ────────────────────────────────────────────

    SoakPhase GetPhase(int frame)
    {
        var pct = (double)frame / _cfg.TotalFrames;
        return pct switch
        {
            < 0.10 => SoakPhase.WarmUp,
            < 0.40 => SoakPhase.StableMutate,
            < 0.55 => SoakPhase.MigrationStorm,
            < 0.70 => SoakPhase.HierarchyStress,
            < 0.90 => SoakPhase.AllocatorChurn,
            _ => SoakPhase.Cooldown
        };
    }

    static string PhaseName(SoakPhase p) => p switch
    {
        SoakPhase.WarmUp => "warm_up",
        SoakPhase.StableMutate => "stable_mutate",
        SoakPhase.MigrationStorm => "migration_storm",
        SoakPhase.HierarchyStress => "hierarchy_stress",
        SoakPhase.AllocatorChurn => "allocator_churn",
        SoakPhase.Cooldown => "cooldown",
        _ => "unknown"
    };

    // ── Alive list management ────────────────────────────────────

    /// <summary>
    /// Rebuilds realOwned (Alive) by querying the world for entities with
    /// OwnerTag matching this host. OwnerTag is set at creation by the creating
    /// host and remains stable across ID recycle — unlike modulo-based partitioning
    /// which drifts when freed IDs are reassigned.
    /// </summary>
    void RebuildAlive(HostState host)
    {
        host.Alive.Clear();
        var query = host.World.Query(new QueryDescription().With<OwnerTag>());
        foreach (var chunk in query.GetChunks())
        {
            foreach (var entity in chunk.GetEntities())
            {
                var owner = host.World.Get<OwnerTag>(entity);
                if (owner.HostId == host.HostId)
                {
                    host.Alive.Add(entity);
                    if (host.HostId == 0)
                        EnsureRefState(host, entity);
                }
            }
        }
    }

    // ── Op weight adjustment ─────────────────────────────────────

    void AdjustOpWeights(SoakPhase phase, int count,
        out double createW, out double destroyW, out double addW, out double setW,
        out double removeW, out double cloneW, out double addHierW, out double removeHierW)
    {
        createW = 25; destroyW = 15; addW = 15; setW = 25;
        removeW = 10; cloneW = 5; addHierW = 3; removeHierW = 2;

        if (phase == SoakPhase.WarmUp)
        {
            createW = 80; destroyW = 0; addW = 10; setW = 10;
            removeW = 0; cloneW = 0; addHierW = 0; removeHierW = 0;
        }
        else if (phase == SoakPhase.MigrationStorm)
        {
            createW = 10; destroyW = 20; addW = 25; setW = 10;
            removeW = 25; cloneW = 5; addHierW = 3; removeHierW = 2;
        }
        else if (phase == SoakPhase.HierarchyStress)
        {
            createW = 20; destroyW = 15; addW = 10; setW = 20;
            removeW = 5; cloneW = 10; addHierW = 15; removeHierW = 5;
        }
        else if (phase == SoakPhase.AllocatorChurn)
        {
            createW = 40; destroyW = 35; addW = 5; setW = 10;
            removeW = 5; cloneW = 3; addHierW = 1; removeHierW = 1;
        }
        else // Cooldown
        {
            createW = 15; destroyW = 15; addW = 15; setW = 30;
            removeW = 10; cloneW = 5; addHierW = 5; removeHierW = 5;
        }

        // Entity cap regulation
        if (count >= _cfg.EntityCap) { createW = 0; cloneW = 0; }
        else if (count <= _cfg.EntityFloor && phase != SoakPhase.WarmUp) destroyW = 0;

        // Normalize weights
        var total = createW + destroyW + addW + setW + removeW + cloneW + addHierW + removeHierW;
        if (total > 0)
        {
            createW /= total; destroyW /= total; addW /= total; setW /= total;
            removeW /= total; cloneW /= total; addHierW /= total; removeHierW /= total;
        }
    }

    // ── Random op dispatch ───────────────────────────────────────

    void RandomOp(HostState host, double createW, double destroyW, double addW, double setW,
        double removeW, double cloneW, double addHierW, double removeHierW)
    {
        var roll = host.Rng.NextDouble();
        var cumulative = 0.0;

        cumulative += createW;
        if (roll < cumulative) { OpCreate(host); return; }

        cumulative += destroyW;
        if (roll < cumulative) { OpDestroy(host); return; }

        cumulative += addW;
        if (roll < cumulative) { OpAdd(host); return; }

        cumulative += setW;
        if (roll < cumulative) { OpSet(host); return; }

        cumulative += removeW;
        if (roll < cumulative) { OpRemove(host); return; }

        cumulative += cloneW;
        if (roll < cumulative) { OpClone(host); return; }

        cumulative += addHierW;
        if (roll < cumulative) { OpAddChild(host); return; }

        OpRemoveChild(host);
    }

    // ── Target pool helper ───────────────────────────────────────

    /// <summary>Picks a random target from the combined pool: Alive (real owned)
    /// + CreatedThisFrame (placeholders created this frame).</summary>
    (bool Found, Entity Entity, bool IsPlaceholder) PickTarget(HostState host)
    {
        var total = host.Alive.Count + host.CreatedThisFrame.Count;
        if (total == 0) return (false, default, false);
        var idx = host.Rng.Next(total);
        if (idx < host.Alive.Count)
            return (true, host.Alive[idx], false);
        return (true, host.CreatedThisFrame[idx - host.Alive.Count], true);
    }

    // ── Operation implementations ────────────────────────────────

    void OpCreate(HostState host)
    {
        var e = host.Stream.Create();
        // Stamp ownership at creation — stable, never changes on ID recycle
        host.Stream.Add(e, new OwnerTag(host.HostId));
        host.CreatedThisFrame.Add(e);
        var compCount = host.Rng.Next(1, 4);
        int? aV = null; long? bV = null;
        for (var i = 0; i < compCount; i++)
        {
            var t = host.Rng.Next(4);
            if (t == 0 && !HasVirtualComponent<CompA>(host, e, t)) { var v = host.Rng.Next(); host.Stream.Add(e, new CompA(v)); SetVirtualPresence(host, e, t, true); aV = v; }
            else if (t == 1 && !HasVirtualComponent<CompB>(host, e, t)) { var v = (long)host.Rng.Next(); host.Stream.Add(e, new CompB(v)); SetVirtualPresence(host, e, t, true); bV = v; }
            else if (t == 2 && !HasVirtualComponent<CompC>(host, e, t)) { host.Stream.Add(e, new CompC((float)host.Rng.NextDouble(), (float)host.Rng.NextDouble())); SetVirtualPresence(host, e, t, true); }
            else if (t == 3 && !HasVirtualComponent<CompD>(host, e, t)) { host.Stream.Add(e, new CompD(host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next())); SetVirtualPresence(host, e, t, true); }
        }
        // RefModel: track CompA/CompB values on host[0] only
        TrackRefEntity(host, e, aV, bV);
        LogOp(host, $"Create({e}) comps={compCount}");
        host.Creates++;
        _peakEntityCount = Math.Max(_peakEntityCount, host.Alive.Count + host.CreatedThisFrame.Count);
    }

    void OpDestroy(HostState host)
    {
        var (found, e, isPl) = PickTarget(host);
        if (!found) return;

        if (isPl)
        {
            // Destroy placeholder = cancel creation (covers B5/B6 cancel+reorder path)
            RemoveRefSubtree(host, e);
            host.Stream.Destroy(e);
            host.CreatedThisFrame.Remove(e);
            LogOp(host, $"Destroy({e}) placeholder cancel");
            host.Destroys++;
            return;
        }

        // Real entity destroy
        if (host.PendingHierarchyChildren.Contains(e)) return;
        if (HasDestroyedVirtualAncestor(host, e)) return;
        host.Alive.Remove(e);
        host.DestroyedThisFrame.Add(e);
        RemoveRefSubtree(host, e);
        host.Stream.Destroy(e);
        LogOp(host, $"Destroy({e})");
        host.Destroys++;
    }

    void OpAdd(HostState host)
    {
        var (found, e, isPl) = PickTarget(host);
        if (!found) return;
        // Placeholders aren't in the world yet — skip IsAlive check.
        // Real entities must still be alive.
        if (!isPl && !host.World.IsAlive(e)) return;
        if (IsScheduledForDestroy(host, e)) return;

        var t = host.Rng.Next(4);
        if (t == 0 && !HasVirtualComponent<CompA>(host, e, t)) { var v = host.Rng.Next(); host.Stream.Add(e, new CompA(v)); SetVirtualPresence(host, e, t, true); AddRefA(host, e, v); host.Adds++; }
        else if (t == 1 && !HasVirtualComponent<CompB>(host, e, t)) { var v = (long)host.Rng.Next(); host.Stream.Add(e, new CompB(v)); SetVirtualPresence(host, e, t, true); AddRefB(host, e, v); host.Adds++; }
        else if (t == 2 && !HasVirtualComponent<CompC>(host, e, t)) { host.Stream.Add(e, new CompC((float)host.Rng.NextDouble(), (float)host.Rng.NextDouble())); SetVirtualPresence(host, e, t, true); host.Adds++; }
        else if (t == 3 && !HasVirtualComponent<CompD>(host, e, t)) { host.Stream.Add(e, new CompD(host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next(), host.Rng.Next())); SetVirtualPresence(host, e, t, true); host.Adds++; }
        LogOp(host, $"Add({e}) t={t}");
    }

    void OpSet(HostState host)
    {
        var (found, e, isPl) = PickTarget(host);
        if (!found) return;
        if (!isPl && !host.World.IsAlive(e)) return;
        if (IsScheduledForDestroy(host, e)) return;

        var t = host.Rng.Next(3);
        if (t == 0 && HasVirtualComponent<CompA>(host, e, t)) { var v = host.Rng.Next(); host.Stream.Set(e, new CompA(v)); AddRefA(host, e, v); host.Sets++; }
        else if (t == 1 && HasVirtualComponent<CompB>(host, e, t)) { var v = (long)host.Rng.Next(); host.Stream.Set(e, new CompB(v)); AddRefB(host, e, v); host.Sets++; }
        else if (t == 2 && HasVirtualComponent<CompC>(host, e, t)) { host.Stream.Set(e, new CompC((float)host.Rng.NextDouble(), (float)host.Rng.NextDouble())); host.Sets++; }
        LogOp(host, $"Set({e}) t={t}");
    }

    void OpRemove(HostState host)
    {
        var (found, e, isPl) = PickTarget(host);
        if (!found || isPl) return;
        if (!host.World.IsAlive(e)) return;
        if (IsScheduledForDestroy(host, e)) return;

        var t = host.Rng.Next(4);
        var migrated = false;
        if (t == 0 && HasVirtualComponent<CompA>(host, e, t)) { host.Stream.Remove<CompA>(e); SetVirtualPresence(host, e, t, false); RemoveRefA(host, e); migrated = true; }
        else if (t == 1 && HasVirtualComponent<CompB>(host, e, t)) { host.Stream.Remove<CompB>(e); SetVirtualPresence(host, e, t, false); RemoveRefB(host, e); migrated = true; }
        else if (t == 2 && HasVirtualComponent<CompC>(host, e, t)) { host.Stream.Remove<CompC>(e); SetVirtualPresence(host, e, t, false); migrated = true; }
        else if (t == 3 && HasVirtualComponent<CompD>(host, e, t)) { host.Stream.Remove<CompD>(e); SetVirtualPresence(host, e, t, false); migrated = true; }
        if (migrated) { host.Removes++; host.Migrations++; }
        LogOp(host, $"Remove({e}) t={t}");
    }

    static bool HasVirtualComponent<T>(HostState host, Entity entity, int componentKind)
        where T : unmanaged
    {
        if (host.PendingComponentPresence.TryGetValue((entity, componentKind), out var isPresent))
            return isPresent;
        return host.World.Has<T>(entity);
    }

    static void SetVirtualPresence(HostState host, Entity entity, int componentKind, bool isPresent)
        => host.PendingComponentPresence[(entity, componentKind)] = isPresent;

    static void InitializeClonePresence(HostState host, Entity source, Entity clone)
    {
        SetVirtualPresence(host, clone, 0, HasVirtualComponent<CompA>(host, source, 0));
        SetVirtualPresence(host, clone, 1, HasVirtualComponent<CompB>(host, source, 1));
        SetVirtualPresence(host, clone, 2, HasVirtualComponent<CompC>(host, source, 2));
        SetVirtualPresence(host, clone, 3, HasVirtualComponent<CompD>(host, source, 3));
    }

    void OpClone(HostState host)
    {
        var (found, e, isPl) = PickTarget(host);
        if (!found || isPl) return;
        if (!host.World.IsAlive(e)) return;
        if (IsScheduledForDestroy(host, e)) return;

        var sourceRef = host.HostId == 0 ? EnsureRefState(host, e) : null;
        var clone = host.Stream.Clone(e);
        InitializeClonePresence(host, e, clone);
        // Cloned entity gets the cloning host's OwnerTag.
        host.Stream.Add(clone, new OwnerTag(host.HostId));
        host.CreatedThisFrame.Add(clone);
        if (sourceRef is not null)
            TrackRefEntity(host, clone, sourceRef.A, sourceRef.B);
        LogOp(host, $"Clone({e})\u2192({clone})");
        host.Clones++;
    }

    void OpAddChild(HostState host)
    {
        if (host.Alive.Count == 0) return;
        var parent = host.Alive[host.Rng.Next(host.Alive.Count)];
        if (IsScheduledForDestroy(host, parent)) return;

        // Create a new child entity for hierarchy linking
        var child = host.Stream.Create();
        host.Stream.Add(child, new OwnerTag(host.HostId));
        host.CreatedThisFrame.Add(child);
        TrackRefEntity(host, child, null, null);

        // Check for cycles — uses pending intents from stream
        if (WouldCreateCycle(host, parent, child))
        {
            // Orphan child is harmless; all hosts replay the same orphan
            host.CreatedThisFrame.Remove(child);
            return;
        }

        host.Stream.AddChild(parent, child);
        host.PendingHierarchyChildren.Add(child);
        host.PendingHierarchyParents.Add(parent);
        LogOp(host, $"AddChild({parent}, {child})");
        host.AddHier++;
    }

    void OpRemoveChild(HostState host)
    {
        var (found, e, isPl) = PickTarget(host);
        if (!found || isPl) return;
        if (IsScheduledForDestroy(host, e)) return;
        host.Stream.RemoveChild(e);
        LogOp(host, $"RemoveChild({e})");
        host.RemoveHier++;
    }

    // ── Cycle detection (replicates soak's WouldCreateCycle) ────

    bool WouldCreateCycle(HostState host, Entity parent, Entity child)
    {
        var intents = new List<(Entity Child, Entity Parent)>();
        var pending = host.Stream.ActiveHierarchyForTesting
            as System.Collections.Generic.IDictionary<Entity, CommandStreamCore.HierarchyIntent>;
        if (pending is not null)
        {
            foreach (var kvp in pending)
            {
                if (kvp.Value.IsAdd)
                    intents.Add((kvp.Key, kvp.Value.Parent));
            }
        }
        intents.Add((child, parent));

        if (intents.Count <= 1)
        {
            var cur = parent;
            while (cur.Id >= 0)
            {
                if (cur == child) return true;
                if (!host.World.TryGetParent(cur, out cur)) break;
            }
            return false;
        }

        intents.Sort((a, b) => a.Child.Id.CompareTo(b.Child.Id));

        var localParent = new Dictionary<int, Entity>();
        foreach (var (c, p) in intents)
        {
            var seen = new HashSet<int>();
            var cur = p;
            while (cur.Id >= 0)
            {
                if (!seen.Add(cur.Id))
                    break;
                if (cur == c)
                    return true;

                if (localParent.TryGetValue(cur.Id, out var nextP) && nextP.Id >= 0)
                {
                    cur = nextP;
                }
                else if (host.World.TryGetParent(cur, out var worldP))
                {
                    cur = worldP;
                }
                else
                {
                    break;
                }
            }
            localParent[c.Id] = p;
        }
        return false;
    }

    // ── Hierarchy helpers ────────────────────────────────────────

    bool IsScheduledForDestroy(HostState host, Entity entity)
        => host.DestroyedThisFrame.Contains(entity) || HasDestroyedVirtualAncestor(host, entity);

    bool HasDestroyedVirtualAncestor(HostState host, Entity entity)
    {
        var current = entity;
        var limit = host.World.EntityCount + host.PendingHierarchyChildren.Count + 1;
        for (var i = 0; i < limit && TryGetVirtualParent(host, current, out var parent); i++)
        {
            if (host.DestroyedThisFrame.Contains(parent))
                return true;
            current = parent;
        }
        return false;
    }

    static bool TryGetVirtualParent(HostState host, Entity child, out Entity parent)
    {
        if (host.Stream.ActiveHierarchyForTesting is
                IDictionary<Entity, CommandStreamCore.HierarchyIntent> pending &&
            pending.TryGetValue(child, out var intent))
        {
            if (intent.IsAdd)
            {
                parent = intent.Parent;
                return true;
            }

            parent = default;
            return false;
        }

        if (!child.IsPlaceholder)
            return host.World.TryGetParent(child, out parent);

        parent = default;
        return false;
    }

    // ── Reference model helpers (host[0] only) ──────────────────

    void TrackRefEntity(HostState host, Entity entity, int? a, long? b)
    {
        if (host.HostId != 0) return;
        if (!_refModel.TryAdd(entity, new RefState(host.Stream.Track(entity), a, b)))
            throw new InvalidOperationException($"RefModel already tracks entity {entity}.");
    }

    RefState EnsureRefState(HostState host, Entity entity)
    {
        if (host.HostId != 0)
            throw new InvalidOperationException("RefModel is only maintained for host[0].");
        if (_refModel.TryGetValue(entity, out var existing))
            return existing;
        if (entity.IsPlaceholder || !host.World.IsAlive(entity))
            throw new InvalidOperationException($"RefModel cannot initialize unresolved entity {entity}.");

        int? a = host.World.TryGet(entity, out CompA actualA) ? actualA.Value : null;
        long? b = host.World.TryGet(entity, out CompB actualB) ? actualB.Value : null;
        var state = new RefState(host.Stream.Track(entity), a, b);
        _refModel.Add(entity, state);
        return state;
    }

    void ResolveRefModelEntities()
    {
        if (_refModel.Count == 0) return;

        List<(Entity Placeholder, Entity Real, RefState State)>? resolved = null;
        foreach (var (entity, state) in _refModel)
        {
            if (!entity.IsPlaceholder) continue;
            var real = state.Slot.Value;
            if (real.IsPlaceholder)
                throw new InvalidOperationException($"RefModel slot for {entity} was not resolved by local replay.");
            (resolved ??= []).Add((entity, real, state));
        }

        if (resolved is null) return;
        foreach (var (placeholder, real, state) in resolved)
        {
            _refModel.Remove(placeholder);
            if (!_refModel.TryAdd(real, state))
                throw new InvalidOperationException($"RefModel resolution collided on real entity {real}.");
        }
    }

    void RemoveRefSubtree(HostState host, Entity root)
    {
        if (host.HostId != 0 || _refModel.Count == 0) return;

        List<Entity>? removed = null;
        foreach (var entity in _refModel.Keys)
        {
            if (IsVirtualDescendantOrSelf(host, entity, root))
                (removed ??= []).Add(entity);
        }

        if (removed is null) return;
        foreach (var entity in removed)
            _refModel.Remove(entity);
    }

    static bool IsVirtualDescendantOrSelf(HostState host, Entity entity, Entity root)
    {
        var current = entity;
        var limit = host.World.EntityCount + host.PendingHierarchyChildren.Count + 1;
        for (var i = 0; i < limit; i++)
        {
            if (current == root) return true;
            if (!TryGetVirtualParent(host, current, out current)) return false;
        }
        throw new InvalidOperationException($"Virtual hierarchy walk exceeded its bound from {entity} to {root}.");
    }

    void AddRefA(HostState host, Entity e, int v)
    {
        if (host.HostId != 0) return;
        var state = EnsureRefState(host, e);
        _refModel[e] = state with { A = v };
    }

    void AddRefB(HostState host, Entity e, long v)
    {
        if (host.HostId != 0) return;
        var state = EnsureRefState(host, e);
        _refModel[e] = state with { B = v };
    }

    void RemoveRefA(HostState host, Entity e)
    {
        if (host.HostId != 0) return;
        var state = EnsureRefState(host, e);
        _refModel[e] = state with { A = null };
    }

    void RemoveRefB(HostState host, Entity e)
    {
        if (host.HostId != 0) return;
        var state = EnsureRefState(host, e);
        _refModel[e] = state with { B = null };
    }

    // ── Verification ─────────────────────────────────────────────

    bool Verify(int frame, SoakPhase phase)
    {
        // 1. EntityCount — O(1), every frame
        var refCount = _hosts[0].World.EntityCount;
        for (var h = 1; h < _cfg.HostCount; h++)
        {
            if (_hosts[h].World.EntityCount != refCount)
            {
                Fail(frame, phase, $"EntityCount mismatch: host[0]={refCount} host[{h}]={_hosts[h].World.EntityCount}");
                return false;
            }
        }

        // 2. CanonicalChecksum — configurable for long runs, always on final frame.
        var doChecksum = frame == _cfg.TotalFrames ||
            _cfg.ChecksumInterval <= 1 || frame % _cfg.ChecksumInterval == 0;
        if (doChecksum)
        {
            var refCs = _hosts[0].World.CanonicalChecksum();
            for (var h = 1; h < _cfg.HostCount; h++)
            {
                var cs = _hosts[h].World.CanonicalChecksum();
                if (!refCs.AsSpan().SequenceEqual(cs))
                {
                    DumpDesync(frame, phase, h, refCs, cs);
                    return false;
                }
            }
        }

        // 3. WorldValidator — at ValidateInterval
        var doHeavy = _cfg.ValidateInterval <= 1 || frame % _cfg.ValidateInterval == 0;
        if (doHeavy)
        {
            for (var h = 0; h < _cfg.HostCount; h++)
            {
                var result = WorldValidator.Validate(_hosts[h].World);
                if (!result.IsValid)
                {
                    Fail(frame, phase, $"WorldValidator failed on host[{h}]:\n  {string.Join("\n  ", result.Issues)}");
                    return false;
                }
            }
        }

        // 4. RefModel mutation oracle (host[0] only, full tracked set every 100 frames).
        if (_refModel.Count > 0 && (frame == _cfg.TotalFrames || frame % 100 == 0))
        {
            foreach (var (entity, state) in _refModel)
            {
                _refChecks++;
                if (!_hosts[0].World.IsAlive(entity))
                {
                    Fail(frame, phase, $"RefModel expected entity {entity} to be alive.");
                    return false;
                }

                var hasA = _hosts[0].World.Has<CompA>(entity);
                if (hasA != state.A.HasValue ||
                    (hasA && _hosts[0].World.Get<CompA>(entity).Value != state.A!.Value))
                {
                    Fail(frame, phase,
                        $"RefModel mismatch on host[0]: entity {entity} CompA " +
                        $"expected={(state.A.HasValue ? state.A.Value : "missing")} " +
                        $"actual={(hasA ? _hosts[0].World.Get<CompA>(entity).Value : "missing")}");
                    return false;
                }

                var hasB = _hosts[0].World.Has<CompB>(entity);
                if (hasB != state.B.HasValue ||
                    (hasB && _hosts[0].World.Get<CompB>(entity).Value != state.B!.Value))
                {
                    Fail(frame, phase,
                        $"RefModel mismatch on host[0]: entity {entity} CompB " +
                        $"expected={(state.B.HasValue ? state.B.Value : "missing")} " +
                        $"actual={(hasB ? _hosts[0].World.Get<CompB>(entity).Value : "missing")}");
                    return false;
                }
            }
        }

        return true;
    }

    // ── Exception diagnostic ─────────────────────────────────────

    void FailWithException(int frame, SoakPhase phase, string stage, Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  {new string('\u2550', 56)}");
        sb.AppendLine($"  FAIL  frame={frame}  phase={PhaseName(phase)}");
        sb.AppendLine($"  EXCEPTION in {stage}: {ex.GetType().Name}");
        sb.AppendLine($"  Message: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            sb.AppendLine($"  Stack trace:");
            foreach (var line in ex.StackTrace.Split('\n'))
                sb.AppendLine($"    {line.TrimEnd('\r')}");
        }
        sb.AppendLine($"  seed={_cfg.Seed}  hosts={_cfg.HostCount}");

        // Per-host summary
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var host = _hosts[h];
            sb.AppendLine($"  Host[{h}]:  alive={host.Alive.Count}  createdThisFrame={host.CreatedThisFrame.Count}" +
                $"  ent={host.World.EntityCount}" +
                $"  C={host.Creates} D={host.Destroys} A={host.Adds} S={host.Sets}" +
                $"  R={host.Removes} Cl={host.Clones} H+={host.AddHier} H-={host.RemoveHier}");
        }

        // Checksums
        sb.AppendLine($"  CanonicalChecksums:");
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var cs = _hosts[h].World.CanonicalChecksum();
            sb.AppendLine($"    host[{h}] = {Convert.ToHexString(cs)}");
        }

        // WorldDigest
        sb.AppendLine($"  WorldDigest breakdown:");
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var dig = WorldDigest.Compute(_hosts[h].World);
            sb.AppendLine($"    host[{h}]:  occupancy={Convert.ToHexString(dig.Occupancy)}" +
                $"  freeList={Convert.ToHexString(dig.FreeList)}" +
                $"  hierarchy={Convert.ToHexString(dig.Hierarchy)}");
        }

        AppendFreeListDump(sb);

        for (var h = 0; h < _cfg.HostCount; h++)
        {
            sb.AppendLine($"  Host[{h}] op log ({_hosts[h].OpLog.Count}):");
            foreach (var op in _hosts[h].OpLog)
                sb.AppendLine($"    {op}");
        }

        sb.AppendLine($"  Repro: dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak --" +
            $" --seed {_cfg.Seed} --hosts {_cfg.HostCount} --entity-cap {_cfg.EntityCap}" +
            $" --entity-floor {_cfg.EntityFloor} --ops-per-frame {_cfg.MaxOpsPerFrame}" +
            $" --frames {_cfg.TotalFrames}");

        Console.Error.Write(sb.ToString());

        if (_cfg.PauseOnFail)
        {
            Console.Error.WriteLine("  Press any key to continue...");
            Console.ReadKey(true);
        }
    }

    // ── Desync diagnostic ────────────────────────────────────────

    void DumpDesync(int frame, SoakPhase phase, int firstBadHost, byte[] refCs, byte[] badCs)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  {new string('\u2550', 56)}");
        sb.AppendLine($"  FAIL  frame={frame}  phase={PhaseName(phase)}");
        sb.AppendLine($"  CanonicalChecksum mismatch: host[0] vs host[{firstBadHost}]");
        sb.AppendLine($"  seed={_cfg.Seed}  hosts={_cfg.HostCount}");

        // Per-host summary
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var host = _hosts[h];
            sb.AppendLine($"  Host[{h}]:  alive={host.Alive.Count}  createdThisFrame={host.CreatedThisFrame.Count}" +
                $"  ent={host.World.EntityCount}" +
                $"  C={host.Creates} D={host.Destroys} A={host.Adds} S={host.Sets}" +
                $"  R={host.Removes} Cl={host.Clones} H+={host.AddHier} H-={host.RemoveHier}");
        }

        // Checksums
        sb.AppendLine($"  CanonicalChecksums:");
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var cs = _hosts[h].World.CanonicalChecksum();
            var marker = h == 0 ? " (ref)" : h == firstBadHost ? " \u2190 MISMATCH" : "";
            sb.AppendLine($"    host[{h}] = {Convert.ToHexString(cs)}{marker}");
        }

        // WorldDigest breakdown for all hosts
        sb.AppendLine($"  WorldDigest breakdown:");
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var dig = WorldDigest.Compute(_hosts[h].World);
            sb.AppendLine($"    host[{h}]:  occupancy={Convert.ToHexString(dig.Occupancy)}" +
                $"  freeList={Convert.ToHexString(dig.FreeList)}" +
                $"  hierarchy={Convert.ToHexString(dig.Hierarchy)}");
        }

        // FreeList side-by-side (via reflection)
        AppendFreeListDump(sb);

        // Op logs
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            sb.AppendLine($"  Host[{h}] op log ({_hosts[h].OpLog.Count}):");
            foreach (var op in _hosts[h].OpLog)
                sb.AppendLine($"    {op}");
        }

        // Repro command
        sb.AppendLine($"  Repro: dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak --" +
            $" --seed {_cfg.Seed} --hosts {_cfg.HostCount} --entity-cap {_cfg.EntityCap}" +
            $" --entity-floor {_cfg.EntityFloor} --ops-per-frame {_cfg.MaxOpsPerFrame}" +
            $" --frames {_cfg.TotalFrames}");

        Console.Error.Write(sb.ToString());

        if (_cfg.PauseOnFail)
        {
            Console.Error.WriteLine("  Press any key to continue...");
            Console.ReadKey(true);
        }
    }

    void AppendFreeListDump(System.Text.StringBuilder sb)
    {
        if (FreeListProperty is null)
        {
            sb.AppendLine("  FreeList: (reflection unavailable)");
            return;
        }

        // Collect free lists from all hosts via reflection.
        // World.FreeList returns ReadOnlySpan<RecycledEntity> which is a
        // by-ref-only type; we can't call the getter via MethodInfo because
        // spans cannot be boxed. Instead we extract the underlying array+count
        // via private fields _freeIds and _freeIdCount.
        var freeIdsField = typeof(World).GetField("_freeIds",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var freeIdCountField = typeof(World).GetField("_freeIdCount",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (freeIdsField is null || freeIdCountField is null)
        {
            sb.AppendLine("  FreeList: (field reflection unavailable)");
            return;
        }

        var lists = new (int Id, int Version)[_cfg.HostCount][];
        var maxLen = 0;

        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var ids = (Array)freeIdsField.GetValue(_hosts[h].World)!;
            var count = (int)freeIdCountField.GetValue(_hosts[h].World)!;
            var entries = new (int, int)[count];
            for (var i = 0; i < count; i++)
            {
                var item = ids.GetValue(i)!;
                var id = (int)item.GetType().GetField("Id")!.GetValue(item)!;
                var ver = (int)item.GetType().GetField("Version")!.GetValue(item)!;
                entries[i] = (id, ver);
            }
            lists[h] = entries;
            if (count > maxLen) maxLen = count;
        }

        sb.AppendLine($"  FreeList side-by-side (max={maxLen} slots):");
        for (var i = 0; i < maxLen; i++)
        {
            var line = $"    [{i,3}]";
            var first = "";
            var allSame = true;
            for (var h = 0; h < _cfg.HostCount; h++)
            {
                string entry;
                if (i < lists[h].Length)
                    entry = $"{lists[h][i].Id}(v{lists[h][i].Version})";
                else
                    entry = "(none)";
                line += $"  {entry,16}";
                if (h == 0) first = entry;
                else if (entry != first) allSame = false;
            }
            if (!allSame) line += "  \u2190 MISMATCH";
            sb.AppendLine(line);
        }
    }

    void Fail(int frame, SoakPhase phase, string reason)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  {new string('\u2550', 56)}");
        sb.AppendLine($"  FAIL  frame={frame}  phase={PhaseName(phase)}");
        sb.AppendLine($"  {reason}");
        sb.AppendLine($"  seed={_cfg.Seed}  hosts={_cfg.HostCount}");

        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var host = _hosts[h];
            sb.AppendLine($"  Host[{h}]:  alive={host.Alive.Count}  createdThisFrame={host.CreatedThisFrame.Count}" +
                $"  ent={host.World.EntityCount}" +
                $"  C={host.Creates} D={host.Destroys} A={host.Adds} S={host.Sets}" +
                $"  R={host.Removes} Cl={host.Clones} H+={host.AddHier} H-={host.RemoveHier}");
        }

        // Checksums
        sb.AppendLine($"  CanonicalChecksums:");
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var cs = _hosts[h].World.CanonicalChecksum();
            sb.AppendLine($"    host[{h}] = {Convert.ToHexString(cs)}");
        }

        // WorldDigest
        sb.AppendLine($"  WorldDigest breakdown:");
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var dig = WorldDigest.Compute(_hosts[h].World);
            sb.AppendLine($"    host[{h}]:  occupancy={Convert.ToHexString(dig.Occupancy)}" +
                $"  freeList={Convert.ToHexString(dig.FreeList)}" +
                $"  hierarchy={Convert.ToHexString(dig.Hierarchy)}");
        }

        AppendFreeListDump(sb);

        for (var h = 0; h < _cfg.HostCount; h++)
        {
            sb.AppendLine($"  Host[{h}] op log ({_hosts[h].OpLog.Count}):");
            foreach (var op in _hosts[h].OpLog)
                sb.AppendLine($"    {op}");
        }

        sb.AppendLine($"  Repro: dotnet run -c Release --project tools/lockstep-soak/MiniArch.LockstepSoak --" +
            $" --seed {_cfg.Seed} --hosts {_cfg.HostCount} --entity-cap {_cfg.EntityCap}" +
            $" --entity-floor {_cfg.EntityFloor} --ops-per-frame {_cfg.MaxOpsPerFrame}" +
            $" --frames {_cfg.TotalFrames}");

        Console.Error.Write(sb.ToString());

        if (_cfg.PauseOnFail)
        {
            Console.Error.WriteLine("  Press any key to continue...");
            Console.ReadKey(true);
        }
    }

    // ── Checkpoint ───────────────────────────────────────────────

    bool RunCheckpoint(int frame)
    {
        // Cross-host WorldDiff: compare host[0] with each other host
        for (var h = 1; h < _cfg.HostCount; h++)
        {
            var delta = WorldDiff.Compare(_hosts[0].World, _hosts[h].World);
            if (!delta.AreIdentical)
            {
                Fail(frame, SoakPhase.Cooldown, $"WorldDiff divergence: host[0] vs host[{h}]");
                foreach (var d in delta.EntityDiffs)
                    Console.Error.WriteLine($"  {d}");
                return false;
            }
        }

        // Per-host Snapshot roundtrip + Clone self-check
        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var cs = _hosts[h].World.CanonicalChecksum();

            // Snapshot roundtrip
            using var ms = new MemoryStream();
            WorldSnapshot.Save(ms, _hosts[h].World);
            ms.Position = 0;
            var loaded = WorldSnapshot.Load(ms);
            var loadCs = loaded.CanonicalChecksum();
            if (!cs.AsSpan().SequenceEqual(loadCs))
            {
                loaded.Dispose();
                Fail(frame, SoakPhase.Cooldown, $"Snapshot roundtrip checksum mismatch on host[{h}]");
                return false;
            }
            loaded.Dispose();

            // Clone self-check
            var cloned = _hosts[h].World.Clone();
            var cloneCs = cloned.CanonicalChecksum();
            if (!cs.AsSpan().SequenceEqual(cloneCs))
            {
                cloned.Dispose();
                Fail(frame, SoakPhase.Cooldown, $"Clone checksum mismatch on host[{h}]");
                return false;
            }
            cloned.Dispose();
        }

        return true;
    }

    // ── Logging helpers ──────────────────────────────────────────

    void LogOp(HostState host, string desc)
    {
        if (host.OpLog.Count < MaxOpLog)
            host.OpLog.Add(desc);
    }

    char EntityTrend(int frame)
    {
        var prev = _prevDetailEntCount >= 0 ? _prevDetailEntCount : _hosts[0].Alive.Count;
        return _hosts[0].Alive.Count > prev ? '\u2197' : _hosts[0].Alive.Count < prev ? '\u2198' : '\u2192';
    }

    // ── Detail output ────────────────────────────────────────────

    void PrintDetail(int frame, SoakPhase phase)
    {
        var pct = (int)((double)frame / _cfg.TotalFrames * 100);
        var entDir = EntityTrend(frame);
        var phaseStr = PhaseName(phase);

        var dt = _lastDetailFrame > 0 ? _sw.Elapsed.TotalSeconds - _prevDetailElapsed : _sw.Elapsed.TotalSeconds;
        var df = frame - (long)_lastDetailFrame;
        var fps = dt > 0 ? (int)(df / dt) : 0;

        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);
        var dg0 = g0 - _prevDetailG0;
        var dg1 = g1 - _prevDetailG1;
        var dg2 = g2 - _prevDetailG2;
        var mem = GC.GetTotalMemory(false) >> 20;
        var ws = Environment.WorkingSet >> 20;
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var dAlloc = alloc - _prevDetailAllocBytes;
        var dNet = _frameDeltaBytes - _prevDetailDeltaBytes;

        _prevDetailDeltaBytes = _frameDeltaBytes;
        _prevDetailElapsed = _sw.Elapsed.TotalSeconds;
        _prevDetailG0 = g0;
        _prevDetailG1 = g1;
        _prevDetailG2 = g2;
        _prevDetailAllocBytes = alloc;
        _prevDetailEntCount = _hosts[0].Alive.Count;
        _lastDetailFrame = frame;

        var checks = "\u2713\u2713\u2713\u2713";
        var gcStr = $"{dg0,2}/{dg1,2}/{dg2,2}";
        var allocStr = dAlloc < 1_048_576
            ? $"{dAlloc >> 10}KB"
            : $"{dAlloc >> 20}.{(dAlloc >> 10) % 1024 * 10 / 1024}MB";
        var netStr = dNet >= 1024
            ? $"{dNet >> 10}KB"
            : $"{dNet}B";

        // Entity counts per host (real owned entities)
        var entStr = string.Join("/", _hosts.Select(h => $"{h.Alive.Count}"));

        Console.WriteLine(
            $"\r  [{frame,7}] {pct,3}%  " +
            $"ent=[{entStr}]{entDir}  " +
            $"{phaseStr,-16}  " +
            $"{checks}  " +
            $"fps={fps,4}  " +
            $"GC{gcStr}  " +
            $"mem={mem}MB  " +
            $"ws={ws}MB  " +
            $"+{allocStr}  " +
            $"~{netStr}");
    }

    // ── Final output ─────────────────────────────────────────────

    void PrintFinal(bool passed)
    {
        _sw.Stop();
        var elapsed = _sw.Elapsed;
        if (passed)
        {
            Console.WriteLine($"  {new string('\u2550', 56)}");
            Console.WriteLine($"  {' ',15}P A S S  \u2014  {_cfg.TotalFrames:N0} frames in {elapsed:hh\\:mm\\:ss}");
        }
        else
        {
            Console.WriteLine($"  {new string('\u2550', 56)}");
            Console.WriteLine($"  {' ',15}F A I L  \u2014  at frame {_lastDetailFrame}, elapsed {elapsed:hh\\:mm\\:ss}");
        }
        Console.WriteLine($"  {new string('\u2550', 56)}");

        var tf = Math.Max(1L, _cfg.TotalFrames);
        var totalAlloc = GC.GetAllocatedBytesForCurrentThread() - _runStartAlloc;

        // Aggregate op counts across all hosts
        long totCreates = 0, totDestroys = 0, totAdds = 0, totSets = 0;
        long totRemoves = 0, totClones = 0, totAddHier = 0, totRemoveHier = 0;
        long totMigrations = 0;
        var peakEnt = 0;
        var refModelCount = _refModel.Count;

        for (var h = 0; h < _cfg.HostCount; h++)
        {
            var host = _hosts[h];
            totCreates += host.Creates;
            totDestroys += host.Destroys;
            totAdds += host.Adds;
            totSets += host.Sets;
            totRemoves += host.Removes;
            totClones += host.Clones;
            totAddHier += host.AddHier;
            totRemoveHier += host.RemoveHier;
            totMigrations += host.Migrations;
            if (host.PeakEntityCount > peakEnt) peakEnt = host.PeakEntityCount;
        }

        Console.WriteLine();
        Console.WriteLine("  operations (aggregate across all hosts)");
        Console.WriteLine($"    create {totCreates,8}  destroy {totDestroys,8}");
        Console.WriteLine($"    add    {totAdds,8}  set     {totSets,8}");
        Console.WriteLine($"    remove {totRemoves,8}  clone   {totClones,8}");
        Console.WriteLine($"    addCh  {totAddHier,8}  remCh   {totRemoveHier,8}");
        Console.WriteLine($"    \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
        Console.WriteLine($"    total  {totCreates + totDestroys + totAdds + totSets + totRemoves + totClones + totAddHier + totRemoveHier,8}");
        Console.WriteLine();
        Console.WriteLine($"  migrations      {totMigrations,8}");
        Console.WriteLine($"  peak entities   {peakEnt,8}");
        Console.WriteLine($"  oracle tracked  {refModelCount,8}");
        Console.WriteLine($"  oracle checks   {_refChecks,8}");
        Console.WriteLine();
        Console.WriteLine("  memory & gc");
        Console.WriteLine($"    gen0  {GC.CollectionCount(0) - _runStartG0,5}  managed  {GC.GetTotalMemory(false) >> 20,3}MB");
        Console.WriteLine($"    gen1  {GC.CollectionCount(1) - _runStartG1,5}  ws       {Environment.WorkingSet >> 20,3}MB");
        Console.WriteLine($"    gen2  {GC.CollectionCount(2) - _runStartG2,5}");
        Console.WriteLine();
        Console.WriteLine("  thread alloc");
        Console.WriteLine($"    total  {(totalAlloc >> 20),4}MB  avg  {(totalAlloc / tf >> 10),4}KB/f");
        Console.WriteLine();
        Console.WriteLine($"  network  total {_frameDeltaBytes >> 10,5}KB  avg  {_frameDeltaBytes / tf,5}B/f");

        if (_cfg.Determinism && FinalChecksum != null)
            Console.WriteLine($"  final checksum  {Convert.ToHexString(FinalChecksum)}");
    }

    public string GetSweepSummaryLine()
    {
        var totalAlloc = (GC.GetAllocatedBytesForCurrentThread() - _runStartAlloc) >> 20;
        var g0 = GC.CollectionCount(0) - _runStartG0;
        var g1 = GC.CollectionCount(1) - _runStartG1;
        var g2 = GC.CollectionCount(2) - _runStartG2;
        var netKb = _frameDeltaBytes >> 10;
        return $"  seed={_cfg.Seed,7}  PASS  frames={_cfg.TotalFrames,7}  hosts={_cfg.HostCount}  peak_ent={_peakEntityCount,5}  GC={g0}/{g1}/{g2}  alloc={totalAlloc}MB  net={netKb}KB";
    }
}
