# Changelog

## 2.2.0 (2026-06-30)

- **New: `IChunkForEach` interface** — zero-allocation chunk iteration via struct-generic `Query.ForEachChunk<TForEach>(ref TForEach)` and `ForEachChunkParallel<TForEach>(in TForEach)`. JIT devirtualises the per-chunk call; no delegate allocation when the inline-lambda pattern was previously used uncached.
- **New: `WorldStateSnapshot.IsRecycled`** — lifecycle flag exposing whether a snapshot handle has been restored. `RestoreState` now throws `InvalidOperationException` on a recycled handle (previously silently corrupted world state).
- **Changed: CaptureState/RestoreState now backed by a pool** — supports GGPO-style rollback windows deeper than 1 frame (previously only depth=1 was zero-alloc). Multiple snapshots may be live simultaneously and restored out of order on misprediction.
- **Internal rename: `MiniArch.Core.Query` → `MiniArch.Core.QueryCache`** — no public API change. Removes the namespace collision between the public `MiniArch.Query` struct and the internal cache type.
- **Removed public `EntityInfo` type** — `RowIndex` was public but meaningless without `Archetype` (which was internal). Replaced by `World.TryGetEntityVersion(Entity, out int version)`. `EntityInfo` is now internal-only; `TryGetLocation` is now internal.

## 2.1.0 (2026-06-30)

- **API rename**: `Link`/`Unlink` → `AddChild`/`RemoveChild` (directional parent-child semantics)
- **Zero-alloc children traversal**: `World.EnumerateChildren()` → public `ChildrenEnumerable` struct
- **O(1) children check**: `World.HasChildren(entity)`
- **Dropped**: `World.GetChildren()` (List allocation; use `EnumerateChildren`)

## 0.1.0 (2026-06-13)

- Initial public release
- Archetype ECS runtime with `World`, `Entity`, `QueryDescription`
- Chunk-level iteration via `MiniArch.Core.QueryCache`
- `CommandBuffer` — deferred command recording with per-entity deduplication
- `CommandStream` — byte-stream command recording, 20–48% faster than CommandBuffer
- `FrameDelta` — self-contained delta for cross-world replay
- `World.Replay()` — deterministic replay of FrameDelta
- `World.Clone()` — deep copy for rollback checkpoints
- `WorldSnapshot.Save/Load` — full binary serialization
- `SubmitAndSnapshotAsync()` — pipelined submit + delta building
- Hierarchy: `AddChild` / `RemoveChild` with cascade destroy
- Deterministic entity ID allocation with LIFO recycling
- 1000-frame fuzz-tested cross-world replay
