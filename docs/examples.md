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
// resolveSlots: true updates the tracked EntitySlot from placeholder→real
stream.Replay(delta, resolveSlots: true);

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

## 7. EntitySlot Across Multiple Frames

An `EntitySlot` obtained from `Track()` persists across frames — you hold the same struct and its `.Value` keeps returning the real entity after each Submit, without re-registering.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var stream = new CommandStream(world) { DeferredEntities = true };

// Frame 1: create, track, submit — slot resolves from placeholder → real
var slot = stream.Track(stream.Create());
stream.Add(slot.Value, new Health(100));
stream.Submit();

Console.WriteLine($"Frame 1: {slot.Value}"); // Entity(0, 1) — resolved
Console.WriteLine(world.Get<Health>(slot.Value)); // Health(100)

// Frame 2: use the same slot.Value directly — no re-Track needed
var stream2 = new CommandStream(world);
stream2.Set(slot.Value, new Health(50));
stream2.Submit();

Console.WriteLine($"Frame 2: {world.Get<Health>(slot.Value)}"); // Health(50)

// Track on an already-real entity: zero heap alloc (inline Entity mode)
var alias = stream2.Track(slot.Value);
Console.WriteLine(alias.Value); // Entity(0, 1) — same entity, zero alloc

readonly record struct Health(int Value);
```

> **Performance:** `Track()` allocates a heap `Slot` only for placeholders (deferred mode). Calling `Track()` on a real entity stores the `Entity` inline — no allocation. In practice this means you can `Track()` unconditionally without worrying about GC pressure in steady-state frames.

---

## 8. Forgetting `resolveSlots: true`

`Replay()` defaults to `resolveSlots: false`. If your delta contains placeholders that you tracked, the slot stays unresolved — you get back a placeholder `Entity(-1, …)` instead of the real entity.

```csharp
using MiniArch;
using MiniArch.Core;

// ── BUG: default resolveSlots: false leaves slot unresolved ──────────────
var world = new World();
var stream = new CommandStream(world) { DeferredEntities = true };

var slot = stream.Track(stream.Create());
stream.Add(slot.Value, new Health(100));

FrameDelta delta = stream.Snapshot();
stream.Clear();

stream.Replay(delta);  // default: resolveSlots = false — slot NOT resolved

Console.WriteLine(slot.Value.IsPlaceholder); // True — still a placeholder!
Console.WriteLine(slot.Value.Id);            // -1  — cannot use this to access the entity

// ── FIX: pass resolveSlots: true on your own delta ────────────────────────
var world2 = new World();
var stream2 = new CommandStream(world2) { DeferredEntities = true };

var slot2 = stream2.Track(stream2.Create());
stream2.Add(slot2.Value, new Health(100));

FrameDelta delta2 = stream2.Snapshot();
stream2.Clear();

stream2.Replay(delta2, resolveSlots: true);

Console.WriteLine(slot2.Value.IsPlaceholder);    // False — resolved!
Console.WriteLine(world2.Get<Health>(slot2.Value)); // Health(100)

readonly record struct Health(int Value);
```

> **Rule of thumb:** Pass `resolveSlots: true` only when replaying **your own delta** (the one whose placeholders you tracked). Peer deltas don't contain your placeholders, so `resolveSlots` has no effect on them — leaving the default `false` is correct and slightly faster.

---

## 9. Rollback (CaptureState / RestoreState)

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

## 10. WorldSnapshot (Binary Serialization)

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

## 11. Entity Hierarchy

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

## 12. EntityAccessor (Batched Multi-Component Access)

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

## 13. Parallel Chunk Iteration (IChunkForEach)

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

> **Note:** For stateful jobs on the parallel path, write to external shared
> state (e.g. `ConcurrentBag<T>`), thread-local storage with explicit merge,
> or a thread-safe collector. The struct is captured by value into the
> `Parallel.For` closure — all workers share the same captured copy. Mutating
> the struct's own fields is a data race on the closure copy, and the caller's
> variable is never updated.

---

## 14. Authority Server + Mirror Clients

A single source host (authority) records and applies commands locally, then distributes a **real-id delta** to mirror clients. Mirrors replay verbatim — their ID allocators stay synchronized because they replay every delta from frame 0.

```csharp
using MiniArch;
using MiniArch.Core;

