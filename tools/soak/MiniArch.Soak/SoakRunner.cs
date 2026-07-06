using System.Diagnostics;
using MiniArch;
using MiniArch.Core;
using MiniArch.Diagnostics;

namespace MiniArch.Soak;

enum SoakPhase { WarmUp, StableMutate, MigrationStorm, HierarchyStress, AllocatorChurn, Cooldown }

sealed class SoakRunner
{
    readonly SoakConfig _cfg;
    readonly World _source;
    readonly World _shadow;
    readonly CommandStream _stream;
    readonly Random _rng;
    readonly Stopwatch _sw = new();
    readonly List<Entity> _alive = [];
    long _lastDetailFrame;
    double _prevDetailElapsed;
    int _prevDetailEntCount;
    long _prevDetailG0, _prevDetailG1, _prevDetailG2;
    long _prevDetailAllocBytes, _prevDetailDeltaBytes;

    // Per-run baseline for GC/alloc accounting. In sweep mode multiple seeds
    // share one process; GC.CollectionCount is process-wide, so we subtract the
    // baseline captured at each Run() start to report per-seed numbers.
    long _runStartAlloc;
    int _runStartG0, _runStartG1, _runStartG2;

    long _creates, _destroys, _adds, _sets, _removes, _clones, _addHier, _removeHier;
    long _migrations;
    int _peakEntityCount;

    // Allocation & network tracking
    long _frameOpsBytes, _frameSubmitBytes, _frameReplayBytes, _frameValidateBytes;
    long _frameParseBytes;  // Deserialize only (should be 0 after warmup)
    long _frameDeltaBytes;

    // Reference model — independent oracle for CompA and CompB
    sealed record RefState(int? A, long? B);
    readonly Dictionary<Entity, RefState> _refModel = [];

    // Per-frame pending remove tracking: prevents recording Add/Set for a
    // component that was already removed in the same frame.
    readonly HashSet<(Entity E, int CompIdx)> _pendingRemoves = [];

    // Tracks all entities explicitly destroyed this frame, so we can skip
    // child destroys when the parent is also being destroyed (cascade will
    // handle the child).
    readonly HashSet<Entity> _destroyedThisFrame = [];

    // Tracks entities made children via AddChild this frame, so OpDestroy can
    // skip them — destroying the parent cascade-destroys children, creating a
    // duplicate destroy entry.
    readonly HashSet<Entity> _pendingHierarchyChildren = [];

    // Tracks entities used as parents in AddChild this frame, to prevent
    // same-frame cycles (e.g., AddChild(A,B) then AddChild(B,A)).
    readonly HashSet<Entity> _pendingHierarchyParents = [];

    readonly CommandStream _shadowStream;

    public byte[]? FinalChecksum { get; private set; }
    readonly FrameDelta _snapDelta = new();
    readonly FrameDelta _replayDelta = new();

    // Operation log: last N ops per frame for crash diagnostics
    const int MaxOpLog = 64;
    readonly List<string> _opLog = new(MaxOpLog);

    public SoakRunner(SoakConfig cfg)
    {
        _cfg = cfg;
        _source = new World();
        _shadow = new World();
        _stream = new CommandStream(_source);
        _shadowStream = new CommandStream(_shadow);
        _rng = new Random(cfg.Seed);
    }

    public bool Run()
    {
        Console.WriteLine($"  Soak  seed={_cfg.Seed}  frames={_cfg.TotalFrames}  cap={_cfg.EntityCap}  floor={_cfg.EntityFloor}  ops/f={_cfg.MaxOpsPerFrame}");
        Console.WriteLine($"  {new string('─', 60)}");

        // Snapshot process-wide GC counters at run start so PrintFinal and
        // GetSweepSummaryLine report per-seed deltas even when multiple seeds
        // share one process (sweep mode).
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
            FinalChecksum = _source.CanonicalChecksum();
        PrintFinal(result);
        return result;
    }

