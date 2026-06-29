using BulletLockstep.Demo;
using MiniArch;

int slice = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 2;
int hostCount = args.Length > 1 && int.TryParse(args[1], out var h) ? h : 4;
int frameCount = args.Length > 2 && int.TryParse(args[2], out var f) ? f : 1000;

return slice switch
{
    2 => RunSlice2(hostCount, frameCount),
    3 => RunSlice3(hostCount, frameCount),
    4 => RunSlice4(hostCount, frameCount),
    5 => RunSlice5(hostCount, frameCount),
    _ => BadSlice(slice),
};

static int BadSlice(int slice)
{
    Console.Error.WriteLine($"Unknown slice: {slice}. Supported: 2, 3, 4, 5.");
    return 2;
}

// ── Slice 2: baseline lockstep integrity ──────────────────────────────
static int RunSlice2(int hostCount, int frameCount)
{
    Console.WriteLine($"BulletLockstep Slice 2 (placeholder mode + deterministic systems)");
    Console.WriteLine($"  {hostCount} hosts, {frameCount} frames");
    Console.WriteLine();

    var sim = new LockstepSimulator(hostCount) { SpawnPlayers = false };
    GcBaseline(out var gc0, out var gc1, out var gc2, out var bytes);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    int mismatch = RunLockstep(sim, frameCount);

    sw.Stop();
    GcDelta(gc0, gc1, gc2, bytes, out var gc, out var alloc);

    if (mismatch >= 0) return ReportFail(sim, mismatch);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"PASS: all {frameCount} frames consistent across {hostCount} hosts");
    Console.ResetColor();
    Report(sw, frameCount, gc, alloc, sim);
    return 0;
}

// ── Slice 4: archetype migration via Add/Remove status ───────────────
// Players spawn at frame 0 via placeholder delta. Each frame, deterministic
// systems Add<Shield> / Add<BurningTimer> / Remove<...> them, migrating
// players between archetypes. Verifies that placeholder replay + structural
// migration stay byte-identical across hosts even as players move between
// archetypes (with different local entity ids per host).
static int RunSlice4(int hostCount, int frameCount)
{
    Console.WriteLine($"BulletLockstep Slice 4 (archetype migration via Add/Remove status)");
    Console.WriteLine($"  {hostCount} hosts, {frameCount} frames");
    Console.WriteLine();

    var sim = new LockstepSimulator(hostCount) { SpawnPlayers = true };
    GcBaseline(out var gc0, out var gc1, out var gc2, out var bytes);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    int mismatch = RunLockstep(sim, frameCount);

    sw.Stop();
    GcDelta(gc0, gc1, gc2, bytes, out var gc, out var alloc);

    if (mismatch >= 0) return ReportFail(sim, mismatch);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"PASS: all {frameCount} frames consistent across {hostCount} hosts");
    Console.ResetColor();
    Report(sw, frameCount, gc, alloc, sim);

    // Verify the 4 expected player archetype variants coexisted at some point.
    // At end-of-run snapshot the per-host archetype breakdown.
    var archStats = sim.Hosts[0].World.GetArchetypeStats();
    Console.WriteLine();
    Console.WriteLine($"  archetype variants present at end of run: {archStats.Length}");
    foreach (var a in archStats)
        Console.WriteLine($"    [{a.EntityCount,4} entities] components: {string.Join(", ", a.ComponentTypes.Select(t => t.Name))}");
    return 0;
}