// ── Authority server ──────────────────────────────────────────────────
var authority = new World();
var authStream = new CommandStream(authority) { DeferredEntities = false };
var player = authStream.Create();
authStream.Add(player, new Position(100, 200));

// Pipelined: main thread applies locally, background builds delta
var deltaTask = authStream.SubmitAndSnapshotAsync();
var delta = deltaTask.GetAwaiter().GetResult();

Console.WriteLine(delta.DeltaCount); // ≥3 (Reserve + Create + components in payload)
Console.WriteLine(delta.IsEmpty);    // False

// ── Mirror clients start from empty and replay ────────────────────────
var mirror = new World();
new CommandStream(mirror).Replay(delta);

// All mirrors converge to identical logical state
Console.WriteLine(mirror.Get<Position>(new Entity(0, 1))); // Position(100, 200)

// Verify with canonical checksum (same logical state regardless of construction path)
byte[] authHash = authority.CanonicalChecksum();
byte[] mirHash  = mirror.CanonicalChecksum();
Console.WriteLine(Convert.ToHexString(authHash) == Convert.ToHexString(mirHash)); // True

// Multiple mirrors: create N worlds, replay the same delta on each
for (var i = 0; i < 3; i++)
{
    var m = new World();
    new CommandStream(m).Replay(delta);
}

readonly record struct Position(float X, float Y);
```

> **Key constraint:** Mirror clients must replay the same delta sequence from frame 0 (empty world) to keep ID allocators in sync. The replay system enforces this — if a replay tries to allocate an ID that the local allocator has already passed, it throws.

---

## 15. P2P Lockstep Multi-Host

Each peer owns an independent `World` + independent ID allocator. `DeferredEntities = true` makes `Create()` return **placeholder** entities (`Entity(-1, seq)`). Each peer `Snapshot`s a placeholder delta, exchanges deltas with all peers, then each replays every delta (including its own) in deterministic host-ID order.

```csharp
using MiniArch;
using MiniArch.Core;

// Three lockstep peers — each owns an independent world + id allocator
var peers = new LockstepHost[3];
for (var i = 0; i < peers.Length; i++)
    peers[i] = new LockstepHost(i);

// ── Frame 0: each peer records its own player ─────────────────────────
Entity? firstPlayer = null;
for (var i = 0; i < peers.Length; i++)
{
    var p = peers[i].Stream.Create();               // placeholder Entity(-1, 0)
    peers[i].Stream.Add(p, new PlayerTag(i));
    peers[i].Stream.Add(p, new Position(i * 100, 0));
    if (i == 0) firstPlayer = p;                    // save for EntitySlot tracking
}

// Track the first host's player to capture the real ID after replay
var slot = peers[0].Stream.Track(firstPlayer!.Value);
Console.WriteLine(slot.Value.IsPlaceholder); // True — still deferred

// ── Snapshot placeholder deltas (do NOT Submit) ───────────────────────
var deltas = new FrameDelta[peers.Length];
for (var i = 0; i < peers.Length; i++)
{
    deltas[i] = peers[i].Stream.Snapshot();
    peers[i].Stream.Clear();     // discard recorded commands, keep replay state
}

// ── Each peer replays ALL deltas in deterministic host-id order ───────
for (var host = 0; host < peers.Length; host++)
    for (var source = 0; source < peers.Length; source++)
        peers[host].Stream.Replay(deltas[source], resolveSlots: source == host);

// EntitySlot resolved: slot.Value is now the real entity ID in each host's world
Console.WriteLine(slot.Value.IsPlaceholder); // False
Console.WriteLine(slot.Value.Id);            // 0

// All peers agree on state
Console.WriteLine(peers[0].World.Get<Position>(new Entity(0, 1))); // Position(0, 0)
Console.WriteLine(peers[1].World.Get<Position>(new Entity(0, 1))); // Position(0, 0)

// ── Checksums match across all hosts ─────────────────────────────────
var refHash = peers[0].World.CanonicalChecksum();
for (var i = 1; i < peers.Length; i++)
{
    var hash = peers[i].World.CanonicalChecksum();
    Console.WriteLine(Convert.ToHexString(refHash) == Convert.ToHexString(hash)); // True
}

