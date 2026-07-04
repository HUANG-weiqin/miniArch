# MiniArch Examples

Each example below is self-contained: it defines its own component types, creates its own `World`, and can be pasted into a `dotnet new console` project with `MiniArch` installed.

> In each example, type declarations (structs, etc.) must appear **after** all top-level executable statements — this is required by C# top-level-statement syntax.

---

## 1. Basic CRUD

Create, read, update, delete entities and their components.

```csharp
using MiniArch;

var world = new World();
var entity = world.Create(new Position(0, 0), new Health(100));

// Read
Position pos = world.Get<Position>(entity);
bool hasHealth = world.Has<Health>(entity);

// Update (component must exist for Set)
world.Set(entity, new Position(10, 20));
ref var hp = ref world.GetRef<Health>(entity);  // zero-copy in-place
hp = new Health(hp.Value - 10);

// Delete
world.Remove<Health>(entity);   // remove component (archetype migration)
world.Destroy(entity);          // remove entity entirely

readonly record struct Position(float X, float Y);
readonly record struct Health(int Value);
```

---

## 2. Entity Iteration

```csharp
using MiniArch;

var world = new World();
world.Create(new Position(1, 2), new Health(100));
world.Create(new Position(3, 4), new Health(50));

// Entity-level iteration
var query = world.Query(new QueryDescription().With<Position>());
foreach (var entity in query)
{
    Position pos = world.Get<Position>(entity);
    Console.WriteLine(pos);
}

// Chunk-level iteration (fast path — direct span access)
foreach (var chunk in query.GetChunks())
{
    var positions = chunk.GetSpan<Position>();
    for (int i = 0; i < positions.Length; i++)
        positions[i] = new Position(positions[i].X + 1, positions[i].Y + 1);
}

readonly record struct Position(float X, float Y);
readonly record struct Health(int Value);
```

---

## 3. Sorted Query

Iterate entities sorted by entity ID or by component value.

```csharp
using MiniArch;

var world = new World();
world.Create(new Position(5, 5), new Health(30));
world.Create(new Position(1, 2), new Health(100));

var query = world.Query(new QueryDescription().With<Health>());

// By entity ID (ascending)
foreach (var e in query.OrderByEntityId())
    Console.WriteLine(world.Get<Health>(e));

// By component value (batch-linear scan, then sort)
foreach (var e in query.OrderByComponent<Health>((a, b) => a.Value.CompareTo(b.Value)))
    Console.WriteLine(world.Get<Health>(e));

readonly record struct Health(int Value);
readonly record struct Position(float X, float Y);
```

---

## 4. CommandStream

Record deferred mutations and apply them in one batch. MiniArch's sole
deferred recorder — flat byte stream, single-pass Set.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var entity = world.Create(new Position(0, 0));

var stream = new CommandStream(world);
stream.Set(entity, new Position(10, 20));
stream.Add(entity, new Velocity(5, 6));
stream.Submit();

Console.WriteLine(world.Get<Position>(entity)); // Position(10, 20)

// Extract delta without applying, then discard
stream.Set(entity, new Position(99, 99));
FrameDelta delta = stream.Snapshot();
stream.Clear();

// Pipelined submit + delta (record a command first, then snapshot)
stream.Set(entity, new Position(42, 42));
FrameDelta asyncDelta = await stream.SubmitAndSnapshotAsync();

// For lockstep scenarios with placeholder entity IDs:
var lockstep = new CommandStream(world) { DeferredEntities = true };
var slot = lockstep.Track(lockstep.Create());
lockstep.Add(slot.Value, new Health(100));
lockstep.Submit();  // slot.Value auto-resolves from placeholder → real

readonly record struct Position(float X, float Y);
readonly record struct Velocity(float X, float Y);
readonly record struct Health(int Value);
```

---

## 5. Deterministic Replay (FrameDelta)

Record world mutations in deferred mode (placeholder entities), then replay
on the same world to produce identical state.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
world.Create(new Position(1, 2), new Health(100));

// Record Set operations in deferred mode
var stream = new CommandStream(world) { DeferredEntities = true };
var slot = stream.Track(stream.Create());
stream.Add(slot.Value, new Health(50));
stream.Set(new Entity(0, 1), new Position(10, 20));

// Snapshot: extract delta and discard recorded commands
FrameDelta delta = stream.Snapshot();
stream.Clear();

// Replay: the delta is applied, producing identical state
stream.Replay(delta);

// slot.Value is now the real entity ID assigned during replay
Console.WriteLine(world.Get<Health>(slot.Value)); // Health(50)
Console.WriteLine(world.Get<Position>(new Entity(0, 1))); // Position(10, 20)

readonly record struct Position(float X, float Y);
readonly record struct Health(int Value);
```

---

## 6. Placeholder Resolution (EntitySlot)