    bool RunCore()
    {
        for (var frame = 1; frame <= _cfg.TotalFrames; frame++)
        {
            var phase = GetPhase(frame);
            _alive.RemoveAll(e => !_source.IsAlive(e));
            var count = _alive.Count;

            // Entity count regulation
            AdjustOpWeights(phase, count, out var createW, out var destroyW, out var addW, out var setW,
                out var removeW, out var cloneW, out var addHierW, out var removeHierW);

            // Generate random operations
            _pendingRemoves.Clear();
            _pendingHierarchyChildren.Clear();
            _pendingHierarchyParents.Clear();
            _destroyedThisFrame.Clear();
            _opLog.Clear();
            var beforeOps = GC.GetAllocatedBytesForCurrentThread();
            var opsThisFrame = _rng.Next(1, _cfg.MaxOpsPerFrame + 1);
            for (var op = 0; op < opsThisFrame; op++)
                RandomOp(createW, destroyW, addW, setW, removeW, cloneW, addHierW, removeHierW);
            _frameOpsBytes += GC.GetAllocatedBytesForCurrentThread() - beforeOps;

            // Layer 0: Submit + Replay + Validate
            if (!SubmitAndValidate(frame, phase)) return false;

            // Layer 1: detail line
            var printedDetail = false;
            if (!_cfg.Quiet)
            {
                if (frame == 0) Console.WriteLine();
                if (frame % _cfg.DetailInterval == 0)
                {
                    PrintDetail(frame, phase);
                    printedDetail = true;
                }
            }

            // Layer 2: full checkpoint
            if (frame % _cfg.CheckpointInterval == 0)
            {
                if (!RunCheckpoint(frame)) return false;
                if (!_cfg.Quiet && !printedDetail)
                    Console.WriteLine($"\r[{frame,8}] checkpoint passed");
                printedDetail = true;
            }

            // Quick inline entity count (only when no detail this frame)
            if (!_cfg.Quiet && !printedDetail && frame % 500 == 0)
                Console.Write($"\r  [{frame,7}]  ent={_alive.Count,5}{EntityTrend(frame)}\r");
        }
        return true;
    }

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