// Helper type — placed after all top-level statements
sealed class LockstepHost
{
    public int HostId { get; }
    public World World { get; }
    public CommandStream Stream { get; }
    public LockstepHost(int hostId)
    {
        HostId = hostId;
        World = new World();
        Stream = new CommandStream(World) { DeferredEntities = true };
    }
}

readonly record struct PlayerTag(int HostId);
readonly record struct Position(float X, float Y);
```

> The **canonical lockstep pattern**: each host records with `DeferredEntities=true`, `Snapshot`s a placeholder delta, `Clear`s its stream, then `Replay`s every peer's delta (own + others) in a fixed order. Because each host maps `seq → local id` independently, there is no single point of failure and no id-sync protocol — the ECS guarantees deterministic entity IDs per host.

---

## 16. Relay-Only Source Host

The source records commands but **never applies them locally via `Submit`**. Instead it calls `Snapshot()` + `Clear()` to produce a delta without mutating its own world, then later `Replay`s its own delta alongside all peer deltas — guaranteeing the source follows exactly the same replay path as every other host.

```csharp
using MiniArch;
using MiniArch.Core;

var sourceWorld = new World();
var stream = new CommandStream(sourceWorld) { DeferredEntities = true };

// Source records actions (never submits)
var bullet = stream.Create();
stream.Add(bullet, new Position(100, 200));
stream.Add(bullet, new Velocity(10, 0));

// Snapshot → Clear — source world is untouched
var sourceDelta = stream.Snapshot();
stream.Clear();
Console.WriteLine(sourceWorld.GetStats().EntityCount); // 0

// ── Distribution: peer replays the delta ──────────────────────────────
var peer = new World();
new CommandStream(peer).Replay(sourceDelta);
Console.WriteLine(peer.Get<Position>(new Entity(0, 1))); // Position(100, 200)

// ── Source replays its own delta (same path as peers) ─────────────────
new CommandStream(sourceWorld).Replay(sourceDelta);
Console.WriteLine(sourceWorld.Get<Position>(new Entity(0, 1))); // Position(100, 200)

// Source and peer are byte-identical
Console.WriteLine(Convert.ToHexString(sourceWorld.CanonicalChecksum())
               == Convert.ToHexString(peer.CanonicalChecksum())); // True

readonly record struct Position(float X, float Y);
readonly record struct Velocity(float X, float Y);
```

> This pattern is essential for **relay servers** or **GGPO** where the source host must not diverge from peers by taking a different execution path. The source produces a delta, hands it off, then replays it back alongside all other deltas — everyone sees the same ops in the same order.

---

## 17. Delta Wire Transport & Registry Handshake

Shows the full round-trip for shipping a `FrameDelta` over the network: `AsSpan()` for zero-copy wire format, `Deserialize()` on the receiving side, `Validate()` as defense-in-depth against malformed deltas (important for untrusted peers), and `ComponentSchema.Fingerprint()` to verify registry compatibility at connect time.

```csharp
using MiniArch;
using MiniArch.Core;

// ── Producer side ─────────────────────────────────────────────────────
var sourceWorld = new World();
var sourceStream = new CommandStream(sourceWorld);
var e = sourceStream.Create();
sourceStream.Add(e, new Health(100));
sourceStream.Add(e, new Position(10, 20));

var delta = sourceStream.Snapshot();
sourceStream.Clear();

// Zero-copy wire format — send this span over Socket/UDP
var wireBytes = delta.AsSpan();

// ── Consumer side (potentially untrusted source) ──────────────────────
// 1. Deserialize from wire bytes
var received = FrameDelta.Deserialize(wireBytes);

// 2. Validate structural integrity (safe for untrusted deltas)
received.Validate();
// Throws InvalidOperationException if:
//   - Create missing preceding Reserve
//   - component data size mismatch
//   - unknown component type id
//   - duplicate component types in a Create payload
//   - placeholder seq outside valid range, etc.

// 3. Replay into a fresh world
var targetWorld = new World();
new CommandStream(targetWorld).Replay(received);
Console.WriteLine(targetWorld.Get<Health>(new Entity(0, 1))); // Health(100)

