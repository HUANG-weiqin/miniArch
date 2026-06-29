using MiniArch;

namespace BulletLockstep.Demo.Systems;

// Entry stored in a grid cell. Carries the logical sort keys (HostId,
// SpawnFrame) needed for deterministic multi-host ordering — entity ids
// differ across hosts in placeholder mode.
public readonly struct GridEntry(Entity entity, int hostId, int spawnFrame, int x, int y)
{
    public readonly Entity Entity = entity;
    public readonly int HostId = hostId;
    public readonly int SpawnFrame = spawnFrame;
    public readonly int X = x;
    public readonly int Y = y;
}

public interface IEntryVisitor
{
    void Visit(GridEntry entry);
}

// Deterministic spatial hash grid for broad-phase collision. Cell size is a
// fixed int (milli-pixels). Entries are bucketed by (cellX, cellY) hash.
//
// Determinism contract: bucket iteration order does NOT depend on dictionary
// insertion order. Callers that need a deterministic ordering must sort the
// returned entity set by a logical key (e.g. entity's HostId/SpawnFrame), not
// by Entity id (which differs across hosts in placeholder mode).
//
// Reused across builds via Clear — single-threaded simulator owns one grid.
public sealed class SpatialGrid
{
    private const int CellSize = 16_000;  // milli-pix; > 2x max entity radius

    private readonly Dictionary<long, List<GridEntry>> _cells = new(256);

    public void Clear() => _cells.Clear();

    public void Insert(Entity entity, int hostId, int spawnFrame, int x, int y)
    {
        var key = CellKey(x, y);
        if (!_cells.TryGetValue(key, out var bucket))
        {
            bucket = new List<GridEntry>(8);
            _cells[key] = bucket;
        }
        bucket.Add(new GridEntry(entity, hostId, spawnFrame, x, y));
    }

    // Visits every entry in the cell containing (x, y) plus the 8 neighbours.
    // Visitor receives each entry; visitor decides narrow-phase (distance check).
    public void VisitNeighborhood(int x, int y, IEntryVisitor visitor)
    {
        var cx = FloorDiv(x, CellSize);
        var cy = FloorDiv(y, CellSize);
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var key = CombineCell(cx + dx, cy + dy);
                if (!_cells.TryGetValue(key, out var bucket))
                    continue;
                for (var i = 0; i < bucket.Count; i++)
                    visitor.Visit(bucket[i]);
            }
        }
    }

    // Cell hashing. cellX and cellY pack into a single long so we get one
    // dictionary lookup per cell. FloorDiv handles negative coordinates.
    private static long CellKey(int x, int y)
        => CombineCell(FloorDiv(x, CellSize), FloorDiv(y, CellSize));

    private static long CombineCell(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

    private static int FloorDiv(int a, int b)
    {
        var q = a / b;
        return (a % b != 0 && (a ^ b) < 0) ? q - 1 : q;
    }
}
