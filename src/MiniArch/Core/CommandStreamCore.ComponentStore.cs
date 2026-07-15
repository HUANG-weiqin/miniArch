using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniArch.Core;

public abstract partial class CommandStreamCore
{
    private protected static class CommandTypeInfo<T> where T : unmanaged
    {
        public static readonly ComponentType Type = Component<T>.ComponentType;
    }

    // ── Internal types ────────────────────────────────────────────────

    private protected const byte KindAdd = 0;
    private protected const byte KindSet = 1;
    private protected const byte KindRemove = 2;

    /// <summary>
    /// Merges component store overlay entries directly into a flat array during Clone,
    /// eliminating the intermediate <see cref="List{T}"/> allocation of <c>OverlayCollector</c>.
    /// <para/>
    /// Callbacks from <see cref="ComponentStore.ForEachEntityEntry"/> go straight into
    /// the caller's stackalloc (or pooled) arrays — no lambda closures, no temp lists.
    /// </summary>
    private protected ref struct ComponentMerger
    {
        private readonly CommandStreamCore _core;
        private Span<ComponentType> _types;
        private Span<int> _offsets;
        private Span<int> _sizes;
        private ref int _count;
        private ComponentType[]? _rentedTypes;
        private int[]? _rentedOffsets;
        private int[]? _rentedSizes;

        public ComponentMerger(CommandStreamCore core,
            Span<ComponentType> types, Span<int> offsets, Span<int> sizes,
            ref int count)
        {
            _core = core;
            _types = types;
            _offsets = offsets;
            _sizes = sizes;
            _count = ref count;
            _rentedTypes = null;
            _rentedOffsets = null;
            _rentedSizes = null;
        }

        public readonly Span<ComponentType> Types => _types;
        public readonly Span<int> Offsets => _offsets;
        public readonly Span<int> Sizes => _sizes;
        public readonly int Count => _count;

        /// <summary>Returns any rented arrays back to the pool. Safe to call even if no arrays were rented.</summary>
        public void ReturnRented()
        {
            if (_rentedTypes is not null)
            {
                ArrayPool<ComponentType>.Shared.Return(_rentedTypes);
                _rentedTypes = null;
            }
            if (_rentedOffsets is not null)
            {
                ArrayPool<int>.Shared.Return(_rentedOffsets);
                _rentedOffsets = null;
            }
            if (_rentedSizes is not null)
            {
                ArrayPool<int>.Shared.Return(_rentedSizes);
                _rentedSizes = null;
            }
        }

        /// <summary>
        /// Merges one overlay entry directly into the typed arrays.
        /// <c>KindRemove</c> → find and shift-delete from the arrays.
        /// <c>KindAdd</c> / <c>KindSet</c> → overwrite in place or append.
        /// </summary>
        public void Add(ComponentType type, byte kind, ReadOnlySpan<byte> data)
        {
            if (kind == KindRemove)
            {
                // KindRemove: find type and remove by shifting remaining elements left
                for (var i = 0; i < _count; i++)
                {
                    if (_types[i] == type)
                    {
                        var shiftCount = _count - i - 1;
                        for (var j = 0; j < shiftCount; j++)
                        {
                            _types[i + j] = _types[i + j + 1];
                            _offsets[i + j] = _offsets[i + j + 1];
                            _sizes[i + j] = _sizes[i + j + 1];
                        }
                        _count--;
                        return;
                    }
                }
                // Type not found → nothing to remove
                return;
            }

            // KindAdd / KindSet: ensure capacity for at least one more entry
            if (_count >= _types.Length)
                Grow();

            // Reserve batch buffer space and copy data
            var offset = _core.ReserveBatchBufSpace(data.Length);
            data.CopyTo(new Span<byte>(_core._frozen.BatchBuf, offset, data.Length));

            // Find existing type → overwrite; otherwise append
            for (var i = 0; i < _count; i++)
            {
                if (_types[i] == type)
                {
                    _offsets[i] = offset;
                    _sizes[i] = data.Length;
                    return;
                }
            }

            // Append new entry
            _types[_count] = type;
            _offsets[_count] = offset;
            _sizes[_count] = data.Length;
            _count++;
        }

        private void Grow()
        {
            var newLen = _types.Length == 0 ? 64 : _types.Length * 2;

            var newTypes = ArrayPool<ComponentType>.Shared.Rent(newLen);
            var newOffsets = ArrayPool<int>.Shared.Rent(newLen);
            var newSizes = ArrayPool<int>.Shared.Rent(newLen);

            _types[.._count].CopyTo(newTypes);
            _offsets[.._count].CopyTo(newOffsets);
            _sizes[.._count].CopyTo(newSizes);

            ReturnRented();

            _types = newTypes;
            _offsets = newOffsets;
            _sizes = newSizes;
            _rentedTypes = newTypes;
            _rentedOffsets = newOffsets;
            _rentedSizes = newSizes;
        }
    }