// ── Registry handshake (do once at connect time) ──────────────────────
// In real code, exchange fingerprint bytes between peers before exchanging deltas.
// Here we compare two local fingerprints (same binary, so they match):
var fpLocal = ComponentSchema.Fingerprint();
var fpRemote = ComponentSchema.Fingerprint(); // would come from peer in practice
if (!fpLocal.AsSpan().SequenceEqual(fpRemote))
    Console.WriteLine("WARNING: ComponentRegistry divergence — are peers on the same build?");
else
    Console.WriteLine("Registry compatible — safe to exchange deltas");

readonly record struct Position(float X, float Y);
readonly record struct Health(int Value);
```

> **Security note:** Always call `Validate()` on deltas received from the network before passing them to `Replay()`. The `MaxFrameBytes` (16 MiB) and `MaxOpsPerFrame` (1M) limits prevent OOM attacks. `ComponentSchema.Fingerprint()` uses SHA-256 and helps catch silent registry mismatches during development.

---

## 18. Placeholder as Component Reference (Auto-Resolve)

The signature feature: **store a placeholder entity in another component's `Entity` field**. When the stream is `Submit`ed or `Replay`ed, `EntityFieldResolver` automatically rewrites the placeholder to the real entity — so a frame's `Create` + `Add` with cross-references "just works" without post-processing.

```csharp
using MiniArch;
using MiniArch.Core;
using System.Runtime.InteropServices;

var world = new World();
var stream = new CommandStream(world) { DeferredEntities = true };

// Create a target entity (returns placeholder)
var target = stream.Create();
stream.Add(target, new Position(500, 500));

// Create a follower that references the target in its component
var follower = stream.Create();
stream.Add(follower, new Follow { Target = target }); // placeholder stored in Entity field
stream.Add(follower, new Position(0, 0));

// Submit: Target field is auto-resolved (placeholder → real entity)
stream.Submit();

// target (created first → Entity(0,1)), follower (created second → Entity(1,1))
ref var follow = ref world.GetRef<Follow>(new Entity(1, 1)); // the follower's Follow component
Console.WriteLine(follow.Target.Id);  // 0 — resolved from placeholder to real target Entity(0,1)
Console.WriteLine(world.Get<Position>(follow.Target)); // Position(500, 500)

// ── Same thing with Replay (placeholder delta → resolved on every host) ──
var peerWorld = new World();
var peerStream = new CommandStream(peerWorld) { DeferredEntities = true };
// ... same recording pattern ...
// Snapshot → Replay → same auto-resolution ensures deterministic cross-references

[StructLayout(LayoutKind.Sequential)] // Required — EntityFieldResolver scans byte offsets
readonly record struct Position(float X, float Y);

[StructLayout(LayoutKind.Sequential)]
readonly record struct Follow
{
    public Entity Target; // <-- auto-resolved by EntityFieldResolver
}
```

> **Requirements:** The component type that holds an `Entity` field must use `[StructLayout(LayoutKind.Sequential)]` (or `LayoutKind.Explicit`). `LayoutKind.Auto` (the default for some .NET types) throws `InvalidOperationException`. Nested structs are NOT scanned — only direct `Entity` fields are discovered.

---

## 19. Deferred Hierarchy + Clone Subtree

Two unique deferred-entity capabilities combined: (1) `AddChild` between two **placeholders** — the parent-child intent is recorded and both sides resolve on Submit. (2) `CommandStream.Clone` deep-copies an entity plus all its hierarchy children as deferred placeholders — perfect for "spawn squad from template" in lockstep.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var stream = new CommandStream(world) { DeferredEntities = true };

// ── Hierarchy between two placeholders ─────────────────────────────────
var boss = stream.Create();
stream.Add(boss, new Position(500, 500));
stream.Add(boss, new Health(1000));

var weakPoint = stream.Create();
stream.Add(weakPoint, new Position(500, 500));
stream.Add(weakPoint, new Health(100));
stream.AddChild(boss, weakPoint); // both are placeholders —resolved on Submit

stream.Submit();

// boss = Entity(0,1), weakPoint = Entity(1,1); hierarchy established
Console.WriteLine(world.TryGetParent(new Entity(1, 1), out Entity parent)); // True
Console.WriteLine(parent == new Entity(0, 1));                              // True

// ── Deferred Clone — deep-copy boss + entire subtree ──────────────────
var cloneStream = new CommandStream(world) { DeferredEntities = true };
var clone = cloneStream.Clone(new Entity(0, 1)); // returns placeholder
cloneStream.Submit();

// Clone is a new entity (Entity(2,1)) with all components + a child (Entity(3,1))
Console.WriteLine(world.Get<Position>(new Entity(2, 1))); // Position(500, 500)
Console.WriteLine(world.TryGetParent(new Entity(3, 1), out Entity p2)); // True
Console.WriteLine(p2 == new Entity(2, 1));                              // True

readonly record struct Position(float X, float Y);
readonly record struct Health(int Value);
```

