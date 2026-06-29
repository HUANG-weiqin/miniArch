using BulletLockstep.Demo;

int hostCount = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 4;
int frameCount = args.Length > 1 && int.TryParse(args[1], out var f) ? f : 1000;

Console.WriteLine($"BulletLockstep Slice 2 (placeholder mode + deterministic systems)");
Console.WriteLine($"  {hostCount} hosts, {frameCount} frames");
Console.WriteLine();

var sim = new LockstepSimulator(hostCount);

// Baseline GC for steady-state measurement.
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var gc0Start = GC.CollectionCount(0);
var gc1Start = GC.CollectionCount(1);
var gc2Start = GC.CollectionCount(2);
var bytesStart = GC.GetTotalAllocatedBytes(precise: true);

int mismatchFrame = -1;
var sw = System.Diagnostics.Stopwatch.StartNew();
for (var frame = 0; frame < frameCount; frame++)
{
    if (!sim.Tick(frame))
    {
        mismatchFrame = frame;
        break;
    }
}
sw.Stop();

var bytesEnd = GC.GetTotalAllocatedBytes(precise: true);
var gc0End = GC.CollectionCount(0);
var gc1End = GC.CollectionCount(1);
var gc2End = GC.CollectionCount(2);
var allocDelta = bytesEnd - bytesStart;

if (mismatchFrame >= 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"FAIL: checksum mismatch at frame {mismatchFrame}");
    foreach (var h in sim.Hosts)
        Console.WriteLine($"  host {h.HostId}: {Convert.ToHexString(h.Checksum())}");
    Console.ResetColor();
    return 1;
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"PASS: all {frameCount} frames consistent across {hostCount} hosts");
Console.ResetColor();
Console.WriteLine();
Console.WriteLine($"Time:      {sw.ElapsedMilliseconds} ms ({(double)sw.ElapsedMilliseconds / frameCount:F3} ms/frame)");
Console.WriteLine($"GC:        {gc0End - gc0Start}/{gc1End - gc1Start}/{gc2End - gc2Start}");
Console.WriteLine($"Allocated: {allocDelta:N0} bytes ({(double)allocDelta / frameCount:N0} bytes/frame)");
Console.WriteLine();
foreach (var h in sim.Hosts)
{
    var s = h.World.GetStats();
    Console.WriteLine($"  host {h.HostId}: {s.EntityCount} alive entities, {s.RecycledEntityCount} recycled, {s.ArchetypeCount} archetypes");
}
return 0;
