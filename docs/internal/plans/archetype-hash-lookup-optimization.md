# Archetype Hash Lookup Optimization

## Goal

Eliminate the last remaining heap allocation in `CommandStream.ResolveArchetypeForSpan` and `CommandBuffer.BuildCreatedEntityComponents` when the local archetype cache misses.

Current code allocates `new ComponentType[n]` (~24 bytes) on every cache miss. We want to replace this with a zero-allocation hash lookup.

## Background

Both `CommandStream` and `CommandBuffer` have a local LRU archetype cache (16 slots in CommandStream, 4 slots in CommandBuffer). On cache miss, they currently do:

```csharp
// CommandStream (CommandStream.cs line ~962)
var signatureTypes = new ComponentType[components.Length];  // ← HEAP ALLOC
for (var i = 0; i < components.Length; i++)
    signatureTypes[i] = components[i].ComponentType;
var archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(signatureTypes));
```

```csharp
// CommandBuffer (CommandBuffer.cs line ~941)
var comps = new ComponentType[idx];  // ← HEAP ALLOC
Array.Copy(types, comps, idx);
archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(comps));
```

The archetype ALREADY EXISTS in the World (created on a previous tick). But the local cache missed, and we need to find it. The `GetOrCreateArchetype` call will find the existing archetype (via `_archetypes.TryGetValue`), but we still pay for the `new ComponentType[]` allocation to construct the lookup key.

## Design: Add a 64-bit hash dictionary as a secondary index

### World.cs changes

Add a dictionary that maps a commutative (order-independent) 64-bit hash of component types directly to the Archetype:

```csharp
// New field
private readonly Dictionary<ulong, Archetype> _archetypeByHash = new();

// New methods
internal static ulong ComputeArchetypeHash(ReadOnlySpan<ComponentType> types)
{
    // Commutative hash: XOR-based, order-independent
    var hash = (ulong)types.Length * 0x9E3779B97F4A7C15;
    for (var i = 0; i < types.Length; i++)
        hash ^= Permute64(types[i].Value);
    return hash;
}

private static ulong Permute64(int value)
{
    // SplitMix64: maps a small integer to a well-distributed 64-bit value
    var x = (ulong)value + 1;
    x ^= x >> 33;
    x *= 0xFF51AFD7ED558CCD;
    x ^= x >> 33;
    x *= 0xC4CEB9FE1A85EC53;
    x ^= x >> 33;
    return x;
}

internal Archetype? FindArchetype(ulong hash, ReadOnlySpan<ComponentType> types)
{
    if (_archetypeByHash.TryGetValue(hash, out var archetype))
    {
        // Verify match (hash collision guard)
        if (archetype.Signature.AsSpan().SequenceEqual(types))
            return archetype;
    }
    return null;
}
```

Modify `GetOrCreateArchetype` to populate the hash index:

```csharp
internal Archetype GetOrCreateArchetype(Signature signature)
{
    if (_archetypes.TryGetValue(signature, out var archetype))
        return archetype;

    archetype = new Archetype(signature, ResolveComponentTypes(signature), _chunkCapacity);
    _archetypes.Add(signature, archetype);
    _archetypeByHash[ComputeArchetypeHash(signature.AsSpan())] = archetype;  // ← NEW
    PublishArchetypeSnapshot(archetype);
    return archetype;
}
```

Also add `_archetypeByHash.Clear()` next to every `_archetypes.Clear()` call (in `Dispose()` and `Reset()`).

### CommandStream.cs changes

Replace the cache miss allocation in `ResolveArchetypeForSpan` (around line 962):

```csharp
// BEFORE:
var signatureTypes = new ComponentType[components.Length];
for (var i = 0; i < components.Length; i++)
    signatureTypes[i] = components[i].ComponentType;
var archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(signatureTypes));

// AFTER:
// Copy to stack or temp array, sort, compute hash, look up
var sortTypes = new ComponentType[components.Length];
for (var i = 0; i < components.Length; i++)
    sortTypes[i] = components[i].ComponentType;
Array.Sort(sortTypes);
var typeSpan = new ReadOnlySpan<ComponentType>(sortTypes);
var archetypeHash = World.ComputeArchetypeHash(typeSpan);
var found = _world.FindArchetype(archetypeHash, typeSpan);
if (found != null)
{
    archetype = found;
}
else
{
    // Truly new archetype (extremely rare during gameplay)
    var signatureTypes = new ComponentType[components.Length];
    for (var i = 0; i < components.Length; i++)
        signatureTypes[i] = sortTypes[i];
    archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(signatureTypes));
}
```

Note: The `archetype` variable must be declared before the if/else block:
```csharp
Archetype archetype;
if (found != null) { archetype = found; }
else { var comps = ...; archetype = _world.GetOrCreateArchetype(...); }
```

### CommandBuffer.cs changes

Same pattern in `BuildCreatedEntityComponents` (around line 941). Types are already sorted (line 934: `Array.Sort(types, sources, 0, idx)`). So just compute hash + look up:

```csharp
// BEFORE:
var comps = new ComponentType[idx];
Array.Copy(types, comps, idx);
archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(comps));

// AFTER:
var typeSpan = new ReadOnlySpan<ComponentType>(types, 0, idx);
var archetypeHash = World.ComputeArchetypeHash(typeSpan);
var found = _world.FindArchetype(archetypeHash, typeSpan);
if (found != null)
{
    archetype = found;
}
else
{
    var comps = new ComponentType[idx];
    Array.Copy(types, comps, idx);
    archetype = _world.GetOrCreateArchetype(Signature.CreateNormalized(comps));
    InsertArchetypeCache(types, idx, archetype, hash);
}
```

## Known Issue: Mysterious IndexOutOfRangeException

When implementing this, I encountered a crash that I could not debug:

- **Crash**: `IndexOutOfRangeException` at `GroupByArchetype` method in CommandStream.cs, reported at the line `var groupKeys = ArrayPool<int>.Shared.Rent(maxGroups)` (line 834 in the current file).
- **This line cannot physically throw IndexOutOfRangeException.** The JIT is reporting the wrong source line.
- **Only occurs when any hash-related code exists** in the codebase (even in a separate `[MethodImpl(MethodImplOptions.NoInlining)]` method).
- **Does NOT occur** when `ArchetypeCacheSize` is changed from 4 to 16 (confirmed).
- **Does NOT occur** when the hash dictionary insertion is commented out but the hash computation + FindArchetype remains.
- The crash persists with clean builds (`dotnet build --force`, clean bin/obj).

Hypotheses to investigate:
1. The JIT might be inlining the caller (`GroupByArchetype` into `MaterializeCreatedEntities` into `Submit`) and the line number mapping is completely lost.
2. There might be a buffer overrun in `Array.Sort` on `ComponentType[]` (a readonly record struct) that corrupts the heap, causing a delayed crash in unrelated code.
3. `Span<ComponentType>.Sort()` (used in `SpanHelper.SortAndDeduplicate`) might behave differently from `Array.Sort(ComponentType[])` for some edge case.
4. There might be a build artifact issue where stale IL is being used despite clean build.

## Verification

After implementation, run:
```bash
dotnet test -c Release tests/MiniArch.Tests/MiniArch.Tests.csproj --filter CommandBufferTests
dotnet run -c Release --project perf/CommandBufferGame.Perf -- --warmup 2 --measure 5
dotnet run -c Release --project perf/HeroComing.Perf
```

Expected result: All three engines show 8.0 KB Heap Δ in both SteadyCombat and ParticleStorm scenarios. No crash in CommandStream ParticleStorm scenario.
