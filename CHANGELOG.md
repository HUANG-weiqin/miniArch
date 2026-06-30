# Changelog

## 2.1.0 (2026-06-30)

- **API rename**: `Link`/`Unlink` → `AddChild`/`RemoveChild` (directional parent-child semantics)
- **Zero-alloc children traversal**: `World.EnumerateChildren()` → public `ChildrenEnumerable` struct
- **O(1) children check**: `World.HasChildren(entity)`
- **Dropped**: `World.GetChildren()` (List allocation; use `EnumerateChildren`)

## 0.1.0 (2026-06-13)

- Initial public release
- Archetype ECS runtime with `World`, `Entity`, `QueryDescription`
- Chunk-level iteration via `MiniArch.Core.Query`
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