In lockstep mode, new entities get placeholder IDs during recording that
resolve to real IDs on Submit — ensuring deterministic ID assignment across hosts.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var stream = new CommandStream(world) { DeferredEntities = true };

// Track returns an EntitySlot that auto-resolves
var slot = stream.Track(stream.Create());
Console.WriteLine($"Before Submit: {slot.Value}");  // placeholder Entity(-1, ...)

stream.Add(slot.Value, new Health(100));
stream.Submit();

Console.WriteLine($"After Submit: {slot.Value}");   // real Entity(0, 1)
Console.WriteLine(world.Get<Health>(slot.Value));   // Health(100)

readonly record struct Health(int Value);
```

---

## 7. Rollback (CaptureState / RestoreState)

Zero-alloc in-place rollback for GGPO-style prediction. Handles are pooled.

```csharp
using MiniArch;

var world = new World();
var entity = world.Create(new Position(0, 0));

var handle = world.CaptureState();  // save

world.Set(entity, new Position(10, 10));
Console.WriteLine(world.Get<Position>(entity)); // Position(10, 10)

world.RestoreState(handle);         // revert, handle recycled
Console.WriteLine(world.Get<Position>(entity)); // Position(0, 0)

// handle.IsRecycled is now true; calling RestoreState again throws

// For branching / long-lived checkpoints, use World.Clone():
var branch = world.Clone();

readonly record struct Position(float X, float Y);
```

---

## 8. WorldSnapshot (Binary Serialization)

Save and restore the entire world state.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
world.Create(new Position(1, 2), new Health(100));

using var stream = new MemoryStream();
WorldSnapshot.Save(stream, world);

stream.Position = 0;
var loaded = WorldSnapshot.Load(stream);
Console.WriteLine(loaded.Get<Position>(new Entity(0, 1))); // Position(1, 2)

readonly record struct Position(float X, float Y);
readonly record struct Health(int Value);
```

Useful for replays, save files, and cross-process communication.

---

## 9. Entity Hierarchy

Parent-child relationships with cascade destroy.

```csharp
using MiniArch;

var world = new World();
var parent = world.Create(new Position(0, 0));
var child = world.Create(new Position(5, 5));

world.AddChild(parent, child);
Console.WriteLine(world.TryGetParent(child, out Entity p)); // True, p == parent

world.Destroy(parent);   // child is also destroyed (cascade)
Console.WriteLine(world.IsAlive(child));  // False

readonly record struct Position(float X, float Y);
```

---

## 10. EntityAccessor (Batched Multi-Component Access)

Access multiple components on the same entity without repeated lookups.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var entity = world.Create(new Position(0, 0), new Velocity(1, 2), new Health(100));

var accessor = world.Access(entity);
ref Position pos = ref accessor.Get<Position>();
ref Velocity vel = ref accessor.Get<Velocity>();
accessor.Set(new Health(80));
bool hasArmor = accessor.Has<Armor>();  // false

readonly record struct Position(float X, float Y);
readonly record struct Velocity(float X, float Y);
readonly record struct Health(int Value);
readonly record struct Armor(int Value);
```

---

## 11. Parallel Chunk Iteration (IChunkForEach)

Multi-threaded chunk processing with zero-allocation via struct jobs.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
for (int i = 0; i < 1000; i++)
    world.Create(new Position(i, i), new Velocity(1, 1));

// Stateless job — run in parallel
var query = world.Query(new QueryDescription().With<Position>().With<Velocity>());
query.ForEachChunkParallel(new MoveJob());

// Stateful accumulation (sequential only — ref allows sharing across chunks)
var sum = new SumJob();
query.ForEachChunk(ref sum);
Console.WriteLine($"Total distance: {sum.Total}");

readonly record struct Position(float X, float Y);
readonly record struct Velocity(float X, float Y);

readonly struct MoveJob : IChunkForEach
{
    public void OnChunk(ChunkView chunk)
    {
        var pos = chunk.GetSpan<Position>();
        var vel = chunk.GetSpan<Velocity>();
        for (int i = 0; i < pos.Length; i++)
            pos[i] = new Position(pos[i].X + vel[i].X, pos[i].Y + vel[i].Y);
    }
}

struct SumJob : IChunkForEach
{
    public float Total;
    public void OnChunk(ChunkView chunk)
    {
        var pos = chunk.GetSpan<Position>();
        for (int i = 0; i < pos.Length; i++)
            Total += MathF.Sqrt(pos[i].X * pos[i].X + pos[i].Y * pos[i].Y);
    }
}
```

> **Note:** For stateful jobs on the parallel path, use `[ThreadStatic]` fields for
> per-worker accumulation, then merge results after the call returns. The struct
> is copied per worker, so fields are not shared.

---

## Next

- Full API signatures → [api.md](api.md)
- Benchmark comparisons → [comparison.md](comparison.md)
- Runnable multiplayer demo → [samples/BulletLockstep.Demo/](../samples/BulletLockstep.Demo/)
