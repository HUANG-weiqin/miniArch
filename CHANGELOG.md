# Changelog

## Unreleased

## 7.2.1 (2026-10-15)

- **Repack of the v7.2.0 tag:** this version rebuilds exactly the tree of tag `v7.2.0` and contains no functional changes of its own. The `MiniArch.7.2.0` package already in the local feed was packed from that tag plus one later commit (`69d6eda refactor: consolidate world query caching`), so its provenance did not match the tag. Pin `7.2.1` to consume the tag content with exact provenance.

## 7.2.0 (2026-10-12)

- **Prevent deferred snapshot desynchronization:** `Snapshot` and `SnapshotInto` now reject a frame containing an immediate Create that was later cancelled, because its allocator Reserve+Release history cannot be omitted from a placeholder delta. Real-id cancellation deltas remain unchanged.
- **Harden untrusted FrameDelta validation:** `Validate()` rejects Add/Set/Remove after Destroy of the same full entity handle. Repeated Destroy remains idempotent, and a recycled ID with a new version remains valid. FrameDelta wire format is unchanged.
- **Reject disposed World state exports:** `Clone`, snapshot Save/checksums, and world diagnostics/statistics now throw `ObjectDisposedException` instead of reporting a valid empty world. Lightweight `EntityCount` and `EntityCapacity` properties are unchanged.

## 7.1.0 (2026-10-11)

- **Recalibrate the DestroyMany cold-span check:** full-batch and ≥50%-density span samples now share a 0.65× floor versus the guarded loop, based on three fixed calibration runs. This is an acceptance-policy change, not a runtime speedup; query thresholds, workload, timing boundaries, correctness checks, allocation measurement, and the Hero gate are unchanged.
- **Reduce batch-destroy scratch:** archetype-group buffers now grow independently from per-entity candidate buffers. Bulk span deletion reuses Clear's record-kill loop with one free-list capacity reservation, and no longer revalidates records already verified during collection. Duplicate/stale handling, entity-version advancement, and free-list order are unchanged; warmed reuse stays allocation-free.
- **Clarify cancelled pending handles:** a cancelled pending handle must not be reused as a command target. Historical deferred duplicate-cancel tolerance is not a general no-op guarantee; runtime validation is unchanged.
- **Simplify flat-entity removal:** `Destroy(in QueryDescription)` now shares `Clear`'s direct archetype-reset path, avoiding batch collection/grouping scratch while preserving entity-version and free-list order. Both public entry points remain available.
- **Simplify CommandStream:** remove the redundant cancelled-source Clone scan and unreachable Destroy-placeholder handling; reuse the existing single-target create path for Clone. Invalid-handle preflight, component Entity-reference resolution, and pending/materialized component-copy semantics remain unchanged.

## 7.0.0 (2026-10-11)

- **Fix: steady-state parallel query allocations.** `ForEachChunkParallel` now reuses native ThreadPool work items and per-call state instead of allocating a closure and TPL dispatch objects on every call. Cached delegates and struct jobs are allocation-free after warmup; first use, growth, exceptions, and user callback/context-notification allocations are excluded. Synchronous completion, nested calls, shared by-value struct state, and execution-context flow/restoration remain supported. Public API signatures are unchanged.
- **Rollback safety:** `CaptureState()` rejects outstanding real-id reservations. A successful `RestoreState()` invalidates old recorded CommandStream frames; `Clear()` safely discards them without releasing reservations on the restored timeline, and the same stream can then be reused. Frozen async encoding may finish with a historical delta after restore; retained handles/refs/spans are not made safe by this guard.
- **Command consistency:** typed apply and wire emit keep separate paths with shared prepared-frame rules. Every consume path rejects inconsistent live `CreateMany` groups and duplicate writer component types before application/resolution or target clearing; fully cancelled groups are skipped. Existing failure cleanup and non-transactional apply semantics remain.
- **Fix:** a reused async frozen state's stale pending-map index could misclassify an existing entity as pending, dropping Clone components or Set commands, or causing an index error. Lookup now checks the logical pending-batch count before reading the entry.
- **Memory:** World and rollback free lists store only ordered IDs, deriving versions from entity records. Batch-destroy scratch grows on demand by its actual slot/batch/archetype-row dimension instead of eagerly with World capacity; warmed reuse remains supported.
- **Breaking: built-in entity hierarchy removed.** `World` and `CommandStream` no longer expose `AddChild`/`RemoveChild`, parent/children/ancestor traversal, cascade destroy, or subtree clone; hierarchy diagnostics are removed. `Destroy(Entity)` affects only that entity, and `Clone(Entity)` copies only that entity's components. Whole-World `Clone()` and ordinary component `Entity` references (including deferred placeholder resolution) remain.
- **Wire compatibility is explicit:** v5 snapshot layout remains, with hierarchy count always 0; older v5 files without hierarchy remain readable, but nonzero hierarchy counts are rejected rather than discarded. Non-hierarchy FrameDelta opcode values and formats remain unchanged; old `0x04`/`0x05` AddChild/RemoveChild operations are rejected, not ignored or renumbered. Users needing old relationships must export/migrate them with a hierarchy-capable version before loading in the flat runtime. See [migration boundary](.knowledge/kb-hierarchy-runtime.md).

