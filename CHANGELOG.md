# Changelog

## 2.3.0 (2026-07-05)

- **Refactor: CommandStream split** — `CommandStream` split into `SingleThreadCommandStream` (single writer) and `ParallelCommandStream` (thread-safe). Mutators changed from `abstract`+`override` to `new` (non-virtual), eliminating virtual dispatch overhead in hot paths. Parallel arrays replaced with struct arrays for cache locality.
- **New: Diagnostics tools** — `WorldDiff` for lockstep divergence diagnosis, `WorldValidator` for structural integrity checks, `EntityDump`/`WorldDigest` for runtime introspection.
- **Perf: CommandStream hot path** — `GetRecordFast` skips redundant entity lookup in `ApplyToWorld`; `SetComponentAtFlat` provides flat-index fast path; Set-only operations bypass full-record plumbing.
- **Perf: Bounds check elimination** — `GetOrCreateStore<T>` and `TryGetRecord` round-trip eliminated; `ComponentStore` hot paths; `_columnByteOffsets[index]` hoisted in `CopyRemovedRow`/`CopySegmentColumn`.
- **Perf: Capacity rounding** — segment capacity rounded to power of 2, replacing `DIV` with `SHR+AND` in `GetSegmentAndLocal`.
- **Chunk storage hardening** — DEBUG-only invariant assertions (`AssertSegmentInvariants`, cross-mode `RestoreTo` guards); `GetColumnRef` helper replacing raw pointer arithmetic; `NonChunkedSegmentIndex` constant; APIs renamed to `GetFlat*` for honesty.
- **Feat: `Entity.IsPlaceholder`/`IsUnmappedSentinel`** — public flags for deferred entity lifecycle inspection.
- **Feat: CRC32 tail integrity** — `WorldSnapshot` v4 CRC32 checksum for corrupted-data detection.
- **FrameDelta hardening** — wire size budgets for OOM/DoS protection; `Validate()` defense-in-depth with attack surface tests; removed `FormatVersion`/header/magic/endianness checks (YAGNI); entity `Id` bias `+1` for compact varint encoding.
- **EntitySlot / Replay rework** — `CommandStream.Replay` with `EntitySlot` auto-resolution; `Track` method for slot tracking; `OriginStream` field in `FrameDelta`; implicit `EntitySlot → Entity` conversion; `World.Replay` and `FrameDelta.Concat` removed (use `CommandStream.Replay`).
- **Strict Add/Set** — `World.Add` (strict) and `World.Set` (strict) separated; last upsert holdout in deferred path cleaned up.
- **Removed** — `ICommandRecorder` interface (YAGNI), public `World.Replay`/`FrameDelta.Concat` (superseded), `GetFirst<T>` (use `GetSingleton<T>`), legacy query chunks enumerable, `SpanSorting` dead overload.

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