> **Tip:** `CommandStream.Clone` records the deep-copy as pending creation — in `DeferredEntities=true` mode the clone and all its descendants are placeholders. This guarantees deterministic ID assignment across all hosts replaying the same delta.

---

## 20. Deferred Destroy / Cancel Pending

Sometimes you record entities speculatively, then decide not to commit them. `CommandStream.Destroy` on a **placeholder** cancels that pending entity (marks the batch slot as canceled). If the placeholder has children via `AddChild`, the entire subtree is cancelled recursively.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var stream = new CommandStream(world) { DeferredEntities = true };

// ── Speculative creation + cancellation ───────────────────────────────
var maybe = stream.Create();
stream.Add(maybe, new Health(100));
// ... condition evaluates to false ...
stream.Destroy(maybe); // cancel the pending entity

stream.Submit();
Console.WriteLine(world.GetStats().EntityCount); // 0 — nothing materialized

// ── Cascade cancellation: destroy a parent, children follow ───────────
var parent = stream.Create();
var child = stream.Create();
stream.AddChild(parent, child);
stream.Destroy(parent); // also cancels child recursively
stream.Submit();
Console.WriteLine(world.GetStats().EntityCount); // 0

// ── Destroy existing (real) entity ────────────────────────────────────
world.Create(new Health(1));
var destroyStream = new CommandStream(world);
destroyStream.Destroy(new Entity(0, 1));
destroyStream.Submit();
Console.WriteLine(world.IsAlive(new Entity(0, 1))); // False

readonly record struct Health(int Value);
```

> **Internals:** Cancelling a pending entity calls `CancelPendingDescendants` which BFS through every hierarchy entry where the entity is a parent, cancels all pending children, and removes `AddChild` intents for existing (non-pending) children. The batch slot is kept (so `PendingBatchCount` stays stable) but marked as cancelled — `Submit` and `BuildDelta` both skip cancelled batches.

---

## 21. Pending Batch Fast Path + GetSingleton

When you `Create` then `Add`/`Set` multiple components on the same entity in a `CommandStream`, all component data accumulates in the **pending batch buffer** — a single contiguous byte array. `Submit` materializes the entity **once** into the correct archetype, avoiding the per-`Add` archetype migration cost that the immediate `World` API would incur.

```csharp
using MiniArch;
using MiniArch.Core;

var world = new World();
var stream = new CommandStream(world);

// Fast path: all Adds go into the pending batch, Submit materializes once
var fast = stream.Create();
stream.Add(fast, new Health(100));
stream.Add(fast, new Position(10, 20));
stream.Add(fast, new Velocity(1, 0));
// The three writes land in BatchBuf; Submit does a single materialization
// into archetype {Health, Position, Velocity}
stream.Submit();

// Contrast with immediate World API: each Add triggers archetype migration
var slow = world.Create(new Health(200));        // archetype  {Health}
world.Add(slow, new Position(30, 40));           // migrate →  {Health, Position}
world.Add(slow, new Velocity(2, 0));             // migrate →  {Health, Position, Velocity}
// 3 archetype operations vs. 1

// ── GetSingleton convenience ──────────────────────────────────────────
// When exactly one entity has a component, GetSingleton returns its Entity
world.Create(new GameConfig { TickRate = 60 });
Entity configEntity = world.GetSingleton<GameConfig>();
var config = world.Get<GameConfig>(configEntity);
Console.WriteLine(config.TickRate); // 60
// Throws if zero or more than one entity has GameConfig