## 6.2.0 (2026-10-06)

- **Fix: default `Query` order no longer depends on other worlds' component registration or load order** — matched archetypes are ordered by the ordinally sorted set of stable component type identities, rather than process-local component IDs. Loading the same v5 world before or after another world now yields the same default query order. This **can change observable iteration order** versus 6.1.0; bulk `Clear(query)` / `Destroy(query)` can consequently recycle entity IDs in a different order. Entity order within each archetype still follows physical storage order.
- **Fix: `CommandStream.CreateMany` accepts component IDs at or above 512** — a valid high-ID component no longer fails the duplicate-component check; genuinely duplicated writer component types are still rejected.
- **Perf: generic `World.Create` archetype caches are per-world** — alternating creation in multiple warmed worlds reuses each world's archetype plans without per-call allocations.
- The query ordering work happens when a new archetype is published; steady-state query enumeration adds no allocation, while loading many distinct archetypes can cost more time and temporary allocations. Public API signatures and the v5 snapshot format are unchanged. Runtime component IDs and the `FrameDelta` wire format are unchanged; cross-process `FrameDelta` replay still requires matching component registration/schema.

## 6.1.0 (2026-09-28)

- **Changed: canonical v5 content covers non-empty archetypes only** — `WorldSnapshot.Save` and `ComputeCanonicalChecksum` share one `WriteCanonicalWorld` traversal, and both now skip archetypes with `EntityCount == 0`; the schema table likewise contains only the component types those non-empty archetypes actually use. Two worlds that differ only by empty archetypes (created then destroyed, or left behind by `RestoreState`) now produce the same `CanonicalChecksum()` and the same `Save` bytes as a world that never had that component combination. **Stored snapshots and canonical checksums of such worlds change value.** Types used only by empty archetypes are no longer serialized or schema-validated.
- Old v5 snapshots that contain empty archetype entries still load unchanged. Re-saving such a world normalizes the empty entries away, so the original bytes are not preserved — old files are readable one way, not byte-stable.
- Unchanged canonical fields: chunk capacity, every slot version, hierarchy relations, and the ordered free list. `Query` iteration order is unaffected — it is decided by signature sorting, not by which empty archetypes exist.
- `World.Checksum()` (the legacy rollback/lockstep projection) is unchanged; it already excluded empty archetypes.
- **Diagnostics: `WorldDigest.PerArchetype` keys are now dense ordinals over non-empty archetypes** (previously a leading empty archetype shifted every key). `WorldDigest.Total` therefore no longer changes because of empty archetypes, but remains sensitive to physical row order.
- **Changed: `World.RestoreState` no longer invalidates the create-archetype cache** — archetypes created after the capture survive the restore, so the cached create plans still point at live archetypes. After warm-up, a capture → create → restore → re-create cycle is allocation-free. `World.Reset` still invalidates the cache because it drops the archetypes themselves.
- Public API signatures unchanged.

## 6.0.0 (2026-09-02)

