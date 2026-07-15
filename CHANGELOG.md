# Changelog

## 4.0.0 (2026-07-15)

- **Breaking: unsafe chunk column API made explicit** — `ChunkView.GetComponentSpanAt<T>` is now `UnsafeGetComponentSpanAt<T>`; the cached index must come from the same archetype and is invalid after structural change. Debug validates the type/index pair, Release keeps the opt-in unchecked fast path.
- **Breaking: redundant entity sentinel removed** — `Entity.IsUnmappedSentinel` was removed; use the single `IsPlaceholder` representation for deferred entities.
- **Harden: archetype growth commits last** — segment-capacity arithmetic is checked and storage migration completes before publishing new capacity/segment fields.
- **Harden: CommandStream preflight** — component presence, hierarchy overlay, pending slots, and async ownership are validated before allocator/materialization or worker handoff. This narrows known partial-submit paths but is not a general rollback transaction.
- **Perf: component liveness decided at consume time** — single and parallel record paths no longer repeat `World.IsAlive` for existing Add/Set/Remove commands. Consume-time pruning still uses the full `(Id, Version)` handle; stale-only streams correctly report no submitted work.
- **Refactor: split `CommandStreamCore` partials** — hierarchy, pending batches, component stores, and submit/async code moved into focused files. Canonical IL/JIT anchors and Release profiler gates were checked before and after the split.
- **Fix: Debug structural scope recovery** — structural mutation scopes now restore their debug counter through exceptions.
- **Verification: lockstep soak made trustworthy and faster** — same-frame component presence, tracked placeholder resolution, CompA/CompB mutation checks, record-time exception reporting, and configurable `--checksum-interval` were added; obsolete historical “proof” claims were retired.

## 3.6.0 (2026-07-15)

- **New: `ComponentSchema.Export()` / `Import()`** — cross-process schema handshake for determinism across peers. Authoritative peer exports the ordered list of registered component types as a portable `byte[]` blob; joining peer imports it to build a schema-index → type mapping. Subsequent wire messages can reference components by schema index instead of process-local `ComponentType.Value`, which may differ between peers.
- **Harden: External-boundary validation** — `ComponentSchema.Export/Import` and `WorldSnapshot.Load` hardened with input validation, size caps, and edge-case attack tests (null/empty/corrupt/max-size inputs).
- **Harden: `WorldSnapshot.Load` overflow & size validation** — segment capacity overflow guard, component type limit enforcement, and comprehensive boundary tests.
- **Public API: +2 methods** — `ComponentSchema.Export()` → `byte[]`, `ComponentSchema.Import(byte[] data)` → `Type[]`.
- **Cleanup: CommandStream.CreateMany deduplication** — `ICreateManyWriter` constraint docs deduplicated, type vars hoisted before loop, stale `.gitignore` entry removed. Net −116 lines of code.

## 3.0.0 (2026-07-10)

- **Breaking: Change tracking replaced with Watch API** — `ChangeTracker`/`ValueTracker`/`IChangeTracker` removed, replaced by `ChangeWatch<TComponent, THandler>` / `ChangeWatch<TComponent, TValue, THandler>` / `TransitionWatch<THandler>` pull-event model. See `kb-change-tracking.md`.
- **Breaking: `LayoutKind.Auto` struct components rejected** — `World.Add`/`Set` now throws on components with `LayoutKind.Auto` packing, guaranteeing cross-host determinism. See `kb-determinism-proof.md`.
- **Breaking: Enum components now accepted** — `LayoutKind.Sequential` enum components are now valid; previously they were incorrectly rejected.
- **New: `Exact()` strict archetype matching** — `QueryDescription.Exact()` modifier for queries that must match the exact archetype signature, not a superset.
- **New: `ComponentBucketQuery<T>`** — deterministic per-key component value query: caller provides `Span<TKey>` and `Span<Entity>`, receives matching entities in query order. Zero core intrusion, zero allocation after setup.
- **New: Fingerprint sort cache** — `OrderByEntityId` sorts by archetype fingerprint hash instead of entity id comparison, keeping sort as a pure function in `Chunk`.
- **Perf: `Exact()` archetype pre-filter** — skips archetype scanning when exact match is set.
- **Perf: M2 prevalidate pending submit slots** — earlier detection of slot exhaustion with epoch-guard optimization.
- **Test: Public API sentinel** — automated tracking of public API surface changes.
- **Test: M3 cross-feature parity matrix** — structural/query/hierarchy/snapshot cross-feature coverage.
- **Test: M4 metamorphic parity** — Submit vs Replay vs Restore three-way convergence.
- **Test: M7 soak pressure matrix** — 32×100K + 3×1M + boundary soak all PASS.
- **Docs: Full knowledge base consistency audit** — 24 `.knowledge/` pages, CONTRIBUTING, 6 scripts updated.
- **Docs: Managed entity sidecar evaluation** — No-Go verdict: prototype beats dictionary but doesn't beat competent dense user enough to justify new public API. Full report at `docs/plans/2026-07-10-managed-entity-sidecar-value-report.md`.
- **Chore: Dead code cleanup** — M6 removed 15 lines of unused code; redundant `#if DEBUG` around `Debug.Assert` removed.
- **Chore: Warnings resolved** — pre-existing test warnings fixed.

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