    void AdjustOpWeights(SoakPhase phase, int count,
        out double createW, out double destroyW, out double addW, out double setW,
        out double removeW, out double cloneW, out double addHierW, out double removeHierW)
    {
        // Default weights for StableMutate: balanced
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

        // Entity cap regulation: hard cap & hard floor
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

    void RandomOp(double createW, double destroyW, double addW, double setW,
        double removeW, double cloneW, double addHierW, double removeHierW)
    {
        var roll = _rng.NextDouble();
        var cumulative = 0.0;

        cumulative += createW;
        if (roll < cumulative) { OpCreate(); return; }

        cumulative += destroyW;
        if (roll < cumulative) { OpDestroy(); return; }

        cumulative += addW;
        if (roll < cumulative) { OpAdd(); return; }

        cumulative += setW;
        if (roll < cumulative) { OpSet(); return; }

        cumulative += removeW;
        if (roll < cumulative) { OpRemove(); return; }

        cumulative += cloneW;
        if (roll < cumulative) { OpClone(); return; }

        cumulative += addHierW;
        if (roll < cumulative) { OpAddChild(); return; }

        OpRemoveChild();
    }

    void OpCreate()
    {
        var e = _stream.Create();
        _alive.Add(e);
        var compCount = _rng.Next(1, 4);
        int? aV = null; long? bV = null;
        for (var i = 0; i < compCount; i++)
        {
            var t = _rng.Next(4);
            if (t == 0 && !aV.HasValue) { var v = _rng.Next(); _stream.Add(e, new CompA(v)); aV = v; }
            else if (t == 1 && !bV.HasValue) { var v = (long)_rng.Next(); _stream.Add(e, new CompB(v)); bV = v; }
            else if (t == 2) { _stream.Add(e, new CompC((float)_rng.NextDouble(), (float)_rng.NextDouble())); }
            else if (t == 3) { _stream.Add(e, new CompD(_rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next())); }
        }
        _refModel[e] = new RefState(aV, bV);
        LogOp($"Create({e}) comps={compCount}");
        _creates++;
        _peakEntityCount = Math.Max(_peakEntityCount, _alive.Count);
    }

    void OpDestroy()
    {
        if (_alive.Count == 0) return;
        // Pick from the back of _alive to avoid O(n) removal cost
        var idx = _rng.Next(_alive.Count);
        var e = _alive[idx];
        if (_pendingHierarchyChildren.Contains(e)) return; // cascade-destroyed with parent
        // If e has a parent ancestor that's already scheduled for destroy
        // this frame, skip —the parent's cascade will handle e.
        if (HasAncestorDestroyedThisFrame(e)) return;
        _alive.RemoveAt(idx);
        _destroyedThisFrame.Add(e);

        _stream.Destroy(e);
        _refModel.Remove(e);
        LogOp($"Destroy({e})");
        _destroys++;
    }

    void OpAdd()
    {
        if (_alive.Count == 0) return;
        var e = _alive[_rng.Next(_alive.Count)];
        if (!_source.IsAlive(e)) return;

        var t = _rng.Next(4);
        if (t == 0 && !_source.Has<CompA>(e) && !_pendingRemoves.Contains((e, 0))) { var v = _rng.Next(); _stream.Add(e, new CompA(v)); AddRefA(e, v); _adds++; }
        else if (t == 1 && !_source.Has<CompB>(e) && !_pendingRemoves.Contains((e, 1))) { var v = (long)_rng.Next(); _stream.Add(e, new CompB(v)); AddRefB(e, v); _adds++; }
        else if (t == 2 && !_source.Has<CompC>(e) && !_pendingRemoves.Contains((e, 2))) { _stream.Add(e, new CompC((float)_rng.NextDouble(), (float)_rng.NextDouble())); _adds++; }
        else if (t == 3 && !_source.Has<CompD>(e) && !_pendingRemoves.Contains((e, 3))) { _stream.Add(e, new CompD(_rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next(), _rng.Next())); _adds++; }
        LogOp($"Add({e}) t={t}");
    }

    void OpSet()
    {
        if (_alive.Count == 0) return;
        var e = _alive[_rng.Next(_alive.Count)];
        if (!_source.IsAlive(e)) return;

        var t = _rng.Next(3);
        if (t == 0 && _source.Has<CompA>(e) && !_pendingRemoves.Contains((e, 0))) { var v = _rng.Next(); _stream.Set(e, new CompA(v)); AddRefA(e, v); _sets++; }
        else if (t == 1 && _source.Has<CompB>(e) && !_pendingRemoves.Contains((e, 1))) { var v = (long)_rng.Next(); _stream.Set(e, new CompB(v)); AddRefB(e, v); _sets++; }
        else if (t == 2 && _source.Has<CompC>(e) && !_pendingRemoves.Contains((e, 2))) { _stream.Set(e, new CompC((float)_rng.NextDouble(), (float)_rng.NextDouble())); _sets++; }
        LogOp($"Set({e}) t={t}");
    }

    void OpRemove()
    {
        if (_alive.Count == 0) return;
        var e = _alive[_rng.Next(_alive.Count)];
        if (!_source.IsAlive(e)) return;

        var t = _rng.Next(4);
        var migrated = false;
        if (t == 0 && _source.Has<CompA>(e)) { _stream.Remove<CompA>(e); RemoveRefA(e); migrated = true; _pendingRemoves.Add((e, 0)); }
        else if (t == 1 && _source.Has<CompB>(e)) { _stream.Remove<CompB>(e); RemoveRefB(e); migrated = true; _pendingRemoves.Add((e, 1)); }
        else if (t == 2 && _source.Has<CompC>(e)) { _stream.Remove<CompC>(e); migrated = true; _pendingRemoves.Add((e, 2)); }
        else if (t == 3 && _source.Has<CompD>(e)) { _stream.Remove<CompD>(e); migrated = true; _pendingRemoves.Add((e, 3)); }
        if (migrated) { _removes++; _migrations++; }
        LogOp($"Remove({e}) t={t}");
    }

    void OpClone()
    {
        if (_alive.Count == 0) return;
        var source = _alive[_rng.Next(_alive.Count)];
        if (!_source.IsAlive(source)) return;
        try
        {
            var clone = _stream.Clone(source);
            _alive.Add(clone);
            LogOp($"Clone({source})→({clone})");
            _clones++;
        }
        catch { /* Clone can fail if source dies between the check and the clone — ignore */ }
    }

    void OpAddChild()
    {
        if (_alive.Count < 2) return;
        var parent = _alive[_rng.Next(_alive.Count)];
        var child = _alive[_rng.Next(_alive.Count)];
        if (parent == child) return;
        // Prevent cycles: child already made a parent, or parent already a child
        if (_pendingHierarchyParents.Contains(child)) return;
        if (_pendingHierarchyChildren.Contains(parent)) return;
        // Walk up from parent; if we reach child, it would be a cycle.
        var cur = parent;
        while (cur != default)
        {
            if (cur == child) return;
            if (!_source.TryGetParent(cur, out cur)) break;
        }
        _stream.AddChild(parent, child);
        _pendingHierarchyChildren.Add(child);
        _pendingHierarchyParents.Add(parent);
        LogOp($"AddChild({parent}, {child})");
        _addHier++;
    }

    void OpRemoveChild()
    {
        if (_alive.Count == 0) return;
        var child = _alive[_rng.Next(_alive.Count)];
        _stream.RemoveChild(child);
        LogOp($"RemoveChild({child})");
        _removeHier++;
    }

    // Reference model helpers
    void AddRefA(Entity e, int v)
    {
        if (_refModel.TryGetValue(e, out var s)) _refModel[e] = s with { A = v };
        else _refModel[e] = new RefState(v, null);
    }
    void AddRefB(Entity e, long v)
    {
        if (_refModel.TryGetValue(e, out var s)) _refModel[e] = s with { B = v };
        else _refModel[e] = new RefState(null, v);
    }
    void RemoveRefA(Entity e)
    {
        if (_refModel.TryGetValue(e, out var s)) _refModel[e] = s with { A = null };
    }
    void RemoveRefB(Entity e)
    {
        if (_refModel.TryGetValue(e, out var s)) _refModel[e] = s with { B = null };
    }

    bool SubmitAndValidate(int frame, SoakPhase phase)
    {
        // Track library allocations vs test harness allocations
        var beforeLib = GC.GetAllocatedBytesForCurrentThread();

        // Submit phase
        try
        {
            _stream.SnapshotInto(_snapDelta);
            _stream.Submit();
        }
        catch (Exception ex)
        {
            Fail(frame, phase, $"Submit failed: {ex.Message}");
            return false;
        }
        var afterSubmit = GC.GetAllocatedBytesForCurrentThread();
        _frameSubmitBytes += afterSubmit - beforeLib;

        // Replay phase
        if (!_snapDelta.IsEmpty)
        {
            try
            {
                var bytes = _snapDelta.AsSpan();
                _frameDeltaBytes += bytes.Length;
                _shadowStream.Clear();

                var beforeDeser = GC.GetAllocatedBytesForCurrentThread();
                _replayDelta.Deserialize(bytes);
                _frameParseBytes += GC.GetAllocatedBytesForCurrentThread() - beforeDeser;

                _shadowStream.Replay(_replayDelta);
            }
            catch (Exception ex)
            {
                Fail(frame, phase, $"Replay failed: {ex.Message}");
                return false;
            }
        }
        _frameReplayBytes += GC.GetAllocatedBytesForCurrentThread() - afterSubmit;

        // Entity count is O(1) — check every frame.
        if (_source.EntityCount != _shadow.EntityCount)
        {
            Fail(frame, phase, $"EntityCount mismatch: source={_source.EntityCount} shadow={_shadow.EntityCount}");
            return false;
        }

        // Heavy validation (WorldValidator + CanonicalChecksum) runs at
        // the configured interval to keep the test responsive.
        var doHeavy = _cfg.ValidateInterval <= 1 || frame % _cfg.ValidateInterval == 0;
        if (doHeavy)
        {
            var beforeVal = GC.GetAllocatedBytesForCurrentThread();
            var srcValid = WorldValidator.Validate(_source);
            var shdValid = WorldValidator.Validate(_shadow);
            if (!srcValid.IsValid || !shdValid.IsValid)
            {
                Fail(frame, phase, $"WorldValidator failed:\n  source: {string.Join("\n  ", srcValid.Issues)}\n  shadow: {string.Join("\n  ", shdValid.Issues)}");
                return false;
            }

            var srcCs = _source.CanonicalChecksum();
            var shdCs = _shadow.CanonicalChecksum();
            _frameValidateBytes += GC.GetAllocatedBytesForCurrentThread() - beforeVal;
            if (!srcCs.AsSpan().SequenceEqual(shdCs))
            {
                var srcDigest = WorldDigest.Compute(_source);
                var shdDigest = WorldDigest.Compute(_shadow);
                Fail(frame, phase,
                    $"CanonicalChecksum mismatch\n" +
                    $"  source: {Convert.ToHexString(srcCs)}\n" +
                    $"  shadow: {Convert.ToHexString(shdCs)}\n" +
                    $"  digest source occupancy={Convert.ToHexString(srcDigest.Occupancy)} free={Convert.ToHexString(srcDigest.FreeList)}\n" +
                    $"  digest shadow  occupancy={Convert.ToHexString(shdDigest.Occupancy)} free={Convert.ToHexString(shdDigest.FreeList)}");
                return false;
            }
        }

        // Periodic reference model spot-check
        if (_refModel.Count > 0 && frame % 100 == 0)
        {
            var sample = _refModel.Keys.Take(10).ToList();
            foreach (var e in sample)
            {
                if (!_source.IsAlive(e)) continue;
                var st = _refModel[e];
                if (st.A.HasValue && (!_source.Has<CompA>(e) || _source.Get<CompA>(e).Value != st.A.Value))
                {
                    Fail(frame, phase, $"Reference model mismatch: entity {e} CompA expected={st.A.Value} actual={(_source.Has<CompA>(e) ? _source.Get<CompA>(e).Value.ToString() : "missing")}");
                    return false;
                }
                if (st.B.HasValue && (!_source.Has<CompB>(e) || _source.Get<CompB>(e).Value != st.B.Value))
                {
                    Fail(frame, phase, $"Reference model mismatch: entity {e} CompB expected={st.B.Value} actual={(_source.Has<CompB>(e) ? _source.Get<CompB>(e).Value.ToString() : "missing")}");
                    return false;
                }
            }
        }

        return true;
    }

    bool RunCheckpoint(int frame)
    {
        var delta = WorldDiff.Compare(_source, _shadow);
        if (!delta.AreIdentical)
        {
            Fail(frame, SoakPhase.Cooldown, $"WorldDiff divergence");
            foreach (var d in delta.EntityDiffs)
                Console.Error.WriteLine($"  {d}");
            return false;
        }

        // Snapshot roundtrip
        var srcCs = _source.CanonicalChecksum();
        using var ms = new MemoryStream();
        WorldSnapshot.Save(ms, _source);
        ms.Position = 0;
        var loaded = WorldSnapshot.Load(ms);
        var loadCs = loaded.CanonicalChecksum();
        if (!srcCs.AsSpan().SequenceEqual(loadCs))
        {
            loaded.Dispose();
            Fail(frame, SoakPhase.Cooldown, "Snapshot roundtrip checksum mismatch");
            return false;
        }
        loaded.Dispose();

        // Clone verification
        var cloned = _source.Clone();
        var cloneCs = cloned.CanonicalChecksum();
        if (!srcCs.AsSpan().SequenceEqual(cloneCs))
        {
            cloned.Dispose();
            Fail(frame, SoakPhase.Cooldown, "Clone checksum mismatch");
            return false;
        }
        cloned.Dispose();

        return true;
    }

    // ── Logging ───────────────────────────────────────────────

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
        _prevDetailEntCount = _alive.Count;
        _lastDetailFrame = frame;

        var checks = "✓✓✓✓";
        var gcStr = $"{dg0,2}/{dg1,2}/{dg2,2}";
        var allocStr = dAlloc < 1_048_576
            ? $"{dAlloc >> 10}KB"
            : $"{dAlloc >> 20}.{(dAlloc >> 10) % 1024 * 10 / 1024}MB";
        var netStr = dNet >= 1024
            ? $"{dNet >> 10}KB"
            : $"{dNet}B";

        Console.WriteLine(
            $"\r  [{frame,7}] {pct,3}%  " +
            $"ent={_alive.Count,5}{entDir}  " +
            $"{phaseStr,-16}  " +
            $"{checks}  " +
            $"fps={fps,4}  " +
            $"GC{gcStr}  " +
            $"mem={mem}MB  " +
            $"ws={ws}MB  " +
            $"+{allocStr}  " +
            $"~{netStr}");
    }