    private protected abstract class ComponentStore
    {
        public abstract bool HasCommands { get; }
        public abstract bool HasStructuralCommands { get; }
        public abstract void PreflightValidate(
            World world, int[] generations, byte[] presence, int epoch, bool cacheSetRows);
        public abstract void ApplyToWorld(World world);
        public abstract void EmitToDelta(FrameDelta delta);
        public abstract bool PruneStaleCommands(World world);
        public abstract void Clear();
        public abstract void ReplacePlaceholders(Entity[] resolveMap);
        public abstract void SealParallelWrites();
        protected internal abstract void ForEachEntityEntry(Entity entity, ref ComponentMerger merger);

#if DEBUG
        internal bool _isReadOnly;
#endif
    }

    private struct StoreEntry<T> where T : unmanaged
    {
        public Entity Entity;
        public byte Kind;
        public T Value;
    }

    private protected sealed class ComponentStore<T> : ComponentStore where T : unmanaged
    {
        // ── Main (merged) storage — read path ──
        private StoreEntry<T>[] _entries = [];
        private int _count;

        // ── Kind tracking: enables a branchless Set-only fast path in ApplyToWorld ──
        // _allSetKind is true only when every entry in this store has Kind == KindSet.
        private bool _allSetKind = true;

        // Set-only preflight caches row locations. When all entries share one
        // archetype, Apply reuses these rows instead of reading entity records a
        // second time after validation.
        private int[] _preflightSetRows = [];
        private Archetype? _preflightSetArchetype;
        private bool _preflightSetRowsValid;

        // ── Per-thread local buffers — write path (parallel recording) ──
        private sealed class LocalBuffer
        {
            public StoreEntry<T>[] Entries = new StoreEntry<T>[256];
            public int Count;

            /// <summary>Append one entry. Returns true if this was the first entry (buffer was empty).</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Append(Entity entity, in T value, byte kind)
            {
                var i = Count;
                if ((uint)i >= (uint)Entries.Length)
                {
                    var newLen = Entries.Length == 0 ? 256 : Entries.Length * 2;
                    Array.Resize(ref Entries, newLen);
                }

                Entries[i] = new StoreEntry<T>
                {
                    Entity = entity,
                    Kind = kind,
                    Value = value,
                };
                Count = i + 1;
                return i == 0;
            }
        }

        // ── ThreadLocal storage (used for enumeration) ──
        private readonly ThreadLocal<LocalBuffer> _locals =
            new(() => new LocalBuffer(), trackAllValues: true);

        // ── [ThreadStatic] front-cache: avoids ThreadLocal.Value lookup on hot path ──
        private static int s_nextCacheId;
        private readonly int _cacheId = Interlocked.Increment(ref s_nextCacheId);

        [ThreadStatic] private static int t_cachedStoreId;
        [ThreadStatic] private static LocalBuffer? t_cachedLocal;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private LocalBuffer GetLocal()
        {
            if (t_cachedStoreId == _cacheId)
            {
                var local = t_cachedLocal;
                if (local is not null) return local;
            }
            return GetLocalSlow();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private LocalBuffer GetLocalSlow()
        {
            var local = _locals.Value!;
            t_cachedStoreId = _cacheId;
            t_cachedLocal = local;
            return local;
        }

        private volatile int _hasLocalWrites;

        // ── Public API ──

        public override bool HasCommands => _count > 0 || _hasLocalWrites != 0;
        public override bool HasStructuralCommands => HasCommands && !_allSetKind;

        public void Append(Entity entity, in T value, byte kind)
        {
#if DEBUG
            Debug.Assert(!_isReadOnly, "Cannot write to a read-only ComponentStore");
#endif
            EnsureStoreCapacity();
            var count = _count;
            ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), count);
            entry.Entity = entity;
            entry.Kind = kind;
            entry.Value = value;
            _count = count + 1;
            if (kind != KindSet) _allSetKind = false;
        }