readonly record struct Health(int Value);
readonly record struct Position(float X, float Y);
readonly record struct Velocity(float X, float Y);
readonly record struct GameConfig { public int TickRate; }
```

> **Tip:** The `_lastCreated` fast path avoids scanning the pending-batch ID array — `Add`/`Set` on the entity returned by the most recent `Create()` is direct (no dictionary lookup). This is the common case ("create and populate") and is optimized accordingly.

---

## 22. WithAny (OR-match) + Without (Exclusion)

`QueryDescription` supports three filter types: `With<T>` (AND-required), `WithAny<T>` (OR-match — at least one of the listed types must be present), and `Without<T>` (exclusion). These compose freely.

```csharp
using MiniArch;

var world = new World();
world.Create(new Moving(), new Burning(), new Health(100));
world.Create(new Moving(), new Poisoned(), new Health(80));
world.Create(new Moving(), new Health(50));       // no status
world.Create(new Burning(), new Dead());           // excluded — dead
world.Create(new Poisoned(), new Dead());          // excluded — dead

// Moving AND (Burning OR Poisoned) AND NOT Dead
var query = world.Query(
    new QueryDescription()
        .With<Moving>()
        .WithAny<Burning>()
        .WithAny<Poisoned>()
        .Without<Dead>());

Console.WriteLine($"Matching: {CountEntities(world, query)}"); // 2

// Pure exclusion — all alive entities
var alive = world.Query(new QueryDescription().Without<Dead>());
Console.WriteLine($"Alive: {CountEntities(world, alive)}");    // 3

static int CountEntities(World w, Query q)
{
    var count = 0;
    foreach (var _ in q) count++;
    return count;
}

readonly record struct Moving;
readonly record struct Burning;
readonly record struct Poisoned;
readonly record struct Dead;
readonly record struct Health(int Value);
```

> **Performance:** `With<T>` and `Without<T>` are evaluated at the archetype level (bitmask intersection) — O(number of archetypes), not O(entities). `WithAny<T>` identifies archetypes that contain at least one of the listed components, then the entity-level filter defaults to "accept all entities in those archetypes" (since any entity in the archetype has the required components). For fine-grained per-entity OR logic, use entity-level iteration with `Has<T>`.

---

## 23. Multi-Frame Rollback Window (GGPO)

`World.CaptureState()` and `World.RestoreState()` support a ring-buffer rollback window of arbitrary depth — not just single-frame save/restore. Handles are pooled, achieving **zero allocation in steady state**.

```csharp
using MiniArch;
using MiniArch.Core;

const int WindowDepth = 4;
var world = new World();
var ring = new WorldStateSnapshot[WindowDepth];

// Simulate frames: capture before each mutation
// ring[0] = Capture(empty) → Create(Position(0,0))
// ring[1] = Capture(Position(0,0)) → Set(Position(10,0))
// ring[2] = Capture(Position(10,0)) → Set(Position(20,0))
// ring[3] = Capture(Position(20,0)) → Set(Position(30,0))
for (var frame = 0; frame < WindowDepth; frame++)
{
    ring[frame] = world.CaptureState();
    if (frame == 0)
        world.Create(new Position(frame * 10, 0));
    else
        world.Set(new Entity(0, 1), new Position(frame * 10, 0));
}

// Before rollback: world is at final state (frame 3 applied)
Console.WriteLine(world.Get<Position>(new Entity(0, 1))); // Position(30, 0)

// Restore ring[1] → revert to state BEFORE frame 1 mutation (Position(0,0))
world.RestoreState(ring[1]); // handle recycled to pool
Console.WriteLine(world.Get<Position>(new Entity(0, 1))); // Position(0, 0)

// Recycled handle: IsRecycled is true; calling RestoreState again throws
Console.WriteLine(ring[1].IsRecycled); // True

// ring[2] is still valid (captured at Position(10,0) before frame 2's Set)
world.RestoreState(ring[2]);
Console.WriteLine(world.Get<Position>(new Entity(0, 1))); // Position(10, 0)