// ── Slice 5: hierarchy + Boss + cascade destroy ──────────────────────
// Host 0 spawns Boss + 5 WeakPoints linked via World.Link at frame 0. Boss
// drains HP deterministically; on death World.Destroy(boss) cascades through
// the hierarchy, removing all weakpoints. Homing bullets target host players
// by PlayerTag.HostId (no cross-host entity references).
static int RunSlice5(int hostCount, int frameCount)
{
    Console.WriteLine($"BulletLockstep Slice 5 (hierarchy + boss + cascade destroy)");
    Console.WriteLine($"  {hostCount} hosts, {frameCount} frames");
    Console.WriteLine();

    var sim = new LockstepSimulator(hostCount) { SpawnPlayers = true, SpawnBoss = true };
    GcBaseline(out var gc0, out var gc1, out var gc2, out var bytes);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    int mismatch = RunLockstep(sim, frameCount);

    sw.Stop();
    GcDelta(gc0, gc1, gc2, bytes, out var gc, out var alloc);

    if (mismatch >= 0) return ReportFail(sim, mismatch);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"PASS: all {frameCount} frames consistent across {hostCount} hosts");
    Console.ResetColor();
    Report(sw, frameCount, gc, alloc, sim);

    // Verify boss lived and died via cascade. Look at entity counts.
    var bossAlive = false;
    var weakPointCount = 0;
    var bossDesc = new QueryDescription().With<BossTag>();
    var wpDesc = new QueryDescription().With<WeakPointTag>();
    foreach (var _ in sim.Hosts[0].World.Query(in bossDesc)) bossAlive = true;
    foreach (var _ in sim.Hosts[0].World.Query(in wpDesc)) weakPointCount++;
    Console.WriteLine();
    Console.WriteLine($"  end state: boss alive={bossAlive}, weakpoints={weakPointCount}");
    Console.WriteLine($"  (boss HP drain: {500} frames to die from full; cascade should clear weakpoints)");
    return 0;
}

// ── Slice 3: rollback recovery ────────────────────────────────────────
// Proves the rollback core semantic:
//   capture(F) -> run M frames -> restore(F) -> re-replay same M deltas
//   => final state identical to never having rolled back.
//
// This is the property any GGPO-style netcode relies on: a host can save a
// frame, speculate ahead, then on correction roll back and re-run with the
// authoritative inputs to converge with peers.
static int RunSlice3(int hostCount, int frameCount)
{
    const int CheckpointFrame = 50;
    const int RollbackWindow = 10;
    int postRollbackFrame = CheckpointFrame + RollbackWindow;

    if (frameCount < postRollbackFrame)
    {
        Console.Error.WriteLine($"frameCount must be >= {postRollbackFrame} for Slice 3");
        return 2;
    }

    Console.WriteLine($"BulletLockstep Slice 3 (rollback recovery)");
    Console.WriteLine($"  {hostCount} hosts, checkpoint @ F{CheckpointFrame}, window +{RollbackWindow}");
    Console.WriteLine();

    var sim = new LockstepSimulator(hostCount) { SpawnPlayers = false };

    // Phase A: run normally up to (but not including) the checkpoint frame.
    int mismatch = RunLockstep(sim, CheckpointFrame);
    if (mismatch >= 0) return ReportFail(sim, mismatch);
    Console.WriteLine($"[A] ran {CheckpointFrame} frames normally — all hosts consistent");

    // Phase B: host 0 captures state at frame F (after F-1 has been applied).
    //         Other hosts do nothing — they continue to be the "authority".
    var checkpoint = sim.Hosts[0].World.CaptureState();
    var checkpointChecksum = sim.Hosts[0].Checksum();
    Console.WriteLine($"[B] host 0 captured state at F{CheckpointFrame} " +
                      $"(checksum {Convert.ToHexString(checkpointChecksum)[..8]})");

    // Phase C: run M more frames. Collect deltas so host 0 can re-replay them
    //         after rollback. All hosts stay consistent during this phase —
    //         we're testing that re-replay from a checkpoint produces the
    //         same result as the original forward run.
    var savedDeltas = new MiniArch.Core.FrameDelta[RollbackWindow][];
    for (var i = 0; i < RollbackWindow; i++)
    {
        int frame = CheckpointFrame + i;
        if (!sim.Tick(frame, out savedDeltas[i]))
        {
            Console.Error.WriteLine($"divergence during phase C at F{frame}");
            return 1;
        }
    }
    var authorityChecksum = sim.Hosts[1].Checksum();  // any non-rolled-back host
    Console.WriteLine($"[C] ran +{RollbackWindow} frames forward — all hosts consistent");

    // Phase D: host 0 rolls back to the checkpoint. Its world is now at F
    //         state. Its checksum must match what we recorded at capture time.
    sim.Hosts[0].World.RestoreState(checkpoint);
    var restoredChecksum = sim.Hosts[0].Checksum();
    if (!BytesEqual(restoredChecksum, checkpointChecksum))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[D] FAIL: restore produced different state than capture:");
        Console.WriteLine($"     captured : {Convert.ToHexString(checkpointChecksum)}");
        Console.WriteLine($"     restored : {Convert.ToHexString(restoredChecksum)}");
        Console.ResetColor();
        return 1;
    }
    Console.WriteLine($"[D] host 0 restored to F{CheckpointFrame} — checksum matches capture");

    // Phase E: host 0 re-replays the M saved deltas and re-runs the
    //         deterministic systems for each frame. It must converge to the
    //         same state as the authority (the hosts that never rolled back).
    for (var i = 0; i < RollbackWindow; i++)
    {
        sim.ReplayAndTickSystemsOnHost(0, savedDeltas[i], CheckpointFrame + i);
    }
    var replayedChecksum = sim.Hosts[0].Checksum();
    if (!BytesEqual(replayedChecksum, authorityChecksum))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[E] FAIL: re-replay did not converge to authority state:");
        Console.WriteLine($"     authority: {Convert.ToHexString(authorityChecksum)}");
        Console.WriteLine($"     replayed : {Convert.ToHexString(replayedChecksum)}");
        Console.ResetColor();
        return 1;
    }

    // Phase F: continue running forward normally. The restored host must stay
    //         in lockstep with everyone else — proving restore fully repaired
    //         internal state (free list, archetype caches, etc.) so the world
    //         is healthy enough to keep accepting new replays.
    for (var frame = postRollbackFrame; frame < frameCount; frame++)
    {
        if (!sim.Tick(frame, out _))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[F] FAIL: divergence at F{frame} after rollback recovery");
            Console.ResetColor();
            return 1;
        }
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[E+F] PASS: rollback+re-replay converged with authority,");
    Console.WriteLine($"        and host 0 stayed in lockstep for the remaining " +
                      $"{frameCount - postRollbackFrame} frames");
    Console.ResetColor();
    Console.WriteLine();
    foreach (var hh in sim.Hosts)
    {
        var st = hh.World.GetStats();
        Console.WriteLine($"  host {hh.HostId}: {st.EntityCount} alive, " +
                          $"{st.RecycledEntityCount} recycled, checksum " +
                          $"{Convert.ToHexString(hh.Checksum())[..8]}");
    }
    return 0;
}