- **Breaking: `WorldSnapshot` is now canonical format v5 only** — `Load` rejects v3, v4, and unknown versions with `InvalidDataException`. Existing snapshot files must be regenerated or migrated outside MiniArch.
- Removed raw-memory component persistence and hashing. Snapshot values are encoded from cached field plans: all instance fields (including private/backing fields), nested unmanaged structs, fixed buffers, and inline arrays; primitives use fixed-width little-endian, booleans are canonical `0`/`1`, enums use their underlying type, and floating-point bit patterns are preserved.
- Snapshot/schema bytes no longer contain CLR padding, offsets, alignment, pack, declaration order, or runtime `ComponentType.Value`; worlds with identical logical fields and v5 persistence state are stable across poisoned padding, processes, and component registration order.
- `Save` and `ComputeCanonicalChecksum` now share the exact canonical world traversal. `Save` writes `payload + CRC32(payload)`; canonical checksum is SHA-256 of that same payload without the CRC trailer. The payload includes chunk capacity, all slot versions, schemas, empty archetypes, hierarchy, and ordered free-list state.
- `Load` now validates CRC, exact type identity and field shape, ordering, lengths, duplicates, component values, hierarchy, free-list state, and trailing bytes before constructing a world; partially constructed worlds are disposed if construction fails.
- `ComputeChecksum` / `World.Checksum()` keep their narrower rollback/lockstep projection for compatibility, but component values also use the canonical field codec. `CanonicalChecksum()` now explicitly means exact v5 persistence state, not storage-independent gameplay equivalence.
- Unsupported canonical shapes fail fast: pointers/function pointers, native-sized integers, recursive shapes, managed/by-ref-like values, AutoLayout, and overlapping fields. No raw fallback is provided.

## 5.3.0 (2026-09-01)

- **Breaking: parallel command recording removed** — `ParallelCommandStream` and its dedicated performance harness were removed after the implementation failed to demonstrate a break-even workload. Use one single-threaded `CommandStream` per recording owner and merge work at the application boundary when needed.
- **Breaking: redundant recorder base removed** — `CommandStreamCore` was folded into the sole sealed `CommandStream`; all record and consume APIs now have one owner.
- **Perf: homogeneous pending Create plans are reused** — consecutive pending creates with the same simple schema share component metadata and archetype column lookup while preserving per-entity row allocation and chunk layout.
- **Perf: profiling and regression measurement hardened** — CommandStream profiling now uses corrected elapsed-time, phase-unit, checksum, allocation, trace-boundary, and stable-workload measurements.

## 5.2.0 (2026-08-15)

- **Nested Entity support** — unmanaged components now resolve Entity references at arbitrary depth through sequential/explicit structs, record private backing fields, generic wrappers, and InlineArray elements.
- **Deterministic offsets** — cold scanning uses actual CLR field offsets and `ComponentSizeCache.GetSize` for InlineArray stride; source Submit/Snapshot/FrameDelta.Validate/Replay/Clone/Parallel consumers share the cached offsets.
- **Fail-fast layout guards** — Entity-bearing LayoutKind.Auto graphs and invalid partially overlapping Entity ranges are rejected; exact explicit aliases are deduplicated.
- **Perf** — steady-state paths add no managed allocation.

## 4.0.0 (2026-07-15)

- **Breaking: unsafe chunk column API made explicit** — `ChunkView.GetComponentSpanAt<T>` is now `UnsafeGetComponentSpanAt<T>`; the cached index must come from the same archetype and is invalid after structural change. Debug validates the type/index pair, Release keeps the opt-in unchecked fast path.
- **Breaking: redundant entity sentinel removed** — `Entity.IsUnmappedSentinel` was removed; use the single `IsPlaceholder` representation for deferred entities.
- **Harden: archetype growth commits last** — segment-capacity arithmetic is checked and storage migration completes before publishing new capacity/segment fields.
- **Changed: CommandStream validation is explicit** — `Validate()` checks embedded-placeholder/layout, component-presence, and final hierarchy-overlay contracts without mutating the World. `Submit`/`Snapshot`/async consume paths no longer run presence or hierarchy checks implicitly; apply-time failures may partially mutate the World. Mandatory pending-slot, explicit/embedded Entity-reference and layout, FrameDelta-budget, and async-ownership guards remain at their required consume boundaries. This is not a general rollback transaction.
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