    void PrintFinal(bool passed)
    {
        _sw.Stop();
        var elapsed = _sw.Elapsed;
        if (passed)
        {
            Console.WriteLine($"  {new string('═', 56)}");
            Console.WriteLine($"  {' ',15}P A S S  —  {_cfg.TotalFrames:N0} frames in {elapsed:hh\\:mm\\:ss}");
        }
        else
        {
            Console.WriteLine($"  {new string('═', 56)}");
            Console.WriteLine($"  {' ',15}F A I L  —  at frame {_lastDetailFrame}, elapsed {elapsed:hh\\:mm\\:ss}");
        }
        Console.WriteLine($"  {new string('═', 56)}");

        var tf = Math.Max(1L, _cfg.TotalFrames);
        var totalAlloc = GC.GetAllocatedBytesForCurrentThread() - _runStartAlloc;

        // Operations table
        Console.WriteLine();
        Console.WriteLine("  operations");
        Console.WriteLine($"    create {_creates,8}  destroy {_destroys,8}");
        Console.WriteLine($"    add    {_adds,8}  set     {_sets,8}");
        Console.WriteLine($"    remove {_removes,8}  clone   {_clones,8}");
        Console.WriteLine($"    addCh  {_addHier,8}  remCh   {_removeHier,8}");
        Console.WriteLine($"    ─────────────────────────────────────");
        Console.WriteLine($"    total  {_creates + _destroys + _adds + _sets + _removes + _clones + _addHier + _removeHier,8}");
        Console.WriteLine();
        Console.WriteLine($"  migrations      {_migrations,8}");
        Console.WriteLine($"  peak entities   {_peakEntityCount,8}");
        Console.WriteLine($"  oracle tracked  {_refModel.Count,8}");
        Console.WriteLine();
        Console.WriteLine("  memory & gc");
        Console.WriteLine($"    gen0  {GC.CollectionCount(0) - _runStartG0,5}  managed  {GC.GetTotalMemory(false) >> 20,3}MB");
        Console.WriteLine($"    gen1  {GC.CollectionCount(1) - _runStartG1,5}  ws       {Environment.WorkingSet >> 20,3}MB");
        Console.WriteLine($"    gen2  {GC.CollectionCount(2) - _runStartG2,5}");
        Console.WriteLine();
        Console.WriteLine("  thread alloc");
        Console.WriteLine($"    total  {(totalAlloc >> 20),4}MB  avg  {(totalAlloc / tf >> 10),4}KB/f");
        Console.WriteLine($"    ops    {_frameOpsBytes >> 20,4}MB  {_frameOpsBytes / tf >> 10,4}KB/f");
        Console.WriteLine($"    submit {_frameSubmitBytes >> 20,4}MB  {_frameSubmitBytes / tf >> 10,4}KB/f");
        Console.WriteLine($"    replay {_frameReplayBytes >> 20,4}MB  {_frameReplayBytes / tf >> 10,4}KB/f");
        Console.WriteLine($"      deserialize  {_frameParseBytes >> 10,4}KB  {_frameParseBytes / tf,4}B/f");
        Console.WriteLine($"      grow         {(_frameReplayBytes - _frameParseBytes) >> 10,4}KB  {(_frameReplayBytes - _frameParseBytes) / tf,4}B/f");
        Console.WriteLine($"    val    {_frameValidateBytes >> 20,4}MB  {_frameValidateBytes / tf >> 10,4}KB/f");
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
        return $"  seed={_cfg.Seed,7}  PASS  frames={_cfg.TotalFrames,7}  peak_ent={_peakEntityCount,5}  GC={g0}/{g1}/{g2}  alloc={totalAlloc}MB  net={netKb}KB";
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

    char EntityTrend(int frame)
    {
        var prev = _prevDetailEntCount >= 0 ? _prevDetailEntCount : _alive.Count;
        return _alive.Count > prev ? '↗' : _alive.Count < prev ? '↘' : '→';
    }

    void LogOp(string desc)
    {
        if (_opLog.Count < MaxOpLog)
            _opLog.Add(desc);
    }

    /// <summary>
    /// Returns true if <paramref name="e"/> has a parent-chain ancestor that
    /// is already in <see cref="_destroyedThisFrame"/> —meaning the parent's
    /// cascade will destroy <paramref name="e"/>, so an explicit destroy
    /// would be a duplicate.
    /// </summary>
    bool HasAncestorDestroyedThisFrame(Entity e)
    {
        var cur = e;
        while (_source.TryGetParent(cur, out var parent))
        {
            if (_destroyedThisFrame.Contains(parent))
                return true;
            cur = parent;
        }
        return false;
    }

    void Fail(int frame, SoakPhase phase, string reason)
    {
        Console.Error.WriteLine($"  {new string('═', 50)}");
        Console.Error.WriteLine($"  FAIL  frame={frame}  phase={PhaseName(phase)}");
        Console.Error.WriteLine($"  {reason}");
        Console.Error.WriteLine($"  seed={_cfg.Seed}");
        Console.Error.WriteLine($"  alive={_alive.Count}  oracle={_refModel.Count}");
        Console.Error.WriteLine($"  operations: C={_creates} D={_destroys} A={_adds} S={_sets} R={_removes} Cl={_clones} H+={_addHier} H-={_removeHier}");
        Console.Error.WriteLine($"  migrations={_migrations}  peak_ent={_peakEntityCount}");

        // Dump operation log
        Console.Error.WriteLine($"  last ops this frame ({_opLog.Count}):");
        foreach (var op in _opLog)
            Console.Error.WriteLine($"    {op}");

        // Try to extract the failing entity from the error message and dump its state.
        // Format example: "Entity Entity(3591, v16) is no longer alive."
        if (reason.Contains("Entity("))
        {
            try
            {
                var start = reason.IndexOf("Entity(");
                var end = reason.IndexOf(')', start) + 1;
                var entityStr = reason[start..end];
                // Parse "Entity(3591, v16)"
                var inner = entityStr.AsSpan("Entity(".Length, entityStr.Length - "Entity(".Length - 1);
                var comma = inner.IndexOf(", ");
                var idSpan = inner[..comma];
                var verSpan = inner[(comma + 2)..];
                var id = int.Parse(idSpan);
                var ver = int.Parse(verSpan);
                var entity = new Entity(id, ver);

                // Dump entity state from both worlds.
                foreach (var (world, label) in new[] { (_source, "source"), (_shadow, "shadow") })
                {
                    var report = EntityDump.Describe(world, entity);
                    Console.Error.WriteLine($"  {label}:");
                    foreach (var line in report.ToString().Split(Environment.NewLine))
                        Console.Error.WriteLine($"    {line}");
                }
            }
            catch { /* best-effort diagnostics */ }
        }

        if (_cfg.PauseOnFail)
        {
            Console.Error.WriteLine("  Press any key to continue...");
            Console.ReadKey(true);
        }
    }
}