// ── Shared helpers ────────────────────────────────────────────────────

static int RunLockstep(LockstepSimulator sim, int frameCount)
{
    for (var frame = 0; frame < frameCount; frame++)
    {
        if (!sim.Tick(frame, out _))
            return frame;
    }
    return -1;
}

static int ReportFail(LockstepSimulator sim, int mismatchFrame)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"FAIL: checksum mismatch at frame {mismatchFrame}");
    foreach (var h in sim.Hosts)
        Console.WriteLine($"  host {h.HostId}: {Convert.ToHexString(h.Checksum())}");
    Console.ResetColor();
    return 1;
}

static void GcBaseline(out int gc0, out int gc1, out int gc2, out long bytes)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    gc0 = GC.CollectionCount(0);
    gc1 = GC.CollectionCount(1);
    gc2 = GC.CollectionCount(2);
    bytes = GC.GetTotalAllocatedBytes(precise: true);
}

static void GcDelta(int gc0, int gc1, int gc2, long bytes,
                    out string gc, out long alloc)
{
    alloc = GC.GetTotalAllocatedBytes(precise: true) - bytes;
    gc = $"{GC.CollectionCount(0) - gc0}/{GC.CollectionCount(1) - gc1}/{GC.CollectionCount(2) - gc2}";
}

static void Report(System.Diagnostics.Stopwatch sw, int frameCount,
                   string gc, long alloc, LockstepSimulator sim)
{
    Console.WriteLine();
    Console.WriteLine($"Time:      {sw.ElapsedMilliseconds} ms ({(double)sw.ElapsedMilliseconds / frameCount:F3} ms/frame)");
    Console.WriteLine($"GC:        {gc}");
    Console.WriteLine($"Allocated: {alloc:N0} bytes ({(double)alloc / frameCount:N0} bytes/frame)");
    Console.WriteLine();
    foreach (var h in sim.Hosts)
    {
        var s = h.World.GetStats();
        Console.WriteLine($"  host {h.HostId}: {s.EntityCount} alive, {s.RecycledEntityCount} recycled, {s.ArchetypeCount} archetypes");
    }
}

static bool BytesEqual(byte[] a, byte[] b)
{
    if (a.Length != b.Length) return false;
    for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
    return true;
}