        public void AppendRemove(Entity entity)
        {
#if DEBUG
            Debug.Assert(!_isReadOnly, "Cannot write to a read-only ComponentStore");
#endif
            EnsureStoreCapacity();
            var count = _count;
            ref var entry = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries), count);
            entry.Entity = entity;
            entry.Kind = KindRemove;
            entry.Value = default;
            _count = count + 1;
            _allSetKind = false;
        }

        public void AppendConcurrent(Entity entity, in T value, byte kind)
        {
#if DEBUG
            Debug.Assert(!_isReadOnly, "Cannot write to a read-only ComponentStore");
#endif
            var local = GetLocal();
            if (local.Append(entity, value, kind))
                _hasLocalWrites = 1;
        }

        public override void SealParallelWrites()
        {
            if (_hasLocalWrites == 0)
                return;

            // Parallel writes may contain non-Set kinds; conservatively disable fast path.
            _allSetKind = false;

            var locals = _locals.Values;

            // Count total entries and find first non-empty local
            int total = _count, nonEmpty = 0;
            LocalBuffer? firstNonEmpty = null;
            foreach (var local in locals)
            {
                if (local.Count > 0)
                {
                    total += local.Count;
                    nonEmpty++;
                    firstNonEmpty ??= local;
                }
            }

            if (nonEmpty == 0)
            {
                _hasLocalWrites = 0;
                return;
            }

            // Steal: when _entries is empty and only one writer, steal its array.
            // This eliminates the Array.Copy entirely for the common single-writer case
            // and also for cases where serial Append happened on an empty store followed
            // by a single parallel writer.
            if (_count == 0 && nonEmpty == 1)
            {
                var oldEntries = _entries;
                _entries = firstNonEmpty!.Entries;
                _count = firstNonEmpty.Count;
                firstNonEmpty.Entries = oldEntries; // reuse old empty/small array
                firstNonEmpty.Count = 0;
                _hasLocalWrites = 0;
                return;
            }

            // Normal merge: copy all local buffers into _entries
            EnsureCapacity(total);

            var dst = _count;
            foreach (var local in locals)
            {
                var n = local.Count;
                if (n == 0) continue;

                Array.Copy(local.Entries, 0, _entries, dst, n);
                dst += n;
                local.Count = 0;
            }

            _count = dst;
            _hasLocalWrites = 0;
        }

        public override void Clear()
        {
            _count = 0;
            _allSetKind = true;
            _preflightSetArchetype = null;
            _preflightSetRowsValid = false;
            if (_hasLocalWrites != 0)
            {
                foreach (var local in _locals.Values)
                    local.Count = 0;
                _hasLocalWrites = 0;
            }
        }

        public override bool PruneStaleCommands(World world)
        {
            var write = 0;
            var allSetKind = true;

            for (var read = 0; read < _count; read++)
            {
                ref var entry = ref _entries[read];
                if (entry.Entity.IsPlaceholder || !world.IsAlive(entry.Entity))
                    continue;

                if (write != read)
                    _entries[write] = entry;

                if (_entries[write].Kind != KindSet)
                    allSetKind = false;

                write++;
            }

            _count = write;
            _allSetKind = allSetKind;
            return write != 0;
        }

        // ── Private helpers ──

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int required)
        {
            if ((uint)required <= (uint)_entries.Length) return;
            var newLen = _entries.Length == 0 ? 256 : _entries.Length;
            while (newLen < required) newLen *= 2;
            Array.Resize(ref _entries, newLen);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureStoreCapacity()
        {
            if (_count < _entries.Length) return;
            var newLen = _entries.Length == 0 ? 256 : _entries.Length * 2;
            Array.Resize(ref _entries, newLen);
        }

        // ── Read-only consumers (must be called AFTER SealParallelWrites) ──

        public override void PreflightValidate(
            World world, int[] generations, byte[] presence, int epoch, bool cacheSetRows)
        {
            var count = _count;
            var compType = Component<T>.ComponentType;
            ref var entriesRef = ref MemoryMarshal.GetArrayDataReference(_entries);
            _preflightSetArchetype = null;
            _preflightSetRowsValid = false;

            // Set-only is the dominant gameplay path. It does not need virtual
            // state because Set never changes presence. Cache row indices while
            // validating so the uniform-archetype Apply path does not read every
            // entity record again.
            if (_allSetKind)
            {
                if (cacheSetRows && _preflightSetRows.Length < count)
                    Array.Resize(ref _preflightSetRows, Math.Max(count, Math.Max(256, _preflightSetRows.Length * 2)));

                Archetype? lastArchetype = null;
                var lastHasComponent = false;
                var uniformArchetype = true;
                for (var i = 0; i < count; i++)
                {
                    ref var entry = ref Unsafe.Add(ref entriesRef, i);
                    var record = world.GetRecordFast(entry.Entity);
                    var archetype = record.Archetype!;
                    if (cacheSetRows)
                        _preflightSetRows[i] = record.RowIndex;
                    if (archetype != lastArchetype)
                    {
                        if (lastArchetype is not null)
                            uniformArchetype = false;
                        lastArchetype = archetype;
                        lastHasComponent = archetype.TryGetComponentIndex(compType, out _);
                    }

                    if (!lastHasComponent)
                        throw new InvalidOperationException(
                            $"Entity {entry.Entity} does not have component {typeof(T).Name}.");
                }

                if (cacheSetRows)
                {
                    _preflightSetArchetype = uniformArchetype ? lastArchetype : null;
                    _preflightSetRowsValid = true;
                }
                return;
            }

            for (var i = 0; i < count; i++)
            {
                ref var entry = ref Unsafe.Add(ref entriesRef, i);
                var entityId = entry.Entity.Id;
                Debug.Assert((uint)entityId < (uint)generations.Length,
                    $"Preflight entity id {entityId} is outside scratch capacity {generations.Length}.");

                if (generations[entityId] != epoch)
                {
                    var record = world.GetRecordFast(entry.Entity);
                    generations[entityId] = epoch;
                    presence[entityId] = record.Archetype!.TryGetComponentIndex(compType, out _)
                        ? (byte)1
                        : (byte)0;
                }

                var isPresent = presence[entityId] != 0;
                if (entry.Kind == KindAdd)
                {
                    if (isPresent)
                    {
                        throw new InvalidOperationException(
                            $"Entity {entry.Entity} already has component type {compType.Value}. " +
                            "Use Set<T> to overwrite, or remove the component first.");
                    }
                    presence[entityId] = 1;
                }
                else if (entry.Kind == KindSet)
                {
                    if (!isPresent)
                        throw new InvalidOperationException(
                            $"Entity {entry.Entity} does not have component {typeof(T).Name}.");
                }
                else
                {
                    // Remove is intentionally idempotent: missing components are
                    // a no-op in World.Remove<T> and CommandStream preserves it.
                    presence[entityId] = 0;
                }
            }
        }

        public override void ApplyToWorld(World world)
        {
            var count = _count;
            var compType = Component<T>.ComponentType;
            ref var entriesRef = ref MemoryMarshal.GetArrayDataReference(_entries);

            // Set-only fast path: every entry is KindSet, so we skip the per-entry
            // Kind branch and the lastArch invalidation that Add/Remove require.
            if (_allSetKind)
            {
                if (_preflightSetRowsValid && _preflightSetArchetype is { } validatedArch)
                {
                    if (!validatedArch.TryGetComponentIndex(compType, out var validatedColIdx))
                    {
                        throw new InvalidOperationException(
                            $"Preflight cache lost component {typeof(T).Name}.");
                    }

                    var validatedByteOffset = validatedArch.GetColumnByteOffset(validatedColIdx);
                    if (!validatedArch.IsChunked)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            ref var entry = ref Unsafe.Add(ref entriesRef, i);
                            validatedArch.SetComponentAtFlatNoTrack<T>(
                                validatedColIdx, validatedByteOffset, _preflightSetRows[i], in entry.Value);
                        }
                    }
                    else
                    {
                        for (var i = 0; i < count; i++)
                        {
                            ref var entry = ref Unsafe.Add(ref entriesRef, i);
                            validatedArch.SetComponentAtTypedNoTrack(
                                validatedColIdx, _preflightSetRows[i], in entry.Value);
                        }
                    }
                    return;
                }

                Archetype? fastArch = null;
                int fastColIdx = -1;
                int fastByteOffset = 0;
                bool fastIsChunked = false;

                for (var i = 0; i < count; i++)
                {
                    ref var entry = ref Unsafe.Add(ref entriesRef, i);
                    var record = world.GetRecordFast(entry.Entity);
                    Debug.Assert(record.Archetype is not null && record.Version == entry.Entity.Version,
                        $"GetRecordFast returned stale or unoccupied record for entity {entry.Entity}.");
                    var arch = record.Archetype!;
                    if (arch != fastArch)
                    {
                        fastArch = arch;
                        if (!arch.TryGetComponentIndex(compType, out fastColIdx))
                            throw new InvalidOperationException(
                                $"Entity {entry.Entity} does not have component {typeof(T).Name}.");
                        fastByteOffset = arch.GetColumnByteOffset(fastColIdx);
                        fastIsChunked = arch.IsChunked;
                    }
                    if (!fastIsChunked)
                        arch.SetComponentAtFlatNoTrack<T>(fastColIdx, fastByteOffset, record.RowIndex, in entry.Value);
                    else
                        arch.SetComponentAtTypedNoTrack(fastColIdx, record.RowIndex, in entry.Value);
                }
                return;
            }

            // Mixed-kind path: full Kind dispatch + archetype cache invalidation.
            Archetype? lastArchMixed = null;
            int lastColIdx = -1;
            int lastByteOffsetMixed = 0;
            bool lastIsChunkedMixed = false;

            for (var i = 0; i < count; i++)
            {
                ref var entry = ref Unsafe.Add(ref entriesRef, i);
                var record = world.GetRecordFast(entry.Entity);
                Debug.Assert(record.Archetype is not null && record.Version == entry.Entity.Version,
                    $"GetRecordFast returned stale or unoccupied record for entity {entry.Entity}.");

                if (entry.Kind == KindSet)
                {
                    var arch = record.Archetype!;
                    if (arch != lastArchMixed)
                    {
                        lastArchMixed = arch;
                        if (!arch.TryGetComponentIndex(compType, out lastColIdx))
                            throw new InvalidOperationException(
                                $"Entity {entry.Entity} does not have component {typeof(T).Name}.");
                        lastByteOffsetMixed = arch.GetColumnByteOffset(lastColIdx);
                        lastIsChunkedMixed = arch.IsChunked;
                    }

                    if (!lastIsChunkedMixed)
                        arch.SetComponentAtFlatNoTrack<T>(lastColIdx, lastByteOffsetMixed, record.RowIndex, in entry.Value);
                    else
                        arch.SetComponentAtTypedNoTrack(lastColIdx, record.RowIndex, in entry.Value);
                }
                else
                {
                    lastArchMixed = null;
                    if (entry.Kind == KindAdd)
                        world.ApplyTypedAdd(entry.Entity, record, compType, in entry.Value);
                    else
                        world.RemoveBoxed(entry.Entity, record, compType);
                }
            }
        }

        public override void EmitToDelta(FrameDelta delta)
        {
            var compType = Component<T>.ComponentType;
            var size = Unsafe.SizeOf<T>();
            for (var i = 0; i < _count; i++)
            {
                switch (_entries[i].Kind)
                {
                    case KindAdd:
                        unsafe
                        {
                            fixed (T* pFixed = &_entries[i].Value)
                                delta.AddAddUnsafe(_entries[i].Entity, compType, (byte*)pFixed, size);
                        }
                        break;
                    case KindSet:
                        unsafe
                        {
                            fixed (T* pFixed = &_entries[i].Value)
                                delta.AddSetUnsafe(_entries[i].Entity, compType, (byte*)pFixed, size);
                        }
                        break;
                    case KindRemove:
                        delta.AddRemove(_entries[i].Entity, compType);
                        break;
                }
            }
        }

        public override void ReplacePlaceholders(Entity[] resolveMap)
        {
            var typeId = Component<T>.ComponentType;
            var offsets = EntityFieldResolver.GetOffsets(typeId);
            var dataSpan = new ReadOnlySpan<Entity>(resolveMap);

            for (var i = 0; i < _count; i++)
            {
                ref var entry = ref _entries[i];

                if (entry.Entity.IsPlaceholder)
                {
                    var resolved = resolveMap[entry.Entity.Version];
                    if (resolved.Id >= 0) entry.Entity = resolved;
                }

                if (offsets.Length > 0 && entry.Kind != KindRemove)
                {
                    EntityFieldResolver.ResolveInPlace(
                        MemoryMarshal.AsBytes(new Span<T>(ref entry.Value)),
                        typeId, dataSpan);
                }
            }
        }

        protected internal override void ForEachEntityEntry(Entity entity, ref ComponentMerger merger)
        {
            var ct = Component<T>.ComponentType;
            for (var i = 0; i < _count; i++)
            {
                ref var entry = ref _entries[i];
                if (entry.Entity == entity)
                {
                    merger.Add(ct, entry.Kind,
                        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(ref entry.Value)));
                }
            }
        }
    }

}