readonly record struct Position(float X, float Y);
```

> **GGPO pattern:** Keep a ring buffer of `WorldStateSnapshot` handles. Each frame: `CaptureState()` (pushes the oldest slot back to the pool), simulate, check for misprediction. On misprediction: `RestoreState(ring[safeFrame])`, re-simulate with correct inputs. Zero GC after warm-up because handles are pooled (see `WorldStateSnapshot` lifecycle docs).

---

## 24. World.Clone() Branching + Subtree Copy

`World.Clone()` creates a fully independent fork of the entire world — no shared internal arrays. Use it for speculative "what-if" simulation or as an alternative to `CaptureState`/`RestoreState` for long-lived checkpoints. `World.Clone(Entity)` deep-copies a single entity and its entire subtree.

```csharp
using MiniArch;

var world = new World();
var player = world.Create(new Position(0, 0), new Health(100));
var pet = world.Create(new Position(5, 5));
world.AddChild(player, pet); // hierarchy: player → pet

// ── Fork a speculative branch ─────────────────────────────────────────
var branch = world.Clone(); // fully independent copy

// Simulate 5 frames on the branch with "what-if" input
for (var frame = 1; frame <= 5; frame++)
{
    branch.Set(new Entity(0, 1), new Position(frame * 10, 0));
    if (branch.Get<Health>(new Entity(0, 1)).Value <= 0)
        break;
}
Console.WriteLine(branch.Get<Position>(new Entity(0, 1))); // Position(50, 0)

// Original world is untouched
Console.WriteLine(world.Get<Position>(new Entity(0, 1))); // Position(0, 0)

// ── Deep-copy a single entity + subtree ───────────────────────────────
var standalonePet = world.Clone(pet); // new entity with Position(5, 5), no parent
Console.WriteLine(world.TryGetParent(standalonePet, out _)); // False
Console.WriteLine(world.Get<Position>(standalonePet));        // Position(5, 5)

readonly record struct Position(float X, float Y);
readonly record struct Health(int Value);
```

> **Clone vs CaptureState:** `World.Clone()` is for **branching** — the result is a new `World` that can outlive the original and be modified independently. `CaptureState`/`RestoreState` is for **in-place rollback** — faster (array swap vs. full copy) but the snapshot is invalidated on restore. Use `Clone` when you need both the original and the branch to coexist.

---

## 25. ParallelRecording (Multi-Threaded Command Recording)

When `CommandStream.ParallelRecording = true`, all `Create`, `Add`, `Set`, `Remove`, and `Destroy` calls become thread-safe. Useful when game systems run on multiple worker threads and each produces commands independently. `Submit` must still be single-threaded.

```csharp
using MiniArch;
using MiniArch.Core;
using System.Threading.Tasks;

var world = new World();
// Pre-spawn entities via the fast pending-batch path
var spawnStream = new CommandStream(world);
for (var i = 0; i < 10_000; i++)
    spawnStream.Create();
spawnStream.Submit();

// ── Multi-threaded recording ─────────────────────────────────────────
var stream = new CommandStream(world) { ParallelRecording = true };

Parallel.For(0, 10_000, i =>
{
    // Each worker records a Position component for its assigned entity
    stream.Add(new Entity(i, 1), new Position(i, i * 2));
});

stream.ParallelRecording = false; // back to single-threaded
stream.Submit();

// All updates applied
Console.WriteLine(world.Get<Position>(new Entity(9999, 1))); // Position(9999, 19998)

readonly record struct Position(float X, float Y);
```

> **Rules:**
> - Enable `ParallelRecording` before the parallel section, disable after. Do not toggle while multiple threads are recording.
> - Do **not** record concurrently into **multiple** `CommandStream` instances targeting the same `World` — only one stream may be in parallel mode per world.
> - `Submit` must be single-threaded and exclusive. It is not concurrent-safe.
> - Parallel `Set`/`Add` on existing entities uses per-component-type concurrent append stores (lock-free for `Add`/`Set`; `Destroy`/`Create` are lock-protected). Entity creation in parallel mode uses a shared lock (`_storeCreateLock`) — acceptable because creates are typically a small fraction of total commands.

---

## Next

- Full API signatures → [api.md](api.md)
- Benchmark comparisons → [comparison.md](comparison.md)
- Runnable multiplayer demo → [samples/BulletLockstep.Demo/](../samples/BulletLockstep.Demo/)
